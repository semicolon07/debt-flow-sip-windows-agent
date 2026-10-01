[CmdletBinding()]
param(
    [string]$OutputRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$arguments = @("-Phase", "p2")
if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $arguments += @("-OutputRoot", $OutputRoot)
}

& (Join-Path $PSScriptRoot "verify-p1-windows.ps1") @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
