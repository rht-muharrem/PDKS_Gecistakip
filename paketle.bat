@echo off
chcp 65001 >nul
title Projeyi paketle

cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0paketle.ps1"

echo.
pause
