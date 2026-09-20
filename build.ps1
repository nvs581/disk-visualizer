param([switch]$Run, [switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path $compiler)) { throw 'The Windows .NET Framework 4.x compiler is required.' }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Windows.Forms.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', ('/out:' + (Join-Path $output 'DiskVisualizer.exe')), ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')))
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $framework $reference) }
$arguments += Get-ChildItem (Join-Path $PSScriptRoot 'src\*.cs') | ForEach-Object { $_.FullName }
$arguments += '/resource:' + (Join-Path $PSScriptRoot 'src\Main.xaml') + ',Main.xaml'
$arguments += '/win32icon:' + (Join-Path $PSScriptRoot 'assets\disk-visualizer.ico')
$arguments += '/resource:' + (Join-Path $PSScriptRoot 'assets\disk-visualizer.ico') + ',App.ico'
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item (Join-Path $PSScriptRoot 'src\DiskVisualizer.exe.config') $output -Force
Write-Host "Built $output\DiskVisualizer.exe"
if ($Test) {
    $report = Join-Path $output 'test-results.txt'
    $process = Start-Process (Join-Path $output 'DiskVisualizer.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
    if (Test-Path $report) { Get-Content $report }
    if ($process.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
if ($Run) { Start-Process (Join-Path $output 'DiskVisualizer.exe') }
