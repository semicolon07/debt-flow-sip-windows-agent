[CmdletBinding()]
param(
    [string]$OutputRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$verificationParameters = @{
    Phase = "p2"
}
if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $verificationParameters.OutputRoot = $OutputRoot
}

& (Join-Path $PSScriptRoot "verify-p1-windows.ps1") @verificationParameters
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
