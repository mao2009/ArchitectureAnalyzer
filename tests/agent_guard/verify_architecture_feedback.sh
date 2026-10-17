#!/usr/bin/env bash
# Real .NET consumer RED / GREEN / missing analyzer regression.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
sample="$repo_root/tests/GateVerification/SampleConsumer"
project="$sample/SampleConsumer.csproj"
violation="$sample/Domain/Violation.cs"
fixture="$repo_root/tests/GateVerification/Fixtures/Violation.cs.txt"
temp="$(mktemp -d)"
cp "$project" "$temp/SampleConsumer.csproj"
cleanup() {
  cp "$temp/SampleConsumer.csproj" "$project"
  rm -f "$violation"
  rm -rf "$temp"
}
trap cleanup EXIT
if [[ -e "$violation" ]]; then
  echo "Expected no injected violation in checked-out sample" >&2
  exit 2
fi

dotnet restore "$project"
set +e
python "$repo_root/scripts/agent_diagnostic_feedback.py" --root "$sample" --project SampleConsumer.csproj \
  --backend build --strict >"$temp/green.json"
green=$?
set -e
if [[ $green -ne 0 ]]; then
  cat "$temp/green.json"
  echo "Expected clean consumer to pass strict gate (exit $green)" >&2
  exit 1
fi
python - "$temp/green.json" <<'PY'
import json,sys
x=json.load(open(sys.argv[1]))
assert x["status"] == "ALLOW", x
PY

cp "$fixture" "$violation"
set +e
python "$repo_root/scripts/agent_diagnostic_feedback.py" --root "$sample" --project SampleConsumer.csproj \
  --backend build --strict >"$temp/red.json"
red=$?
set -e
[[ $red -eq 1 ]] || { cat "$temp/red.json"; echo "Violation must be denied" >&2; exit 1; }
python - "$temp/red.json" <<'PY'
import json,sys
x=json.load(open(sys.argv[1]))
assert x["status"] == "DENY" and any(d["id"] == "AARC002" for d in x["diagnostics"]), x
PY
rm -f "$violation"

python - "$project" <<'PY'
import pathlib,re,sys
path=pathlib.Path(sys.argv[1])
s=path.read_text()
s,n=re.subn(r'<ProjectReference\s+Include="[^"]*ArchitectureAnalyzer\.csproj"\s+OutputItemType="Analyzer"\s+ReferenceOutputAssembly="false"\s*/>', "", s, count=1)
assert n == 1
path.write_text(s)
PY
set +e
python "$repo_root/scripts/agent_diagnostic_feedback.py" --root "$sample" --project SampleConsumer.csproj \
  --backend build --strict >"$temp/removed.json"
removed=$?
set -e
[[ $removed -eq 2 ]] || { cat "$temp/removed.json"; echo "Removed analyzer must fail closed" >&2; exit 1; }
python - "$temp/removed.json" <<'PY'
import json,sys
x=json.load(open(sys.argv[1]))
assert x["status"] == "ERROR" and "reference not present" in x["error"], x
PY
echo "ArchitectureAnalyzer: real consumer green, violation red, and missing-analyzer denial verified."
