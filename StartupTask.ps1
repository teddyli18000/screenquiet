# Shared current-user task ownership checks; no elevation or saved password.
$sqStartupSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sqTaskName = 'ScreenQuiet-' + $sqStartupSid
$sqTaskDescription = 'ScreenQuiet current-user startup and recovery v1'
function Get-SqStartupTask {
    $sqTask = Get-ScheduledTask -TaskName $sqTaskName -ErrorAction SilentlyContinue
    if ($sqTask) {
        $sqActions = @($sqTask.Actions)
        $sqPrincipalSid = $sqTask.Principal.UserId
        if ($sqPrincipalSid -notmatch '^S-1-') {
            $sqPrincipalSid = ([Security.Principal.NTAccount]::new($sqPrincipalSid)).Translate([Security.Principal.SecurityIdentifier]).Value
        }
        if ($sqTask.Description -ne $sqTaskDescription -or $sqActions.Count -ne 1 -or
            $sqActions[0].Execute -ne $sqExecutable -or $sqActions[0].Arguments -ne '--managed' -or
            $sqPrincipalSid -ne $sqStartupSid) { throw 'Existing startup task is not owned by this installation; preserved it.' }
    }
    return $sqTask
}
function Register-SqStartupTask {
    $null = Get-SqStartupTask
    $sqAction = New-ScheduledTaskAction -Execute $sqExecutable -Argument '--managed' -WorkingDirectory $sqInstallDir
    $sqPrincipal = New-ScheduledTaskPrincipal -UserId $sqStartupSid -LogonType Interactive -RunLevel Limited
    $sqLogon = New-ScheduledTaskTrigger -AtLogOn -User $sqStartupSid
    $sqRetry = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $sqSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $null = Register-ScheduledTask -TaskName $sqTaskName -Description $sqTaskDescription -Action $sqAction -Principal $sqPrincipal -Trigger @($sqLogon,$sqRetry) -Settings $sqSettings -Force
}
