param([Parameter(Mandatory=$true)][string]$Dataset, [int]$Repetitions = 5, [switch]$Duplicates, [string]$ReportName = 'benchmark')
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'bin\DiskVisualizer.exe'
$baseline = Join-Path $PSScriptRoot 'bin\baseline\DiskVisualizer.exe'
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$reportPath = Join-Path $PSScriptRoot ('bin\' + [IO.Path]::GetFileName($ReportName) + '.csv')
$tempResult = Join-Path $PSScriptRoot 'bin\benchmark-run.txt'
$modes = @('traversal', 'allocation')
if (Test-Path $baseline) { $modes = @('baseline') + $modes }
if ($Duplicates) { $modes += 'duplicates' }
$rows = @('Mode,Entries,FileBytes,Errors,ElapsedMs,CpuMs,FinalPrivateBytes,PeakWorkingSet,FirstProgressMs,AllocationMs,DuplicatesMs,UnknownAllocation,DuplicateGroups')
for ($run = 0; $run -le $Repetitions; $run++) {
    $order = @($modes)
    if ($run % 2 -eq 0) { [array]::Reverse($order) }
    foreach ($mode in $order) {
        $arguments = '--benchmark ' + $mode + ' "' + $datasetPath + '" "' + $tempResult + '" "' + $baseline + '"'
        $process = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw (Get-Content -Raw $tempResult) }
        if ($run -gt 0) { $rows += (Get-Content -Raw $tempResult) }
    }
    $rows | Set-Content -Encoding UTF8 $reportPath
    Write-Host "Completed repetition $run (0 = warmup)"
}
Write-Host "Saved $reportPath"
Import-Csv -LiteralPath $reportPath | Group-Object Mode | ForEach-Object {
    $times = @($_.Group | ForEach-Object { [double]::Parse($_.ElapsedMs, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
    [pscustomobject]@{
        Mode = $_.Name
        Runs = $times.Count
        MedianMs = [math]::Round(($times[[int][math]::Floor(($times.Count - 1) / 2)] + $times[[int][math]::Floor($times.Count / 2)]) / 2, 3)
        MinMs = $times[0]
        MaxMs = $times[$times.Count - 1]
    }
} | Format-Table -AutoSize
