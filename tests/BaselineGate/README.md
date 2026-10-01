# Baseline ratcheting gate

This fixture proves the supported incremental-adoption workflow with real builds and the baseline
generator tool.

The checked-in project contains one known AARC002 violation and one matching
`architecture.baseline.json` entry.

[`verify-baseline.sh`](verify-baseline.sh) proves:

1. the known violation is tolerated without changing AARC002 severity;
2. a newly introduced source-type/target-type violation still fails with AARC002;
3. `architecture-baseline generate` captures the complete current debt set while ignoring the old
   baseline only for the capture build;
4. after the original violation is fixed, regeneration removes only that stale entry;
5. after all debt is fixed, regeneration produces an empty baseline;
6. the script restores the repository fixture and finishes with a clean build.

The fixture explicitly declares `CompilerVisibleProperty Include="ArchitectureAnalyzerBaselineMode"`
because it consumes the analyzer by project reference. Normal NuGet consumers receive that item
from the analyzer package's `buildTransitive` props.

See [`docs/baseline.md`](../../docs/baseline.md).
