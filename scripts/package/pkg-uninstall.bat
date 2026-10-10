@echo off
rem Uninstaller for the PHOSPHOR package. Template: scripts\package\pkg-uninstall.bat (copied as Удалить.bat).
chcp 65001 >nul

echo Удаление PHOSPHOR...
echo Будет восстановлена прежняя заставка и удалена папка %LOCALAPPDATA%\Programs\Phosphor
echo Скрипт спросит подтверждение.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
pause
