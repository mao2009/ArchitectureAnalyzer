using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArchitectureAnalyzer.CompatibilitySuite;

/// <summary>Whether a scenario is expected to be architecturally clean or faulty.</summary>
public enum FixtureKind
{
    /// <summary>The baseline reports nothing; the scenario must stay clean on both sides.</summary>
    Valid,

    /// <summary>The baseline reports at least one diagnostic.</summary>
    Violation,
}

/// <summary>How a difference between the two analyzers is accounted for.</summary>
public enum DivergenceClass
{
    /// <summary>Both analyzers produce the same findings.</summary>
    None,

    /// <summary>ArchitectureAnalyzer fails to detect something the baseline caught.</summary>
    CapabilityRegression,

    /// <summary>ArchitectureAnalyzer deliberately catches more, or catches it better.</summary>
    IntentionalImprovement,

    /// <summary>The same fault is reported, only presented differently.</summary>
    HarmlessPresentationDifference,

    /// <summary>The baseline's behaviour is itself wrong and was deliberately not reproduced.</summary>
    BaselineBug,
}

/// <summary>
/// The difference a fixture is allowed to exhibit, declared up front. Anything else — a new
/// difference, or a declared one that disappears — fails the suite.
/// </summary>
/// <param name="Class">Classification of the difference.</param>
/// <param name="PsxrOnly">Comma-joined categories the baseline reports and AARC does not.</param>
/// <param name="AarcOnly">Comma-joined categories AARC reports and the baseline does not.</param>
/// <param name="Note">Evidence and cross-reference to the divergence catalogue.</param>
public sealed record ExpectedDivergence(
    DivergenceClass Class,
    string PsxrOnly,
    string AarcOnly,
    string Note)
{
    /// <summary>The two analyzers agree exactly.</summary>
    public static readonly ExpectedDivergence Equivalent =
        new(DivergenceClass.None, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// One compatibility scenario from the baseline fixture matrix.
/// </summary>
/// <param name="Id">The normative fixture ID from the baseline document §4.</param>
/// <param name="Kind">Whether the baseline considers the scenario clean or faulty.</param>
/// <param name="Rule">The semantic category the scenario exercises.</param>
/// <param name="Sources">Scenario sources, in compilation order (which F-M13 depends on).</param>
/// <param name="Divergence">The declared, classified difference between the two analyzers.</param>
public sealed record ParityFixture(
    string Id,
    FixtureKind Kind,
    ParityCategory Rule,
    ImmutableArray<FixtureSource> Sources,
    ExpectedDivergence Divergence);

/// <summary>
/// The 57 scenarios of the baseline fixture matrix
/// (<c>docs/compatibility/psxrecomp-analyzer-baseline.md</c> §4), as compilable sources.
/// </summary>
/// <remarks>
/// Fixture IDs are normative and must not be renumbered. Each scenario is compiled once and fed to
/// both analyzers; the sources here are the shared input, never a per-analyzer variant.
/// </remarks>
public static class ParityFixtures
{
    private const string Using = "using PSXRecomp.Architecture;";

    /// <summary>Every scenario in the matrix, in fixture-ID order.</summary>
    public static ImmutableArray<ParityFixture> All { get; } = Build();

    private static ImmutableArray<ParityFixture> Build()
    {
        var fixtures = ImmutableArray.CreateBuilder<ParityFixture>();

        // ---------------------------------------------------------------- §4.1 PSXR001 / AARC004
        fixtures.Add(Valid("F-M01", ParityCategory.MissingLayerDeclaration, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Tagged
            {
            }
            """));

        fixtures.Add(Violation("F-M02", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            internal sealed class Untagged
            {
            }
            """));

        fixtures.Add(Valid("F-M03", ParityCategory.MissingLayerDeclaration, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed partial class Split
            {
            }

            internal sealed partial class Split
            {
            }
            """));

        fixtures.Add(Valid("F-M04", ParityCategory.MissingLayerDeclaration, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Outer
            {
                internal sealed class Inner
                {
                }
            }
            """));

        fixtures.Add(Violation("F-M05", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            internal class OuterUntagged
            {
                internal sealed class InnerUntagged
                {
                }
            }
            """));

        fixtures.Add(new ParityFixture(
            "F-M06",
            FixtureKind.Valid,
            ParityCategory.MissingLayerDeclaration,
            Sources(("/0/Generated.g.cs", """
                namespace Scenario;

                internal class OuterUntagged
                {
                    internal sealed class InnerUntagged
                    {
                    }
                }
                """)),
            ExpectedDivergence.Equivalent));

        fixtures.Add(Valid("F-M07", ParityCategory.MissingLayerDeclaration, """
            namespace PSXRecomp.Architecture;

            internal sealed class NotAMarker
            {
            }
            """));

        fixtures.Add(Valid("F-M08", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            internal struct BareStruct
            {
            }

            internal interface IBare
            {
            }

            internal enum BareEnum
            {
                None,
            }

            internal delegate void BareDelegate();

            internal record struct BareRecordStruct(int Value);
            """));

        fixtures.Add(Violation("F-M09", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            public record BareRecord(int Value);
            """));

        fixtures.Add(Violation("F-M10", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            public sealed class Box<T>
            {
            }
            """));

        fixtures.Add(Violation("F-M11", ParityCategory.MissingLayerDeclaration, """
            namespace Scenario;

            public static class Helpers
            {
            }
            """));

        fixtures.Add(Violation("F-M12", ParityCategory.MissingLayerDeclaration, """
            namespace PSXRecomp.Core;

            internal class OuterUntagged
            {
                internal sealed class InnerUntagged
                {
                }
            }
            """));

        // The generated part is added first: that ordering is what makes the divergence observable.
        fixtures.Add(new ParityFixture(
            "F-M13",
            FixtureKind.Violation,
            ParityCategory.MissingLayerDeclaration,
            Sources(
                ("/0/Split.g.cs", """
                    namespace Scenario;

                    internal sealed partial class Split
                    {
                    }
                    """),
                ("/0/Split.cs", """
                    namespace Scenario;

                    internal sealed partial class Split
                    {
                    }
                    """)),
            new ExpectedDivergence(
                DivergenceClass.CapabilityRegression,
                PsxrOnly: nameof(ParityCategory.MissingLayerDeclaration),
                AarcOnly: "",
                Note: "Baseline doc D4: AARC004 inspects only Locations[0], so a partial type whose "
                    + "first part is generated escapes the declaration check entirely, while PSXR001 "
                    + "falls through to the first non-generated part. Tracked as a follow-up against "
                    + "the AARC004 implementation in #42; fixing it is out of scope for #33.")));

        fixtures.Add(new ParityFixture(
            "F-M14",
            FixtureKind.Valid,
            ParityCategory.MissingLayerDeclaration,
            Sources(
                ("/0/Split.g.cs", $$"""
                    {{Using}}

                    namespace Scenario;

                    [Domain]
                    internal sealed partial class Split
                    {
                    }
                    """),
                ("/0/Split.cs", """
                    namespace Scenario;

                    internal sealed partial class Split
                    {
                    }
                    """)),
            ExpectedDivergence.Equivalent));

        fixtures.Add(new ParityFixture(
            "F-M15",
            FixtureKind.Valid,
            ParityCategory.MissingLayerDeclaration,
            Sources(
                ("/0/Split.g.cs", """
                    namespace Scenario;

                    internal sealed partial class Split
                    {
                    }
                    """),
                ("/0/Split.designer.cs", """
                    namespace Scenario;

                    internal sealed partial class Split
                    {
                    }
                    """)),
            ExpectedDivergence.Equivalent));

        // ---------------------------------------------------------------- §4.2 PSXR002 / AARC005
        fixtures.Add(Violation("F-P01", ParityCategory.MultipleLayerDeclarations, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            [Application]
            internal sealed class Conflicted
            {
            }
            """));

        fixtures.Add(Violation("F-X01", ParityCategory.MultipleLayerDeclarations, $$"""
            {{Using}}

            namespace PSXRecompStudio.ViewModels;

            [Domain]
            [Infrastructure]
            internal sealed class Conflicted
            {
            }
            """));

        // ---------------------------------------------------------------- §4.3 PSXR003 / AARC006
        fixtures.Add(Violation("F-N01", ParityCategory.NamespaceLayerMismatch, $$"""
            {{Using}}

            namespace PSXRecompStudio.ViewModels;

            [Domain]
            internal sealed class Misplaced
            {
            }
            """));

        fixtures.Add(Valid("F-N02", ParityCategory.NamespaceLayerMismatch, $$"""
            {{Using}}

            namespace PSXRecomp.Core;

            [Domain]
            internal sealed class Consistent
            {
            }
            """));

        fixtures.Add(Valid("F-N03", ParityCategory.NamespaceLayerMismatch, $$"""
            {{Using}}

            namespace PSXRecomp.Infrastructure;

            [Infrastructure]
            internal sealed class NativeAdapter
            {
            }
            """));

        fixtures.Add(Valid("F-N04", ParityCategory.NamespaceLayerMismatch, $$"""
            {{Using}}

            namespace Scenario;

            [Infrastructure]
            public sealed class Anywhere
            {
            }
            """));

        // ---------------------------------------------------------------- §4.4 PSXR004 / AARC002
        fixtures.Add(Violation("F-D01", ParityCategory.ForbiddenDependency, ForbiddenDependencySource));

        fixtures.Add(Violation("F-D02", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Test]
            internal sealed class TestHelper
            {
            }

            [Domain]
            internal sealed class ProductionType
            {
                public TestHelper Helper { get; set; }
            }
            """));

        fixtures.Add(Valid("F-D03", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class DomainEntity
            {
            }

            [Application]
            internal sealed class AppFacade
            {
                public DomainEntity Create() => new DomainEntity();
            }
            """));

        fixtures.Add(Valid("F-D04", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class DomainEntity
            {
            }

            [Test]
            internal sealed class DomainEntityTests
            {
                public DomainEntity Create() => new DomainEntity();
            }
            """));

        fixtures.Add(Violation("F-D05", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class AppBox<T>
            {
            }

            [Domain]
            internal sealed class DomainHolder
            {
                public AppBox<int> Box = new();
            }
            """));

        fixtures.Add(Valid("F-D06", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class AppMarkerAttribute : System.Attribute
            {
            }

            [Domain]
            internal sealed class DomainConsumer
            {
                [AppMarker]
                public void Tagged()
                {
                }
            }
            """));

        fixtures.Add(Violation("F-D07", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class AppService
            {
            }

            [Domain]
            internal sealed class DomainService
            {
                public AppService First;
                public AppService Second;

                public AppService Create() => new AppService();
            }
            """));

        fixtures.Add(Violation("F-D08", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class AppService
            {
            }

            [Domain]
            internal sealed class Outer
            {
                internal sealed class Inner
                {
                    public AppService Dependency = new();
                }
            }
            """));

        fixtures.Add(Violation("F-D09", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Infrastructure]
            internal sealed class NativeAdapter
            {
            }

            [Application]
            internal sealed class Facade
            {
                public NativeAdapter Adapter = new();
            }
            """));

        fixtures.Add(Violation("F-D10", ParityCategory.ForbiddenDependency, $$"""
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class AppService
            {
            }

            [Infrastructure]
            internal sealed class NativeAdapter
            {
                public AppService Service = new();
            }
            """));

        fixtures.Add(new ParityFixture(
            "F-D11",
            FixtureKind.Valid,
            ParityCategory.ForbiddenDependency,
            Sources(("/0/Wired.g.cs", ForbiddenDependencySource)),
            ExpectedDivergence.Equivalent));

        // ---------------------------------------------------------------- §4.5 PSXR005 / AARC003
        fixtures.Add(Violation("F-A01", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Greeter
            {
                public void Greet() => Console.WriteLine("hello");
            }
            """));

        fixtures.Add(Violation("F-A02", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Clock
            {
                public long Stamp() => DateTime.Now.Ticks;
            }
            """));

        fixtures.Add(Violation("F-A03", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Picker
            {
                public int Pick() => Random.Shared.Next();
            }
            """));

        fixtures.Add(Violation("F-A04", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Roller
            {
                private readonly Random _random = new Random(42);

                public int Roll() => _random.Next();
            }
            """));

        fixtures.Add(Violation("F-A05", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class IdFactory
            {
                public Guid NewId() => Guid.NewGuid();
            }
            """));

        fixtures.Add(Violation("F-A06", ParityCategory.ForbiddenApi, $$"""
            using System.IO;
            {{Using}}

            namespace Scenario;

            [Application]
            internal sealed class StorageProbe
            {
                public bool Exists(string path) => File.Exists(path);
            }
            """));

        fixtures.Add(Violation("F-A07", ParityCategory.ForbiddenApi, $$"""
            using System.Net.Http;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class NetworkProbe
            {
                public HttpClient Client = new System.Net.Http.HttpClient();
            }
            """));

        fixtures.Add(Violation("F-A08", ParityCategory.ForbiddenApi, $$"""
            using System.Threading.Tasks;
            {{Using}}

            namespace Scenario;

            [Test]
            internal sealed class AsyncProbe
            {
                public void Wait() => Task.Delay(1);
            }
            """));

        fixtures.Add(Violation("F-A09", ParityCategory.ForbiddenApi, $$"""
            using System.Diagnostics;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Runner
            {
                public Process Child = new Process();
            }
            """));

        fixtures.Add(Violation("F-A10", ParityCategory.ForbiddenApi, $$"""
            using System.Threading;
            {{Using}}

            namespace Scenario;

            [Test]
            internal sealed class Worker
            {
                public Thread WorkerThread = new Thread(() => { });
            }
            """));

        fixtures.Add(Valid("F-A11", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Calculator
            {
                public int Max(int left, int right) => Math.Max(left, right);

                public long Elapsed(long before, long after) => after - before;
            }
            """));

        fixtures.Add(Violation("F-A12", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Liner
            {
                public string Break() => Environment.NewLine;
            }
            """));

        fixtures.Add(Violation("F-A13", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class Chatty
            {
                public void One()
                {
                    Console.WriteLine("a");
                    Console.WriteLine("b");
                }

                public void Two()
                {
                    Console.WriteLine("c");
                }
            }
            """));

        fixtures.Add(Valid("F-A14", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal static class RandomExtensions
            {
                public static int Next2(this Random source) => 0;
            }

            [Domain]
            internal sealed class Roller
            {
                public int Roll(Random source) => source.Next2();
            }
            """));

        fixtures.Add(Violation("F-A15", ParityCategory.ForbiddenApi, """
            using System;

            namespace Unmapped.Space;

            internal sealed class Loud
            {
                public void Say() => Console.WriteLine("x");
            }
            """));

        fixtures.Add(Violation("F-A16", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace PSXRecomp.Core;

            [Domain]
            internal sealed class Loud
            {
                public void Say() => Console.WriteLine("x");
            }
            """));

        fixtures.Add(Violation("F-A17", ParityCategory.ForbiddenApi, $$"""
            using System;
            {{Using}}

            namespace Scenario;

            [Domain]
            internal sealed class DomainStamp
            {
                public DateTimeOffset Now() => DateTimeOffset.UtcNow;
            }

            [Test]
            internal sealed class TestStamp
            {
                public DateTimeOffset Now() => DateTimeOffset.UtcNow;
            }
            """));

        fixtures.Add(Valid("F-A18", ParityCategory.ForbiddenApi, $$"""
            using System.Net.Http;
            {{Using}}

            namespace Scenario;

            [Test]
            internal sealed class NetProbe
            {
                public HttpClient Client = new System.Net.Http.HttpClient();
            }
            """));

        fixtures.Add(Valid("F-A19", ParityCategory.ForbiddenApi, """
            using System;

            namespace PSXRecomp.Architecture;

            internal sealed class MarkerHelper
            {
                public void Say() => Console.WriteLine("x");
            }
            """));

        // ---------------------------------------------------------------- §4.6 PSXR006 / AARC007
        fixtures.Add(Violation("F-I01", ParityCategory.InteropBoundary, InteropDllImportSource(
            "Scenario", "Infrastructure")));

        fixtures.Add(Valid("F-I02", ParityCategory.InteropBoundary, InteropDllImportSource(
            "PSXRecomp.Core", "Domain")));

        fixtures.Add(new ParityFixture(
            "F-I03",
            FixtureKind.Violation,
            ParityCategory.InteropBoundary,
            Sources(("/0/Test0.cs", InteropLibraryImportSource("Scenario", "Infrastructure"))),
            new ExpectedDivergence(
                DivergenceClass.HarmlessPresentationDifference,
                PsxrOnly: nameof(ParityCategory.InteropBoundary),
                AarcOnly: "",
                Note: "Baseline doc D5: PSXR006 runs per MethodDeclaration with no de-duplication, so "
                    + "a partial interop method is reported once per declaring part (2). AARC007 "
                    + "de-duplicates on (method, attribute) and reports the same fault once, at the "
                    + "part that carries the attribute. No fault becomes invisible.")));

        fixtures.Add(Valid("F-I04", ParityCategory.InteropBoundary, InteropLibraryImportSource(
            "PSXRecomp.Core", "Domain")));

        fixtures.Add(Valid("F-I05", ParityCategory.InteropBoundary, $$"""
            {{Using}}

            namespace Scenario;

            [Infrastructure]
            internal sealed class DllImportAttribute : System.Attribute
            {
                public DllImportAttribute(string library) => Library = library;

                public string Library { get; }
            }

            [Infrastructure]
            internal static class NativeCalls
            {
                [DllImport("psx")]
                internal static void DoWork()
                {
                }
            }
            """));

        fixtures.Add(new ParityFixture(
            "F-I06",
            FixtureKind.Valid,
            ParityCategory.InteropBoundary,
            Sources(("/0/Native.g.cs", InteropDllImportSource("Scenario", "Infrastructure"))),
            ExpectedDivergence.Equivalent));

        return fixtures.ToImmutable();
    }

    /// <summary>The F-D01 scenario, reused verbatim by F-D11 in a generated file.</summary>
    private const string ForbiddenDependencySource = $$"""
        {{Using}}

        namespace Scenario;

        [Application]
        internal sealed class AppService
        {
        }

        [Domain]
        internal sealed class DomainService
        {
            public AppService Dependency = new();
        }
        """;

    private static string InteropDllImportSource(string namespaceName, string layer) => $$"""
        using System.Runtime.InteropServices;
        {{Using}}

        namespace {{namespaceName}};

        [{{layer}}]
        internal static class NativeCalls
        {
            [DllImport("psx")]
            internal static extern void DoWork();
        }
        """;

    private static string InteropLibraryImportSource(string namespaceName, string layer) => $$"""
        using System.Runtime.InteropServices;
        {{Using}}

        namespace {{namespaceName}};

        [{{layer}}]
        internal static partial class NativeCalls
        {
            [LibraryImport("psx")]
            internal static partial void DoWork();

            internal static partial void DoWork()
            {
            }
        }
        """;

    private static ParityFixture Valid(string id, ParityCategory rule, string source) =>
        new(id, FixtureKind.Valid, rule, Sources(("/0/Test0.cs", source)), ExpectedDivergence.Equivalent);

    private static ParityFixture Violation(string id, ParityCategory rule, string source) =>
        new(id, FixtureKind.Violation, rule, Sources(("/0/Test0.cs", source)), ExpectedDivergence.Equivalent);

    private static ImmutableArray<FixtureSource> Sources(params (string Path, string Source)[] sources)
    {
        var builder = ImmutableArray.CreateBuilder<FixtureSource>(sources.Length);
        foreach (var (path, source) in sources)
        {
            builder.Add(new FixtureSource(path, source));
        }

        return builder.MoveToImmutable();
    }
}
