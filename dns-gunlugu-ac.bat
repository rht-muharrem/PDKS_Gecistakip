@echo off
chcp 65001 >nul
title DNS Analytical gunlugunu ac

rem ================================================================
rem  DC UZERINDE, yonetici olarak calistirin.
rem  DNS sunucusunun sorgu gunlugunu (Analytical) etkinlestirir.
rem  Bir kez calistirmak yeterlidir; yeniden baslatmalarda acik kalir.
rem ================================================================

net session >nul 2>&1
if errorlevel 1 (
    echo [HATA] Yonetici olarak calistirin.
    pause
    exit /b 1
)

rem Analitik (direct) kanalin ayari, kanal ACIKKEN degistirilemez.
rem Bu yuzden once kapatiyoruz (zaten kapaliysa hatayi yok say), sonra ayarlayip aciyoruz.
echo DNS Analytical gunlugu etkinlestiriliyor...

wevtutil set-log "Microsoft-Windows-DNSServer/Analytical" /enabled:false >nul 2>&1

wevtutil set-log "Microsoft-Windows-DNSServer/Analytical" /enabled:true /rt:false /q:true
if errorlevel 1 (
    echo.
    echo [HATA] Gunluk acilamadi. Bu makinede DNS Sunucu rolu kurulu mu?
    echo Kanal adi farkliysa su komutla listeleyin:
    echo    wevtutil el ^| findstr /i dns
    pause
    exit /b 1
)

echo.
echo Tamam. Gunluk acik. Durumu gormek icin:
echo    wevtutil gl "Microsoft-Windows-DNSServer/Analytical"
echo.
pause
