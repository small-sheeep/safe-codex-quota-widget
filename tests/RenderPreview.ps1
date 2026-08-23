[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'tests\RenderPreview.cs'
$harness = Join-Path $projectRoot 'bin\RenderPreview.exe'
$widget = Join-Path $projectRoot 'bin\SafeCodexQuotaWidget.exe'
$preview = Join-Path $projectRoot 'preview.png'
$windowsBase = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\WindowsBase') -Recurse -Filter WindowsBase.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$presentationFramework = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\PresentationFramework') -Recurse -Filter PresentationFramework.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$presentationCore = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_64\PresentationCore') -Recurse -Filter PresentationCore.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$systemXaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName

& $compiler /nologo /target:exe /optimize+ /platform:anycpu "/out:$harness" /reference:System.dll "/reference:$windowsBase" "/reference:$presentationCore" "/reference:$presentationFramework" "/reference:$systemXaml" $source
if ($LASTEXITCODE -ne 0) { throw "Preview harness compile failed: $LASTEXITCODE" }

& $harness $widget $preview
if ($LASTEXITCODE -ne 0) { throw "Preview render failed: $LASTEXITCODE" }
Write-Host "Preview: $preview"
