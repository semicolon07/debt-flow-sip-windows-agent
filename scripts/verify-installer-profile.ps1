[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "installer_profile_verification_requires_windows"
}

$installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\DebtFlow\SipAgent"))
$dataRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "DebtFlow\SipAgent"))
$stateKey = "HKCU:\Software\DebtFlow\SipAgent\Installer"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "DebtFlowSipAgent"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{476433C5-E4DF-4CD6-9E42-29F01E4BC339}_is1"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Debt Flow SIP Agent.lnk"

$conflicts = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $installRoot) { $conflicts.Add("install_root") }
if (Test-Path -LiteralPath $dataRoot) { $conflicts.Add("data_root") }
if (Test-Path -LiteralPath $stateKey) { $conflicts.Add("installer_state_registry") }
if (Test-Path -LiteralPath $uninstallKey) { $conflicts.Add("uninstall_registry") }
if (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf) { $conflicts.Add("start_menu_shortcut") }
$existingRun = Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue
if ($null -ne $existingRun) { $conflicts.Add("startup_run_value") }
if ($null -ne (Get-Process -Name "DebtFlow.SipAgent.Host" -ErrorAction SilentlyContinue)) {
    $conflicts.Add("running_agent_process")
}

$evidence = [ordered]@{
    schemaVersion = 1
    clean = $conflicts.Count -eq 0
    conflictIds = @($conflicts)
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$evidencePath = ""
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $evidencePath = Join-Path $outputRoot "installer-profile-verification.json"
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
}

if ($conflicts.Count -gt 0) {
    $conflictList = [string]::Join(",", $conflicts)
    $evidenceSuffix = if ([string]::IsNullOrWhiteSpace($evidencePath)) { "" } else { ":evidence=$evidencePath" }
    throw "installer_verification_requires_clean_current_user_profile:conflicts=$conflictList$evidenceSuffix"
}

Write-Host "Installer verification profile is clean."
