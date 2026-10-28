@echo off
chcp 65001 >nul
setlocal
title ServerMonitor sunucu kurulumu



cd /d "%~dp0"

set "SERVICE_NAME=ServerMonitor"
set "INSTALL_DIR=C:\ServerMonitor"
set "EXE=%INSTALL_DIR%\ServerMonitor.exe"

net session >nul 2>&1
if errorlevel 1 (
    echo [HATA] Yonetici olarak calistirin (sag tik -^> Yonetici olarak calistir).
    pause
    exit /b 1
)

if not exist "%~dp0ServerMonitor.exe" (
    echo [HATA] Bu klasorde ServerMonitor.exe yok. Zip'i acip bu betigi ONUN icinden calistirin.
    pause
    exit /b 1
)


sc.exe query %SERVICE_NAME% >nul 2>&1
if not errorlevel 1 (
    echo Mevcut servis durduruluyor...
    sc.exe stop %SERVICE_NAME% >nul 2>&1
    timeout /t 5 /nobreak >nul
)


echo Dosyalar %INSTALL_DIR% klasorune kopyalaniyor...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

if exist "%INSTALL_DIR%\servers.json" (

    robocopy "%~dp0." "%INSTALL_DIR%" /E /XD keys /XF sunucuda-kur.bat servers.json history.db history.db-shm history.db-wal /NFL /NDL /NJH /NJS /NP >nul
) else (

    robocopy "%~dp0." "%INSTALL_DIR%" /E /XD keys /XF sunucuda-kur.bat history.db history.db-shm history.db-wal /NFL /NDL /NJH /NJS /NP >nul
)
if %ERRORLEVEL% GEQ 8 (
    echo [HATA] Dosyalar kopyalanamadi.
    pause
    exit /b 1
)


sc.exe query %SERVICE_NAME% >nul 2>&1
if errorlevel 1 (
    echo Servis olusturuluyor...
    sc.exe create %SERVICE_NAME% binPath= "\"%EXE%\"" start= delayed-auto DisplayName= "Sunucu Durum Paneli" >nul
    sc.exe description %SERVICE_NAME% "Sunuculari ping/port/HTTP/WMI ile izler" >nul
    sc.exe failure %SERVICE_NAME% reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul
) else (
    echo Servis zaten var, dosyalar guncellendi.
)

netsh advfirewall firewall delete rule name="ServerMonitor Panel" >nul 2>&1
netsh advfirewall firewall add rule name="ServerMonitor Panel" dir=in action=allow protocol=TCP localport=5080 remoteip=localsubnet profile=any >nul

echo Servis baslatiliyor...
sc.exe start %SERVICE_NAME% >nul
timeout /t 3 /nobreak >nul
sc.exe query %SERVICE_NAME% | findstr /i "STATE"

echo.
echo ================================================================
echo  Kuruldu.  Panel:  http://%COMPUTERNAME%:5080   (veya http://192.168.2.206:5080)
echo.
echo  SON 2 ADIM (WMI verisi gelmesi icin sart):
echo   1) services.msc -^> "Sunucu Durum Paneli" -^> Ozellikler -^> Oturum Ac sekmesi
echo      -^> WMI yetkili domain hesabini secin (GEN\muharrem.rahat) -^> parolayi girin
echo      -^> servisi yeniden baslatin.
echo   2) Panele girip WMI ve SMTP parolalarini YENIDEN girin
echo      (sifreleme hesaba/makineye bagli oldugu icin eskiler cozulemez).
echo ================================================================
pause
endlocal
