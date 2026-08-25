using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace ArchitectureAnalyzer.Contract;

/// <summary>
/// Outcome of parsing and validating an Architecture Contract document.
/// </summary>
public sealed class ArchitectureContractLoadResult
{
    private ArchitectureContractLoadResult(ArchitectureContract? contract, string? errorReason)
    {
        Contract = contract;
        ErrorReason = errorReason;
    }

    /// <summary>The parsed contract when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public ArchitectureContract? Contract { get; }

    /// <summary>
    /// The failure reason, suitable as argument 1 of AARC001, when <see cref="Succeeded"/> is
    /// <see langword="false"/>.
    /// </summary>
    public string? ErrorReason { get; }

    /// <summary>Whether a usable contract was produced.</summary>
    public bool Succeeded => Contract is not null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="contract">The parsed contract.</param>
    /// <returns>A successful result.</returns>
    public static ArchitectureContractLoadResult Success(ArchitectureContract contract)
    {
        return new ArchitectureContractLoadResult(contract, errorReason: null);
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="reason">Why the contract could not be loaded.</param>
    /// <returns>A failed result.</returns>
    public static ArchitectureContractLoadResult Failure(string reason)
    {
        return new ArchitectureContractLoadResult(contract: null, errorReason: reason);
    }
}

/// <summary>
/// Parses and validates <c>architecture.contract.json</c> documents.
/// </summary>
/// <remarks>
/// Deliberately free of any Roslyn type so the contract format can be unit tested — and later
/// reused by non-compiler tooling — without a compilation or an analyzer test harness.
/// </remarks>
public static class ArchitectureContractLoader
{
    /// <summary>The file name (case-insensitive) that identifies a contract in AdditionalFiles.</summary>
    public const string ContractFileName = "architecture.contract.json";

    /// <summary>
    /// Parses and validates a contract document.
    /// </summary>
    /// <param name="json">Raw JSON text.</param>
    /// <returns>A successful result carrying the contract, or a failure carrying the reason.</returns>
    public static ArchitectureContractLoadResult Load(string? json)
    {
        if (json is null)
        {
            return ArchitectureContractLoadResult.Failure("file not found");
        }

        if (json.Trim().Length == 0)
        {
            return ArchitectureContractLoadResult.Failure("the file is empty");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return ArchitectureContractLoadResult.Failure(ex.Message);
        }

        using (document)
        {
            return LoadCore(document.RootElement);
        }
    }

    private static ArchitectureContractLoadResult LoadCore(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ArchitectureContractLoadResult.Failure(
                "the contract root must be a JSON object");
        }

        var layersBuilder = ImmutableArray.CreateBuilder<LayerDefinition>();
        var declaredLayers = new HashSet<string>(StringComparer.Ordinal);

        if (!TryGetArray(root, "layers", out var layersElement, out var layersError))
        {
            return ArchitectureContractLoadResult.Failure(layersError!);
        }

        if (layersElement.ValueKind != JsonValueKind.Array)
        {
            return ArchitectureContractLoadResult.Failure("required property 'layers' is missing");
        }

        foreach (var layerElement in layersElement.EnumerateArray())
        {
            if (layerElement.ValueKind != JsonValueKind.Object)
            {
                return ArchitectureContractLoadResult.Failure("each entry of 'layers' must be a JSON object");
            }

            if (!TryGetNonEmptyString(layerElement, "name", out var name, out var nameError))
            {
                return ArchitectureContractLoadResult.Failure("in 'layers': " + nameError);
            }

            if (!declaredLayers.Add(name!))
            {
                return ArchitectureContractLoadResult.Failure(
                    $"layer '{name}' is declared more than once in layers");
            }

            var roots = ImmutableArray.CreateBuilder<string>();
            if (!TryGetArray(layerElement, "namespaceRoots", out var rootsElement, out var rootsError))
            {
                return ArchitectureContractLoadResult.Failure("in 'layers': " + rootsError);
            }

            if (rootsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rootElement in rootsElement.EnumerateArray())
                {
                    if (rootElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(rootElement.GetString()))
                    {
                        return ArchitectureContractLoadResult.Failure(
                            $"each entry of 'namespaceRoots' of layer '{name}' must be a non-empty string");
                    }

                    roots.Add(rootElement.GetString()!);
                }
            }

            layersBuilder.Add(new LayerDefinition(name!, roots.ToImmutable()));
        }

        var dependenciesBuilder = ImmutableArray.CreateBuilder<ForbiddenDependencyRule>();
        if (!TryGetArray(root, "forbiddenDependencies", out var dependenciesElement, out var dependenciesError))
        {
            return ArchitectureContractLoadResult.Failure(dependenciesError!);
        }

        if (dependenciesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dependenciesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return ArchitectureContractLoadResult.Failure(
                        "each entry of 'forbiddenDependencies' must be a JSON object");
                }

                if (!TryGetNonEmptyString(entry, "from", out var from, out var fromError))
                {
                    return ArchitectureContractLoadResult.Failure("in 'forbiddenDependencies': " + fromError);
                }

                if (!TryGetNonEmptyString(entry, "to", out var to, out var toError))
                {
                    return ArchitectureContractLoadResult.Failure("in 'forbiddenDependencies': " + toError);
                }

                if (!declaredLayers.Contains(from!))
                {
                    return ArchitectureContractLoadResult.Failure(UndeclaredLayer(from!, "forbiddenDependencies"));
                }

                if (!declaredLayers.Contains(to!))
                {
                    return ArchitectureContractLoadResult.Failure(UndeclaredLayer(to!, "forbiddenDependencies"));
                }

                dependenciesBuilder.Add(new ForbiddenDependencyRule(from!, to!, ReadReason(entry)));
            }
        }

        var apisBuilder = ImmutableArray.CreateBuilder<ForbiddenApiRule>();
        if (!TryGetArray(root, "forbiddenApis", out var apisElement, out var apisError))
        {
            return ArchitectureContractLoadResult.Failure(apisError!);
        }

        if (apisElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in apisElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return ArchitectureContractLoadResult.Failure(
                        "each entry of 'forbiddenApis' must be a JSON object");
                }

                if (!TryGetNonEmptyString(entry, "layer", out var layer, out var layerError))
                {
                    return ArchitectureContractLoadResult.Failure("in 'forbiddenApis': " + layerError);
                }

                if (!declaredLayers.Contains(layer!))
                {
                    return ArchitectureContractLoadResult.Failure(UndeclaredLayer(layer!, "forbiddenApis"));
                }

                if (!TryGetNonEmptyString(entry, "type", out var type, out var typeError))
                {
                    return ArchitectureContractLoadResult.Failure("in 'forbiddenApis': " + typeError);
                }

                string? member = null;
                if (entry.TryGetProperty("member", out var memberElement)
                    && memberElement.ValueKind != JsonValueKind.Null)
                {
                    if (memberElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(memberElement.GetString()))
                    {
                        return ArchitectureContractLoadResult.Failure(
                            "in 'forbiddenApis': property 'member' must be a non-empty string when present");
                    }

                    member = memberElement.GetString();
                }

                var wholeType = false;
                if (entry.TryGetProperty("wholeType", out var wholeTypeElement)
                    && wholeTypeElement.ValueKind != JsonValueKind.Null)
                {
                    if (wholeTypeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return ArchitectureContractLoadResult.Failure(
                            "in 'forbiddenApis': property 'wholeType' must be a boolean when present");
                    }

                    wholeType = wholeTypeElement.GetBoolean();
                }

                apisBuilder.Add(new ForbiddenApiRule(layer!, type!, member, wholeType, ReadReason(entry)));
            }
        }

        return ArchitectureContractLoadResult.Success(new ArchitectureContract(
            layersBuilder.ToImmutable(),
            dependenciesBuilder.ToImmutable(),
            apisBuilder.ToImmutable()));
    }

    private static string UndeclaredLayer(string layerName, string section)
    {
        return $"layer '{layerName}' referenced in {section} is not declared in layers";
    }

    private static string ReadReason(JsonElement entry)
    {
        return entry.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? reason.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryGetArray(JsonElement parent, string propertyName, out JsonElement value, out string? error)
    {
        if (!parent.TryGetProperty(propertyName, out value) || value.ValueKind == JsonValueKind.Null)
        {
            value = default;
            error = null;
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = $"property '{propertyName}' must be a JSON array";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetNonEmptyString(JsonElement parent, string propertyName, out string? value, out string? error)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = $"required property '{propertyName}' is missing";
            return false;
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = null;
            error = $"property '{propertyName}' must be a non-empty string";
            return false;
        }

        value = element.GetString();
        error = null;
        return true;
    }
}
