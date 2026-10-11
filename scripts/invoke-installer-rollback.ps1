[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "",
    [string]$TargetVersion = "",
    [string]$LogPath = "",
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "installer_rollback_requires_windows"
}

$installerKey = "HKCU:\Software\DebtFlow\SipAgent\Installer"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "DebtFlowSipAgent"
$canonicalRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\DebtFlow\SipAgent")).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = $canonicalRoot }
$resolvedRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if (-not $resolvedRoot.Equals($canonicalRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "rollback_install_root_not_owned"
}
if (-not (Test-Path -LiteralPath $installerKey)) { throw "rollback_installer_state_missing" }
$state = Get-ItemProperty -LiteralPath $installerKey
if ([string]$state.InstallRoot -ne $resolvedRoot) { throw "rollback_registry_root_mismatch" }

function Assert-OwnedAgentPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = (Join-Path $resolvedRoot "versions").TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($fullPath) -ne "DebtFlow.SipAgent.Host.exe") {
        throw "rollback_agent_path_not_owned:$Path"
    }
    return $fullPath
}

$currentPath = Assert-OwnedAgentPath ([string]$state.ActivePath)
$currentVersion = [string]$state.InstalledVersion
$previousPath = Assert-OwnedAgentPath ([string]$state.PreviousPath)
$previousVersion = [string]$state.PreviousVersion
if (-not [string]::IsNullOrWhiteSpace($TargetVersion) -and $TargetVersion -ne $previousVersion) {
    throw "rollback_target_must_equal_retained_previous_version"
}
if ($currentVersion -notmatch '^\d+\.\d+\.\d+$' -or $previousVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "rollback_version_invalid"
}
if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $previousPath -PathType Leaf)) {
    throw "rollback_agent_binary_missing"
}

function Read-VerifiedManifest([string]$AgentPath, [string]$ExpectedVersion) {
    $directory = Split-Path -Parent $AgentPath
    $manifestPath = Join-Path $directory "release-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "rollback_manifest_missing:$ExpectedVersion" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $ExpectedVersion -or $manifest.runtimeIdentifier -ne "win-x64") {
        throw "rollback_manifest_identity_mismatch:$ExpectedVersion"
    }
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative.Contains("..", [System.StringComparison]::Ordinal)) {
            throw "rollback_manifest_path_invalid:$ExpectedVersion"
        }
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $directory $relative))
        if (-not $filePath.StartsWith($directory.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "rollback_manifest_file_missing:$ExpectedVersion"
        }
        $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne [string]$entry.sha256 -or (Get-Item -LiteralPath $filePath).Length -ne [long]$entry.bytes) {
            throw "rollback_manifest_file_mismatch:$ExpectedVersion"
        }
    }
    return $manifest
}

$currentManifest = Read-VerifiedManifest $currentPath $currentVersion
$previousManifest = Read-VerifiedManifest $previousPath $previousVersion
function Read-AgentMetadata([string]$AgentPath, [string]$ExpectedVersion) {
    $json = & $AgentPath --print-release-metadata
    if ($LASTEXITCODE -ne 0) { throw "rollback_release_metadata_command_failed:$ExpectedVersion" }
    $metadata = $json | ConvertFrom-Json
    if ($metadata.version -ne $ExpectedVersion -or $metadata.runtimeIdentifier -ne "win-x64") {
        throw "rollback_release_metadata_identity_mismatch:$ExpectedVersion"
    }
    return $metadata
}
$currentMetadata = Read-AgentMetadata $currentPath $currentVersion
$previousMetadata = Read-AgentMetadata $previousPath $previousVersion
if ($currentMetadata.protocolVersion -ne $currentManifest.protocolVersion -or
    $currentMetadata.sqliteSchemaVersion -ne $currentManifest.sqliteSchemaVersion -or
    $previousMetadata.protocolVersion -ne $previousManifest.protocolVersion -or
    $previousMetadata.sqliteSchemaVersion -ne $previousManifest.sqliteSchemaVersion) {
    throw "rollback_release_metadata_manifest_mismatch"
}
if ($currentManifest.protocolVersion -ne $previousManifest.protocolVersion -or
    $currentManifest.sqliteSchemaVersion -ne $previousManifest.sqliteSchemaVersion) {
    throw "rollback_protocol_or_schema_incompatible"
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $resolvedRoot "rollback-$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssZ')).log"
}
$resolvedLog = [System.IO.Path]::GetFullPath($LogPath)
if (-not $resolvedLog.StartsWith($resolvedRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "rollback_log_path_not_owned"
}
function Write-RollbackLog([string]$Event, [string]$Detail) {
    Add-Content -LiteralPath $resolvedLog -Encoding UTF8 -Value (
        "$([DateTimeOffset]::UtcNow.ToString('O')) event=$Event $Detail")
}

if (-not $PSCmdlet.ShouldProcess("Debt Flow SIP Agent $currentVersion", "activate retained version $previousVersion")) {
    return
}

Write-RollbackLog "started" "from=$currentVersion to=$previousVersion"
& $currentPath --prepare-update
$prepareExit = $LASTEXITCODE
Write-RollbackLog "prepare_update" "exit_code=$prepareExit"
if ($prepareExit -ne 0) { throw "rollback_prepare_update_failed:exit_code=$prepareExit" }

& $previousPath --storage-preflight
$preflightExit = $LASTEXITCODE
Write-RollbackLog "storage_preflight" "exit_code=$preflightExit"
if ($preflightExit -ne 0) { throw "rollback_storage_preflight_failed:exit_code=$preflightExit" }

$oldValues = @{
    InstalledVersion = [string]$state.InstalledVersion
    ActivePath = [string]$state.ActivePath
    PreviousVersion = [string]$state.PreviousVersion
    PreviousPath = [string]$state.PreviousPath
}
try {
    Set-ItemProperty -LiteralPath $installerKey -Name PreviousVersion -Value $currentVersion
    Set-ItemProperty -LiteralPath $installerKey -Name PreviousPath -Value $currentPath
    Set-ItemProperty -LiteralPath $installerKey -Name InstalledVersion -Value $previousVersion
    Set-ItemProperty -LiteralPath $installerKey -Name ActivePath -Value $previousPath

    $runProperties = Get-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction SilentlyContinue
    $runValue = if ($null -eq $runProperties) { $null } else { $runProperties.$runValueName }
    if ($runValue -is [string] -and $runValue -match '^"(?<exe>[^"]+)" --background$') {
        try {
            $ownedRunPath = Assert-OwnedAgentPath $Matches.exe
            Set-ItemProperty -LiteralPath $runKey -Name $runValueName -Value "`"$previousPath`" --background"
        }
        catch {
            Write-RollbackLog "startup_preserved" "reason=not_owned"
        }
    }

    $shortcutPaths = @(
        (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Debt Flow SIP Agent.lnk"),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "Debt Flow SIP Agent.lnk")
    )
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutPath in $shortcutPaths) {
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $previousPath
            $shortcut.WorkingDirectory = Split-Path -Parent $previousPath
            $shortcut.Save()
        }
    }
}
catch {
    foreach ($name in $oldValues.Keys) {
        Set-ItemProperty -LiteralPath $installerKey -Name $name -Value $oldValues[$name]
    }
    Write-RollbackLog "activation_failed" "registry_restored=true"
    throw
}

Write-RollbackLog "completed" "active=$previousVersion retained_previous=$currentVersion"
if (-not $NoLaunch) { Start-Process -FilePath $previousPath -WorkingDirectory (Split-Path -Parent $previousPath) }
Write-Host "Rollback completed: $currentVersion -> $previousVersion"
