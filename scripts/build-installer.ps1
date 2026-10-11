[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackageDirectory,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$GitCommit,
    [string]$Tag = "",
    [string]$IsccPath = "",
    [string]$LogPath = "",
    [switch]$AuthenticodeSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "installer_build_requires_windows"
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "installer_version_invalid" }
if ($GitCommit -notmatch '^[0-9a-fA-F]{40}$') { throw "installer_git_commit_invalid" }
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$Version" }
if ($Tag -ne "v$Version") { throw "installer_tag_version_mismatch" }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $packageRoot "release-manifest.json"
$scriptPath = Join-Path $repositoryRoot "installer\DebtFlow.SipAgent.iss"
$applicationIconPath = Join-Path $repositoryRoot "assets\branding\DebtFlow.SipAgent.ico"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "release_manifest_missing" }
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "inno_script_missing" }
if (-not (Test-Path -LiteralPath $applicationIconPath -PathType Leaf)) { throw "application_icon_missing" }
$applicationIconHash = (Get-FileHash -LiteralPath $applicationIconPath -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if ([string]::IsNullOrWhiteSpace($LogPath)) { $LogPath = Join-Path $outputRoot "inno-compile.log" }
$logRoot = Split-Path -Parent ([System.IO.Path]::GetFullPath($LogPath))
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

function Get-InnoCompilerVersion([string]$CompilerPath) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $CompilerPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add("--version")

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "inno_compiler_version_command_start_failed" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
            throw "inno_compiler_version_command_timeout"
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
        $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "inno_compiler_version_command_failed:exit_code=$($process.ExitCode)"
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            throw "inno_compiler_version_command_unexpected_stderr"
        }
        if ($stdout -notmatch '^7\.1\.0(?:\.0)?$') {
            $reportedVersion = if ([string]::IsNullOrWhiteSpace($stdout)) { "empty" } else { $stdout }
            throw "inno_compiler_version_mismatch:expected=7.1.0 actual=$reportedVersion"
        }
        return $stdout
    }
    finally {
        $process.Dispose()
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or
    $manifest.version -ne $Version -or
    $manifest.gitCommit -ne $GitCommit -or
    $manifest.runtimeIdentifier -ne "win-x64" -or
    $manifest.protocolVersion -ne 1 -or
    $manifest.sqliteSchemaVersion -ne 4 -or
    $manifest.selfContained -ne $true -or
    $manifest.singleFile -ne $false) {
    throw "release_manifest_contract_mismatch"
}
if ([bool]$manifest.authenticodeSigned -ne $AuthenticodeSigned.IsPresent) {
    throw "release_manifest_signing_state_mismatch"
}

$expectedFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.files)) {
    $relativePath = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains("..", [System.StringComparison]::Ordinal) -or
        -not $expectedFiles.Add($relativePath.Replace('/', '\'))) {
        throw "release_manifest_file_path_invalid:$relativePath"
    }
    $filePath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $relativePath))
    $packagePrefix = $packageRoot.TrimEnd('\') + '\'
    if (-not $filePath.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "release_manifest_file_outside_package:$relativePath"
    }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "release_manifest_file_missing:$relativePath"
    }
    $file = Get-Item -LiteralPath $filePath
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne [long]$entry.bytes -or $actualHash -ne [string]$entry.sha256) {
        throw "release_manifest_file_mismatch:$relativePath"
    }
}

$actualFiles = @(
    Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        ForEach-Object {
            [System.IO.Path]::GetRelativePath($packageRoot, $_.FullName)
        }
)
foreach ($actualFile in $actualFiles) {
    if ($actualFile -ne "release-manifest.json" -and -not $expectedFiles.Contains($actualFile)) {
        throw "release_package_unmanifested_file:$actualFile"
    }
}
if ($actualFiles.Count -ne ($expectedFiles.Count + 1)) { throw "release_package_file_count_mismatch" }

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:LOCALAPPDATA} "Programs\Inno Setup 7\ISCC.exe")
    )
    $foundCandidates = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($foundCandidates.Count -gt 0) { $IsccPath = $foundCandidates[0] }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "inno_compiler_7_1_0_not_found"
}
$iscc = Get-Item -LiteralPath $IsccPath
function Get-PeMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "inno_compiler_not_pe" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "inno_compiler_pe_header_invalid" }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}
if ((Get-PeMachine $iscc.FullName) -ne 0x8664) { throw "inno_compiler_not_x64" }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $iscc.FullName
if ($compilerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $compilerSignature.SignerCertificate) {
    throw "inno_compiler_authenticode_invalid:$($compilerSignature.Status)"
}
$compilerHash = (Get-FileHash -LiteralPath $iscc.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$innoVersion = Get-InnoCompilerVersion $iscc.FullName

$arguments = @(
    "/DAppVersion=$Version",
    "/DGitCommit=$GitCommit",
    "/DPackageDirectory=$packageRoot",
    "/DOutputDirectory=$outputRoot",
    "/DAppIconPath=$applicationIconPath",
    "/DExpectedProtocolVersion=1",
    "/DExpectedSqliteSchemaVersion=4",
    $scriptPath
)
& $iscc.FullName @arguments 2>&1 | Tee-Object -FilePath $LogPath
if ($LASTEXITCODE -ne 0) { throw "inno_compile_failed:exit_code=$LASTEXITCODE" }

$setupName = "debt-flow-sip-agent-$Version-win-x64-setup.exe"
$setupPath = Join-Path $outputRoot $setupName
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "installer_output_missing" }
$setupFile = Get-Item -LiteralPath $setupPath
if ($setupFile.Length -le 0) { throw "installer_output_empty" }
$setupVersion = $setupFile.VersionInfo.ProductVersion
if (-not $setupVersion.StartsWith($Version, [System.StringComparison]::Ordinal)) {
    throw "installer_version_resource_mismatch:expected=$Version actual=$setupVersion"
}
$signature = Get-AuthenticodeSignature -LiteralPath $setupPath
if ($AuthenticodeSigned) {
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "installer_authenticode_invalid:$($signature.Status)"
    }
}
elseif ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
    throw "installer_unexpectedly_signed"
}

$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$setupPath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$setupHash *$setupName" -Encoding ASCII
$packageManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerManifestPath = Join-Path $outputRoot "installer-manifest.json"
[ordered]@{
    schemaVersion = 1
    applicationVersion = $Version
    gitCommit = $GitCommit.ToLowerInvariant()
    tag = $Tag
    appId = "{476433C5-E4DF-4CD6-9E42-29F01E4BC339}"
    applicationIconSha256 = $applicationIconHash
    innoSetup = [ordered]@{
        version = $innoVersion
        architecture = "x64"
        compilerSha256 = $compilerHash
        authenticodeStatus = [string]$compilerSignature.Status
        signerSubject = $compilerSignature.SignerCertificate.Subject
        signerThumbprint = $compilerSignature.SignerCertificate.Thumbprint
    }
    setup = [ordered]@{
        fileName = $setupName
        bytes = $setupFile.Length
        sha256 = $setupHash
        authenticodeSigned = $AuthenticodeSigned.IsPresent
        authenticodeStatus = [string]$signature.Status
    }
    sourcePackageManifestSha256 = $packageManifestHash
    protocolVersion = 1
    sqliteSchemaVersion = 4
    runtimeIdentifier = "win-x64"
    installScope = "current-user"
    minimumOperatingSystem = "10.0.17763"
    applicationAuthenticodeSigned = [bool]$manifest.authenticodeSigned
    uninstallerAuthenticodeSigned = $false
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $installerManifestPath -Encoding UTF8

Write-Host "Inno installer created: $setupPath"
Write-Host "Installer manifest: $installerManifestPath"
