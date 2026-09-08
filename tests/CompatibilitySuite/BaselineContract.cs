using System;
using System.IO;
using System.Reflection;

namespace ArchitectureAnalyzer.CompatibilitySuite;

/// <summary>
/// The Architecture Contract that expresses, as data, exactly the rules PSXRecomp.Analyzer has
/// compiled into C# at the pinned baseline SHA.
/// </summary>
/// <remarks>
/// Transcribed from <c>ArchitectureFacts</c> (layers, namespace roots, marker attributes,
/// forbidden edges), <c>ForbiddenApiCatalog</c> (per-layer API rules and their reason strings) and
/// <c>AnalyzePInvokeDeclaration</c> (interop boundary). Reason strings are byte-identical to the
/// baseline's, because the parity comparison treats them as facts the message must carry.
/// Rule order inside a layer is preserved as well: both analyzers de-duplicate per member on the
/// rule's index, so re-ordering would change which operation a chained match is reported at.
/// See <c>docs/compatibility/psxrecomp-analyzer-baseline.md</c> §3.1-3.4 and §6.
/// </remarks>
public static class BaselineContract
{
    /// <summary>
    /// The baseline marker attribute definitions, read verbatim from the frozen copy of
    /// <c>PSXRecompArchitectureAttributes.cs</c> so fixtures declare layers exactly as the
    /// baseline's own tests do.
    /// </summary>
    public static string MarkerAttributeSource { get; } = ReadEmbedded("PSXRecompArchitectureAttributes.cs");

    /// <summary>The contract handed to ArchitectureAnalyzer for every parity scenario.</summary>
    public const string Json = """
        {
          "layers": [
            { "name": "Domain",         "namespaceRoots": [ "PSXRecomp.Core" ] },
            { "name": "Application",    "namespaceRoots": [ "PSXRecompStudio" ] },
            { "name": "Infrastructure", "namespaceRoots": [ "PSXRecomp.Infrastructure" ] },
            { "name": "Analyzer",       "namespaceRoots": [ "PSXRecomp.Analyzer" ] },
            { "name": "Test",           "namespaceRoots": [ "PSXRecomp.Tests" ] },
            { "name": "Generated",      "namespaceRoots": [ "PSXRecomp.Generated" ] }
          ],

          "layerDeclaration": {
            "required": true,
            "validateNamespaceConsistency": true,
            "markerNamespace": "PSXRecomp.Architecture",
            "markerAttributes": [
              { "attributeFqn": "PSXRecomp.Architecture.DomainAttribute",         "layer": "Domain" },
              { "attributeFqn": "PSXRecomp.Architecture.ApplicationAttribute",    "layer": "Application" },
              { "attributeFqn": "PSXRecomp.Architecture.InfrastructureAttribute", "layer": "Infrastructure" },
              { "attributeFqn": "PSXRecomp.Architecture.AnalyzerAttribute",       "layer": "Analyzer" },
              { "attributeFqn": "PSXRecomp.Architecture.TestAttribute",           "layer": "Test" },
              { "attributeFqn": "PSXRecomp.Architecture.GeneratedAttribute",      "layer": "Generated" }
            ]
          },

          "forbiddenDependencies": [
            { "from": "Domain",         "to": "Application",    "reason": "the Domain layer must not depend on the outer Application layer" },
            { "from": "Application",    "to": "Infrastructure", "reason": "the Application layer must reach Infrastructure only through the Domain interop boundary" },
            { "from": "Infrastructure", "to": "Application",    "reason": "the Infrastructure layer must not depend on the Application layer" },
            { "from": "Domain",         "to": "Test",           "reason": "production code must not depend on test code" },
            { "from": "Application",    "to": "Test",           "reason": "production code must not depend on test code" },
            { "from": "Infrastructure", "to": "Test",           "reason": "production code must not depend on test code" }
          ],

          "forbiddenApis": [
            { "layer": "Domain", "type": "System.Console",             "reason": "standard output must be abstracted behind an Infrastructure adapter" },
            { "layer": "Domain", "type": "System.IO.File",             "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Domain", "type": "System.IO.Directory",        "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Domain", "type": "System.Environment",         "reason": "execution environment dependencies break determinism" },
            { "layer": "Domain", "type": "System.Diagnostics.Process", "reason": "process control is an Infrastructure responsibility" },
            { "layer": "Domain", "type": "System.DateTimeOffset",      "reason": "non-deterministic time sources break determinism" },
            { "layer": "Domain", "type": "System.DateTime", "member": "Now",     "reason": "non-deterministic time sources break determinism" },
            { "layer": "Domain", "type": "System.DateTime", "member": "UtcNow",  "reason": "non-deterministic time sources break determinism" },
            { "layer": "Domain", "type": "System.Guid",     "member": "NewGuid", "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Domain", "type": "System.Random",              "wholeType": true, "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Domain", "type": "System.Net.Http.HttpClient", "wholeType": true, "reason": "network access is an Infrastructure responsibility" },
            { "layer": "Domain", "type": "System.Net.Sockets.Socket",  "wholeType": true, "reason": "network access is an Infrastructure responsibility" },

            { "layer": "Application", "type": "System.Console",      "reason": "standard output must be abstracted behind an Infrastructure adapter" },
            { "layer": "Application", "type": "System.IO.File",      "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Application", "type": "System.IO.Directory", "reason": "external I/O is an Infrastructure responsibility" },

            { "layer": "Infrastructure", "type": "System.Console",      "reason": "standard output must be abstracted behind an adapter interface" },
            { "layer": "Infrastructure", "type": "System.IO.File",      "reason": "external I/O must be abstracted behind an adapter interface" },
            { "layer": "Infrastructure", "type": "System.IO.Directory", "reason": "external I/O must be abstracted behind an adapter interface" },

            { "layer": "Test", "type": "System.Console",             "reason": "standard output must be abstracted behind an Infrastructure adapter" },
            { "layer": "Test", "type": "System.IO.File",             "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Test", "type": "System.IO.Directory",        "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Test", "type": "System.Environment",         "reason": "execution environment dependencies break determinism" },
            { "layer": "Test", "type": "System.Diagnostics.Process", "reason": "process control is an Infrastructure responsibility" },
            { "layer": "Test", "type": "System.Threading.Thread",    "reason": "manual thread management breaks deterministic execution" },
            { "layer": "Test", "type": "System.DateTime", "member": "Now",     "reason": "non-deterministic time sources break determinism" },
            { "layer": "Test", "type": "System.DateTime", "member": "UtcNow",  "reason": "non-deterministic time sources break determinism" },
            { "layer": "Test", "type": "System.Guid",     "member": "NewGuid", "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Test", "type": "System.Random",   "wholeType": true,   "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Test", "type": "System.Threading.Tasks.Task", "member": "Delay", "reason": "asynchronous timing must use controlled schedulers" },

            { "layer": "Analyzer", "type": "System.Console",             "reason": "standard output must be abstracted behind an Infrastructure adapter" },
            { "layer": "Analyzer", "type": "System.IO.File",             "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Analyzer", "type": "System.IO.Directory",        "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Analyzer", "type": "System.Environment",         "reason": "execution environment dependencies break determinism" },
            { "layer": "Analyzer", "type": "System.Diagnostics.Process", "reason": "process control is an Infrastructure responsibility" },
            { "layer": "Analyzer", "type": "System.Threading.Thread",    "reason": "manual thread management breaks deterministic execution" },
            { "layer": "Analyzer", "type": "System.DateTime", "member": "Now",     "reason": "non-deterministic time sources break determinism" },
            { "layer": "Analyzer", "type": "System.DateTime", "member": "UtcNow",  "reason": "non-deterministic time sources break determinism" },
            { "layer": "Analyzer", "type": "System.Guid",     "member": "NewGuid", "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Analyzer", "type": "System.Random",   "wholeType": true,   "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Analyzer", "type": "System.Threading.Tasks.Task", "member": "Delay", "reason": "asynchronous timing must use controlled schedulers" },

            { "layer": "Generated", "type": "System.Console",             "reason": "standard output must be abstracted behind an Infrastructure adapter" },
            { "layer": "Generated", "type": "System.IO.File",             "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Generated", "type": "System.IO.Directory",        "reason": "external I/O is an Infrastructure responsibility" },
            { "layer": "Generated", "type": "System.Environment",         "reason": "execution environment dependencies break determinism" },
            { "layer": "Generated", "type": "System.Diagnostics.Process", "reason": "process control is an Infrastructure responsibility" },
            { "layer": "Generated", "type": "System.Threading.Thread",    "reason": "manual thread management breaks deterministic execution" },
            { "layer": "Generated", "type": "System.DateTime", "member": "Now",     "reason": "non-deterministic time sources break determinism" },
            { "layer": "Generated", "type": "System.DateTime", "member": "UtcNow",  "reason": "non-deterministic time sources break determinism" },
            { "layer": "Generated", "type": "System.Guid",     "member": "NewGuid", "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Generated", "type": "System.Random",   "wholeType": true,   "reason": "non-deterministic randomness is forbidden" },
            { "layer": "Generated", "type": "System.Threading.Tasks.Task", "member": "Delay", "reason": "asynchronous timing must use controlled schedulers" }
          ],

          "interopBoundaryRules": [
            { "attribute": "System.Runtime.InteropServices.DllImportAttribute",    "allowedLayer": "Domain", "reason": "the C ABI boundary must stay in one place" },
            { "attribute": "System.Runtime.InteropServices.LibraryImportAttribute", "allowedLayer": "Domain", "reason": "the C ABI boundary must stay in one place" }
          ]
        }
        """;

    /// <summary>
    /// The baseline's own <c>.editorconfig</c> pins PSXR001-006 to <c>error</c>. AARC006 defaults
    /// to <c>Warning</c>, so restoring baseline strictness needs this one pin (baseline doc D7).
    /// </summary>
    public const string Aarc006SeverityPinKey = "dotnet_diagnostic.AARC006.severity";

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
