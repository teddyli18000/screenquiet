param(
    [ValidateSet('Install','Remove')][string]$Mode = 'Install',
    [string]$BridgeSource,
    [string]$ProbeSource,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$sqIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($sqIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator context required for the two vendor launch rules.' }
$sqProtectedDir = Join-Path $env:ProgramFiles 'ScreenQuiet'
$sqHelper = Join-Path $sqProtectedDir 'LauncherBridge.exe'
$sqIfeoRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$sqTargets = @(
    'C:\Program Files (x86)\Lenovo\baiying\LenovoBaiyingLauncher.exe',
    'C:\Program Files (x86)\Lenovo\baiying\LenovoBaiying.exe'
)
$sqOwner = 'ScreenQuiet.FnF9.v1'
$sqCreatedDir = $false
$sqAdded = @()
function Remove-SqRule([string]$sqTarget) {
    $sqParent = Join-Path $sqIfeoRoot ([IO.Path]::GetFileName($sqTarget))
    $sqChild = Join-Path $sqParent 'ScreenQuietFnF9'
    if (Test-Path -LiteralPath $sqChild) {
        $sqProperties = Get-ItemProperty -LiteralPath $sqChild
        if ($sqProperties.ScreenQuietOwner -ne $sqOwner -or $sqProperties.FilterFullPath -ne $sqTarget -or $sqProperties.Debugger -ne ('"' + $sqHelper + '"')) { throw 'Launch rule changed externally; preserved it.' }
        Remove-Item -LiteralPath $sqChild -Recurse
    }
    if ((Test-Path -LiteralPath $sqParent) -and @(Get-ChildItem -LiteralPath $sqParent).Count -eq 0) {
        $sqKey = Get-Item -LiteralPath $sqParent
        $sqNames = @($sqKey.GetValueNames())
        if ($sqNames.Count -eq 2 -and $sqNames -contains 'UseFilter' -and $sqNames -contains 'ScreenQuietOwner' -and $sqKey.GetValue('ScreenQuietOwner') -eq $sqOwner) { Remove-Item -LiteralPath $sqParent }
    }
}
function Add-SqRule([string]$sqTarget) {
    $sqParent = Join-Path $sqIfeoRoot ([IO.Path]::GetFileName($sqTarget))
    if (Test-Path -LiteralPath $sqParent) { throw ('Existing IFEO settings preserved: ' + $sqParent) }
    $sqChild = Join-Path $sqParent 'ScreenQuietFnF9'
    try {
    $null = New-Item -Path $sqChild -Force
    $null = New-ItemProperty -LiteralPath $sqParent -Name UseFilter -PropertyType DWord -Value 1
    $null = New-ItemProperty -LiteralPath $sqParent -Name ScreenQuietOwner -PropertyType String -Value $sqOwner
    $null = New-ItemProperty -LiteralPath $sqChild -Name ScreenQuietOwner -PropertyType String -Value $sqOwner
    $null = New-ItemProperty -LiteralPath $sqChild -Name FilterFullPath -PropertyType String -Value $sqTarget
    # Deliberately no switches: IFEO appends the original EXE and arguments.
    $null = New-ItemProperty -LiteralPath $sqChild -Name Debugger -PropertyType String -Value ('"' + $sqHelper + '"')
    }
    catch {
        # The parent was absent before this call; undo a partially written rule.
        if (Test-Path -LiteralPath $sqParent) { Remove-Item -LiteralPath $sqParent -Recurse }
        throw
    }
}
try {
    if ($Mode -eq 'Remove') {
        foreach ($sqTarget in $sqTargets) { Remove-SqRule $sqTarget }
        if (Test-Path -LiteralPath $sqHelper) { Remove-Item -LiteralPath $sqHelper }
        if ((Test-Path -LiteralPath $sqProtectedDir) -and @(Get-ChildItem -LiteralPath $sqProtectedDir -Force).Count -eq 0) { Remove-Item -LiteralPath $sqProtectedDir }
        if ($ReportPath) { 'Vendor launch rules removed.' | Set-Content -LiteralPath $ReportPath }
        exit 0
    }
    foreach ($sqTarget in $sqTargets) {
        if (-not (Test-Path -LiteralPath $sqTarget)) { throw ('Vendor image missing: ' + $sqTarget) }
        if (Test-Path -LiteralPath (Join-Path $sqIfeoRoot ([IO.Path]::GetFileName($sqTarget)))) { throw 'Existing vendor launch rule found; installation stopped.' }
    }
    if (Test-Path -LiteralPath $sqProtectedDir) { throw 'Protected helper directory already exists; installation stopped.' }
    $null = New-Item -ItemType Directory -Path $sqProtectedDir
    $sqCreatedDir = $true
    Copy-Item -LiteralPath $BridgeSource -Destination $sqHelper
    $sqProbe = Join-Path $sqProtectedDir 'ScreenQuietBridgeProbe.exe'
    Copy-Item -LiteralPath $ProbeSource -Destination $sqProbe
    $sqProbeResult = $ReportPath + '.forwarded.txt'
    $sqSuppressedResult = $ReportPath + '.suppressed.txt'
    $sqOtherPathResult = $ReportPath + '.other-path.txt'
    foreach ($sqResult in @($sqProbeResult,$sqSuppressedResult,$sqOtherPathResult)) { if (Test-Path -LiteralPath $sqResult) { throw 'Test result path already exists.' } }
    $sqGate = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,'Local\ScreenQuiet.FnRoute')
    try {
        Add-SqRule $sqProbe
        $sqForward = Start-Process -FilePath $sqProbe -ArgumentList ('"'+$sqProbeResult+'" "space value" "quote\"value" "trailing\\"') -PassThru -WindowStyle Hidden
        if (-not $sqForward.WaitForExit(7000) -or $sqForward.ExitCode -ne 0) { throw 'IFEO forwarding failed.' }
        $sqDeadline = [DateTime]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $sqProbeResult) -and [DateTime]::UtcNow -lt $sqDeadline) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $sqProbeResult)) { throw 'Forwarded probe did not run.' }
        $sqLines = @(Get-Content -LiteralPath $sqProbeResult)
        if ($sqLines.Count -ne 4 -or $sqLines[0] -ne 'Debugger=False' -or $sqLines[1] -ne 'c3BhY2UgdmFsdWU=' -or $sqLines[2] -ne 'cXVvdGUidmFsdWU=' -or $sqLines[3] -ne 'dHJhaWxpbmdc') { throw 'Forwarded arguments/debugger state changed.' }
        $null = $sqGate.Set()
        $sqSuppressed = Start-Process -FilePath $sqProbe -ArgumentList ('"'+$sqSuppressedResult+'"') -PassThru -WindowStyle Hidden
        if (-not $sqSuppressed.WaitForExit(5000) -or $sqSuppressed.ExitCode -ne 0) { throw 'Fn gate suppression failed.' }
        Start-Sleep -Milliseconds 300
        if (Test-Path -LiteralPath $sqSuppressedResult) { throw 'Fn gate unexpectedly forwarded target.' }
        $sqOther = Start-Process -FilePath $ProbeSource -ArgumentList ('"'+$sqOtherPathResult+'"') -PassThru -WindowStyle Hidden
        if (-not $sqOther.WaitForExit(5000) -or -not (Test-Path -LiteralPath $sqOtherPathResult)) { throw 'Full-path filter affected another path.' }
    }
    finally {
        $null = $sqGate.Reset()
        $sqGate.Dispose()
        Remove-SqRule $sqProbe
        if (Test-Path -LiteralPath $sqProbe) { Remove-Item -LiteralPath $sqProbe }
    }
    $sqAdded = @()
    try {
        foreach ($sqTarget in $sqTargets) { Add-SqRule $sqTarget; $sqAdded += $sqTarget }
    }
    catch { foreach ($sqTarget in $sqAdded) { Remove-SqRule $sqTarget }; throw }
    @{
        Date = [DateTime]::Now.ToString('s')
        Helper = $sqHelper
        HelperSha256 = (Get-FileHash -LiteralPath $sqHelper -Algorithm SHA256).Hash
        Tests = @('IFEO manual forwarding and arguments','Detached before application Main','Fn event suppression','Same basename different path unaffected')
        Targets = $sqTargets
        OriginalRules = 'Both absent; no existing values overwritten'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath
    exit 0
}
catch {
    $sqFailure = $_.Exception.ToString()
    if ($Mode -eq 'Install' -and $sqCreatedDir) {
        foreach ($sqTarget in $sqAdded) {
            try { Remove-SqRule $sqTarget } catch { $sqFailure += "`r`nRollback: " + $_.Exception.Message }
        }
        if ($sqProbe) {
            try { Remove-SqRule $sqProbe } catch { $sqFailure += "`r`nProbe cleanup: " + $_.Exception.Message }
        }
        # Keep the protected helper if a rule could not be removed.
        $sqRulesRemain = @($sqTargets | Where-Object { Test-Path -LiteralPath (Join-Path (Join-Path $sqIfeoRoot ([IO.Path]::GetFileName($_))) 'ScreenQuietFnF9') }).Count -gt 0
        if (-not $sqRulesRemain) {
            foreach ($sqFile in @($sqHelper,$sqProbe)) { if ($sqFile -and (Test-Path -LiteralPath $sqFile)) { Remove-Item -LiteralPath $sqFile } }
            if (@(Get-ChildItem -LiteralPath $sqProtectedDir -Force).Count -eq 0) { Remove-Item -LiteralPath $sqProtectedDir }
        }
    }
    if ($ReportPath) { $sqFailure | Set-Content -LiteralPath ($ReportPath+'.error.txt') }
    exit 1
}
