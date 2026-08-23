[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appPath = Join-Path $projectRoot 'bin\CodexQuotaProbe.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    throw 'Not built. Run Build.ps1 first.'
}

& $appPath --probe
if ($LASTEXITCODE -ne 0) {
    throw "Quota probe failed. Exit code: $LASTEXITCODE"
}
