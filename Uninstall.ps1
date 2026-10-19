$ErrorActionPreference = 'Stop'
$sqInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ScreenQuiet'
$sqExecutable = Join-Path $sqInstallDir 'ScreenQuiet.exe'
$sqRecoveryFile = Join-Path $env:LOCALAPPDATA 'ScreenQuiet\display-recovery.xml'
$sqVendorRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$sqHasVendorRule = @('LenovoBaiying.exe','LenovoBaiyingLauncher.exe') | Where-Object { Test-Path -LiteralPath (Join-Path $sqVendorRoot ($_ + '\ScreenQuietFnF9')) }
if ($sqHasVendorRule) {
    $sqCleanupScript = Join-Path $PSScriptRoot 'Configure-VendorRoute.ps1'
    $sqCleanupArgs = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $sqCleanupScript + '" -Mode Remove'
    $sqCleanup = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $sqCleanupArgs -Verb RunAs -PassThru -Wait -WindowStyle Hidden
    if ($sqCleanup.ExitCode -ne 0) { throw 'Vendor launch rule cleanup did not complete; installation retained.' }
}
if (Test-Path -LiteralPath $sqExecutable) {
    Start-Process -FilePath $sqExecutable -ArgumentList '--stop' -Wait -WindowStyle Hidden
    $sqDeadline = [DateTime]::UtcNow.AddSeconds(7)
    while ((Test-Path -LiteralPath $sqRecoveryFile) -and [DateTime]::UtcNow -lt $sqDeadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $sqRecoveryFile) { throw 'Display recovery is pending. Controller and files were retained.' }
}
foreach ($sqExisting in @(Get-Process -Name ScreenQuiet -ErrorAction SilentlyContinue)) {
    if ($sqExisting.Path -eq $sqExecutable) { Stop-Process -Id $sqExisting.Id -ErrorAction SilentlyContinue }
}
$sqRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$sqCurrent = (Get-ItemProperty -LiteralPath $sqRunKey -ErrorAction SilentlyContinue).ScreenQuiet
if ($sqCurrent -eq ('"' + $sqExecutable + '"')) { Remove-ItemProperty -LiteralPath $sqRunKey -Name ScreenQuiet }
$sqShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ScreenQuiet - Restore screen.lnk'
if (Test-Path -LiteralPath $sqShortcut) { Remove-Item -LiteralPath $sqShortcut }
foreach ($sqName in @('ScreenQuiet.exe', 'THIRD-PARTY-NOTICES.txt', 'Uninstall.ps1', 'Configure-VendorRoute.ps1')) {
    $sqFile = Join-Path $sqInstallDir $sqName
    if (Test-Path -LiteralPath $sqFile) { Remove-Item -LiteralPath $sqFile }
}
if ((Test-Path -LiteralPath $sqInstallDir) -and @(Get-ChildItem -LiteralPath $sqInstallDir -Force).Count -eq 0) { Remove-Item -LiteralPath $sqInstallDir }
'ScreenQuiet removed. The separately installed Parsec Virtual Display Driver remains available for uninstall through Windows Settings > Apps.'
