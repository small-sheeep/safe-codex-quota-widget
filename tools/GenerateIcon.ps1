[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$assets = Join-Path $projectRoot 'assets'
$generator = Join-Path $projectRoot 'bin\GenerateIcon.exe'
$source = Join-Path $projectRoot 'tools\GenerateIcon.cs'
$icon = Join-Path $assets 'SafeCodexQuotaWidget.ico'
$preview = Join-Path $assets 'SafeCodexQuotaWidget-icon-preview.png'

New-Item -ItemType Directory -Path $assets -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $generator) -Force | Out-Null

& $compiler /nologo /target:exe /optimize+ /platform:anycpu /codepage:65001 "/out:$generator" /reference:System.dll /reference:System.Drawing.dll $source
if ($LASTEXITCODE -ne 0) { throw "Icon generator compilation failed: $LASTEXITCODE" }

& $generator $icon $preview
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed: $LASTEXITCODE" }

Write-Host "Icon: $icon"
Write-Host "Preview: $preview"
