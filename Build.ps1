[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot 'src\SafeCodexQuotaWidget.cs'
$wpfSourcePath = Join-Path $projectRoot 'src\SafeCodexQuotaWidgetWpf.cs'
$launcherSourcePath = Join-Path $projectRoot 'src\OpenCodexWithQuota.cs'
$outputDirectory = Join-Path $projectRoot 'bin'
$outputPath = Join-Path $outputDirectory 'SafeCodexQuotaWidget.exe'
$probePath = Join-Path $outputDirectory 'CodexQuotaProbe.exe'
$launcherPath = Join-Path $outputDirectory 'OpenCodexWithQuota.exe'
$iconPath = Join-Path $projectRoot 'assets\SafeCodexQuotaWidget.ico'
$iconGenerator = Join-Path $projectRoot 'tools\GenerateIcon.ps1'

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'Windows .NET Framework compiler csc.exe was not found.'
}

$windowsBase = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\WindowsBase') -Recurse -Filter WindowsBase.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$presentationFramework = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\PresentationFramework') -Recurse -Filter PresentationFramework.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$systemXaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$presentationCoreRoot = if ($compiler -like '*Framework64*') { 'Microsoft.NET\assembly\GAC_64\PresentationCore' } else { 'Microsoft.NET\assembly\GAC_32\PresentationCore' }
$presentationCore = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR $presentationCoreRoot) -Recurse -Filter PresentationCore.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $windowsBase -or -not $presentationFramework -or -not $presentationCore -or -not $systemXaml) {
    throw 'Windows WPF framework assemblies were not found.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $iconPath)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $iconGenerator
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed. Exit code: $LASTEXITCODE" }
}

function Invoke-Compile([string]$target, [string]$destination) {
    & $compiler `
        /nologo `
        "/target:$target" `
        /optimize+ `
        /platform:anycpu `
        /codepage:65001 `
        "/out:$destination" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Web.Extensions.dll `
        /reference:System.Windows.Forms.dll `
        $sourcePath

    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed. csc.exe exit code: $LASTEXITCODE"
    }
}

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    /main:SafeCodexQuotaWidget.WpfProgram `
    "/win32icon:$iconPath" `
    "/out:$outputPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    "/reference:$windowsBase" `
    "/reference:$presentationCore" `
    "/reference:$presentationFramework" `
    "/reference:$systemXaml" `
    $sourcePath `
    $wpfSourcePath

if ($LASTEXITCODE -ne 0) {
    throw "WPF widget compilation failed. csc.exe exit code: $LASTEXITCODE"
}

Invoke-Compile -target 'exe' -destination $probePath

& $compiler `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    "/out:$launcherPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Windows.Forms.dll `
    $launcherSourcePath

if ($LASTEXITCODE -ne 0) {
    throw "Launcher compilation failed. csc.exe exit code: $LASTEXITCODE"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath
Write-Host "Built: $outputPath"
Write-Host "SHA-256: $($hash.Hash)"
Write-Host "Launcher: $launcherPath"
