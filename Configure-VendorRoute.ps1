param(
    [ValidateSet('Install','Update','Remove')][string]$Mode = 'Install',
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
function Assert-SqOwnedRules {
    foreach ($sqTarget in $sqTargets) {
        $sqParent = Join-Path $sqIfeoRoot ([IO.Path]::GetFileName($sqTarget))
        $sqChild = Join-Path $sqParent 'ScreenQuietFnF9'
        if (-not (Test-Path -LiteralPath $sqParent -PathType Container) -or -not (Test-Path -LiteralPath $sqChild -PathType Container)) { throw 'Both owned IFEO filter rules must exist before updating the bridge.' }
        $sqParentKey = Get-Item -LiteralPath $sqParent
        $sqChildKey = Get-Item -LiteralPath $sqChild
        if ($sqParentKey.GetValueKind('UseFilter') -ne [Microsoft.Win32.RegistryValueKind]::DWord -or $sqParentKey.GetValue('UseFilter') -ne 1) { throw 'IFEO UseFilter is missing or changed; preserved the installed bridge.' }
        if ($sqParentKey.GetValueKind('ScreenQuietOwner') -ne [Microsoft.Win32.RegistryValueKind]::String -or $sqParentKey.GetValue('ScreenQuietOwner') -cne $sqOwner) { throw 'IFEO rule owner is missing or changed; preserved the installed bridge.' }
        if ($sqChildKey.GetValueKind('ScreenQuietOwner') -ne [Microsoft.Win32.RegistryValueKind]::String -or $sqChildKey.GetValue('ScreenQuietOwner') -cne $sqOwner) { throw 'IFEO filter owner is missing or changed; preserved the installed bridge.' }
        if ($sqChildKey.GetValueKind('FilterFullPath') -ne [Microsoft.Win32.RegistryValueKind]::String -or $sqChildKey.GetValue('FilterFullPath') -cne $sqTarget) { throw 'IFEO full-path filter is missing or changed; preserved the installed bridge.' }
        if ($sqChildKey.GetValueKind('Debugger') -ne [Microsoft.Win32.RegistryValueKind]::String -or $sqChildKey.GetValue('Debugger') -cne ('"' + $sqHelper + '"')) { throw 'IFEO debugger path is missing or changed; preserved the installed bridge.' }
    }
}
function Quote-SqCommandLineArgument([string]$sqValue) {
    $sqBuilder = New-Object Text.StringBuilder
    [void]$sqBuilder.Append([char]34)
    $sqSlashes = 0
    foreach ($sqChar in $sqValue.ToCharArray()) {
        if ([int]$sqChar -eq 92) { $sqSlashes++; continue }
        if ([int]$sqChar -eq 34) { [void]$sqBuilder.Append([char]92,($sqSlashes * 2 + 1)) }
        else { [void]$sqBuilder.Append([char]92,$sqSlashes) }
        [void]$sqBuilder.Append($sqChar)
        $sqSlashes = 0
    }
    [void]$sqBuilder.Append([char]92,($sqSlashes * 2))
    [void]$sqBuilder.Append([char]34)
    return $sqBuilder.ToString()
}
function Invoke-SqBridgeProbe([string]$sqBridge,[string]$sqTarget,[string[]]$sqTargetArguments,[string]$sqWorkingDirectory) {
    $sqStart = New-Object Diagnostics.ProcessStartInfo
    $sqStart.FileName = $sqBridge
    $sqStart.WorkingDirectory = $sqWorkingDirectory
    $sqStart.UseShellExecute = $false
    $sqStart.CreateNoWindow = $true
    $sqArguments = @($sqTarget) + @($sqTargetArguments)
    $sqStart.Arguments = (($sqArguments | ForEach-Object { Quote-SqCommandLineArgument ([string]$_) }) -join ' ')
    $sqProcess = New-Object Diagnostics.Process
    $sqProcess.StartInfo = $sqStart
    try {
        if (-not $sqProcess.Start()) { throw 'Could not start the launch bridge probe.' }
        if (-not $sqProcess.WaitForExit(8000)) { try { $sqProcess.Kill() } catch { }; throw 'Launch bridge probe timed out.' }
        return $sqProcess.ExitCode
    }
    finally { $sqProcess.Dispose() }
}
function Wait-SqProbeFile([string]$sqPath,[int]$sqTimeoutMs = 2500) {
    $sqDeadline = [DateTime]::UtcNow.AddMilliseconds($sqTimeoutMs)
    while (-not (Test-Path -LiteralPath $sqPath) -and [DateTime]::UtcNow -lt $sqDeadline) { Start-Sleep -Milliseconds 50 }
    return (Test-Path -LiteralPath $sqPath)
}
function New-SqProbeDirectory {
    $sqTemp = Join-Path ([IO.Path]::GetTempPath()) ('ScreenQuietBridgeProbe-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $sqTemp
    return $sqTemp
}
function Remove-SqProbeDirectory([string]$sqPath) {
    if (-not (Test-Path -LiteralPath $sqPath -PathType Container)) { return }
    $sqFull = [IO.Path]::GetFullPath($sqPath)
    $sqTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $sqFull.StartsWith($sqTempRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($sqFull).StartsWith('ScreenQuietBridgeProbe-',[StringComparison]::OrdinalIgnoreCase)) { throw 'Refused to clean an unexpected probe directory.' }
    foreach ($sqItem in Get-ChildItem -LiteralPath $sqFull -Force) {
        if ($sqItem.PSIsContainer) { throw 'Unexpected directory found in the isolated probe folder; preserved it.' }
        Remove-Item -LiteralPath $sqItem.FullName -Force
    }
    Remove-Item -LiteralPath $sqFull -Force
}
function Test-SqBridgeForwarding([string]$sqBridge,[string]$sqProbeSource) {
    $sqTestDir = New-SqProbeDirectory
    try {
        $sqProbe = Join-Path $sqTestDir ('ScreenQuietBridgeProbe-' + [Guid]::NewGuid().ToString('N') + '.exe')
        $sqResult = Join-Path $sqTestDir 'forwarded.txt'
        Copy-Item -LiteralPath $sqProbeSource -Destination $sqProbe
        $sqExit = Invoke-SqBridgeProbe $sqBridge $sqProbe @($sqResult,'space value','quote"value','trailing\') $sqTestDir
        if ($sqExit -ne 0 -or -not (Wait-SqProbeFile $sqResult)) { throw 'Ordinary bridge forwarding probe failed.' }
        $sqLines = @(Get-Content -LiteralPath $sqResult)
        if ($sqLines.Count -ne 4 -or $sqLines[0] -ne 'Debugger=False' -or $sqLines[1] -ne 'c3BhY2UgdmFsdWU=' -or $sqLines[2] -ne 'cXVvdGUidmFsdWU=' -or $sqLines[3] -ne 'dHJhaWxpbmdc') { throw 'Forwarded probe arguments or debugger state changed.' }
    }
    finally { Remove-SqProbeDirectory $sqTestDir }
}
function Test-SqBridgePolicy([string]$sqBridge,[string]$sqProbeSource) {
    $sqTestDir = New-SqProbeDirectory
    try {
        $sqOtherProbe = Join-Path $sqTestDir ('ScreenQuietBridgeProbe-' + [Guid]::NewGuid().ToString('N') + '.exe')
        $sqLauncherProbe = Join-Path $sqTestDir 'LenovoBaiyingLauncher.exe'
        Copy-Item -LiteralPath $sqProbeSource -Destination $sqOtherProbe
        Copy-Item -LiteralPath $sqProbeSource -Destination $sqLauncherProbe

        $sqSuppressedMarker = Join-Path $sqTestDir '-customerService'
        $sqExit = Invoke-SqBridgeProbe $sqBridge $sqLauncherProbe @('-customerService') $sqTestDir
        if ($sqExit -ne 0 -or (Wait-SqProbeFile $sqSuppressedMarker 1000)) { throw 'Exact LenovoBaiyingLauncher.exe -customerService probe was not suppressed.' }

        $sqOtherArgumentMarker = Join-Path $sqTestDir '-other'
        $sqExit = Invoke-SqBridgeProbe $sqBridge $sqLauncherProbe @('-other') $sqTestDir
        if ($sqExit -ne 0 -or -not (Wait-SqProbeFile $sqOtherArgumentMarker)) { throw 'Other LenovoBaiyingLauncher.exe arguments were not forwarded.' }

        Remove-Item -LiteralPath $sqSuppressedMarker -Force -ErrorAction SilentlyContinue
        $sqExit = Invoke-SqBridgeProbe $sqBridge $sqOtherProbe @('-customerService') $sqTestDir
        if ($sqExit -ne 0 -or -not (Wait-SqProbeFile $sqSuppressedMarker)) { throw 'The customer-service argument on another executable was unexpectedly suppressed.' }
    }
    finally { Remove-SqProbeDirectory $sqTestDir }
}
try {
    if ($Mode -eq 'Update') {
        if ([string]::IsNullOrWhiteSpace($BridgeSource) -or -not (Test-Path -LiteralPath $BridgeSource -PathType Leaf)) { throw 'A built BridgeSource file is required.' }
        if ([string]::IsNullOrWhiteSpace($ProbeSource) -or -not (Test-Path -LiteralPath $ProbeSource -PathType Leaf)) { throw 'A built ProbeSource file is required.' }
        if ([string]::IsNullOrWhiteSpace($ReportPath)) { throw 'ReportPath is required for a bridge update.' }
        if (-not (Test-Path -LiteralPath $sqProtectedDir -PathType Container) -or -not (Test-Path -LiteralPath $sqHelper -PathType Leaf)) { throw 'The fixed protected helper directory and installed bridge must exist.' }
        Assert-SqOwnedRules
        $sqOriginalHash = (Get-FileHash -LiteralPath $sqHelper -Algorithm SHA256).Hash
        $sqUpdateId = [Guid]::NewGuid().ToString('N')
        $sqStage = Join-Path $sqProtectedDir ('LauncherBridge.exe.new-' + $sqUpdateId)
        $sqBackup = Join-Path $sqProtectedDir ('LauncherBridge.exe.backup-' + $sqUpdateId)
        $sqFailed = Join-Path $sqProtectedDir ('LauncherBridge.exe.failed-' + $sqUpdateId)
        $sqReplaced = $false
        try {
            Test-SqBridgeForwarding $sqHelper $ProbeSource
            Copy-Item -LiteralPath $BridgeSource -Destination $sqStage
            Test-SqBridgeForwarding $sqStage $ProbeSource
            Test-SqBridgePolicy $sqStage $ProbeSource
            $sqNewHash = (Get-FileHash -LiteralPath $sqStage -Algorithm SHA256).Hash
            [IO.File]::Replace($sqStage,$sqHelper,$sqBackup)
            $sqReplaced = $true
            Test-SqBridgeForwarding $sqHelper $ProbeSource
            Test-SqBridgePolicy $sqHelper $ProbeSource
            @{
                Mode = 'Update'
                Date = [DateTime]::Now.ToString('s')
                Helper = $sqHelper
                PreviousHelperSha256 = $sqOriginalHash
                HelperSha256 = $sqNewHash
                RulesValidated = 'Both exact ScreenQuiet-owned IFEO rules matched; registry was unchanged.'
                Tests = @('Ordinary argument forwarding and debugger detach','Exact launcher customer-service suppression without WMI gate','Other launcher arguments forwarded','Same argument on another executable forwarded')
                Result = 'Updated and verified.'
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath
            $sqReplaced = $false
            try { Remove-Item -LiteralPath $sqBackup -Force -ErrorAction Stop } catch { }
        }
        catch {
            $sqUpdateError = $_.Exception.ToString()
            if ($sqReplaced -and (Test-Path -LiteralPath $sqBackup -PathType Leaf)) {
                try {
                    [IO.File]::Replace($sqBackup,$sqHelper,$sqFailed)
                    $sqReplaced = $false
                    if (Test-Path -LiteralPath $sqFailed) { Remove-Item -LiteralPath $sqFailed -Force }
                    $sqUpdateError += "`r`nThe previous bridge was restored."
                }
                catch { $sqUpdateError += "`r`nRollback failed; the previous bridge backup was preserved. " + $_.Exception.Message }
            }
            throw $sqUpdateError
        }
        finally {
            if (Test-Path -LiteralPath $sqStage -PathType Leaf) { Remove-Item -LiteralPath $sqStage -Force }
        }
        exit 0
    }
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
    $sqOtherPathResult = $ReportPath + '.other-path.txt'
    foreach ($sqResult in @($sqProbeResult,$sqOtherPathResult)) { if (Test-Path -LiteralPath $sqResult) { throw 'Test result path already exists.' } }
    try {
        Add-SqRule $sqProbe
        $sqForward = Start-Process -FilePath $sqProbe -ArgumentList ('"'+$sqProbeResult+'" "space value" "quote\"value" "trailing\\"') -PassThru -WindowStyle Hidden
        if (-not $sqForward.WaitForExit(7000) -or $sqForward.ExitCode -ne 0) { throw 'IFEO forwarding failed.' }
        $sqDeadline = [DateTime]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $sqProbeResult) -and [DateTime]::UtcNow -lt $sqDeadline) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $sqProbeResult)) { throw 'Forwarded probe did not run.' }
        $sqLines = @(Get-Content -LiteralPath $sqProbeResult)
        if ($sqLines.Count -ne 4 -or $sqLines[0] -ne 'Debugger=False' -or $sqLines[1] -ne 'c3BhY2UgdmFsdWU=' -or $sqLines[2] -ne 'cXVvdGUidmFsdWU=' -or $sqLines[3] -ne 'dHJhaWxpbmdc') { throw 'Forwarded arguments/debugger state changed.' }
        Test-SqBridgePolicy $sqHelper $ProbeSource
        $sqOther = Start-Process -FilePath $ProbeSource -ArgumentList ('"'+$sqOtherPathResult+'"') -PassThru -WindowStyle Hidden
        if (-not $sqOther.WaitForExit(5000) -or -not (Test-Path -LiteralPath $sqOtherPathResult)) { throw 'Full-path filter affected another path.' }
    }
    finally {
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
        Tests = @('IFEO manual forwarding and arguments','Detached before application Main','Exact launcher customer-service suppression without WMI gate','Other launcher arguments forwarded','Same argument on another executable forwarded','Same basename different path unaffected')
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
