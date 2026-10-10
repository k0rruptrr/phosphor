@echo off
rem Installer for the PHOSPHOR package. Template: scripts\package\pkg-install.bat (copied as Установить.bat).
chcp 65001 >nul
setlocal
rem %~dp0 ends with a backslash, which would escape the closing quote on the command line - strip it.
set "PKG=%~dp0"
set "PKG=%PKG:~0,-1%"

echo Установка PHOSPHOR...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Source "%PKG%" -TimeoutMinutes 5
echo.
if errorlevel 1 (
    echo Установка завершилась с ошибкой. Сообщение выше.
) else (
    echo Готово. Заставка PHOSPHOR включится через 5 минут бездействия.
)
echo.
pause
