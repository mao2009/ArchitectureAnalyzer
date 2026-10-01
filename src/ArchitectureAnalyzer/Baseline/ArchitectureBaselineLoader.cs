using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace ArchitectureAnalyzer.Baseline;

/// <summary>Parses and validates architecture.baseline.json.</summary>
public static class ArchitectureBaselineLoader
{
    /// <summary>Current baseline-file schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Parses baseline JSON into an immutable exact-match set.</summary>
    public static ArchitectureBaselineLoadResult Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ArchitectureBaselineLoadResult.Failure("baseline file is empty");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException ex)
        {
            return ArchitectureBaselineLoadResult.Failure("invalid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ArchitectureBaselineLoadResult.Failure("baseline root must be a JSON object");
            }

            var versionCount = 0;
            int? version = null;
            var entriesCount = 0;
            var entriesElement = default(JsonElement);

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "version", StringComparison.Ordinal))
                {
                    versionCount++;
                    if (versionCount > 1)
                    {
                        return ArchitectureBaselineLoadResult.Failure(
                            "property 'version' must not appear more than once");
                    }

                    if (property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out var parsedVersion))
                    {
                        return ArchitectureBaselineLoadResult.Failure(
                            "property 'version' must be an integer");
                    }

                    version = parsedVersion;
                }
                else if (string.Equals(property.Name, "entries", StringComparison.Ordinal))
                {
                    entriesCount++;
                    if (entriesCount > 1)
                    {
                        return ArchitectureBaselineLoadResult.Failure(
                            "property 'entries' must not appear more than once");
                    }

                    entriesElement = property.Value;
                }
            }

            if (version is null)
            {
                return ArchitectureBaselineLoadResult.Failure("required property 'version' is missing");
            }

            if (version != CurrentVersion)
            {
                return ArchitectureBaselineLoadResult.Failure(
                    $"unsupported baseline version '{version}'; supported version is {CurrentVersion}");
            }

            if (entriesCount == 0)
            {
                return ArchitectureBaselineLoadResult.Failure("required property 'entries' is missing");
            }

            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                return ArchitectureBaselineLoadResult.Failure("property 'entries' must be a JSON array");
            }

            var keys = ImmutableHashSet.CreateBuilder<(string DiagnosticId, string Key)>();
            foreach (var entry in entriesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return ArchitectureBaselineLoadResult.Failure(
                        "each baseline entry must be a JSON object");
                }

                if (!TryReadNonEmptyString(entry, "diagnosticId", out var diagnosticId, out var error))
                {
                    return ArchitectureBaselineLoadResult.Failure("in baseline entry: " + error);
                }

                if (!ArchitectureBaseline.IsBaselinableDiagnosticId(diagnosticId!))
                {
                    return ArchitectureBaselineLoadResult.Failure(
                        $"diagnosticId '{diagnosticId}' cannot be baselined");
                }

                if (!TryReadNonEmptyString(entry, "key", out var key, out error))
                {
                    return ArchitectureBaselineLoadResult.Failure("in baseline entry: " + error);
                }

                if (entry.TryGetProperty("message", out var message)
                    && message.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return ArchitectureBaselineLoadResult.Failure(
                        "optional property 'message' of a baseline entry must be a string");
                }

                if (!keys.Add((diagnosticId!, key!)))
                {
                    return ArchitectureBaselineLoadResult.Failure(
                        $"baseline entry '{diagnosticId}' / '{key}' is declared more than once");
                }
            }

            return ArchitectureBaselineLoadResult.Success(new ArchitectureBaseline(keys.ToImmutable()));
        }
    }

    private static bool TryReadNonEmptyString(
        JsonElement element,
        string propertyName,
        out string? value,
        out string error)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            value = null;
            error = $"required property '{propertyName}' is missing";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = null;
            error = $"property '{propertyName}' must be a non-empty string";
            return false;
        }

        value = property.GetString();
        error = string.Empty;
        return true;
    }
}

/// <summary>Result of loading an architecture baseline.</summary>
public sealed class ArchitectureBaselineLoadResult
{
    private ArchitectureBaselineLoadResult(ArchitectureBaseline? baseline, string? errorReason)
    {
        Baseline = baseline;
        ErrorReason = errorReason;
    }

    /// <summary>Loaded baseline, or null on failure.</summary>
    public ArchitectureBaseline? Baseline { get; }

    /// <summary>Failure reason, or null on success.</summary>
    public string? ErrorReason { get; }

    /// <summary>Whether parsing and validation succeeded.</summary>
    public bool Succeeded => Baseline is not null;

    internal static ArchitectureBaselineLoadResult Success(ArchitectureBaseline baseline)
        => new(baseline, null);

    internal static ArchitectureBaselineLoadResult Failure(string reason)
        => new(null, reason);
}
