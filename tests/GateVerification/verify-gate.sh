#!/usr/bin/env bash
#
# Real-build gate verification for ArchitectureAnalyzer.
#
# The unit tests under src/ArchitectureAnalyzer.Tests exercise an in-memory compilation created
# by Microsoft.CodeAnalysis.Testing. This script proves the stronger claim the project actually
# makes: that a violation fails a genuine `dotnet build` of a project that consumes the analyzer
# the way any other repository would.
#
# Cycle: clean build passes -> inject a violating file -> build must fail with AARC002 ->
#        remove the file -> build passes again.
#
# Exits non-zero if any assertion fails, so CI can gate on it.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="${SCRIPT_DIR}/SampleConsumer/SampleConsumer.csproj"
FIXTURE="${SCRIPT_DIR}/Fixtures/Violation.cs.txt"
VIOLATION="${SCRIPT_DIR}/SampleConsumer/Domain/Violation.cs"
EXPECTED_DIAGNOSTIC_ID="AARC002"

RESULTS=()
FAILURES=0

cleanup() {
  rm -f "${VIOLATION}"
}
trap cleanup EXIT

record() {
  # record <PASS|FAIL> <description>
  RESULTS+=("$1: $2")
  if [ "$1" = "FAIL" ]; then
    FAILURES=$((FAILURES + 1))
  fi
}

build() {
  # build <log-file>; writes the build log to $1 and returns dotnet's exit code
  dotnet build "${PROJECT}" --no-incremental -v:m >"$1" 2>&1
}

if [ ! -f "${PROJECT}" ]; then
  echo "verify-gate: cannot find ${PROJECT}" >&2
  exit 2
fi

if [ ! -f "${FIXTURE}" ]; then
  echo "verify-gate: cannot find ${FIXTURE}" >&2
  exit 2
fi

if [ -e "${VIOLATION}" ]; then
  echo "verify-gate: ${VIOLATION} already exists; it must never be a tracked file" >&2
  exit 2
fi

LOG_DIR="$(mktemp -d)"
trap 'cleanup; rm -rf "${LOG_DIR}"' EXIT

# ---------------------------------------------------------------------------
echo "=== Step 1/3: build the clean SampleConsumer (expect success) ==="
build "${LOG_DIR}/step1.log"
STEP1_EXIT=$?
cat "${LOG_DIR}/step1.log"
if [ "${STEP1_EXIT}" -eq 0 ]; then
  record "PASS" "step 1 - clean build succeeded (exit 0)"
else
  record "FAIL" "step 1 - clean build was expected to succeed but exited ${STEP1_EXIT}"
fi

# ---------------------------------------------------------------------------
echo
echo "=== Step 2/3: inject Fixtures/Violation.cs.txt and rebuild (expect AARC002 failure) ==="
cp "${FIXTURE}" "${VIOLATION}"
build "${LOG_DIR}/step2.log"
STEP2_EXIT=$?
cat "${LOG_DIR}/step2.log"

if [ "${STEP2_EXIT}" -eq 0 ]; then
  record "FAIL" "step 2 - build succeeded but the injected violation should have failed it"
elif grep -q "${EXPECTED_DIAGNOSTIC_ID}" "${LOG_DIR}/step2.log"; then
  record "PASS" "step 2 - build failed (exit ${STEP2_EXIT}) and reported ${EXPECTED_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 2 - build failed (exit ${STEP2_EXIT}) but never reported ${EXPECTED_DIAGNOSTIC_ID}"
fi

rm -f "${VIOLATION}"

# ---------------------------------------------------------------------------
echo
echo "=== Step 3/3: remove the violation and rebuild (expect success again) ==="
build "${LOG_DIR}/step3.log"
STEP3_EXIT=$?
cat "${LOG_DIR}/step3.log"
if [ "${STEP3_EXIT}" -eq 0 ]; then
  record "PASS" "step 3 - build succeeded again after removing the violation (exit 0)"
else
  record "FAIL" "step 3 - build was expected to succeed but exited ${STEP3_EXIT}"
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
