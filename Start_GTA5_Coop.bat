@echo off
title GTA V Story Co-op Launcher v0.9.2
cd /d "%~dp0"
echo Starting GTA V Story Co-op Mod GUI v0.9.2...
rem v0.9.2: guest - install the 100%% save BEFORE the game starts (waits until done)
python "%~dp0coop\gta5_coop_gui.py" --install-guest-save
start "" /D "%~dp0coop" pythonw "%~dp0coop\gta5_coop_gui.py"
echo Starting Grand Theft Auto V (Story Mode - No BattlEye)...
call "%~dp0PlayGTAV.bat"
