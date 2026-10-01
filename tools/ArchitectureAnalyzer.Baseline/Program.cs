using System.Diagnostics;
using System.Text.Json;

namespace ArchitectureAnalyzer.BaselineTool;

internal static class Program
{
    private const string CaptureDiagnosticId = "AARC013";
    private const string CapturePrefix = "Baseline capture for ";
    private static readonly HashSet<string> BaselinableIds = new(StringComparer.Ordinal)
    {
        "AARC002",
        "AARC003",
        "AARC004",
        "AARC005",
        "AARC006",
        "AARC007",
        "AARC010",
        "AARC011",
    };

    private static readonly HashSet<string> FatalArchitectureIds = new(StringComparer.Ordinal)
    {
        "AARC001",
        "AARC008",
        "AARC009",
        "AARC012",
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2
            || !string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 2;
        }

        var projectPath = Path.GetFullPath(args[1]);
        if (!File.Exists(projectPath)
            || !string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("architecture-baseline: expected an existing .csproj path");
            return 2;
        }

        string? outputPath = null;
        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--output", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                outputPath = Path.GetFullPath(args[++index]);
                continue;
            }

            Console.Error.WriteLine($"architecture-baseline: unknown argument '{args[index]}'");
            return 2;
        }

        outputPath ??= Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "architecture.baseline.json");

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "architecture-analyzer-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var sarifPath = Path.Combine(tempDirectory, "diagnostics.sarif");

        try
        {
            // Prime project-reference outputs. A non-zero exit is expected when the target project
            // already has architecture errors; the focused no-dependencies build below is the
            // source of truth and its SARIF is validated before writing anything.
            await RunDotnetAsync(
                "build",
                projectPath,
                "--nologo",
                "-p:ArchitectureAnalyzerBaselineMode=ignore").ConfigureAwait(false);

            var focusedExit = await RunDotnetAsync(
                "build",
                projectPath,
                "--nologo",
                "--no-dependencies",
                "-p:ArchitectureAnalyzerBaselineMode=ignore",
                $"-p:ErrorLog={sarifPath},version=2.1").ConfigureAwait(false);

            if (!File.Exists(sarifPath))
            {
                Console.Error.WriteLine(
                    "architecture-baseline: compiler did not produce the requested SARIF log");
                return focusedExit == 0 ? 1 : focusedExit;
            }

            var collection = ReadDiagnostics(sarifPath);
            if (collection.FatalDiagnostics.Count > 0)
            {
                Console.Error.WriteLine(
                    "architecture-baseline: refusing to update baseline because non-baselinable "
                    + "errors or analyzer-integrity diagnostics were reported:");
                foreach (var diagnostic in collection.FatalDiagnostics)
                {
                    Console.Error.WriteLine("  " + diagnostic);
                }

                return 1;
            }

            var entries = collection.Entries
                .OrderBy(static entry => entry.DiagnosticId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                .ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var model = new BaselineFile(1, entries);
            var json = JsonSerializer.Serialize(
                model,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                });
            File.WriteAllText(outputPath, json + Environment.NewLine);

            Console.WriteLine(
                $"architecture-baseline: wrote {entries.Length} entries to {outputPath}");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only; never hide the actual generation result.
            }
        }
    }

    private static DiagnosticCollection ReadDiagnostics(string sarifPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(sarifPath));
        var entries = new Dictionary<(string DiagnosticId, string Key), BaselineEntry>();
        var fatal = new List<string>();
        var baselinableDiagnosticCount = 0;
        var captureRecordCount = 0;

        if (!document.RootElement.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("SARIF log has no runs array");
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("ruleId", out var ruleIdElement)
                    || ruleIdElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var ruleId = ruleIdElement.GetString()!;
                var message = ReadMessage(result);

                if (string.Equals(ruleId, CaptureDiagnosticId, StringComparison.Ordinal))
                {
                    if (!TryParseCaptureMessage(message, out var capturedId, out var key))
                    {
                        fatal.Add($"{CaptureDiagnosticId}: malformed capture record: {message}");
                        continue;
                    }

                    if (!BaselinableIds.Contains(capturedId!))
                    {
                        fatal.Add(
                            $"{CaptureDiagnosticId}: captured unsupported diagnostic '{capturedId}'");
                        continue;
                    }

                    captureRecordCount++;
                    entries[(capturedId!, key!)] = new BaselineEntry(capturedId!, key!);
                    continue;
                }

                if (FatalArchitectureIds.Contains(ruleId))
                {
                    fatal.Add($"{ruleId}: {message}");
                    continue;
                }

                if (BaselinableIds.Contains(ruleId))
                {
                    baselinableDiagnosticCount++;
                    continue;
                }

                if (IsError(result))
                {
                    fatal.Add($"{ruleId}: {message}");
                }
            }
        }

        if (baselinableDiagnosticCount > 0 && captureRecordCount == 0)
        {
            fatal.Add(
                "baseline capture mode reported architecture-policy diagnostics but no AARC013 "
                + "capture records were present");
        }

        return new DiagnosticCollection(entries.Values.ToArray(), fatal);
    }

    private static bool TryParseCaptureMessage(
        string message,
        out string? diagnosticId,
        out string? key)
    {
        diagnosticId = null;
        key = null;

        if (!message.StartsWith(CapturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = message.Substring(CapturePrefix.Length);
        var separator = remainder.IndexOf(": ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= remainder.Length)
        {
            return false;
        }

        diagnosticId = remainder.Substring(0, separator);
        key = remainder.Substring(separator + 2);
        return diagnosticId.Length > 0 && key.Length > 0;
    }

    private static string ReadMessage(JsonElement result)
    {
        if (!result.TryGetProperty("message", out var message))
        {
            return string.Empty;
        }

        // The compiler may emit SARIF 1.x-style string messages or SARIF 2.x message objects
        // depending on host/MSBuild plumbing. Accept both so baseline generation is host-stable.
        if (message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? string.Empty;
        }

        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool IsError(JsonElement result)
    {
        return result.TryGetProperty("level", out var level)
            && level.ValueKind == JsonValueKind.String
            && string.Equals(level.GetString(), "error", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RunDotnetAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start dotnet");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var standardOutput = await stdout.ConfigureAwait(false);
        var standardError = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            Console.Write(standardOutput);
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            Console.Error.Write(standardError);
        }

        return process.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: architecture-baseline generate <project.csproj> "
            + "[--output <architecture.baseline.json>]");
    }

    private sealed record BaselineFile(int Version, IReadOnlyList<BaselineEntry> Entries);

    private sealed record BaselineEntry(string DiagnosticId, string Key);

    private sealed record DiagnosticCollection(
        IReadOnlyList<BaselineEntry> Entries,
        IReadOnlyList<string> FatalDiagnostics);
}
