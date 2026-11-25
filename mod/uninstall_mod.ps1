param([string]$GameDir = "C:\Games\GTA5")
$files = @(
    "ScriptHookV.dll", "dinput8.dll",
    "ScriptHookVDotNet-Loader.asi", "ScriptHookVDotNet.dll",
    "ScriptHookVDotNet2.dll", "ScriptHookVDotNet3.dll", "ScriptHookVDotNet.ini",
    "scripts\CoopBridge.3.cs", "scripts\CoopBridge.dll"
)
foreach ($rel in $files) {
    $p = Join-Path $GameDir $rel
    if (Test-Path $p) {
        Remove-Item -Force $p
        Write-Host "Fjernet: $rel"
    }
}
Write-Host "GTA 5 Co-op mod avinstallert fra $GameDir."
