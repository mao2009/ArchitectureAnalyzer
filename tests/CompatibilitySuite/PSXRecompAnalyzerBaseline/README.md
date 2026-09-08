# PSXRecomp.Analyzer — frozen compatibility fixture

**This is not a dependency. It is test data.**

These files are a byte-identical copy of the old `PSXRecomp.Analyzer` at one pinned commit. They
exist so the cross-analyzer parity suite (issue #33) can run the *old* analyzer over the *same*
sources as `ArchitectureAnalyzer` and compare the results, with no network access to, and no
checkout of, an external repository at build or CI time.

| | |
|---|---|
| Source repository | `mao2009/PSXRecompStudio` |
| **Pinned SHA** | **`88f5b6f2c209b0980fd96d241f28dce1f840673a`** |
| Source path | `src/PSXRecomp.Analyzer/**` |
| Copied on | 2026-09-08 |

## Rules

- **Never edit these files.** They are the baseline. Editing them makes the parity result a
  statement about a modified analyzer, which is worth nothing.
- **Never advance the SHA** to follow PSXRecompStudio `main`. The baseline document records
  post-baseline drift instead (`docs/compatibility/psxrecomp-analyzer-baseline.md` §7).
- Only `PSXRecomp.Analyzer.Tests` *fixture semantics* were carried over — into
  `../ParityFixtures.cs`, expressed against this repository's own conventions. The baseline test
  project itself is deliberately not copied.

## Verifying provenance

From a PSXRecompStudio clone, every file here must reproduce exactly:

```sh
SHA=88f5b6f2c209b0980fd96d241f28dce1f840673a
for f in PSXRecompArchitectureAnalyzer.cs \
         Architecture/ArchitectureDiagnostics.cs \
         Architecture/ArchitectureFacts.cs \
         Architecture/ForbiddenApiCatalog.cs \
         Architecture/PSXRecompArchitectureAttributes.cs; do
  git show "$SHA:src/PSXRecomp.Analyzer/$f" \
    | diff -u - "<ArchitectureAnalyzer>/tests/CompatibilitySuite/PSXRecompAnalyzerBaseline/$f" \
    && echo "OK $f"
done
```

No provenance header is added to the files themselves, precisely so that this check stays a plain
byte comparison.

## How they are built

The sources are compiled straight into `ArchitectureAnalyzer.CompatibilitySuite` (the SDK's default
`**/*.cs` glob) rather than into a separate analyzer assembly: the suite only ever instantiates
`PSXRecompArchitectureAnalyzer` in-process and hands it a `CSharpCompilation`, so a second project,
a second target framework and a second NuGet package would buy nothing.

`PSXRecompArchitectureAttributes.cs` is the exception — it is excluded from compilation and
embedded as a resource instead, because fixtures need it as *source text* inside the scenario
compilation, exactly as the baseline's own tests did.

The suite's `<NoWarn>` covers `RS2008` and `RS1036`, which demand analyzer-packaging metadata this
frozen copy must not be edited to provide. See `../ArchitectureAnalyzer.CompatibilitySuite.csproj`.
