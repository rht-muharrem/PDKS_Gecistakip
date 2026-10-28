# Sunucu Durum Paneli (ServerMonitor)

ASP.NET Core Web API + saf HTML/JS dashboard. Sunucularını panelden tanıtırsın; uygulama
ping, TCP port ve HTTP ile kontrol eder, Windows sunuculardan WMI ile CPU / RAM / disk
metriklerini toplar ve hepsini canlı grafiklerde gösterir.

## Hızlı başlangıç

**`baslat.bat`** dosyasına çift tıkla. Script sırasıyla:

1. .NET SDK var mı bakar (yoksa indirme linkini gösterir),
2. `dotnet restore` + `dotnet build` yapar,
3. uygulamayı başlatır ve tarayıcıda `http://localhost:5080` adresini açar.

Durdurmak için o pencerede **Ctrl+C**.

> .NET 9 SDK gerekli: https://dotnet.microsoft.com/download/dotnet/9.0

## Sunucu tanıtma

Panelde sağ üstteki **+ Sunucu ekle** ile:

| Bölüm | Ne yapar |
|---|---|
| **Kimlik** | Görünen ad, IP/hostname, grup (dashboard'da başlık olur), izlemeye dahil mi |
| **Ping** | ICMP kontrolü ve "bu ms'nin üstü yavaş sayılsın" eşiği |
| **TCP portları** | İstediğin kadar port + etiket (SQL 1433, RDP 3389, …) |
| **HTTP kontrolleri** | URL, etiket, beklenen HTTP kodu, self-signed sertifika toleransı |
| **Windows metrikleri** | WMI ile CPU/RAM/disk, uyarı eşikleri, hangi sürücüler |

Kaydettiğin anda o sunucu bir kez kontrol edilir ve dashboard'da belirir. Düzenlemek veya
silmek için karta tıkla → **Düzenle**.

Tanımlar `servers.json` dosyasında tutulur; panelden yaptığın her değişiklik oraya yazılır.
İstersen dosyayı elle de düzenleyebilirsin (o durumda uygulamayı yeniden başlat).

Üst bardaki **Aralık** kutusu arka plan kontrol turunun sıklığını belirler (varsayılan 30 sn).

## Canlı grafikler

Bir sunucu kartına tıklayınca sağdan açılan panelde dört grafik var:

- **Yanıt süresi** (ms)
- **CPU kullanımı** (%)
- **RAM kullanımı** (%)
- **Disk doluluğu** — en dolu sürücü (%)

Her grafiğin altında son / en düşük / ortalama / en yüksek değerler yazar. Panel açıkken
5 saniyede bir kendini günceller, yeni ölçüm geldikçe çizgi ilerler.

Grafikler harici bir kütüphane kullanmaz (saf SVG) — internet erişimi olmayan sunucularda da çalışır.

## Kalıcı geçmiş

Her ölçüm uygulama klasöründeki `history.db` (SQLite) dosyasına yazılır. Uygulama yeniden
başladığında grafikler buradan dolar. Varsayılan saklama süresi 30 gün; eski kayıtlar 6 saatte
bir silinir. **⚙️ Ayarlar → Uptime raporu** sekmesinde son 24 saat / 7 gün / 30 gün için
sunucu bazlı uptime yüzdesi görülür.

## Bildirimler

**⚙️ Ayarlar → Bildirim** sekmesinden:

- **E-posta (SMTP)** — iç posta sunucunuz (relay) veya kimlik doğrulamalı SMTP
- **Teams / Slack webhook** — kanala mesaj

Sunucu, belirlenen sayıda tur **üst üste** sorunlu kalırsa bir kez "🔴 SORUN" mesajı gider;
tekrar ayağa kalkınca bir kez "🟢 DÜZELDİ" mesajı ve kesinti süresi gelir. Sorun sürdükçe tekrar
tekrar mesaj atılmaz. **Test gönder** düğmesi ayarları hemen dener.

SMTP parolası `servers.json` içinde DPAPI ile şifreli tutulur.

## Panel girişi

**⚙️ Ayarlar → Güvenlik** sekmesinden kullanıcı adı + parola belirleyip girişi açabilirsin.
Parola PBKDF2 ile özetlenerek saklanır, düz metin hiçbir yerde tutulmaz. Parola değişince
açık tüm oturumlar kapanır.

**Parolayı unutursan:** `servers.json` içinde `"Auth"` bölümünde `"Enabled": false` yap ve
uygulamayı (veya servisi) yeniden başlat.

## Windows servisi olarak kurulum

Paneli bir sunucuda sürekli çalıştırmak için, o sunucuda **yönetici olarak**:

```bash
servis-kur.bat
```

Script uygulamayı `C:\ServerMonitor` altına yayınlar, "Sunucu Durum Paneli" adıyla gecikmeli
otomatik başlayan bir servis oluşturur, çökerse 1 dk sonra yeniden başlatacak şekilde ayarlar
ve 5080 portu için güvenlik duvarı kuralı ekler. Güncelleme için aynı scripti tekrar
çalıştırman yeterli — `servers.json`, `history.db` ve oturum anahtarları korunur.

**Kurulumdan sonra mutlaka:** Servis varsayılan olarak *Local System* ile çalışır ve uzak
sunuculara WMI ile bağlanamaz. `services.msc` → **Sunucu Durum Paneli** → Özellikler →
**Oturum Aç** sekmesinden WMI yetkili bir domain hesabı seç, servisi yeniden başlat. Ardından
panelde WMI ve SMTP parolalarını **yeniden gir** — DPAPI şifrelemesi hesaba bağlı olduğu için
eski hesapla şifrelenmiş parolalar yeni hesapta çözülemez.

Kaldırmak için: `servis-kaldir.bat` (veriler silinmez).

## API

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/api/status` | Tüm sunucuların son durumu + özet |
| GET | `/api/status/{id}` | Tek sunucunun durumu (geçmişiyle birlikte) |
| GET | `/api/status/{id}/history` | Yalnızca grafik zaman serisi |
| POST | `/api/refresh` | Hemen bir kontrol turu çalıştırır |
| POST | `/api/refresh/{id}` | Tek sunucuyu hemen kontrol eder |
| GET | `/api/servers` | Sunucu tanımları (parola **dönmez**) |
| POST | `/api/servers` | Yeni sunucu ekler |
| PUT | `/api/servers/{id}` | Sunucuyu günceller |
| DELETE | `/api/servers/{id}` | Sunucuyu siler |
| GET / PUT | `/api/settings` | Kontrol aralığı, zaman aşımı, geçmiş boyutu |
| GET | `/healthz` | Uygulamanın kendi sağlığı |

## WMI (uzak Windows metrikleri) için gerekenler

1. Hedef sunucuda güvenlik duvarında **WMI-In** kuralını aç:
   ```bash
   netsh advfirewall firewall set rule group="windows management instrumentation (wmi)" new enable=yes
   ```
2. Uygulamayı çalıştıran hesabın hedefte WMI okuma yetkisi olmalı. En temizi bir servis
   hesabı açıp hedef sunucularda **Performance Monitor Users** ve **Distributed COM Users**
   gruplarına eklemektir (yerel yönetici şart değil).
3. Domain dışındaysan panelde kullanıcı adı/parola gir.
   Aynı makineyi (`localhost`) izlerken bu alanları **boş bırak** — WMI kimlik bilgisi kabul etmez.

### Parolalar nasıl saklanıyor

Panelden girilen WMI parolaları `servers.json` içine **Windows DPAPI ile şifrelenmiş**
olarak yazılır (`enc:` önekiyle) ve API hiçbir zaman geri döndürmez.

Şifreleme uygulamayı çalıştıran **kullanıcı hesabına** bağlıdır. Uygulamayı başka bir hesapla
(örneğin Windows servisi olarak farklı bir kullanıcıyla) çalıştırırsan parolalar çözülemez ve
panelden yeniden girilmeleri gerekir. `servers.json` dosyasını başka bir makineye kopyalarsan
da aynı durum geçerlidir.

## Alan adı erişim raporu (DNS)

Bilgisayarların hangi alan adlarına (youtube.com gibi) eriştiğini, domain controller'ın
DNS günlüğünden okuyarak her bilgisayarın kartında gösterebilirsiniz. **Yalnızca alan adı
sayılır** — tam adres, sayfa içeriği veya açık sekme okunmaz.

Nasıl çalışır: DC'ye küçük bir toplayıcı script (`dns-toplayici.ps1`) kurulur. Bu script
DC'nin kendi DNS günlüğünü okuyup özetler ve panele gönderir. Panel DC'ye uzaktan bağlanmaz.

Kurulum (⚙️ Ayarlar → **DNS / Ağ** sekmesinde hazır komutlarla):

1. **DC'de** DNS sorgu günlüğünü açın (bir kez): `dns-gunlugu-ac.bat` (yönetici).
2. `dns-toplayici.ps1`'i DC'ye kopyalayın (örn. `C:\dns-toplayici.ps1`).
3. **DC'de** 15 dakikada bir çalışacak zamanlanmış görevi kurun (komut ayarlar sekmesinde,
   panelin anahtarı gömülü olarak hazır verilir).

İlk özet ~15 dakika içinde düşer, sonra bilgisayar kartına tıklayınca "en çok erişilen alan
adları" görünür.

> **Yasal not:** Çalışanların internet erişimini denetlemek, önceden **yazılı olarak
> bilgilendirilmiş** olmaları koşuluyla ve amaçla orantılı biçimde mümkündür (KVKK). Alan adı
> düzeyinde denetim genelde savunulabilir; sayfa içeriği veya sekme düzeyinde izleme değildir.

## Ağ / erişim notları

- **Ping**: ICMP birçok ortamda kapalıdır. Kapalıysa ping'i kapatıp port veya HTTP kontrolüne güven.
- Uygulama varsayılan olarak `http://0.0.0.0:5080` dinler (`appsettings.json` → `Kestrel`).
- **Bu panelde kimlik doğrulama yok.** Sunucu ekleme/silme ve WMI kimlik bilgisi girme
  yetkisi paneli açabilen herkeste olur. Sadece iç ağda çalıştır; dışarı açacaksan önüne
  IIS/Nginx koyup HTTPS ve kimlik doğrulama ekle.

## Paketleme ve yayınlama

**`paketle.bat`** → `bin/`, `obj/`, `.vs/` hariç bir üst klasöre `ServerMonitor.zip` üretir.

```bash
dotnet publish -c Release -o C:\inetpub\ServerMonitor
```

Windows servisi olarak:

```bash
sc.exe create ServerMonitor binPath= "C:\inetpub\ServerMonitor\ServerMonitor.exe" start= auto obj= "DOMAIN\izleme" password= "gizli-parola"
```

Servis hesabı olarak WMI yetkili bir hesap verirsen panelde ayrıca parola girmen gerekmez.

## Dosya yapısı

```
ServerMonitor/
├─ baslat.bat            → çift tıkla, uygulama açılsın
├─ paketle.bat           → çift tıkla, zip oluşsun
├─ servers.json          → sunucu tanımları (panel buraya yazar)
├─ appsettings.json      → port ve log ayarları
├─ Program.cs            → DI + API endpoint'leri
├─ Models/               → konfigürasyon ve durum modelleri
├─ Services/
│  ├─ ServerRepository.cs     → servers.json okuma/yazma, doğrulama
│  ├─ SecretProtector.cs      → parolaların DPAPI ile şifrelenmesi
│  ├─ NetworkProbe.cs         → ping / TCP / HTTP
│  ├─ WindowsMetricsProbe.cs  → WMI metrikleri
│  ├─ MonitorService.cs       → tur yönetimi, durum birleştirme, zaman serisi
│  ├─ MonitorWorker.cs        → periyodik arka plan servisi
│  └─ StatusStore.cs          → bellek içi durum deposu
└─ wwwroot/              → dashboard (HTML/CSS/JS, saf SVG grafikler)
```

