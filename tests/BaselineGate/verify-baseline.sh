#!/usr/bin/env bash

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${SCRIPT_DIR}/BaselineGate.csproj"
TOOL_PROJECT="${ROOT_DIR}/tools/ArchitectureAnalyzer.Baseline/ArchitectureAnalyzer.Baseline.csproj"
BASELINE="${SCRIPT_DIR}/architecture.baseline.json"
LEGACY_SOURCE="${SCRIPT_DIR}/Domain/LegacyDebt.cs"
FIXED_LEGACY="${SCRIPT_DIR}/Fixtures/LegacyDebtFixed.cs.txt"
NEW_FIXTURE="${SCRIPT_DIR}/Fixtures/NewDebt.cs.txt"
NEW_SOURCE="${SCRIPT_DIR}/Domain/NewDebt.cs"

TEMP_DIR="$(mktemp -d)"
BASELINE_BACKUP="${TEMP_DIR}/architecture.baseline.json"
LEGACY_BACKUP="${TEMP_DIR}/LegacyDebt.cs"
RESULTS=()
FAILURES=0

cleanup() {
  cp "${BASELINE_BACKUP}" "${BASELINE}" 2>/dev/null || true
  cp "${LEGACY_BACKUP}" "${LEGACY_SOURCE}" 2>/dev/null || true
  rm -f "${NEW_SOURCE}"
  rm -rf "${TEMP_DIR}"
}
trap cleanup EXIT

record() {
  RESULTS+=("$1: $2")
  if [ "$1" = "FAIL" ]; then
    FAILURES=$((FAILURES + 1))
  fi
}

build_project() {
  local log="$1"
  dotnet build "${PROJECT}" --no-incremental -v:m >"${log}" 2>&1
}

generate_baseline() {
  local log="$1"
  dotnet run     --project "${TOOL_PROJECT}"     --configuration Release     --     generate "${PROJECT}"     --output "${BASELINE}" >"${log}" 2>&1
}

count_entries() {
  grep -c '"diagnosticId": "AARC002"' "${BASELINE}" || true
}

for required in "${PROJECT}" "${TOOL_PROJECT}" "${BASELINE}" "${LEGACY_SOURCE}" "${FIXED_LEGACY}" "${NEW_FIXTURE}"; do
  if [ ! -f "${required}" ]; then
    echo "verify-baseline: missing ${required}" >&2
    exit 2
  fi
done

if [ -e "${NEW_SOURCE}" ]; then
  echo "verify-baseline: injected NewDebt.cs must never be tracked" >&2
  exit 2
fi

cp "${BASELINE}" "${BASELINE_BACKUP}"
cp "${LEGACY_SOURCE}" "${LEGACY_BACKUP}"

echo "=== Step 1/6: known baseline debt builds successfully ==="
build_project "${TEMP_DIR}/step1.log"
STEP1_EXIT=$?
cat "${TEMP_DIR}/step1.log"
if [ "${STEP1_EXIT}" -eq 0 ]; then
  record "PASS" "step 1 - existing AARC002 debt was tolerated by the checked-in baseline"
else
  record "FAIL" "step 1 - checked-in baseline did not tolerate existing debt"
fi

echo
echo "=== Step 2/6: introduce a new violation (expect AARC002 failure) ==="
cp "${NEW_FIXTURE}" "${NEW_SOURCE}"
build_project "${TEMP_DIR}/step2.log"
STEP2_EXIT=$?
cat "${TEMP_DIR}/step2.log"
if [ "${STEP2_EXIT}" -eq 0 ]; then
  record "FAIL" "step 2 - new architecture debt was incorrectly hidden by the old baseline"
elif grep -q 'AARC002' "${TEMP_DIR}/step2.log"; then
  record "PASS" "step 2 - newly introduced debt failed with AARC002"
else
  record "FAIL" "step 2 - build failed without AARC002"
fi

echo
echo "=== Step 3/6: regenerate baseline (expect both current violations recorded) ==="
generate_baseline "${TEMP_DIR}/step3.log"
STEP3_EXIT=$?
cat "${TEMP_DIR}/step3.log"
ENTRY_COUNT="$(count_entries)"
if [ "${STEP3_EXIT}" -eq 0 ]   && [ "${ENTRY_COUNT}" -eq 2 ]   && grep -q 'BaselineGate.Domain.LegacyDebt -> BaselineGate.Application.AppService' "${BASELINE}"   && grep -q 'BaselineGate.Domain.NewDebt -> BaselineGate.Application.AppService' "${BASELINE}"; then
  record "PASS" "step 3 - generator deterministically recorded both current AARC002 violations"
else
  record "FAIL" "step 3 - generator did not produce the expected two-entry baseline"
fi

build_project "${TEMP_DIR}/step3-build.log"
STEP3_BUILD_EXIT=$?
cat "${TEMP_DIR}/step3-build.log"
if [ "${STEP3_BUILD_EXIT}" -eq 0 ]; then
  record "PASS" "step 3b - regenerated baseline tolerated the reviewed new debt"
else
  record "FAIL" "step 3b - regenerated baseline did not make the current debt set buildable"
fi

echo
echo "=== Step 4/6: fix legacy debt and regenerate (expect stale entry pruned) ==="
cp "${FIXED_LEGACY}" "${LEGACY_SOURCE}"
generate_baseline "${TEMP_DIR}/step4.log"
STEP4_EXIT=$?
cat "${TEMP_DIR}/step4.log"
ENTRY_COUNT="$(count_entries)"
if [ "${STEP4_EXIT}" -eq 0 ]   && [ "${ENTRY_COUNT}" -eq 1 ]   && ! grep -q 'BaselineGate.Domain.LegacyDebt -> BaselineGate.Application.AppService' "${BASELINE}"   && grep -q 'BaselineGate.Domain.NewDebt -> BaselineGate.Application.AppService' "${BASELINE}"; then
  record "PASS" "step 4 - fixed legacy violation disappeared while unrelated debt remained"
else
  record "FAIL" "step 4 - generator did not prune only the fixed legacy entry"
fi

echo
echo "=== Step 5/6: fix remaining debt and regenerate (expect empty baseline) ==="
rm -f "${NEW_SOURCE}"
generate_baseline "${TEMP_DIR}/step5.log"
STEP5_EXIT=$?
cat "${TEMP_DIR}/step5.log"
ENTRY_COUNT="$(count_entries)"
if [ "${STEP5_EXIT}" -eq 0 ] && [ "${ENTRY_COUNT}" -eq 0 ]; then
  record "PASS" "step 5 - baseline shrank cleanly to zero entries"
else
  record "FAIL" "step 5 - empty current debt did not produce an empty baseline"
fi

echo
echo "=== Step 6/6: restore repository fixture and build cleanly ==="
cp "${BASELINE_BACKUP}" "${BASELINE}"
cp "${LEGACY_BACKUP}" "${LEGACY_SOURCE}"
rm -f "${NEW_SOURCE}"
build_project "${TEMP_DIR}/step6.log"
STEP6_EXIT=$?
cat "${TEMP_DIR}/step6.log"
if [ "${STEP6_EXIT}" -eq 0 ]; then
  record "PASS" "step 6 - checked-in fixture restored and builds cleanly"
else
  record "FAIL" "step 6 - restored fixture failed"
fi

echo
echo "================ baseline gate summary ================"
for line in "${RESULTS[@]}"; do
  echo "  ${line}"
done
echo "======================================================="

if [ "${FAILURES}" -ne 0 ]; then
  echo "verify-baseline: FAILED (${FAILURES} checks failed)"
  exit 1
fi

echo "verify-baseline: PASSED (${#RESULTS[@]} checks passed)"
