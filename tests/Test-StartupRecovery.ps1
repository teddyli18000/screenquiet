# Explicit installed-system test. Only terminates an idle, owned controller.
# Does not press a key, send display IOCTLs, or apply a display configuration.
$ErrorActionPreference = 'Stop'
$sqInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ScreenQuiet'
$sqExecutable = Join-Path $sqInstallDir 'ScreenQuiet.exe'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'StartupTask.ps1')
$sqTask = Get-SqStartupTask
if (-not $sqTask -or $sqTask.State -ne 'Running') { throw 'Installed recovery task must be running.' }
$sqRecovery = Join-Path $env:LOCALAPPDATA 'ScreenQuiet\display-recovery.xml'
if (Test-Path -LiteralPath $sqRecovery) { throw 'Display recovery snapshot present; do not terminate controller.' }
$sqControllers = @(Get-Process ScreenQuiet | Where-Object Path -eq $sqExecutable)
if ($sqControllers.Count -ne 1) { throw 'Expected exactly one idle installed controller.' }
$sqBefore = $sqControllers[0]
$sqBegan = [DateTime]::UtcNow
Stop-Process -Id $sqBefore.Id -Force
$sqDeadline = $sqBegan.AddSeconds(95)
$sqAfter = $null
while ([DateTime]::UtcNow -lt $sqDeadline) {
    Start-Sleep -Seconds 2
    $sqAfter = Get-Process ScreenQuiet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $sqExecutable -and $_.Id -ne $sqBefore.Id }
    if ($sqAfter) { break }
}
if (-not $sqAfter) { throw 'Automatic recovery did not occur within 95 seconds.' }
$sqElapsed = ([DateTime]::UtcNow-$sqBegan).TotalSeconds
Start-ScheduledTask -TaskName $sqTaskName
Start-Sleep -Seconds 3
$sqControllers = @(Get-Process ScreenQuiet | Where-Object Path -eq $sqExecutable)
if ($sqControllers.Count -ne 1 -or $sqControllers[0].Id -ne $sqAfter.Id) { throw 'Repeated task start did not preserve a single controller.' }
$sqAfter.Refresh()
$sqCpuBefore = $sqAfter.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 10
$sqAfter.Refresh()
if ($sqAfter.HasExited) { throw 'Recovered controller exited during idle sample.' }
[pscustomobject]@{
    RecoverySeconds = [Math]::Round($sqElapsed,1)
    IdleSampleSeconds = 10
    CpuMilliseconds = $sqAfter.TotalProcessorTime.TotalMilliseconds-$sqCpuBefore
    WorkingSetMiB = [Math]::Round($sqAfter.WorkingSet64/1MB,1)
    MainWindowHandle = $sqAfter.MainWindowHandle.ToInt64()
    SingleInstance = $true
    RecoveryPending = (Test-Path -LiteralPath $sqRecovery)
} | ConvertTo-Json
