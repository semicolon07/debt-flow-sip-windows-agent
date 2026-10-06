[CmdletBinding()]
param(
    [string]$OutputRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "p5_release_requires_windows"
}

$gitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$exactTagOutput = & git -C $repositoryRoot describe --tags --exact-match HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "semantic_release_tag_required"
}
$exactTag = ([string]$exactTagOutput).Trim()
if ($exactTag -notmatch '^v(?<version>[0-9]+\.[0-9]+\.[0-9]+)$') {
    throw "semantic_release_tag_required"
}
$tag = $exactTag
$version = $Matches.version
$artifactName = "debt-flow-sip-agent-$version-win-x64.zip"
if (@(& git -C $repositoryRoot status --porcelain).Count -gt 0) {
    throw "repository_dirty_commit_or_stash_before_release"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\p5-release"
}
$resolvedOutputRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$runRoot = Join-Path $resolvedOutputRoot ((Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssfffZ"))
$verificationRoot = Join-Path $runRoot "verification"
$publishDirectory = Join-Path $runRoot "package"
$logDirectory = Join-Path $runRoot "logs"
$artifactPath = Join-Path $runRoot $artifactName
New-Item -ItemType Directory -Force -Path $runRoot, $logDirectory | Out-Null

& (Join-Path $PSScriptRoot "verify-p2-windows.ps1") -OutputRoot $verificationRoot
if ($LASTEXITCODE -ne 0) { throw "p2_verification_failed" }

function Invoke-CheckedCommand {
    param([string]$Command, [string[]]$Arguments, [string]$LogPath)
    & $Command @Arguments 2>&1 | Tee-Object -FilePath $LogPath
    if ($LASTEXITCODE -ne 0) { throw "command_failed:$Command exit_code=$LASTEXITCODE log=$LogPath" }
}

Push-Location $repositoryRoot
try {
    Invoke-CheckedCommand "dotnet" @("tool", "restore") (Join-Path $logDirectory "tool-restore.log")
    Invoke-CheckedCommand "dotnet" @(
        "publish", "softphone-native-client.csproj", "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "--no-restore", "--disable-build-servers", "-m:1",
        "-p:PublishSingleFile=false", "-p:Version=$version", "-o", $publishDirectory
    ) (Join-Path $logDirectory "publish.log")

    $agentExecutable = Join-Path $publishDirectory "DebtFlow.SipAgent.Host.exe"
    $releaseMetadataJson = (& $agentExecutable --print-release-metadata)
    if ($LASTEXITCODE -ne 0) { throw "release_metadata_command_failed" }
    $releaseMetadata = $releaseMetadataJson | ConvertFrom-Json
    if ($releaseMetadata.version -ne $version -or
        $releaseMetadata.runtimeIdentifier -ne "win-x64" -or
        $releaseMetadata.protocolVersion -ne 1 -or
        $releaseMetadata.sqliteSchemaVersion -ne 4) {
        throw "release_metadata_mismatch:tag=$tag metadata=$releaseMetadataJson"
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot "release\README-th-en.md") `
        -Destination (Join-Path $publishDirectory "README.md")

    Invoke-CheckedCommand "dotnet" @(
        "tool", "run", "sbom-tool", "generate",
        "-b", $publishDirectory,
        "-bc", $repositoryRoot,
        "-pn", "Debt Flow SIP Agent",
        "-pv", $version,
        "-ps", "Debt Flow",
        "-nsb", "https://debt-flow.local/sbom/$version/$gitCommit"
    ) (Join-Path $logDirectory "sbom.log")

    $fileHashes = @(
        Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = [System.IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace('\', '/')
                    bytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $manifestPath = Join-Path $publishDirectory "release-manifest.json"
    [ordered]@{
        schemaVersion = $releaseMetadata.schemaVersion
        version = $releaseMetadata.version
        gitCommit = $gitCommit
        protocolVersion = $releaseMetadata.protocolVersion
        localTransport = "wss"
        certificateProfileVersion = 1
        sqliteSchemaVersion = $releaseMetadata.sqliteSchemaVersion
        capabilities = @($releaseMetadata.capabilities)
        runtimeIdentifier = $releaseMetadata.runtimeIdentifier
        selfContained = $true
        singleFile = $false
        authenticodeSigned = $false
        supportedOperatingSystems = @("Windows 11 x64", "Windows 10 Enterprise LTSC x64")
        files = $fileHashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $forbiddenEndpoint = "ws://localhost:8443"
    $forbiddenCertificateExtensions = @(".pfx", ".p12", ".pem", ".key")
    foreach ($publishedFile in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse) {
        if ($forbiddenCertificateExtensions -contains $publishedFile.Extension.ToLowerInvariant()) {
            throw "shipping_certificate_or_private_key_file_found:$($publishedFile.FullName)"
        }
        $bytes = [System.IO.File]::ReadAllBytes($publishedFile.FullName)
        $utf8Text = [System.Text.Encoding]::UTF8.GetString($bytes)
        $unicodeText = [System.Text.Encoding]::Unicode.GetString($bytes)
        if ($utf8Text.Contains($forbiddenEndpoint, [System.StringComparison]::Ordinal) -or
            $unicodeText.Contains($forbiddenEndpoint, [System.StringComparison]::Ordinal)) {
            throw "shipping_plaintext_websocket_endpoint_found:$($publishedFile.FullName)"
        }
        if ($utf8Text.Contains("-----BEGIN PRIVATE KEY-----", [System.StringComparison]::Ordinal) -or
            $utf8Text.Contains("-----BEGIN RSA PRIVATE KEY-----", [System.StringComparison]::Ordinal) -or
            $utf8Text.Contains("-----BEGIN CERTIFICATE-----", [System.StringComparison]::Ordinal)) {
            throw "shipping_embedded_certificate_material_found:$($publishedFile.FullName)"
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open($artifactPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName) {
                $relative = [System.IO.Path]::GetRelativePath($publishDirectory, $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [System.IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }

    $hash = Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
    Set-Content -LiteralPath "$artifactPath.sha256" `
        -Value "$($hash.Hash.ToLowerInvariant()) *$artifactName" -Encoding ASCII
    Write-Host "P5 release artifact created: $artifactPath"
}
finally {
    Pop-Location
}
