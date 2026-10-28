@echo off
chcp 65001 >nul
setlocal
title ServerMonitor servis kurulumu


cd /d "%~dp0"

set "SERVICE_NAME=ServerMonitor"
set "INSTALL_DIR=C:\ServerMonitor"

net session >nul 2>&1
if errorlevel 1 (
    echo [HATA] Bu dosyayi yonetici olarak calistirmaniz gerekiyor.
    pause
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [HATA] .NET SDK bulunamadi: https://dotnet.microsoft.com/download/dotnet/9.0
    pause
    exit /b 1
)

echo Kurulum klasoru: %INSTALL_DIR%
echo.

sc.exe query %SERVICE_NAME% >nul 2>&1
if not errorlevel 1 (
    echo [1/4] Mevcut servis durduruluyor...
    sc.exe stop %SERVICE_NAME% >nul 2>&1
    timeout /t 5 /nobreak >nul
) else (
    echo [1/4] Onceden kurulu servis yok.
)

echo [2/4] Uygulama yayinlaniyor...
dotnet publish -c Release -o "%INSTALL_DIR%" --nologo -v q -p:CopyOutputSymbolsToPublishDirectory=false
if errorlevel 1 (
    echo [HATA] Yayinlama basarisiz.
    pause
    exit /b 1
)

if not exist "%INSTALL_DIR%\servers.json.kurulu" (
    copy /y "servers.json" "%INSTALL_DIR%\servers.json" >nul
    echo. > "%INSTALL_DIR%\servers.json.kurulu"
)


sc.exe query %SERVICE_NAME% >nul 2>&1
if errorlevel 1 (
    echo [3/4] Servis olusturuluyor...
    sc.exe create %SERVICE_NAME% binPath= "\"%INSTALL_DIR%\ServerMonitor.exe\"" start= delayed-auto DisplayName= "Sunucu Durum Paneli" >nul
    sc.exe description %SERVICE_NAME% "Sunucularin ping, port, HTTP ve WMI ile izlenmesi" >nul

    sc.exe failure %SERVICE_NAME% reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul
) else (
    echo [3/4] Servis zaten var, guncelleniyor.
)

netsh advfirewall firewall delete rule name="ServerMonitor Panel" >nul 2>&1
netsh advfirewall firewall add rule name="ServerMonitor Panel" dir=in action=allow protocol=TCP localport=5080 remoteip=localsubnet profile=any >nul

echo [4/4] Servis baslatiliyor...
sc.exe start %SERVICE_NAME% >nul
timeout /t 3 /nobreak >nul
sc.exe query %SERVICE_NAME% | findstr /i "STATE"

echo.
echo ================================================================
echo  Kurulum tamam. Panel: http://%COMPUTERNAME%:5080
echo.
echo  ONEMLI: Servis su an "Local System" hesabiyla calisiyor. Uzak
echo  sunuculardan WMI verisi alabilmesi icin services.msc'de
echo  "Sunucu Durum Paneli" -^> Ozellikler -^> Oturum Ac sekmesinden
echo  WMI yetkili bir domain hesabi secin ve servisi yeniden baslatin.
echo  Sonra panelde WMI parolalarini YENIDEN girin (sifreleme hesaba bagli).
echo ================================================================
pause
endlocal
