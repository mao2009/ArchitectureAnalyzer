#!/usr/bin/env bash
#
# Real-build gate verification for ArchitectureAnalyzer.
#
# Proves schema-v5 exact exceptions stay narrow while preserving dependency denylisting,
# positive allowlisting, schema-v4 DAG enforcement, and strict layer coverage.
#
# Cycle:
#   1. clean schema-v5 build passes
#   2. exact reviewed AARC002 exception passes
#   3. unrelated dependency on the same forbidden layer edge still fails with AARC002
#   4. allowlist-only dependency violation fails with AARC002
#   5. cyclic declared dependency graph fails with AARC011
#   6. unclassified type fails with AARC010
#   7. the same unclassified type passes when coverage policy is temporarily "ignore"
#   8. restore strict contract/remove fixtures and build cleanly again
#
# Exits non-zero if any assertion fails, so CI can gate on it.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="${SCRIPT_DIR}/SampleConsumer/SampleConsumer.csproj"
CONTRACT="${SCRIPT_DIR}/SampleConsumer/architecture.contract.json"
EXCEPTION_FIXTURE="${SCRIPT_DIR}/Fixtures/ExceptedDependency.cs.txt"
EXCEPTION_SOURCE="${SCRIPT_DIR}/SampleConsumer/Domain/ExceptionBridge.cs"
DEPENDENCY_FIXTURE="${SCRIPT_DIR}/Fixtures/Violation.cs.txt"
DEPENDENCY_VIOLATION="${SCRIPT_DIR}/SampleConsumer/Domain/Violation.cs"
ALLOWLIST_FIXTURE="${SCRIPT_DIR}/Fixtures/AllowlistViolation.cs.txt"
ALLOWLIST_VIOLATION="${SCRIPT_DIR}/SampleConsumer/Application/AllowlistViolation.cs"
CYCLIC_CONTRACT_FIXTURE="${SCRIPT_DIR}/Fixtures/CyclicContract.json.txt"
COVERAGE_FIXTURE="${SCRIPT_DIR}/Fixtures/CoverageGap.cs.txt"
COVERAGE_VIOLATION="${SCRIPT_DIR}/SampleConsumer/CoverageGap.cs"
EXPECTED_DEPENDENCY_DIAGNOSTIC_ID="AARC002"
EXPECTED_COVERAGE_DIAGNOSTIC_ID="AARC010"
EXPECTED_CYCLE_DIAGNOSTIC_ID="AARC011"

RESULTS=()
FAILURES=0
LOG_DIR="$(mktemp -d)"
CONTRACT_BACKUP="${LOG_DIR}/architecture.contract.json"

cleanup() {
  rm -f "${EXCEPTION_SOURCE}" "${DEPENDENCY_VIOLATION}" "${ALLOWLIST_VIOLATION}" "${COVERAGE_VIOLATION}"
  if [ -f "${CONTRACT_BACKUP}" ]; then
    cp "${CONTRACT_BACKUP}" "${CONTRACT}"
  fi
  rm -rf "${LOG_DIR}"
}
trap cleanup EXIT

record() {
  RESULTS+=("$1: $2")
  if [ "$1" = "FAIL" ]; then
    FAILURES=$((FAILURES + 1))
  fi
}

build() {
  dotnet build "${PROJECT}" --no-incremental -v:m >"$1" 2>&1
}

for required in "${PROJECT}" "${CONTRACT}" "${EXCEPTION_FIXTURE}" "${DEPENDENCY_FIXTURE}" "${ALLOWLIST_FIXTURE}" "${CYCLIC_CONTRACT_FIXTURE}" "${COVERAGE_FIXTURE}"; do
  if [ ! -f "${required}" ]; then
    echo "verify-gate: cannot find ${required}" >&2
    exit 2
  fi
done

if [ -e "${EXCEPTION_SOURCE}" ] || [ -e "${DEPENDENCY_VIOLATION}" ] || [ -e "${ALLOWLIST_VIOLATION}" ] || [ -e "${COVERAGE_VIOLATION}" ]; then
  echo "verify-gate: injected verification files must never be tracked" >&2
  exit 2
fi

cp "${CONTRACT}" "${CONTRACT_BACKUP}"

# ---------------------------------------------------------------------------
echo "=== Step 1/8: build the clean schema-v5 SampleConsumer (expect success) ==="
build "${LOG_DIR}/step1.log"
STEP1_EXIT=$?
cat "${LOG_DIR}/step1.log"
if [ "${STEP1_EXIT}" -eq 0 ]; then
  record "PASS" "step 1 - clean schema-v5 build succeeded (exit 0)"
else
  record "FAIL" "step 1 - clean schema-v5 build was expected to succeed but exited ${STEP1_EXIT}"
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 2/8: inject exact reviewed AARC002 exception (expect success) ==="
cp "${EXCEPTION_FIXTURE}" "${EXCEPTION_SOURCE}"
build "${LOG_DIR}/step2.log"
STEP2_EXIT=$?
cat "${LOG_DIR}/step2.log"

if [ "${STEP2_EXIT}" -eq 0 ]; then
  record "PASS" "step 2 - exact dependency exception suppressed only its reviewed violation"
else
  record "FAIL" "step 2 - exact dependency exception was expected to pass but exited ${STEP2_EXIT}"
fi
rm -f "${EXCEPTION_SOURCE}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 3/8: inject unrelated explicit forbidden dependency (expect AARC002 failure) ==="
cp "${DEPENDENCY_FIXTURE}" "${DEPENDENCY_VIOLATION}"
build "${LOG_DIR}/step3.log"
STEP3_EXIT=$?
cat "${LOG_DIR}/step3.log"

if [ "${STEP3_EXIT}" -eq 0 ]; then
  record "FAIL" "step 3 - unrelated forbidden dependency was incorrectly suppressed by the narrow exception"
elif grep -q "${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}" "${LOG_DIR}/step3.log"; then
  record "PASS" "step 3 - unrelated forbidden dependency still failed with ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 3 - build failed but never reported ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
fi
rm -f "${DEPENDENCY_VIOLATION}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 4/8: inject allowlist-only violation (expect AARC002 failure) ==="
cp "${ALLOWLIST_FIXTURE}" "${ALLOWLIST_VIOLATION}"
build "${LOG_DIR}/step4.log"
STEP4_EXIT=$?
cat "${LOG_DIR}/step4.log"

if [ "${STEP4_EXIT}" -eq 0 ]; then
  record "FAIL" "step 4 - build succeeded but Application -> Shared is outside allowedDependencies"
elif grep -q "${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}" "${LOG_DIR}/step4.log"; then
  record "PASS" "step 4 - positive allowlist violation failed and reported ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 4 - build failed but never reported ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
fi
rm -f "${ALLOWLIST_VIOLATION}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 5/8: replace contract with cyclic schema-v4 graph (expect AARC011 failure) ==="
cp "${CYCLIC_CONTRACT_FIXTURE}" "${CONTRACT}"
build "${LOG_DIR}/step5.log"
STEP5_EXIT=$?
cat "${LOG_DIR}/step5.log"

if [ "${STEP5_EXIT}" -eq 0 ]; then
  record "FAIL" "step 5 - cyclic declared graph build succeeded but DAG enforcement should fail it"
elif grep -q "${EXPECTED_CYCLE_DIAGNOSTIC_ID}" "${LOG_DIR}/step5.log"; then
  record "PASS" "step 5 - cyclic declared graph failed and reported ${EXPECTED_CYCLE_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 5 - cyclic graph build failed but never reported ${EXPECTED_CYCLE_DIAGNOSTIC_ID}"
fi
cp "${CONTRACT_BACKUP}" "${CONTRACT}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 6/8: inject unclassified type under strict policy (expect AARC010 failure) ==="
cp "${COVERAGE_FIXTURE}" "${COVERAGE_VIOLATION}"
build "${LOG_DIR}/step6.log"
STEP6_EXIT=$?
cat "${LOG_DIR}/step6.log"

if [ "${STEP6_EXIT}" -eq 0 ]; then
  record "FAIL" "step 6 - strict coverage build succeeded but the unclassified type should fail it"
elif grep -q "${EXPECTED_COVERAGE_DIAGNOSTIC_ID}" "${LOG_DIR}/step6.log"; then
  record "PASS" "step 6 - strict coverage build failed and reported ${EXPECTED_COVERAGE_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 6 - strict coverage build failed but never reported ${EXPECTED_COVERAGE_DIAGNOSTIC_ID}"
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 7/8: switch coverage to ignore and rebuild same coverage gap (expect success) ==="
sed -i 's/"unclassifiedCode": "error"/"unclassifiedCode": "ignore"/' "${CONTRACT}"
if ! grep -q '"unclassifiedCode": "ignore"' "${CONTRACT}"; then
  record "FAIL" "step 7 - could not switch the fixture contract to unclassifiedCode=ignore"
else
  build "${LOG_DIR}/step7.log"
  STEP7_EXIT=$?
  cat "${LOG_DIR}/step7.log"
  if [ "${STEP7_EXIT}" -eq 0 ]; then
    record "PASS" "step 7 - coverage ignore mode accepted the same unclassified type"
  else
    record "FAIL" "step 7 - coverage ignore mode was expected to pass but exited ${STEP7_EXIT}"
  fi
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 8/8: restore strict contract, remove coverage gap and rebuild cleanly ==="
cp "${CONTRACT_BACKUP}" "${CONTRACT}"
rm -f "${COVERAGE_VIOLATION}"
build "${LOG_DIR}/step8.log"
STEP8_EXIT=$?
cat "${LOG_DIR}/step8.log"
if [ "${STEP8_EXIT}" -eq 0 ]; then
  record "PASS" "step 8 - final strict schema-v5 build succeeded after cleanup"
else
  record "FAIL" "step 8 - final clean build was expected to succeed but exited ${STEP8_EXIT}"
fi

# ---------------------------------------------------------------------------
echo
echo "================ verify-gate summary ================"
for line in "${RESULTS[@]}"; do
  echo "  ${line}"
done
echo "====================================================="

if [ "${FAILURES}" -ne 0 ]; then
  echo "verify-gate: FAILED (${FAILURES} of ${#RESULTS[@]} checks failed)"
  exit 1
fi

echo "verify-gate: PASSED (${#RESULTS[@]} of ${#RESULTS[@]} checks passed)"
exit 0
