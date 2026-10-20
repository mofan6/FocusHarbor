$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$outputRoot = Join-Path $projectRoot 'dist'
$assetRoot = Join-Path $projectRoot 'assets'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$outputPath = Join-Path $outputRoot 'FocusHarbor.exe'
$iconPath = Join-Path $assetRoot 'FocusHarbor.ico'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler was not found: $compiler"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    $iconGenerator = Join-Path $outputRoot 'IconGenerator.exe'
    $drawingReference = '/reference:' + (Join-Path $frameworkRoot 'System.Drawing.dll')
    $iconOutput = '/out:' + $iconGenerator
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 $drawingReference $iconOutput (Join-Path $projectRoot 'tools\IconGenerator.cs')
    if ($LASTEXITCODE -ne 0) {
        throw 'Icon generator compilation failed.'
    }
    & $iconGenerator $iconPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw 'Icon generation failed.'
    }
}

$frameworkReferences = @(
    'System.dll',
    'System.Core.dll',
    'System.Xaml.dll',
    'System.Web.Extensions.dll',
    'System.Management.dll',
    'System.Windows.Forms.dll',
    'Microsoft.CSharp.dll'
) | ForEach-Object { Join-Path $frameworkRoot $_ }

$wpfReferences = @(
    'WindowsBase.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll'
) | ForEach-Object { Join-Path (Join-Path $frameworkRoot 'WPF') $_ }

$references = @($frameworkReferences) + @($wpfReferences)

$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName }
if ($sources.Count -lt 6) {
    throw "Expected at least six C# source files, found $($sources.Count)."
}

$arguments = New-Object 'System.Collections.Generic.List[string]'
$arguments.Add('/nologo')
$arguments.Add('/target:winexe')
$arguments.Add('/platform:x64')
$arguments.Add('/optimize+')
$arguments.Add('/codepage:65001')
$arguments.Add('/warn:4')
$arguments.Add('/win32icon:' + $iconPath)
$arguments.Add('/win32manifest:' + (Join-Path $projectRoot 'app.manifest'))
$arguments.Add('/resource:' + (Join-Path $sourceRoot 'MainWindow.xaml') + ',FocusHarbor.MainWindow.xaml')
$arguments.Add('/resource:' + (Join-Path $assetRoot 'UserGuide.md') + ',FocusHarbor.UserGuide.md')
$arguments.Add('/out:' + $outputPath)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "Required framework assembly was not found: $reference"
    }
    $arguments.Add('/reference:' + $reference)
}
foreach ($source in $sources) {
    $arguments.Add($source)
}

& $compiler $arguments.ToArray()
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw 'Compilation reported success but the EXE was not created.'
}

Get-Item -LiteralPath $outputPath | Select-Object FullName, Length, LastWriteTime
