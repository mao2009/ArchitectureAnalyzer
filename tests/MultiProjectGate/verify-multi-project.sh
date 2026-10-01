#!/usr/bin/env bash

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="${SCRIPT_DIR}/MultiProjectGate.slnx"
PRODUCER_PROJECT="${SCRIPT_DIR}/Producer/Producer.csproj"
CONSUMER_PROJECT="${SCRIPT_DIR}/Consumer/Consumer.csproj"
TESTS_PROJECT="${SCRIPT_DIR}/Tests/Tests.csproj"
TOOLING_PROJECT="${SCRIPT_DIR}/Tooling/Tooling.csproj"
PRODUCER_FIXTURE="${SCRIPT_DIR}/Fixtures/ProducerViolation.cs.txt"
CONSUMER_FIXTURE="${SCRIPT_DIR}/Fixtures/ConsumerViolation.cs.txt"
PRODUCER_INJECTED="${SCRIPT_DIR}/Producer/ProducerViolation.cs"
CONSUMER_INJECTED="${SCRIPT_DIR}/Consumer/ConsumerViolation.cs"

LOG_DIR="$(mktemp -d)"
RESULTS=()
FAILURES=0

cleanup() {
  rm -f "${PRODUCER_INJECTED}" "${CONSUMER_INJECTED}"
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
  local project="$1"
  local log="$2"
  dotnet build "${project}" --no-restore --no-incremental -v:m >"${log}" 2>&1
}

for required in   "${SOLUTION}"   "${PRODUCER_PROJECT}"   "${CONSUMER_PROJECT}"   "${TESTS_PROJECT}"   "${TOOLING_PROJECT}"   "${PRODUCER_FIXTURE}"   "${CONSUMER_FIXTURE}"; do
  if [ ! -f "${required}" ]; then
    echo "verify-multi-project: missing ${required}" >&2
    exit 2
  fi
done

if [ -e "${PRODUCER_INJECTED}" ] || [ -e "${CONSUMER_INJECTED}" ]; then
  echo "verify-multi-project: injected .cs files must never be tracked" >&2
  exit 2
fi

if grep -q "<AdditionalFiles" "${TESTS_PROJECT}" || grep -q "<AdditionalFiles" "${TOOLING_PROJECT}"; then
  echo "verify-multi-project: Tests/Tooling must stay contract-free" >&2
  exit 2
fi

echo "=== Restore multi-project fixture ==="
dotnet restore "${SOLUTION}" >"${LOG_DIR}/restore.log" 2>&1
RESTORE_EXIT=$?
cat "${LOG_DIR}/restore.log"
if [ "${RESTORE_EXIT}" -ne 0 ]; then
  echo "verify-multi-project: restore failed" >&2
  exit "${RESTORE_EXIT}"
fi

echo
echo "=== Step 1/5: clean multi-project solution build (expect success) ==="
build "${SOLUTION}" "${LOG_DIR}/step1.log"
STEP1_EXIT=$?
cat "${LOG_DIR}/step1.log"
if [ "${STEP1_EXIT}" -eq 0 ]; then
  record "PASS" "step 1 - clean solution succeeded with independent Producer/Consumer contracts"
else
  record "FAIL" "step 1 - clean solution was expected to succeed but exited ${STEP1_EXIT}"
fi

echo
echo "=== Step 2/5: Producer-specific contract violation (expect AARC003 failure) ==="
cp "${PRODUCER_FIXTURE}" "${PRODUCER_INJECTED}"
build "${PRODUCER_PROJECT}" "${LOG_DIR}/step2.log"
STEP2_EXIT=$?
cat "${LOG_DIR}/step2.log"
if [ "${STEP2_EXIT}" -eq 0 ]; then
  record "FAIL" "step 2 - Producer violation unexpectedly succeeded"
elif grep -q "AARC003" "${LOG_DIR}/step2.log"; then
  record "PASS" "step 2 - Producer's own contract reported AARC003"
else
  record "FAIL" "step 2 - Producer build failed without AARC003"
fi
rm -f "${PRODUCER_INJECTED}"

echo
echo "=== Step 3/5: Consumer cross-project dependency (expect AARC002 failure) ==="
cp "${CONSUMER_FIXTURE}" "${CONSUMER_INJECTED}"
build "${CONSUMER_PROJECT}" "${LOG_DIR}/step3.log"
STEP3_EXIT=$?
cat "${LOG_DIR}/step3.log"
if [ "${STEP3_EXIT}" -eq 0 ]; then
  record "FAIL" "step 3 - Consumer -> Producer violation unexpectedly succeeded"
elif grep -q "AARC002" "${LOG_DIR}/step3.log"; then
  record "PASS" "step 3 - Consumer's contract classified referenced Producer metadata and reported AARC002"
else
  record "FAIL" "step 3 - Consumer build failed without AARC002"
fi
rm -f "${CONSUMER_INJECTED}"

echo
echo "=== Step 4/5: contract-free Tests and Tooling builds (expect success) ==="
build "${TESTS_PROJECT}" "${LOG_DIR}/step4-tests.log"
TESTS_EXIT=$?
build "${TOOLING_PROJECT}" "${LOG_DIR}/step4-tooling.log"
TOOLING_EXIT=$?
cat "${LOG_DIR}/step4-tests.log"
cat "${LOG_DIR}/step4-tooling.log"
if [ "${TESTS_EXIT}" -eq 0 ] && [ "${TOOLING_EXIT}" -eq 0 ]; then
  record "PASS" "step 4 - analyzer-loaded Tests/Tooling projects remained no-op without contracts"
else
  record "FAIL" "step 4 - contract-free Tests/Tooling project failed (tests=${TESTS_EXIT}, tooling=${TOOLING_EXIT})"
fi

echo
echo "=== Step 5/5: final clean solution build (expect success) ==="
build "${SOLUTION}" "${LOG_DIR}/step5.log"
STEP5_EXIT=$?
cat "${LOG_DIR}/step5.log"
if [ "${STEP5_EXIT}" -eq 0 ]; then
  record "PASS" "step 5 - solution recovered cleanly after removing injected violations"
else
  record "FAIL" "step 5 - final clean solution build exited ${STEP5_EXIT}"
fi

echo
echo "================ multi-project gate summary ================"
for line in "${RESULTS[@]}"; do
  echo "  ${line}"
done
echo "============================================================"

if [ "${FAILURES}" -ne 0 ]; then
  echo "verify-multi-project: FAILED (${FAILURES} of ${#RESULTS[@]} checks failed)"
  exit 1
fi

echo "verify-multi-project: PASSED (${#RESULTS[@]} of ${#RESULTS[@]} checks passed)"
