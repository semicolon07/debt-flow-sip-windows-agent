[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallerPath,
    [Parameter(Mandatory)] [string]$ExpectedVersion,
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "installer_verification_requires_windows"
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw "installer_verification_version_invalid" }
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Split-Path -Parent $installer) "installer-verification"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\DebtFlow\SipAgent"))
$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "DebtFlow\SipAgent"))
$stateKey = "HKCU:\Software\DebtFlow\SipAgent\Installer"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "DebtFlowSipAgent"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{476433C5-E4DF-4CD6-9E42-29F01E4BC339}_is1"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Debt Flow SIP Agent.lnk"

if ((Test-Path -LiteralPath $installRoot) -or
    (Test-Path -LiteralPath $dataRoot) -or
    (Test-Path -LiteralPath $stateKey) -or
    (Test-Path -LiteralPath $uninstallKey)) {
    throw "installer_verification_requires_clean_current_user_profile"
}
$existingRun = Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue
if ($null -ne $existingRun) { throw "installer_verification_requires_absent_agent_run_value" }

function Invoke-Setup([string[]]$Arguments, [string]$Phase) {
    $process = Start-Process -FilePath $installer -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "installer_$($Phase)_failed:exit_code=$($process.ExitCode)" }
}

function Get-UninstallerPath {
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw "installer_uninstall_registration_missing" }
    $uninstall = Get-ItemProperty -LiteralPath $uninstallKey
    $command = [string]$uninstall.UninstallString
    if ($command -notmatch '^"(?<path>[^"]+\.exe)"$') { throw "installer_uninstall_command_invalid" }
    $path = [System.IO.Path]::GetFullPath($Matches.path)
    if (-not $path.StartsWith($installRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "installer_uninstall_command_not_owned"
    }
    return $path
}

function Invoke-Uninstall([string[]]$ExtraArguments, [string]$Phase) {
    $uninstaller = Get-UninstallerPath
    $phaseLog = Join-Path $outputRoot "$Phase.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/LOG=`"$phaseLog`"") + $ExtraArguments
    $process = Start-Process -FilePath $uninstaller -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "installer_$($Phase)_failed:exit_code=$($process.ExitCode)" }
}

$freshLog = Join-Path $outputRoot 'fresh-install.log'
$installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$freshLog`"")
Invoke-Setup $installArguments "fresh_install"

$expectedAgent = Join-Path $installRoot "versions\$ExpectedVersion\DebtFlow.SipAgent.Host.exe"
if (-not (Test-Path -LiteralPath $expectedAgent -PathType Leaf)) { throw "installer_agent_missing" }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $expectedAgent) "release-manifest.json") -PathType Leaf)) {
    throw "installer_release_manifest_missing"
}
$state = Get-ItemProperty -LiteralPath $stateKey
if ([string]$state.InstallRoot -ne $installRoot -or
    [string]$state.InstalledVersion -ne $ExpectedVersion -or
    [string]$state.ActivePath -ne $expectedAgent -or
    [int]$state.InstallerSchema -ne 1) {
    throw "installer_registry_state_mismatch"
}
if (-not (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf)) { throw "installer_start_menu_shortcut_missing" }
if ($null -ne (Get-Process -Name "DebtFlow.SipAgent.Host" -ErrorAction SilentlyContinue)) {
    throw "silent_install_started_agent"
}
$metadata = & (Join-Path $PSScriptRoot "read-agent-release-metadata.ps1") -AgentPath $expectedAgent
$metadata | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath (Join-Path $outputRoot "installed-release-metadata.json") `
    -Encoding UTF8
if ($metadata.version -ne $ExpectedVersion -or $metadata.runtimeIdentifier -ne "win-x64" -or
    $metadata.protocolVersion -ne 1 -or $metadata.sqliteSchemaVersion -ne 4) {
    throw "installed_agent_metadata_mismatch"
}

New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$sentinel = Join-Path $dataRoot "installer-preserve-sentinel.txt"
Set-Content -LiteralPath $sentinel -Value "preserve" -Encoding ASCII
New-Item -Path $runKey -Force | Out-Null
$tamperedRun = "`"$expectedAgent`" --background --unexpected"
New-ItemProperty -LiteralPath $runKey -Name $runName -Value $tamperedRun -PropertyType String -Force | Out-Null

$repairLog = Join-Path $outputRoot 'same-version-repair.log'
$repairArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$repairLog`"")
Invoke-Setup $repairArguments "same_version_repair"
if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw "repair_removed_user_data" }
if ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ne $tamperedRun) {
    throw "repair_overwrote_unowned_startup_value"
}

Invoke-Uninstall @() "normal-uninstall"
if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw "normal_uninstall_removed_user_data" }
if ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ne $tamperedRun) {
    throw "normal_uninstall_removed_unowned_startup_value"
}
if ((Test-Path -LiteralPath $stateKey) -or (Test-Path -LiteralPath $uninstallKey) -or
    (Test-Path -LiteralPath (Join-Path $installRoot "versions"))) {
    throw "normal_uninstall_left_owned_program_state"
}
Remove-ItemProperty -LiteralPath $runKey -Name $runName

$purgeFixtureLog = Join-Path $outputRoot 'purge-fixture-install.log'
$purgeInstallArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$purgeFixtureLog`"")
Invoke-Setup $purgeInstallArguments "purge_fixture_install"
Invoke-Uninstall @('/PURGEDATA=1') "explicit-data-purge-uninstall"
if (Test-Path -LiteralPath $dataRoot) { throw "explicit_data_purge_failed" }

[ordered]@{
    schemaVersion = 1
    version = $ExpectedVersion
    freshSilentInstall = "passed"
    silentLaunchPolicy = "passed"
    installedMetadata = "passed"
    sameVersionRepair = "passed"
    preserveDataDefault = "passed"
    preserveUnownedStartupValue = "passed"
    explicitDataPurge = "passed"
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot "installer-verification-result.json") -Encoding UTF8

Write-Host "Installer verification passed: $ExpectedVersion"
