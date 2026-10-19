$ErrorActionPreference = 'Stop'
$sqCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sqSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'LauncherBridge.cs' | ForEach-Object FullName)
& $sqCompiler /nologo /target:winexe /platform:x64 /langversion:5 /main:ScreenQuiet.Program (('/out:' + (Join-Path $PSScriptRoot 'ScreenQuiet.exe'))) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll $sqSources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
& $sqCompiler /nologo /target:winexe /platform:x64 /langversion:5 /main:ScreenQuiet.LauncherBridge (('/out:' + (Join-Path $PSScriptRoot 'LauncherBridge.exe'))) (Join-Path $PSScriptRoot 'src\LauncherBridge.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launch bridge compilation failed.' }
& $sqCompiler /nologo /target:winexe /platform:x64 /langversion:5 (('/out:' + (Join-Path $PSScriptRoot 'tests\ScreenQuietBridgeProbe.exe'))) (Join-Path $PSScriptRoot 'tests\BridgeProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launch bridge probe compilation failed.' }
