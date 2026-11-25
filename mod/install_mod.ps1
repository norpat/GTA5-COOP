param([string]$GameDir = "C:\Games\GTA5")
$ModDir = Join-Path $GameDir "mod"
$ScriptsDir = Join-Path $GameDir "scripts"
New-Item -ItemType Directory -Force $ScriptsDir | Out-Null

# Kopier kun mod-filer (aldri endre spillets egne filer, Steam, Social Club eller BattlEye)
$shvFiles = @("ScriptHookV.dll", "dinput8.dll")
foreach ($f in $shvFiles) {
    Copy-Item -Force (Join-Path $ModDir "runtime\shv\bin\$f") (Join-Path $GameDir $f)
}
$shvdnFiles = @("ScriptHookVDotNet-Loader.asi", "ScriptHookVDotNet.dll", "ScriptHookVDotNet2.dll", "ScriptHookVDotNet3.dll", "ScriptHookVDotNet.ini")
foreach ($f in $shvdnFiles) {
    Copy-Item -Force (Join-Path $ModDir "runtime\shvdn\$f") (Join-Path $GameDir $f)
}
Remove-Item -Force (Join-Path $ScriptsDir "CoopBridge.3.cs") -ErrorAction SilentlyContinue
Copy-Item -Force (Join-Path $ModDir "CoopBridge.dll") (Join-Path $ScriptsDir "CoopBridge.dll")
Write-Host "GTA 5 Co-op mod installert i $GameDir (ScriptHookV + SHVDN + CoopBridge.dll)"
