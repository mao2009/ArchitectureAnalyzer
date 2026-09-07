using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Interop-boundary rules (AARC007) driven by the optional <c>interopBoundaryRules</c> contract
/// section (#3). P/Invoke / LibraryImport placement is expressed generically via configured
/// attribute FQNs and allowed layers.
/// </summary>
public sealed class InteropBoundaryTests
{
    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "NativeInterop", "namespaceRoots": [ "Sample.Interop" ] }
          ],
          "interopBoundaryRules": [
            { "attribute": "System.Runtime.InteropServices.DllImportAttribute", "allowedLayer": "NativeInterop", "reason": "P/Invoke declarations must live in the NativeInterop layer." },
            { "attribute": "System.Runtime.InteropServices.LibraryImportAttribute", "allowedLayer": "NativeInterop", "reason": "Source-generated P/Invoke declarations must live in the NativeInterop layer." }
          ]
        }
        """;

    // ------------------------------------------------------------------------------------------
    // DllImport
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DllImport_AllowedLayer_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Interop
                {
                    public static class Native
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task DllImport_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public static class Worker
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(8, 35)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // LibraryImport
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryImport_AllowedLayer_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Interop
                {
                    public static partial class Native
                    {
                        [LibraryImport("user32.dll")]
                        public static partial void Beep();

                        public static partial void Beep()
                        {
                        }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task LibraryImport_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public static partial class Worker
                    {
                        [LibraryImport("user32.dll")]
                        public static partial void Beep();

                        public static partial void Beep()
                        {
                        }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(8, 36)
                .WithArguments("Beep", "NativeInterop", "Source-generated P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Alias / fully-qualified attribute forms
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AliasedDllImport_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using DllImportAlias = System.Runtime.InteropServices.DllImportAttribute;

                namespace Sample.Domain
                {
                    public static class Worker
                    {
                        [DllImportAlias("kernel32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(8, 35)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task FullyQualifiedDllImport_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public static class Worker
                    {
                        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(6, 35)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Attribute identity is symbolic (FQN), not short-name text
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task FakeDllImportWithSameShortName_DifferentNamespace_IsSilent()
    {
        // A lookalike DllImport on the same short name resolves to a different FQN and is not
        // matched, proving detection is symbol-based.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public sealed class DllImportAttribute : System.Attribute
                    {
                        public DllImportAttribute(string libraryName) { }
                    }

                    public static class Worker
                    {
                        [DllImport("kernel32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task AttributeNotConfigured_IsIgnored()
    {
        // The contract only configures DllImport/LibraryImport; anything else is invisible even
        // when it "looks" like an interop marker.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public sealed class MarkerAttr : System.Attribute
                    {
                        public MarkerAttr(string platform) { }
                    }

                    public static class Worker
                    {
                        [MarkerAttr("windows")]
                        public static void Run()
                        {
                        }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Nested / partial declarations
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task NestedClass_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public class Outer
                    {
                        public class Inner
                        {
                            [DllImport("user32.dll")]
                            public static extern void Beep();
                        }
                    }
                }
                """,
        };

        // The innermost type's namespace (Sample.Domain) classifies the declaration.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(10, 39)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task PartialMethod_ForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public static partial class Worker
                    {
                        [LibraryImport("user32.dll")]
                        public static partial void Beep();

                        public static partial void Beep()
                        {
                        }
                    }
                }
                """,
        };

        // The attribute lives on the declaration part; the report points at its identifier.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(8, 36)
                .WithArguments("Beep", "NativeInterop", "Source-generated P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task UnclassifiedNamespace_NotAllowedLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Helpers
                {
                    public static class Helper
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        // Unclassified code (no matching layer root) is never the allowed layer.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(8, 35)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Backward compatibility / contract validation
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ContractWithoutInteropRules_PInvokeIsSilent()
    {
        const string namespaceOnlyContract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(namespaceOnlyContract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public static class Worker
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task EmptyInteropRulesArray_IsSilent()
    {
        const string emptyContract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "interopBoundaryRules": []
            }
            """;

        var test = new ArchitectureAnalyzerTest(emptyContract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    public static class Worker
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task InteropRuleAllowingUndeclaredLayer_ReportsContractInvalid()
    {
        const string badContract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "interopBoundaryRules": [
                { "attribute": "System.Runtime.InteropServices.DllImportAttribute", "allowedLayer": "NativeInterop", "reason": "must live in interop" }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(badContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public static class Worker
                    {
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "layer 'NativeInterop' referenced in interopBoundaryRules is not declared in layers"));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Attribute-based classification (#36 integration): the containing type's layer comes from
    // ResolveLayer (marker attribute first), so interop placement follows the same semantics.
    // Combined contracts exercise layerDeclaration + interopBoundaryRules together.
    // ------------------------------------------------------------------------------------------

    private const string MarkerSource = """
        namespace Sample.Arch
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class NativeMarker : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class DomainMarker : System.Attribute { }
        }
        """;

    private const string CombinedContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "NativeInterop", "namespaceRoots": [ "Sample.Interop" ] }
          ],
          "layerDeclaration": {
            "required": false,
            "markerAttributes": [
              { "attributeFqn": "Sample.Arch.NativeMarker", "layer": "NativeInterop" },
              { "attributeFqn": "Sample.Arch.DomainMarker", "layer": "Domain" }
            ],
            "markerNamespace": "Sample.Arch"
          },
          "interopBoundaryRules": [
            { "attribute": "System.Runtime.InteropServices.DllImportAttribute", "allowedLayer": "NativeInterop", "reason": "P/Invoke declarations must live in the NativeInterop layer." }
          ]
        }
        """;

    [Fact]
    public async Task MarkerOverride_ToAllowedLayer_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(CombinedContract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Domain
                {
                    [Sample.Arch.NativeMarker]
                    public static class Native
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Test1.cs", MarkerSource));

        await test.RunAsync();
    }

    [Fact]
    public async Task MarkerOverride_ToForbiddenLayer_ReportsAarc007()
    {
        var test = new ArchitectureAnalyzerTest(CombinedContract)
        {
            TestCode = """
                using System.Runtime.InteropServices;

                namespace Sample.Interop
                {
                    [Sample.Arch.DomainMarker]
                    public static class Worker
                    {
                        [DllImport("user32.dll")]
                        public static extern void Beep();
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Test1.cs", MarkerSource));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.InteropBoundaryViolation)
                .WithLocation(9, 35)
                .WithArguments("Beep", "NativeInterop", "P/Invoke declarations must live in the NativeInterop layer."));

        await test.RunAsync();
    }
}