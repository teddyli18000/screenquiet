$ErrorActionPreference = 'Stop'
$sqInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ScreenQuiet'
$sqExecutable = Join-Path $sqInstallDir 'ScreenQuiet.exe'
$sqRecoveryFile = Join-Path $env:LOCALAPPDATA 'ScreenQuiet\display-recovery.xml'
$sqRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$sqCurrent = (Get-ItemProperty -LiteralPath $sqRunKey -ErrorAction SilentlyContinue).ScreenQuiet
if ($sqCurrent -and $sqCurrent -ne ('"' + $sqExecutable + '"')) { throw 'ScreenQuiet startup entry is owned by another installation.' }
foreach ($sqExisting in @(Get-Process -Name ScreenQuiet -ErrorAction SilentlyContinue)) {
    if ($sqExisting.Path -eq $sqExecutable) {
        Start-Process -FilePath $sqExecutable -ArgumentList '--stop' -Wait -WindowStyle Hidden
        $null = $sqExisting.WaitForExit(5000)
        if (Test-Path -LiteralPath $sqRecoveryFile) { throw 'Display recovery is still pending; installation stopped.' }
        if (-not $sqExisting.HasExited) { throw 'Existing controller did not stop.' }
    }
}
$null = New-Item -ItemType Directory -Path $sqInstallDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ScreenQuiet.exe') -Destination $sqExecutable -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $sqInstallDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $sqInstallDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-VendorRoute.ps1') -Destination $sqInstallDir -Force
$sqSmoke = Start-Process -FilePath $sqExecutable -ArgumentList '--smoke' -Wait -PassThru -WindowStyle Hidden
if ($sqSmoke.ExitCode -ne 0) { throw 'Passive startup check failed. Startup was not enabled. See %LOCALAPPDATA%\ScreenQuiet\status.log.' }
$null = New-ItemProperty -LiteralPath $sqRunKey -Name ScreenQuiet -Value ('"' + $sqExecutable + '"') -PropertyType String -Force
$sqShell = New-Object -ComObject WScript.Shell
$sqShortcut = $sqShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'ScreenQuiet - Restore screen.lnk'))
$sqShortcut.TargetPath = $sqExecutable
$sqShortcut.Arguments = '--restore'
$sqShortcut.Description = 'Restore the local display without changing UU settings'
$sqShortcut.Save()
$sqController = Start-Process -FilePath $sqExecutable -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1000
if ($sqController.HasExited) { throw 'Controller exited. See %LOCALAPPDATA%\ScreenQuiet\status.log.' }
'Installed and started ScreenQuiet. No screen-off test was performed.'
