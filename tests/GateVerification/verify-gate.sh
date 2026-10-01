#!/usr/bin/env bash
#
# Real-build gate verification for ArchitectureAnalyzer.
#
# Proves both ordinary architecture-rule enforcement (AARC002) and schema-v2 strict layer
# coverage (AARC010) through a genuine dotnet build of a project-reference consumer.
#
# Cycle:
#   1. clean strict build passes
#   2. forbidden dependency fails with AARC002
#   3. unclassified type fails with AARC010
#   4. the same unclassified type passes when contract policy is temporarily "ignore"
#   5. restore strict contract/remove fixtures and build cleanly again
#
# Exits non-zero if any assertion fails, so CI can gate on it.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="${SCRIPT_DIR}/SampleConsumer/SampleConsumer.csproj"
CONTRACT="${SCRIPT_DIR}/SampleConsumer/architecture.contract.json"
DEPENDENCY_FIXTURE="${SCRIPT_DIR}/Fixtures/Violation.cs.txt"
DEPENDENCY_VIOLATION="${SCRIPT_DIR}/SampleConsumer/Domain/Violation.cs"
COVERAGE_FIXTURE="${SCRIPT_DIR}/Fixtures/CoverageGap.cs.txt"
COVERAGE_VIOLATION="${SCRIPT_DIR}/SampleConsumer/CoverageGap.cs"
EXPECTED_DEPENDENCY_DIAGNOSTIC_ID="AARC002"
EXPECTED_COVERAGE_DIAGNOSTIC_ID="AARC010"

RESULTS=()
FAILURES=0
LOG_DIR="$(mktemp -d)"
CONTRACT_BACKUP="${LOG_DIR}/architecture.contract.json"

cleanup() {
  rm -f "${DEPENDENCY_VIOLATION}" "${COVERAGE_VIOLATION}"
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

for required in "${PROJECT}" "${CONTRACT}" "${DEPENDENCY_FIXTURE}" "${COVERAGE_FIXTURE}"; do
  if [ ! -f "${required}" ]; then
    echo "verify-gate: cannot find ${required}" >&2
    exit 2
  fi
done

if [ -e "${DEPENDENCY_VIOLATION}" ] || [ -e "${COVERAGE_VIOLATION}" ]; then
  echo "verify-gate: injected violation files must never be tracked" >&2
  exit 2
fi

cp "${CONTRACT}" "${CONTRACT_BACKUP}"

# ---------------------------------------------------------------------------
echo "=== Step 1/5: build the clean strict-coverage SampleConsumer (expect success) ==="
build "${LOG_DIR}/step1.log"
STEP1_EXIT=$?
cat "${LOG_DIR}/step1.log"
if [ "${STEP1_EXIT}" -eq 0 ]; then
  record "PASS" "step 1 - clean strict build succeeded (exit 0)"
else
  record "FAIL" "step 1 - clean strict build was expected to succeed but exited ${STEP1_EXIT}"
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 2/5: inject forbidden dependency (expect AARC002 failure) ==="
cp "${DEPENDENCY_FIXTURE}" "${DEPENDENCY_VIOLATION}"
build "${LOG_DIR}/step2.log"
STEP2_EXIT=$?
cat "${LOG_DIR}/step2.log"

if [ "${STEP2_EXIT}" -eq 0 ]; then
  record "FAIL" "step 2 - build succeeded but the dependency violation should have failed it"
elif grep -q "${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}" "${LOG_DIR}/step2.log"; then
  record "PASS" "step 2 - build failed and reported ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 2 - build failed but never reported ${EXPECTED_DEPENDENCY_DIAGNOSTIC_ID}"
fi
rm -f "${DEPENDENCY_VIOLATION}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 3/5: inject unclassified type under strict policy (expect AARC010 failure) ==="
cp "${COVERAGE_FIXTURE}" "${COVERAGE_VIOLATION}"
build "${LOG_DIR}/step3.log"
STEP3_EXIT=$?
cat "${LOG_DIR}/step3.log"

if [ "${STEP3_EXIT}" -eq 0 ]; then
  record "FAIL" "step 3 - strict coverage build succeeded but the unclassified type should fail it"
elif grep -q "${EXPECTED_COVERAGE_DIAGNOSTIC_ID}" "${LOG_DIR}/step3.log"; then
  record "PASS" "step 3 - strict coverage build failed and reported ${EXPECTED_COVERAGE_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 3 - strict coverage build failed but never reported ${EXPECTED_COVERAGE_DIAGNOSTIC_ID}"
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 4/5: switch contract to ignore and rebuild same coverage gap (expect success) ==="
sed -i 's/"unclassifiedCode": "error"/"unclassifiedCode": "ignore"/' "${CONTRACT}"
if ! grep -q '"unclassifiedCode": "ignore"' "${CONTRACT}"; then
  record "FAIL" "step 4 - could not switch the fixture contract to unclassifiedCode=ignore"
else
  build "${LOG_DIR}/step4.log"
  STEP4_EXIT=$?
  cat "${LOG_DIR}/step4.log"
  if [ "${STEP4_EXIT}" -eq 0 ]; then
    record "PASS" "step 4 - legacy/allowed mode accepted the same unclassified type"
  else
    record "FAIL" "step 4 - ignore mode was expected to pass but exited ${STEP4_EXIT}"
  fi
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 5/5: restore strict contract, remove coverage gap and rebuild cleanly ==="
cp "${CONTRACT_BACKUP}" "${CONTRACT}"
rm -f "${COVERAGE_VIOLATION}"
build "${LOG_DIR}/step5.log"
STEP5_EXIT=$?
cat "${LOG_DIR}/step5.log"
if [ "${STEP5_EXIT}" -eq 0 ]; then
  record "PASS" "step 5 - strict clean build succeeded again after cleanup"
else
  record "FAIL" "step 5 - final strict clean build was expected to succeed but exited ${STEP5_EXIT}"
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
