$ErrorActionPreference = 'Stop'
$sqStatusDir = Join-Path $env:LOCALAPPDATA 'ScreenQuiet'
$null = New-Item -ItemType Directory -Path $sqStatusDir -Force
$sqReport = Join-Path $sqStatusDir ('route-check-' + [Guid]::NewGuid().ToString('N') + '.json')
$sqConfigure = Join-Path $PSScriptRoot 'Configure-VendorRoute.ps1'
$sqBridge = Join-Path $PSScriptRoot 'LauncherBridge.exe'
$sqProbe = Join-Path $PSScriptRoot 'tests\ScreenQuietBridgeProbe.exe'
foreach ($sqFile in @($sqConfigure,$sqBridge,$sqProbe)) { if (-not (Test-Path -LiteralPath $sqFile)) { throw ('Required file missing: ' + $sqFile) } }
$sqArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $sqConfigure + '" -Mode Install -BridgeSource "' + $sqBridge + '" -ProbeSource "' + $sqProbe + '" -ReportPath "' + $sqReport + '"'
# This is the only elevation request. Canceling leaves the vendor route unchanged.
$sqSetup = Start-Process -FilePath powershell.exe -ArgumentList $sqArguments -Verb RunAs -PassThru -Wait -WindowStyle Hidden
if ($sqSetup.ExitCode -ne 0) { throw ('Vendor route setup failed. Diagnostic: ' + $sqReport + '.error.txt') }
Copy-Item -LiteralPath $sqReport -Destination (Join-Path $PSScriptRoot 'routing-validation.json') -Force
& (Join-Path $PSScriptRoot 'Install.ps1')
