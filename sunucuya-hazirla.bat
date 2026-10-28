@echo off
chcp 65001 >nul
setlocal
title Sunucu için yayın paketi hazırla



cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [HATA] Bu makinede .NET SDK yok. baslat.bat calisiyorsa vardir.
    pause
    exit /b 1
)

echo Kendinden calisir yayin hazirlaniyor (biraz surebilir)...
if exist "publish-sunucu" rmdir /s /q "publish-sunucu"

dotnet publish -c Release -r win-x64 --self-contained true -o "publish-sunucu" --nologo -v q
if errorlevel 1 (
    echo.
    echo [HATA] Yayin basarisiz. Ayrintili cikti icin:
    echo     dotnet publish -c Release -r win-x64 --self-contained true -o publish-sunucu
    pause
    exit /b 1
)


copy /y "servers.json" "publish-sunucu\servers.json" >nul
copy /y "sunucuda-kur.bat" "publish-sunucu\sunucuda-kur.bat" >nul

echo Zip olusturuluyor...
if exist "ServerMonitor-yayin.zip" del /f /q "ServerMonitor-yayin.zip"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'publish-sunucu\*' -DestinationPath 'ServerMonitor-yayin.zip' -Force"

echo.
echo ============================================================
echo  HAZIR:  %CD%\ServerMonitor-yayin.zip
echo.
echo  Sonraki adimlar:
echo   1) Bu zip'i 192.168.2.206 sunucusuna kopyalayin.
echo   2) Sunucuda bir klasore acin (extract).
echo   3) Icindeki "sunucuda-kur.bat"i YONETICI olarak calistirin.
echo ============================================================
pause
endlocal
