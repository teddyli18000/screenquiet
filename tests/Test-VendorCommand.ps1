param([string]$BridgePath = (Join-Path $PSScriptRoot '..\LauncherBridge.exe'))
$ErrorActionPreference = 'Stop'
$sqTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sqTestDir = Join-Path $sqTempRoot ('ScreenQuiet-Bridge-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sqTestDir
$sqBridge = (Resolve-Path -LiteralPath $BridgePath).Path
$sqLauncher = Join-Path $sqTestDir 'LenovoBaiyingLauncher.exe'
$sqOther = Join-Path $sqTestDir 'OtherLauncher.exe'
function Invoke-SqBridgeProbe([string]$sqTarget, [string]$sqArguments, [string]$sqResult, [bool]$sqSuppressed) {
    if (Test-Path -LiteralPath $sqResult) { Remove-Item -LiteralPath $sqResult }
    $sqCommand = '"' + $sqTarget + '"'
    if ($sqArguments) { $sqCommand += ' ' + $sqArguments }
    $sqProcess = Start-Process -FilePath $sqBridge -ArgumentList $sqCommand -WorkingDirectory $sqTestDir -WindowStyle Hidden -PassThru
    if (-not $sqProcess.WaitForExit(7000) -or $sqProcess.ExitCode -ne 0) { throw 'Bridge probe did not exit successfully.' }
    $sqUntil = [DateTime]::UtcNow.AddSeconds(2)
    while (-not (Test-Path -LiteralPath $sqResult) -and [DateTime]::UtcNow -lt $sqUntil) {
        if ($sqSuppressed) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($sqSuppressed) {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $sqResult) { throw 'Dedicated customer-service command reached target code.' }
    } else {
        if (-not (Test-Path -LiteralPath $sqResult)) { throw 'Ordinary launch was suppressed.' }
        if ((Get-Content -LiteralPath $sqResult -First 1) -ne 'Debugger=False') { throw 'Target retained debugger state.' }
    }
}
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ScreenQuietBridgeProbe.exe') -Destination $sqLauncher
    Copy-Item -LiteralPath $sqLauncher -Destination $sqOther
    $sqMarker = Join-Path $sqTestDir '-customerService'
    Invoke-SqBridgeProbe $sqLauncher '-customerService' $sqMarker $true
    Invoke-SqBridgeProbe $sqLauncher '-CUSTOMERSERVICE' (Join-Path $sqTestDir '-CUSTOMERSERVICE') $true
    Invoke-SqBridgeProbe $sqOther '-customerService' $sqMarker $false
    Invoke-SqBridgeProbe $sqLauncher '-customerService extra' $sqMarker $false
    if ((Get-Content -LiteralPath $sqMarker)[1] -ne 'ZXh0cmE=') { throw 'Extra argument changed during forwarding.' }
    Invoke-SqBridgeProbe $sqLauncher 'StartUp' (Join-Path $sqTestDir 'StartUp') $false
    Invoke-SqBridgeProbe $sqLauncher '' (Join-Path $sqTestDir 'no-arguments.txt') $false
    $sqForward = Join-Path $sqTestDir 'forwarded args.txt'
    Invoke-SqBridgeProbe $sqLauncher ('"' + $sqForward + '" "space value" "quote\"value" "trailing\\"') $sqForward $false
    $sqLines = @(Get-Content -LiteralPath $sqForward)
    if ($sqLines.Count -ne 4 -or $sqLines[1] -ne 'c3BhY2UgdmFsdWU=' -or $sqLines[2] -ne 'cXVvdGUidmFsdWU=' -or $sqLines[3] -ne 'dHJhaWxpbmdc') { throw 'Forwarded arguments changed.' }
    'PASS: dedicated command suppressed without a WMI gate; ordinary, StartUp, no-argument, other-image and extra-argument launches forwarded without debugger state.'
} finally {
    $sqResolved = [IO.Path]::GetFullPath($sqTestDir)
    if (-not $sqResolved.StartsWith($sqTempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($sqResolved) -notlike 'ScreenQuiet-Bridge-*') { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $sqResolved -Recurse -Force
}
