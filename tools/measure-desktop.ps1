param([Parameter(Mandatory=$true)][string]$Executable, [int]$Samples = 10, [int]$IdleSeconds = 60)
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Executable).Path
$measurements = @()
for ($sample = 0; $sample -lt $Samples; $sample++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $resolved -ArgumentList '--demo' -WindowStyle Hidden -PassThru
    try {
        $ready = $process.WaitForInputIdle(10000)
        $timer.Stop()
        if (-not $ready) { throw 'Window did not reach input idle in ten seconds' }
        $process.Refresh()
        $before = $process.TotalProcessorTime.TotalSeconds
        Start-Sleep -Seconds $IdleSeconds
        $process.Refresh()
        $measurements += [pscustomobject]@{ sample=$sample+1; inputIdleMs=$timer.Elapsed.TotalMilliseconds; privateMiB=$process.PrivateMemorySize64/1MB; cpuPercent=100*($process.TotalProcessorTime.TotalSeconds-$before)/$IdleSeconds/[Environment]::ProcessorCount }
    } finally {
        # Disposable demo processes only: cleanup is process termination, not UI
        # automation and not evidence of graceful shutdown (verified separately).
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
$measurements | ConvertTo-Json
# Demo input-idle is a repeatable proxy, not proof that hardware discovery or rendering is complete.
