@echo off
chcp 65001 >nul
setlocal
title ServerMonitor servis kaldirma

rem YONETICI OLARAK calistirin. Kurulum klasoru (veriler dahil) silinmez.

set "SERVICE_NAME=ServerMonitor"

net session >nul 2>&1
if errorlevel 1 (
    echo [HATA] Bu dosyayi yonetici olarak calistirmaniz gerekiyor.
    pause
    exit /b 1
)

sc.exe query %SERVICE_NAME% >nul 2>&1
if errorlevel 1 (
    echo Servis kurulu degil.
    pause
    exit /b 0
)

echo Servis durduruluyor...
sc.exe stop %SERVICE_NAME% >nul 2>&1
timeout /t 5 /nobreak >nul

echo Servis kaldiriliyor...
sc.exe delete %SERVICE_NAME%

netsh advfirewall firewall delete rule name="ServerMonitor Panel" >nul 2>&1

echo.
echo Servis kaldirildi. C:\ServerMonitor klasoru (servers.json, history.db) yerinde duruyor;
echo istemiyorsaniz elle silebilirsiniz.
pause
endlocal
