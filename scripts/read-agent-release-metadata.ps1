[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AgentPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedAgentPath = [System.IO.Path]::GetFullPath($AgentPath)
if (-not (Test-Path -LiteralPath $resolvedAgentPath -PathType Leaf)) {
    throw "release_metadata_agent_missing:$resolvedAgentPath"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $resolvedAgentPath
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add("--print-release-metadata")

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw "release_metadata_process_start_failed"
    }
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
}
finally {
    $process.Dispose()
}

if ($exitCode -ne 0) {
    throw "release_metadata_command_failed:exit_code=$exitCode"
}
if (-not [string]::IsNullOrWhiteSpace($standardError)) {
    throw "release_metadata_unexpected_stderr"
}
if ([string]::IsNullOrWhiteSpace($standardOutput)) {
    throw "release_metadata_output_missing"
}

try {
    $metadata = $standardOutput | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "release_metadata_invalid_json"
}

$requiredProperties = @(
    "schemaVersion",
    "version",
    "protocolVersion",
    "sqliteSchemaVersion",
    "runtimeIdentifier",
    "capabilities"
)
$propertyNames = @($metadata.PSObject.Properties.Name)
foreach ($propertyName in $requiredProperties) {
    if ($propertyNames -notcontains $propertyName) {
        throw "release_metadata_property_missing:$propertyName"
    }
}

$metadata
