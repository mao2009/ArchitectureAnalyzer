# End-to-end proof for Windows that the package produced from the current source tree works
# through normal NuGet/MSBuild analyzer discovery and AdditionalFiles wiring.

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "../..")).Path
$AnalyzerProject = Join-Path $RepoRoot "src/ArchitectureAnalyzer/ArchitectureAnalyzer.csproj"
$ConsumerDir = Join-Path $ScriptDir "Consumer"
$Project = Join-Path $ConsumerDir "PackageConsumer.csproj"
$Fixture = Join-Path $ScriptDir "Fixtures/Violation.cs.txt"
$Violation = Join-Path $ConsumerDir "Domain/Violation.cs"
$PackageVersion = if ($env:ARCHITECTURE_ANALYZER_E2E_VERSION) {
    $env:ARCHITECTURE_ANALYZER_E2E_VERSION
} else {
    "0.0.0-e2e"
}
$ExpectedDiagnosticId = "AARC002"

$WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ArchitectureAnalyzer-e2e-" + [guid]::NewGuid().ToString("N"))
$Feed = Join-Path $WorkDir "feed"
$PackagesDir = Join-Path $WorkDir "packages"
$LogDir = Join-Path $WorkDir "logs"
New-Item -ItemType Directory -Force -Path $Feed, $PackagesDir, $LogDir | Out-Null

$Results = [System.Collections.Generic.List[string]]::new()
$Failures = 0
$PreviousNugetPackages = $env:NUGET_PACKAGES

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("PASS", "FAIL")][string]$Status,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $script:Results.Add($Status + ": " + $Description)
    if ($Status -eq "FAIL") {
        $script:Failures++
    }
}

function Invoke-DotnetLogged {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogFile
    )

    & dotnet @Arguments *> $LogFile
    $exitCode = $LASTEXITCODE
    Get-Content -Path $LogFile | ForEach-Object { Write-Host $_ }
    return $exitCode
}

function Invoke-ConsumerBuild {
    param([Parameter(Mandatory = $true)][string]$LogFile)

    return Invoke-DotnetLogged -Arguments @(
        "build", $Project,
        "--configuration", "Release",
        "--no-restore",
        "--no-incremental",
        "-v:m",
        "-p:ArchitectureAnalyzerPackageVersion=$PackageVersion"
    ) -LogFile $LogFile
}

try {
    foreach ($required in @($AnalyzerProject, $Project, $Fixture)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "verify-package-consumer: cannot find $required"
        }
    }

    if (Select-String -LiteralPath $Project -Pattern "<ProjectReference" -Quiet) {
        throw "verify-package-consumer: package fixture must not contain a ProjectReference"
    }

    if (Test-Path -LiteralPath $Violation) {
        throw "verify-package-consumer: $Violation already exists; it must never be tracked"
    }

    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $ConsumerDir "bin"), (Join-Path $ConsumerDir "obj")
    $env:NUGET_PACKAGES = $PackagesDir

    Write-Host "=== Step 1/5: pack the current analyzer source to a temporary local feed ==="
    $packExit = Invoke-DotnetLogged -Arguments @(
        "pack", $AnalyzerProject,
        "--configuration", "Release",
        "--output", $Feed,
        "-p:PackageVersion=$PackageVersion",
        "-p:Version=$PackageVersion"
    ) -LogFile (Join-Path $LogDir "pack.log")

    $packageFiles = @(Get-ChildItem -LiteralPath $Feed -Filter "*.nupkg" -File)
    $hasAnalyzerAsset = $false
    if ($packExit -eq 0 -and $packageFiles.Count -eq 1) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($packageFiles[0].FullName)
        try {
            $hasAnalyzerAsset = $null -ne ($archive.Entries | Where-Object {
                $_.FullName -eq "analyzers/dotnet/cs/ArchitectureAnalyzer.dll"
            } | Select-Object -First 1)
        }
        finally {
            $archive.Dispose()
        }
    }

    if ($packExit -eq 0 -and $packageFiles.Count -eq 1 -and $hasAnalyzerAsset) {
        Add-Result "PASS" "step 1 - current source packed with the analyzer asset in analyzers/dotnet/cs"
    }
    else {
        Add-Result "FAIL" "step 1 - packing failed or the package analyzer asset is missing"
    }

    Write-Host ""
    Write-Host "=== Step 2/5: restore the standalone consumer from only the local package feed ==="
    $restoreExit = Invoke-DotnetLogged -Arguments @(
        "restore", $Project,
        "--source", $Feed,
        "--packages", $PackagesDir,
        "--force",
        "--no-cache",
        "-p:ArchitectureAnalyzerPackageVersion=$PackageVersion"
    ) -LogFile (Join-Path $LogDir "restore.log")

    if ($restoreExit -eq 0) {
        Add-Result "PASS" "step 2 - isolated restore succeeded from the local feed"
    }
    else {
        Add-Result "FAIL" "step 2 - isolated restore failed"
    }

    Write-Host ""
    Write-Host "=== Step 3/5: build the clean package consumer (expect success) ==="
    $cleanExit = Invoke-ConsumerBuild -LogFile (Join-Path $LogDir "clean.log")
    $runtimeAnalyzer = Get-ChildItem -Path (Join-Path $ConsumerDir "bin") -Filter "ArchitectureAnalyzer.dll" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($cleanExit -eq 0 -and $null -eq $runtimeAnalyzer) {
        Add-Result "PASS" "step 3 - clean build succeeded and analyzer assembly is absent from runtime output"
    }
    elseif ($cleanExit -ne 0) {
        Add-Result "FAIL" "step 3 - clean package consumer build failed (exit $cleanExit)"
    }
    else {
        Add-Result "FAIL" "step 3 - ArchitectureAnalyzer.dll leaked into runtime output: $($runtimeAnalyzer.FullName)"
    }

    Write-Host ""
    Write-Host "=== Step 4/5: inject a forbidden dependency (expect AARC002 failure) ==="
    Copy-Item -LiteralPath $Fixture -Destination $Violation
    $violationLog = Join-Path $LogDir "violation.log"
    $violationExit = Invoke-ConsumerBuild -LogFile $violationLog
    $reportedDiagnostic = Select-String -LiteralPath $violationLog -Pattern $ExpectedDiagnosticId -Quiet

    if ($violationExit -eq 0) {
        Add-Result "FAIL" "step 4 - build succeeded, so the packaged analyzer/AdditionalFiles gate did not fire"
    }
    elseif ($reportedDiagnostic) {
        Add-Result "PASS" "step 4 - build failed and reported $ExpectedDiagnosticId"
    }
    else {
        Add-Result "FAIL" "step 4 - build failed but never reported $ExpectedDiagnosticId"
    }

    Remove-Item -LiteralPath $Violation -Force -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "=== Step 5/5: remove the violation and build cleanly again ==="
    $finalExit = Invoke-ConsumerBuild -LogFile (Join-Path $LogDir "final.log")
    if ($finalExit -eq 0) {
        Add-Result "PASS" "step 5 - clean build succeeded again after removing the violation"
    }
    else {
        Add-Result "FAIL" "step 5 - final clean build failed (exit $finalExit)"
    }

    Write-Host ""
    Write-Host "============= verify-package-consumer summary ============="
    foreach ($line in $Results) {
        Write-Host "  $line"
    }
    Write-Host "==========================================================="

    if ($Failures -ne 0) {
        throw "verify-package-consumer: FAILED ($Failures of $($Results.Count) checks failed)"
    }

    Write-Host "verify-package-consumer: PASSED ($($Results.Count) of $($Results.Count) checks passed)"
}
finally {
    Remove-Item -LiteralPath $Violation -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $ConsumerDir "bin"), (Join-Path $ConsumerDir "obj")
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $WorkDir
    $env:NUGET_PACKAGES = $PreviousNugetPackages
}
