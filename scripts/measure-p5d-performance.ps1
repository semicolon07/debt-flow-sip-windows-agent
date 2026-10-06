[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId,

    [ValidateRange(10, 86400)]
    [int]$DurationSeconds = 900,

    [ValidateRange(250, 60000)]
    [int]$SampleIntervalMilliseconds = 1000,

    [string]$OutputDirectory = (Join-Path (Get-Location) "artifacts\p5d-performance"),

    [switch]$CollectRuntimeCounters
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values,
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    [double[]]$ordered = $Values | Sort-Object
    $index = [Math]::Min($ordered.Count - 1, [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1))
    return $ordered[$index]
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss", [Globalization.CultureInfo]::InvariantCulture)
$csvPath = Join-Path $OutputDirectory "sip-agent-$stamp-samples.csv"
$summaryPath = Join-Path $OutputDirectory "sip-agent-$stamp-summary.json"
$runtimeCountersPath = Join-Path $OutputDirectory "sip-agent-$stamp-runtime-counters.csv"

$target = Get-Process -Id $ProcessId
$target.Refresh()
$processName = $target.ProcessName
$initialWorkingSetBytes = $target.WorkingSet64
$initialPrivateMemoryBytes = $target.PrivateMemorySize64
$initialHandleCount = $target.HandleCount
$initialThreadCount = $target.Threads.Count
$previousCpuMs = $target.TotalProcessorTime.TotalMilliseconds
$previousElapsedMs = 0.0
$processorCount = [Math]::Max(1, [Environment]::ProcessorCount)
$samples = [Collections.Generic.List[object]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
$processExited = $false
$runtimeCounterJob = $null

if ($CollectRuntimeCounters) {
    $dotnetCounters = Get-Command "dotnet-counters" -CommandType Application -ErrorAction Stop
    $counterDuration = [TimeSpan]::FromSeconds($DurationSeconds).ToString("c", [Globalization.CultureInfo]::InvariantCulture)
    $runtimeCounterJob = Start-Job -ScriptBlock {
        param($ToolPath, $TargetProcessId, $OutputPath, $Duration)
        & $ToolPath collect `
            --process-id $TargetProcessId `
            --counters "System.Runtime,DebtFlow.SipAgent" `
            --format csv `
            --output $OutputPath `
            --duration $Duration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet-counters exited with code $LASTEXITCODE"
        }
    } -ArgumentList $dotnetCounters.Source, $ProcessId, $runtimeCountersPath, $counterDuration
}

while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
    Start-Sleep -Milliseconds $SampleIntervalMilliseconds
    try {
        $target.Refresh()
        if ($target.HasExited) {
            $processExited = $true
            break
        }
    }
    catch [InvalidOperationException] {
        $processExited = $true
        break
    }

    $elapsedMs = $timer.Elapsed.TotalMilliseconds
    $cpuMs = $target.TotalProcessorTime.TotalMilliseconds
    $intervalMs = [Math]::Max(1, $elapsedMs - $previousElapsedMs)
    $cpuPercent = [Math]::Max(0, (($cpuMs - $previousCpuMs) / $intervalMs) * 100 / $processorCount)
    $samples.Add([pscustomobject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
        ElapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
        CpuPercent = [Math]::Round($cpuPercent, 3)
        WorkingSetBytes = $target.WorkingSet64
        PrivateMemoryBytes = $target.PrivateMemorySize64
        HandleCount = $target.HandleCount
        ThreadCount = $target.Threads.Count
    })
    $previousCpuMs = $cpuMs
    $previousElapsedMs = $elapsedMs
}

$samples | Export-Csv -NoTypeInformation -Encoding utf8 -Path $csvPath
[double[]]$cpuValues = @($samples | ForEach-Object { [double]$_.CpuPercent })
[double[]]$workingSetValues = @($samples | ForEach-Object { [double]$_.WorkingSetBytes })
[double[]]$privateMemoryValues = @($samples | ForEach-Object { [double]$_.PrivateMemoryBytes })
[double[]]$handleValues = @($samples | ForEach-Object { [double]$_.HandleCount })
[double[]]$threadValues = @($samples | ForEach-Object { [double]$_.ThreadCount })
$runtimeCounterCollectionSucceeded = $null
if ($null -ne $runtimeCounterJob) {
    $runtimeCounterJob | Wait-Job -Timeout 30 | Out-Null
    if ($runtimeCounterJob.State -eq "Running") {
        $runtimeCounterJob | Stop-Job
    }
    $runtimeCounterCollectionSucceeded =
        $runtimeCounterJob.State -eq "Completed" -and (Test-Path -LiteralPath $runtimeCountersPath)
    $runtimeCounterJob | Receive-Job -ErrorAction SilentlyContinue -WarningAction SilentlyContinue | Out-Null
    $runtimeCounterJob | Remove-Job -Force
}
$lastSample = if ($samples.Count -eq 0) { $null } else { $samples[$samples.Count - 1] }

$summary = [ordered]@{
    schemaVersion = 2
    processId = $ProcessId
    processName = $processName
    startedAtUtc = [DateTime]::UtcNow.Subtract($timer.Elapsed).ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    endedAtUtc = [DateTime]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    requestedDurationSeconds = $DurationSeconds
    observedDurationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
    sampleIntervalMilliseconds = $SampleIntervalMilliseconds
    sampleCount = $samples.Count
    processExited = $processExited
    cpuPercent = [ordered]@{
        average = if ($samples.Count -eq 0) { 0 } else { [Math]::Round(($cpuValues | Measure-Object -Average).Average, 3) }
        p95 = [Math]::Round((Get-Percentile -Values $cpuValues -Percentile 0.95), 3)
        maximum = if ($samples.Count -eq 0) { 0 } else { [Math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 3) }
    }
    workingSetBytes = [ordered]@{
        p95 = [long](Get-Percentile -Values $workingSetValues -Percentile 0.95)
        maximum = if ($samples.Count -eq 0) { 0 } else { [long](($workingSetValues | Measure-Object -Maximum).Maximum) }
    }
    privateMemoryBytes = [ordered]@{
        p95 = [long](Get-Percentile -Values $privateMemoryValues -Percentile 0.95)
        maximum = if ($samples.Count -eq 0) { 0 } else { [long](($privateMemoryValues | Measure-Object -Maximum).Maximum) }
    }
    handles = [ordered]@{
        p95 = [long](Get-Percentile -Values $handleValues -Percentile 0.95)
        maximum = if ($samples.Count -eq 0) { 0 } else { [long](($handleValues | Measure-Object -Maximum).Maximum) }
        change = if ($null -eq $lastSample) { 0 } else { [long]$lastSample.HandleCount - $initialHandleCount }
    }
    threads = [ordered]@{
        p95 = [long](Get-Percentile -Values $threadValues -Percentile 0.95)
        maximum = if ($samples.Count -eq 0) { 0 } else { [long](($threadValues | Measure-Object -Maximum).Maximum) }
        change = if ($null -eq $lastSample) { 0 } else { [long]$lastSample.ThreadCount - $initialThreadCount }
    }
    growthBytes = [ordered]@{
        workingSet = if ($null -eq $lastSample) { 0 } else { [long]$lastSample.WorkingSetBytes - $initialWorkingSetBytes }
        privateMemory = if ($null -eq $lastSample) { 0 } else { [long]$lastSample.PrivateMemoryBytes - $initialPrivateMemoryBytes }
    }
    samplesFile = [IO.Path]::GetFileName($csvPath)
    runtimeCountersFile = if ($CollectRuntimeCounters) { [IO.Path]::GetFileName($runtimeCountersPath) } else { $null }
    runtimeCounterCollectionSucceeded = $runtimeCounterCollectionSucceeded
}

$summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -Path $summaryPath
Write-Output $summaryPath
