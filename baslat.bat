@echo off
chcp 65001 >nul
setlocal
title Sunucu Durum Paneli

rem Bu dosyanin bulundugu klasore gec (cift tiklayinca da dogru calissin diye).
cd /d "%~dp0"

set "PANEL_URL=http://localhost:5080"

echo ============================================
echo   Sunucu Durum Paneli
echo ============================================
echo.

rem --- .NET SDK var mi? ---
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [HATA] .NET SDK bulunamadi.
    echo.
    echo .NET 9 SDK kurmak icin:
    echo     https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    echo Kurduktan sonra bu pencereyi kapatip tekrar calistirin.
    echo.
    pause
    exit /b 1
)

for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%v"
echo .NET surumu : %DOTNET_VER%
echo Klasor      : %CD%
echo Panel adresi: %PANEL_URL%
echo.

rem --- Onceden calisan bir ornek varsa kapat (exe kilidi derlemeyi engellemesin) ---
tasklist /fi "imagename eq ServerMonitor.exe" 2>nul | find /i "ServerMonitor.exe" >nul
if not errorlevel 1 (
    echo Onceden calisan ornek kapatiliyor...
    taskkill /im ServerMonitor.exe /f >nul 2>&1
    timeout /t 2 /nobreak >nul
)

rem --- Paketleri indir ---
echo [1/3] Paketler kontrol ediliyor...
dotnet restore >nul
if errorlevel 1 (
    echo.
    echo [HATA] dotnet restore basarisiz oldu. Ayrintili cikti icin:
    echo     dotnet restore
    echo.
    pause
    exit /b 1
)

rem --- Derle ---
echo [2/3] Proje derleniyor...
dotnet build -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo [HATA] Derleme basarisiz oldu. Yukaridaki hatalara bakin.
    echo.
    pause
    exit /b 1
)

rem --- Tarayiciyi ac ve uygulamayi baslat ---
echo [3/3] Uygulama baslatiliyor...
echo.
echo Tarayici birazdan acilacak. Durdurmak icin bu pencerede Ctrl+C yapin.
echo.

start "" /b cmd /c "timeout /t 4 /nobreak >nul & start "" %PANEL_URL%"

dotnet run -c Release --no-build

echo.
echo Uygulama durdu.
pause
endlocal
