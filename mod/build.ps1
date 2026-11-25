# Build CoopBridge.dll (needs ScriptHookVDotNet3.dll next to GTA5.exe or pass -Shvdn)
param([string]$GameDir = "C:\Games\GTA5", [string]$Shvdn = "")
if (-not $Shvdn) { $Shvdn = Join-Path $GameDir "ScriptHookVDotNet3.dll" }
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:library /optimize+ /out:"$PSScriptRoot\CoopBridge.dll" /r:"$Shvdn" "$PSScriptRoot\CoopBridge.3.cs"
