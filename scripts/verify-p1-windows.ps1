[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\p1-windows")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "p1_windows_verification_requires_windows"
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solutionPath = Join-Path $repositoryRoot "softphone-native-client.sln"
$projectPath = Join-Path $repositoryRoot "softphone-native-client.csproj"
$coreTestProject = Join-Path $repositoryRoot "tests\DebtFlow.SipAgent.Core.Tests\DebtFlow.SipAgent.Core.Tests.csproj"
$hostTestProject = Join-Path $repositoryRoot "tests\DebtFlow.SipAgent.Host.Tests\DebtFlow.SipAgent.Host.Tests.csproj"
$gitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$gitDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
if ($gitDirty) {
    throw "repository_dirty_commit_or_stash_before_verification"
}

$runId = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssfffZ")
$resolvedOutputRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$runRoot = Join-Path $resolvedOutputRoot $runId
$logDirectory = Join-Path $runRoot "logs"
$testDirectory = Join-Path $runRoot "test-results"
$publishDirectory = Join-Path $runRoot "win-x64"
$zipPath = Join-Path $runRoot "debt-flow-sip-agent-win-x64.zip"
$evidencePath = Join-Path $runRoot "p1-windows-evidence.json"

New-Item -ItemType Directory -Force -Path $logDirectory, $testDirectory, $publishDirectory | Out-Null

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$LogPath
    )

    & $Command @Arguments 2>&1 | Tee-Object -FilePath $LogPath
    if ($LASTEXITCODE -ne 0) {
        throw "command_failed:$Command exit_code=$LASTEXITCODE log=$LogPath"
    }
}

Push-Location $repositoryRoot
try {
    Invoke-CheckedCommand "dotnet" @(
        "restore", $solutionPath,
        "--locked-mode",
        "--disable-build-servers",
        "-m:1"
    ) (Join-Path $logDirectory "restore.log")

    Invoke-CheckedCommand "dotnet" @(
        "build", $solutionPath,
        "-c", "Release",
        "--no-restore",
        "--disable-build-servers",
        "-m:1"
    ) (Join-Path $logDirectory "build.log")

    Invoke-CheckedCommand "dotnet" @(
        "test", $coreTestProject,
        "-c", "Release",
        "--no-build",
        "--no-restore",
        "--disable-build-servers",
        "-m:1",
        "--logger", "trx;LogFileName=core-tests.trx",
        "--results-directory", $testDirectory
    ) (Join-Path $logDirectory "core-tests.log")

    Invoke-CheckedCommand "dotnet" @(
        "test", $hostTestProject,
        "-c", "Release",
        "--no-build",
        "--no-restore",
        "--disable-build-servers",
        "-m:1",
        "--logger", "trx;LogFileName=host-tests.trx",
        "--results-directory", $testDirectory
    ) (Join-Path $logDirectory "host-tests.log")

    $vulnerabilityPath = Join-Path $runRoot "vulnerabilities.json"
    $vulnerabilityErrorPath = Join-Path $logDirectory "vulnerabilities.stderr.log"
    $vulnerabilityOutput = & dotnet list $solutionPath package `
        --vulnerable `
        --include-transitive `
        --no-restore `
        --format json `
        --output-version 1 2> $vulnerabilityErrorPath
    if ($LASTEXITCODE -ne 0) {
        throw "vulnerability_scan_failed:exit_code=$LASTEXITCODE log=$vulnerabilityErrorPath"
    }

    $vulnerabilityJson = $vulnerabilityOutput -join [Environment]::NewLine
    Set-Content -LiteralPath $vulnerabilityPath -Value $vulnerabilityJson -Encoding UTF8

    function Measure-VulnerabilityEntries {
        param($Node)
        if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsValueType) {
            return 0
        }

        if ($Node -is [System.Collections.IEnumerable]) {
            $collectionTotal = 0
            foreach ($item in $Node) {
                $collectionTotal += Measure-VulnerabilityEntries $item
            }

            return $collectionTotal
        }

        $objectTotal = 0
        foreach ($property in $Node.PSObject.Properties) {
            if ($property.Name -eq "vulnerabilities") {
                $objectTotal += @($property.Value | Where-Object { $null -ne $_ }).Count
            }
            else {
                $objectTotal += Measure-VulnerabilityEntries $property.Value
            }
        }

        return $objectTotal
    }

    $vulnerabilityData = $vulnerabilityJson | ConvertFrom-Json
    $vulnerabilityCount = Measure-VulnerabilityEntries $vulnerabilityData
    if ($vulnerabilityCount -ne 0) {
        throw "vulnerable_package_detected:report=$vulnerabilityPath"
    }

    Invoke-CheckedCommand "dotnet" @(
        "publish", $projectPath,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "--disable-build-servers",
        "-m:1",
        "-p:PublishSingleFile=false",
        "-o", $publishDirectory
    ) (Join-Path $logDirectory "publish.log")

    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        if ($entryNames -notcontains "DebtFlow.SipAgent.Host.exe" -or
            $entryNames -notcontains "agentsettings.example.json") {
            throw "artifact_missing_required_file"
        }

        $archiveEntryCount = $archive.Entries.Count
    }
    finally {
        $archive.Dispose()
    }

    function Get-CounterValue {
        param([System.Xml.XmlNode]$Node, [string]$Name)
        $attribute = $Node.Attributes.GetNamedItem($Name)
        if ($null -eq $attribute) {
            return 0
        }

        return [int]$attribute.Value
    }

    $trxPaths = @(
        Get-Item -LiteralPath (Join-Path $testDirectory "core-tests.trx")
        Get-Item -LiteralPath (Join-Path $testDirectory "host-tests.trx")
    )
    if ($trxPaths.Count -ne 2) {
        throw "test_evidence_missing_suites"
    }

    $testTotal = 0
    $testPassed = 0
    $testFailed = 0
    foreach ($trxPath in $trxPaths) {
        [xml]$trx = Get-Content -LiteralPath $trxPath.FullName -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counters) {
            throw "test_evidence_missing_counters:file=$($trxPath.FullName)"
        }

        $testTotal += Get-CounterValue $counters "total"
        $testPassed += Get-CounterValue $counters "passed"
        $testFailed += (Get-CounterValue $counters "failed") +
            (Get-CounterValue $counters "error") +
            (Get-CounterValue $counters "timeout") +
            (Get-CounterValue $counters "aborted")
    }

    if ($testTotal -lt 66 -or $testPassed -ne $testTotal -or $testFailed -ne 0) {
        throw "test_evidence_invalid:total=$testTotal passed=$testPassed failed=$testFailed"
    }

    $artifact = Get-Item -LiteralPath $zipPath
    $artifactHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    $checksumPath = "$zipPath.sha256"
    Set-Content -LiteralPath $checksumPath `
        -Value "$($artifactHash.Hash.ToLowerInvariant()) *$($artifact.Name)" `
        -Encoding ASCII
    $dotnetVersion = (& dotnet --version).Trim()
    $publishedFileCount = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse).Count

    [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        operatingSystem = [Environment]::OSVersion.VersionString
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        dotnetVersion = $dotnetVersion
        gitCommit = $gitCommit
        gitDirty = $gitDirty
        tests = [ordered]@{
            total = $testTotal
            passed = $testPassed
            failed = $testFailed
            trx = @($trxPaths | ForEach-Object { "test-results/$($_.Name)" })
        }
        vulnerability = [ordered]@{
            count = $vulnerabilityCount
            report = "vulnerabilities.json"
        }
        artifact = [ordered]@{
            file = $artifact.Name
            checksumFile = (Split-Path -Leaf $checksumPath)
            bytes = $artifact.Length
            sha256 = $artifactHash.Hash.ToLowerInvariant()
            publishedFileCount = $publishedFileCount
            archiveEntryCount = $archiveEntryCount
            runtimeIdentifier = "win-x64"
            selfContained = $true
            singleFile = $false
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    Write-Host "P1 Windows automated verification passed."
    Write-Host "Evidence: $evidencePath"
    Write-Host "Artifact: $zipPath"
}
finally {
    Pop-Location
}
