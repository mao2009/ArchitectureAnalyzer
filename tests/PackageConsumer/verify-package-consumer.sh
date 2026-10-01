#!/usr/bin/env bash
#
# End-to-end proof that the package produced from the current source tree works for a standalone
# consumer through normal NuGet/MSBuild analyzer discovery and AdditionalFiles wiring.
#
# The consumer restore uses only a temporary local feed and an isolated NUGET_PACKAGES directory,
# so a published or globally cached loach.ArchitectureAnalyzer package cannot satisfy the test.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ANALYZER_PROJECT="${REPO_ROOT}/src/ArchitectureAnalyzer/ArchitectureAnalyzer.csproj"
CONSUMER_DIR="${SCRIPT_DIR}/Consumer"
PROJECT="${CONSUMER_DIR}/PackageConsumer.csproj"
FIXTURE="${SCRIPT_DIR}/Fixtures/Violation.cs.txt"
VIOLATION="${CONSUMER_DIR}/Domain/Violation.cs"
PACKAGE_VERSION="${ARCHITECTURE_ANALYZER_E2E_VERSION:-0.0.0-e2e}"
EXPECTED_DIAGNOSTIC_ID="AARC002"

RESULTS=()
FAILURES=0
WORK_DIR="$(mktemp -d)"
FEED="${WORK_DIR}/feed"
PACKAGES="${WORK_DIR}/packages"
LOG_DIR="${WORK_DIR}/logs"
mkdir -p "${FEED}" "${PACKAGES}" "${LOG_DIR}"

cleanup() {
  rm -f "${VIOLATION}"
  rm -rf "${CONSUMER_DIR}/bin" "${CONSUMER_DIR}/obj"
  rm -rf "${WORK_DIR}"
}
trap cleanup EXIT

record() {
  RESULTS+=("$1: $2")
  if [ "$1" = "FAIL" ]; then
    FAILURES=$((FAILURES + 1))
  fi
}

print_log() {
  if [ -f "$1" ]; then
    cat "$1"
  fi
}

build_consumer() {
  local log_file="$1"
  NUGET_PACKAGES="${PACKAGES}" dotnet build "${PROJECT}" \
    --configuration Release \
    --no-restore \
    --no-incremental \
    -v:m \
    -p:ArchitectureAnalyzerPackageVersion="${PACKAGE_VERSION}" >"${log_file}" 2>&1
}

for required in "${ANALYZER_PROJECT}" "${PROJECT}" "${FIXTURE}"; do
  if [ ! -f "${required}" ]; then
    echo "verify-package-consumer: cannot find ${required}" >&2
    exit 2
  fi
done

if grep -q '<ProjectReference' "${PROJECT}"; then
  echo "verify-package-consumer: package fixture must not contain a ProjectReference" >&2
  exit 2
fi

if [ -e "${VIOLATION}" ]; then
  echo "verify-package-consumer: ${VIOLATION} already exists; it must never be tracked" >&2
  exit 2
fi

rm -rf "${CONSUMER_DIR}/bin" "${CONSUMER_DIR}/obj"

echo "=== Step 1/5: pack the current analyzer source to a temporary local feed ==="
dotnet pack "${ANALYZER_PROJECT}" \
  --configuration Release \
  --output "${FEED}" \
  -p:PackageVersion="${PACKAGE_VERSION}" \
  -p:Version="${PACKAGE_VERSION}" >"${LOG_DIR}/pack.log" 2>&1
PACK_EXIT=$?
print_log "${LOG_DIR}/pack.log"

shopt -s nullglob
PACKAGE_FILES=("${FEED}"/*.nupkg)
shopt -u nullglob

if [ "${PACK_EXIT}" -eq 0 ] \
  && [ "${#PACKAGE_FILES[@]}" -eq 1 ] \
  && unzip -l "${PACKAGE_FILES[0]}" | grep -q 'analyzers/dotnet/cs/ArchitectureAnalyzer.dll'; then
  record "PASS" "step 1 - current source packed with the analyzer asset in analyzers/dotnet/cs"
else
  record "FAIL" "step 1 - packing failed or the package analyzer asset is missing"
fi

echo
echo "=== Step 2/5: restore the standalone consumer from only the local package feed ==="
NUGET_PACKAGES="${PACKAGES}" dotnet restore "${PROJECT}" \
  --source "${FEED}" \
  --packages "${PACKAGES}" \
  --force \
  --no-cache \
  -p:ArchitectureAnalyzerPackageVersion="${PACKAGE_VERSION}" >"${LOG_DIR}/restore.log" 2>&1
RESTORE_EXIT=$?
print_log "${LOG_DIR}/restore.log"
if [ "${RESTORE_EXIT}" -eq 0 ]; then
  record "PASS" "step 2 - isolated restore succeeded from the local feed"
else
  record "FAIL" "step 2 - isolated restore failed"
fi

echo
echo "=== Step 3/5: build the clean package consumer (expect success) ==="
build_consumer "${LOG_DIR}/clean.log"
CLEAN_EXIT=$?
print_log "${LOG_DIR}/clean.log"

RUNTIME_ANALYZER=""
if [ -d "${CONSUMER_DIR}/bin" ]; then
  RUNTIME_ANALYZER="$(find "${CONSUMER_DIR}/bin" -type f -name 'ArchitectureAnalyzer.dll' -print -quit)"
fi

if [ "${CLEAN_EXIT}" -eq 0 ] && [ -z "${RUNTIME_ANALYZER}" ]; then
  record "PASS" "step 3 - clean build succeeded and analyzer assembly is absent from runtime output"
elif [ "${CLEAN_EXIT}" -ne 0 ]; then
  record "FAIL" "step 3 - clean package consumer build failed (exit ${CLEAN_EXIT})"
else
  record "FAIL" "step 3 - ArchitectureAnalyzer.dll leaked into runtime output: ${RUNTIME_ANALYZER}"
fi

echo
echo "=== Step 4/5: inject a forbidden dependency (expect AARC002 failure) ==="
cp "${FIXTURE}" "${VIOLATION}"
build_consumer "${LOG_DIR}/violation.log"
VIOLATION_EXIT=$?
print_log "${LOG_DIR}/violation.log"

if [ "${VIOLATION_EXIT}" -eq 0 ]; then
  record "FAIL" "step 4 - build succeeded, so the packaged analyzer/AdditionalFiles gate did not fire"
elif grep -q "${EXPECTED_DIAGNOSTIC_ID}" "${LOG_DIR}/violation.log"; then
  record "PASS" "step 4 - build failed and reported ${EXPECTED_DIAGNOSTIC_ID}"
else
  record "FAIL" "step 4 - build failed but never reported ${EXPECTED_DIAGNOSTIC_ID}"
fi

rm -f "${VIOLATION}"

echo
echo "=== Step 5/5: remove the violation and build cleanly again ==="
build_consumer "${LOG_DIR}/final.log"
FINAL_EXIT=$?
print_log "${LOG_DIR}/final.log"
if [ "${FINAL_EXIT}" -eq 0 ]; then
  record "PASS" "step 5 - clean build succeeded again after removing the violation"
else
  record "FAIL" "step 5 - final clean build failed (exit ${FINAL_EXIT})"
fi

echo
echo "============= verify-package-consumer summary ============="
for line in "${RESULTS[@]}"; do
  echo "  ${line}"
done
echo "==========================================================="

if [ "${FAILURES}" -ne 0 ]; then
  echo "verify-package-consumer: FAILED (${FAILURES} of ${#RESULTS[@]} checks failed)"
  exit 1
fi

echo "verify-package-consumer: PASSED (${#RESULTS[@]} of ${#RESULTS[@]} checks passed)"
exit 0
