using System.Collections.Concurrent;
using System.Management;
using System.Net;
using System.Runtime.Versioning;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>
/// Uzak (veya yerel) Windows makinelerden WMI/CIM uzerinden CPU, RAM, disk ve uptime bilgisi toplar.
/// Yalnizca Windows uzerinde calisir; baska platformda Error dolu bir snapshot doner.
/// </summary>
public sealed class WindowsMetricsProbe
{
    private readonly ILogger<WindowsMetricsProbe> _logger;

    public WindowsMetricsProbe(ILogger<WindowsMetricsProbe> logger) => _logger = logger;

    public Task<MetricsSnapshot> GetAsync(string host, WindowsMetricsOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new MetricsSnapshot
            {
                Error = "WMI metrikleri yalnizca Windows uzerinde toplanabilir."
            });
        }

        // WMI cagrilari senkron ve bloklayicidir; thread pool'a tasiyoruz.
        return Task.Run(() => Collect(host, options), ct);
    }

    // =====================================================
    //  Calisan uygulamalar
    // =====================================================

    /// <summary>
    /// Cihazda calisan islemleri okuyup kullaniciya ait uygulamalari ayiklar.
    /// Her turda degil, sadece panelde detay acilinca cagrilir. Komut satiri ve pencere
    /// basliklari bilerek okunmaz (parola / belge adi gibi hassas bilgi icerebilir).
    /// </summary>
    public Task<ProcessSnapshot> GetProcessesAsync(string host, WindowsMetricsOptions options, bool includeBackground, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new ProcessSnapshot
            {
                Error = "İşlem listesi yalnızca Windows üzerinde okunabilir."
            });
        }

        return Task.Run(() => CollectProcesses(host, options, includeBackground), ct);
    }

    private sealed class AppAccumulator
    {
        public required string Name;
        public int Instances;
        public long WorkingSet;
        public DateTimeOffset? Started;
    }

    [SupportedOSPlatform("windows")]
    private ProcessSnapshot CollectProcesses(string host, WindowsMetricsOptions options, bool includeBackground)
    {
        try
        {
            var scope = CreateScope(host, options);
            scope.Connect();

            string? userName = null;
            foreach (var computer in Query(scope, "SELECT UserName FROM Win32_ComputerSystem", options))
                userName = computer["UserName"]?.ToString();

            var total = 0;
            var apps = new Dictionary<string, AppAccumulator>(StringComparer.OrdinalIgnoreCase);

            const string query = "SELECT Name, SessionId, WorkingSetSize, CreationDate, ExecutablePath FROM Win32_Process";

            foreach (var process in Query(scope, query, options))
            {
                total++;

                var name = process["Name"]?.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                var path = process["ExecutablePath"]?.ToString();
                var session = Convert.ToInt64(process["SessionId"]);

                if (!includeBackground && IsBackgroundProcess(name, path, session))
                    continue;

                if (!apps.TryGetValue(name, out var app))
                {
                    app = new AppAccumulator { Name = name };
                    apps[name] = app;
                }

                app.Instances++;
                app.WorkingSet += (long)(ToDouble(process["WorkingSetSize"]) ?? 0);

                var started = ParseWmiDate(process["CreationDate"]);
                if (started is not null && (app.Started is null || started < app.Started))
                    app.Started = started;
            }

            return new ProcessSnapshot
            {
                UserName = string.IsNullOrWhiteSpace(userName) ? null : userName,
                TotalProcesses = total,
                Apps = apps.Values
                    .OrderByDescending(a => a.WorkingSet)
                    .Select(a => new AppProcess
                    {
                        Name = a.Name,
                        DisplayName = FriendlyName(a.Name),
                        Instances = a.Instances,
                        WorkingSetBytes = a.WorkingSet,
                        StartedUtc = a.Started
                    })
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Islem listesi okunamadi: {Host}", host);
            return new ProcessSnapshot { Error = Describe(ex) };
        }
    }

    /// <summary>Kullanicinin actigi bir uygulama degil, isletim sistemi veya arka plan islemi mi?</summary>
    private static bool IsBackgroundProcess(string name, string? path, long session)
    {
        // Session 0: servisler ve sistem islemleri. Kullanici uygulamalari 1 ve uzeri oturumlarda calisir.
        if (session == 0)
            return true;

        if (UserFacingSystemApps.Contains(name))
            return false;

        if (BackgroundProcessNames.Contains(name))
            return true;

        // C:\Windows altindaki her sey (SystemApps dahil) kabuk/arka plan bilesenidir.
        // "\WindowsApps\" (Store uygulamalari, yeni Teams vb.) bu kontrole takilmaz.
        return path is not null && path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>C:\Windows altinda olmasina ragmen kullanicinin bilerek actigi programlar.</summary>
    private static readonly HashSet<string> UserFacingSystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad.exe", "mstsc.exe", "mspaint.exe", "taskmgr.exe", "powershell.exe", "powershell_ise.exe",
        "write.exe", "wordpad.exe", "calc.exe", "snippingtool.exe", "mmc.exe", "regedit.exe", "charmap.exe"
    };

    /// <summary>Program Files altinda calisan ama kullanicinin "acik uygulama" olarak gormedigi yardimcilar.</summary>
    private static readonly HashSet<string> BackgroundProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Idle Process", "System", "Registry", "Memory Compression", "Secure System",
        "explorer.exe", "msedgewebview2.exe", "crashpad_handler.exe", "GoogleCrashHandler.exe", "GoogleCrashHandler64.exe",
        "MicrosoftEdgeUpdate.exe", "GoogleUpdate.exe", "updater.exe", "vmtoolsd.exe", "NVDisplay.Container.exe",
        "OneDrive.Sync.Service.exe", "FileCoAuth.exe", "OfficeClickToRun.exe", "sdxhelper.exe", "AdobeARM.exe",
        "jusched.exe", "igfxEM.exe", "RtkAudUService64.exe", "SecurityHealthSystray.exe", "ms-teamsupdate.exe"
    };

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["firefox.exe"] = "Mozilla Firefox",
        ["opera.exe"] = "Opera",
        ["brave.exe"] = "Brave",
        ["winword.exe"] = "Microsoft Word",
        ["excel.exe"] = "Microsoft Excel",
        ["powerpnt.exe"] = "Microsoft PowerPoint",
        ["outlook.exe"] = "Microsoft Outlook",
        ["olk.exe"] = "Outlook (yeni)",
        ["onenote.exe"] = "Microsoft OneNote",
        ["msaccess.exe"] = "Microsoft Access",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["teams.exe"] = "Microsoft Teams (klasik)",
        ["onedrive.exe"] = "OneDrive",
        ["acrord32.exe"] = "Adobe Acrobat Reader",
        ["acrobat.exe"] = "Adobe Acrobat",
        ["photoshop.exe"] = "Adobe Photoshop",
        ["anydesk.exe"] = "AnyDesk",
        ["teamviewer.exe"] = "TeamViewer",
        ["mstsc.exe"] = "Uzak Masaüstü Bağlantısı",
        ["whatsapp.exe"] = "WhatsApp",
        ["telegram.exe"] = "Telegram",
        ["zoom.exe"] = "Zoom",
        ["spotify.exe"] = "Spotify",
        ["notepad.exe"] = "Not Defteri",
        ["notepad++.exe"] = "Notepad++",
        ["mspaint.exe"] = "Paint",
        ["taskmgr.exe"] = "Görev Yöneticisi",
        ["powershell.exe"] = "Windows PowerShell",
        ["windowsterminal.exe"] = "Windows Terminal",
        ["code.exe"] = "Visual Studio Code",
        ["devenv.exe"] = "Visual Studio",
        ["ssms.exe"] = "SQL Server Management Studio",
        ["mmc.exe"] = "Yönetim Konsolu (MMC)",
        ["putty.exe"] = "PuTTY",
        ["winscp.exe"] = "WinSCP",
        ["winrar.exe"] = "WinRAR",
        ["7zfm.exe"] = "7-Zip",
        ["vlc.exe"] = "VLC",
        ["calc.exe"] = "Hesap Makinesi",
        ["calculatorapp.exe"] = "Hesap Makinesi"
    };

    private static string FriendlyName(string exeName)
    {
        if (FriendlyNames.TryGetValue(exeName, out var friendly))
            return friendly;

        var bare = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        return bare.Length == 0 ? exeName : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ParseWmiDate(object? value)
    {
        if (value is not string text || text.Length == 0)
            return null;

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text)).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    // =====================================================
    //  Kurulu programlar (registry Uninstall anahtarlari)
    // =====================================================

    private const uint HKLM = 0x80000002;
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<InstalledProgramsResult> GetInstalledProgramsAsync(string host, WindowsMetricsOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new InstalledProgramsResult { Error = "Yalnizca Windows uzerinde okunabilir." });

        return Task.Run(() => CollectPrograms(host, options), ct);
    }

    [SupportedOSPlatform("windows")]
    private InstalledProgramsResult CollectPrograms(string host, WindowsMetricsOptions options)
    {
        try
        {
            // Registry StdRegProv "root\default" altinda.
            var scope = CreateScope(host, options, "root\\default");
            scope.Connect();
            var reg = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);

            var byName = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);

            foreach (var (path, is32) in new[] { (UninstallPath, false), (UninstallPath32, true) })
            {
                foreach (var sub in RegEnumKeys(reg, HKLM, path))
                {
                    var keyPath = path + "\\" + sub;

                    var name = RegGetString(reg, HKLM, keyPath, "DisplayName");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // Sistem bilesenlerini ele (her anahtar icin ekstra sorgu sayisini dusuk tutuyoruz).
                    if (RegGetDword(reg, HKLM, keyPath, "SystemComponent") == 1)
                        continue;

                    var program = new InstalledProgram
                    {
                        Name = name.Trim(),
                        Version = RegGetString(reg, HKLM, keyPath, "DisplayVersion"),
                        Publisher = RegGetString(reg, HKLM, keyPath, "Publisher"),
                        InstallDate = ParseInstallDate(RegGetString(reg, HKLM, keyPath, "InstallDate")),
                        Is32Bit = is32
                    };

                    // Ayni program iki dalda gorunebilir; ad+surum ile tekilleştir.
                    byName[program.Name + "|" + program.Version] = program;
                }
            }

            return new InstalledProgramsResult
            {
                Programs = byName.Values
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kurulu programlar okunamadi: {Host}", host);
            return new InstalledProgramsResult { Error = Describe(ex) };
        }
    }

    // =====================================================
    //  Cihaz kunye bilgileri (hostname, MAC, marka/model...)
    // =====================================================

    public Task<DeviceInfo> GetDeviceInfoAsync(string host, WindowsMetricsOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new DeviceInfo { Error = "Yalnizca Windows uzerinde okunabilir." });

        return Task.Run(() => CollectDeviceInfo(host, options), ct);
    }

    [SupportedOSPlatform("windows")]
    private DeviceInfo CollectDeviceInfo(string host, WindowsMetricsOptions options)
    {
        try
        {
            var scope = CreateScope(host, options);
            scope.Connect();

            string? hostname = null, domain = null, manufacturer = null, model = null, user = null;
            long? memory = null;
            int? procs = null;

            foreach (var cs in Query(scope, "SELECT Name, Domain, Manufacturer, Model, TotalPhysicalMemory, UserName, NumberOfLogicalProcessors FROM Win32_ComputerSystem", options))
            {
                hostname = cs["Name"]?.ToString();
                domain = cs["Domain"]?.ToString();
                manufacturer = cs["Manufacturer"]?.ToString()?.Trim();
                model = cs["Model"]?.ToString()?.Trim();
                user = cs["UserName"]?.ToString();
                memory = (long?)ToDouble(cs["TotalPhysicalMemory"]);
                procs = (int?)ToDouble(cs["NumberOfLogicalProcessors"]);
            }

            string? os = null, osArch = null, osBuild = null;
            DateTimeOffset? osInstall = null, lastBoot = null;

            foreach (var o in Query(scope, "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem", options))
            {
                os = o["Caption"]?.ToString()?.Trim();
                osBuild = o["BuildNumber"]?.ToString();
                osArch = o["OSArchitecture"]?.ToString();
                osInstall = ParseWmiDate(o["InstallDate"]);
                lastBoot = ParseWmiDate(o["LastBootUpTime"]);
            }

            string? serial = null, biosVer = null;
            DateTimeOffset? biosDate = null;

            foreach (var b in Query(scope, "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", options))
            {
                serial = b["SerialNumber"]?.ToString()?.Trim();
                biosVer = b["SMBIOSBIOSVersion"]?.ToString()?.Trim();
                biosDate = ParseWmiDate(b["ReleaseDate"]);
            }

            string? cpu = null;
            foreach (var p in Query(scope, "SELECT Name FROM Win32_Processor", options))
            {
                cpu = p["Name"]?.ToString()?.Trim();
                break;
            }

            var adapters = new List<NetworkAdapterInfo>();
            foreach (var n in Query(scope, "SELECT Description, MACAddress, IPAddress, DefaultIPGateway, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE", options))
            {
                var mac = n["MACAddress"]?.ToString();
                var ips = (n["IPAddress"] as string[])?
                    .Where(ip => !string.IsNullOrWhiteSpace(ip) && ip.Contains('.'))   // IPv4'e odaklan
                    .ToList() ?? new List<string>();
                var gw = (n["DefaultIPGateway"] as string[])?.FirstOrDefault();

                adapters.Add(new NetworkAdapterInfo
                {
                    Name = n["Description"]?.ToString()?.Trim() ?? "Ağ bağdaştırıcısı",
                    Mac = string.IsNullOrWhiteSpace(mac) ? null : mac,
                    IpAddresses = ips,
                    Gateway = gw,
                    Dhcp = n["DHCPEnabled"] is bool dhcp && dhcp
                });
            }

            // Antivirüs — yalnızca istemci OS'larda root\SecurityCenter2 vardır (sunucularda yok).
            var antivirus = new List<AntivirusInfo>();
            try
            {
                var sc = CreateScope(host, options, "root\\SecurityCenter2");
                sc.Connect();
                foreach (var av in Query(sc, "SELECT displayName, productState FROM AntiVirusProduct", options))
                {
                    var avName = av["displayName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(avName))
                        continue;
                    var (enabled, upToDate) = DecodeAvState(av["productState"]);
                    antivirus.Add(new AntivirusInfo { Name = avName.Trim(), Enabled = enabled, UpToDate = upToDate });
                }
            }
            catch { /* sunucularda veya SecurityCenter2 yoksa atla */ }

            // Yazıcılar
            var printers = new List<PrinterInfo>();
            foreach (var pr in Query(scope, "SELECT Name, Default, Network, PortName, WorkOffline FROM Win32_Printer", options))
            {
                var prName = pr["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(prName))
                    continue;
                printers.Add(new PrinterInfo
                {
                    Name = prName.Trim(),
                    IsDefault = pr["Default"] is bool def && def,
                    IsNetwork = pr["Network"] is bool net && net,
                    Port = pr["PortName"]?.ToString(),
                    Offline = pr["WorkOffline"] is bool off && off
                });
            }

            return new DeviceInfo
            {
                Hostname = hostname,
                Domain = string.IsNullOrWhiteSpace(domain) ? null : domain,
                Antivirus = antivirus,
                Printers = printers.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                Manufacturer = manufacturer,
                Model = model,
                SerialNumber = string.IsNullOrWhiteSpace(serial) || serial is "System Serial Number" or "To be filled by O.E.M." ? null : serial,
                Cpu = cpu,
                LogicalProcessors = procs,
                MemoryBytes = memory,
                Os = os,
                OsArchitecture = osArch,
                OsBuild = osBuild,
                OsInstallDate = osInstall,
                LastBootUtc = lastBoot,
                BiosVersion = biosVer,
                BiosDate = biosDate,
                LoggedInUser = string.IsNullOrWhiteSpace(user) ? null : user,
                Adapters = adapters
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cihaz bilgileri okunamadi: {Host}", host);
            return new DeviceInfo { Error = Describe(ex) };
        }
    }

    /// <summary>
    /// SecurityCenter2 productState bit maskesini cozer. 6 haneli hex: [saglayici][gercek zamanli][imza].
    /// Ortadaki bayt 10/11 = koruma acik; son bayt 00 = imzalar guncel, 10 = guncel degil.
    /// </summary>
    private static (bool Enabled, bool UpToDate) DecodeAvState(object? state)
    {
        try
        {
            var hex = Convert.ToInt32(state).ToString("X6");
            var enabled = hex.Substring(2, 2) is "10" or "11";
            var upToDate = hex.Substring(4, 2) == "00";
            return (enabled, upToDate);
        }
        catch
        {
            return (false, false);
        }
    }

    // =====================================================
    //  USB depolama aygitlari (takili + gecmis)
    // =====================================================

    private const string UsbStorPath = @"SYSTEM\CurrentControlSet\Enum\USBSTOR";

    // Aygit ozellikleri: baglanma/ayrilma zaman damgalari (FILETIME).
    private const string PropGuid = "{83da6326-97a6-4088-9453-a1923f573b29}";
    private const string PropInstall = "0064";   // ilk kurulum
    private const string PropArrival = "0066";   // son baglanma
    private const string PropRemoval = "0067";   // son ayrilma

    public Task<UsbResult> GetUsbAsync(string host, WindowsMetricsOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new UsbResult { Error = "Yalnizca Windows uzerinde okunabilir." });

        return Task.Run(() => CollectUsb(host, options), ct);
    }

    [SupportedOSPlatform("windows")]
    private UsbResult CollectUsb(string host, WindowsMetricsOptions options)
    {
        try
        {
            // Su an takili USB diskler icin cimv2, gecmis icin registry.
            var cim = CreateScope(host, options);
            cim.Connect();

            var connectedSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentDisks = new List<UsbDevice>();

            foreach (var disk in Query(cim, "SELECT Model, Size, SerialNumber, PNPDeviceID FROM Win32_DiskDrive WHERE InterfaceType='USB'", options))
            {
                var serial = CleanSerial(disk["SerialNumber"]?.ToString() ?? ExtractSerial(disk["PNPDeviceID"]?.ToString()));
                if (serial is not null)
                    connectedSerials.Add(serial);

                currentDisks.Add(new UsbDevice
                {
                    Name = disk["Model"]?.ToString()?.Trim() ?? "USB Disk",
                    Serial = serial,
                    SizeBytes = (long?)ToDouble(disk["Size"]),
                    Connected = true
                });
            }

            // Gecmis: USBSTOR altindaki her aygit.
            var reg = new ManagementClass(CreateRegScope(host, options), new ManagementPath("StdRegProv"), null);
            var history = new List<UsbDevice>();

            foreach (var deviceClass in RegEnumKeys(reg, HKLM, UsbStorPath))
            {
                var classPath = UsbStorPath + "\\" + deviceClass;

                foreach (var instance in RegEnumKeys(reg, HKLM, classPath))
                {
                    var instPath = classPath + "\\" + instance;
                    var friendly = RegGetString(reg, HKLM, instPath, "FriendlyName")
                                   ?? DeviceClassToName(deviceClass);

                    var serial = CleanSerial(instance);
                    var propBase = instPath + "\\Properties\\" + PropGuid;

                    history.Add(new UsbDevice
                    {
                        Name = friendly,
                        Serial = serial,
                        Connected = serial is not null && connectedSerials.Contains(serial),
                        FirstConnectedUtc = ReadUsbTimestamp(reg, propBase, PropInstall),
                        LastConnectedUtc = ReadUsbTimestamp(reg, propBase, PropArrival),
                        LastRemovedUtc = ReadUsbTimestamp(reg, propBase, PropRemoval)
                    });
                }
            }

            // Takili olan ama gecmiste gorunmeyenleri (nadir) de ekle.
            foreach (var disk in currentDisks)
                if (disk.Serial is null || !history.Any(h => string.Equals(h.Serial, disk.Serial, StringComparison.OrdinalIgnoreCase)))
                    history.Add(disk);

            return new UsbResult
            {
                Devices = history
                    .OrderByDescending(d => d.Connected)
                    .ThenByDescending(d => d.LastConnectedUtc ?? DateTimeOffset.MinValue)
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "USB gecmisi okunamadi: {Host}", host);
            return new UsbResult { Error = Describe(ex) };
        }
    }

    [SupportedOSPlatform("windows")]
    private static ManagementScope CreateRegScope(string host, WindowsMetricsOptions options)
    {
        var scope = CreateScope(host, options, "root\\default");
        scope.Connect();
        return scope;
    }

    // =====================================================
    //  StdRegProv (uzak registry) yardimcilari
    // =====================================================

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegEnumKeys(ManagementClass reg, uint hive, string path)
    {
        var inParams = reg.GetMethodParameters("EnumKey");
        inParams["hDefKey"] = hive;
        inParams["sSubKeyName"] = path;

        using var outParams = reg.InvokeMethod("EnumKey", inParams, null);
        if (outParams?["sNames"] is string[] names)
            return names;
        return Array.Empty<string>();
    }

    [SupportedOSPlatform("windows")]
    private static string? RegGetString(ManagementClass reg, uint hive, string path, string name)
    {
        try
        {
            var inParams = reg.GetMethodParameters("GetStringValue");
            inParams["hDefKey"] = hive;
            inParams["sSubKeyName"] = path;
            inParams["sValueName"] = name;

            using var outParams = reg.InvokeMethod("GetStringValue", inParams, null);
            return outParams?["sValue"]?.ToString();
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static uint? RegGetDword(ManagementClass reg, uint hive, string path, string name)
    {
        try
        {
            var inParams = reg.GetMethodParameters("GetDWORDValue");
            inParams["hDefKey"] = hive;
            inParams["sSubKeyName"] = path;
            inParams["sValueName"] = name;

            using var outParams = reg.InvokeMethod("GetDWORDValue", inParams, null);
            var value = outParams?["uValue"];
            return value is null ? null : Convert.ToUInt32(value);
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? RegGetBinary(ManagementClass reg, uint hive, string path, string name)
    {
        try
        {
            var inParams = reg.GetMethodParameters("GetBinaryValue");
            inParams["hDefKey"] = hive;
            inParams["sSubKeyName"] = path;
            inParams["sValueName"] = name;

            using var outParams = reg.InvokeMethod("GetBinaryValue", inParams, null);
            if (outParams?["uValue"] is byte[] bytes && bytes.Length > 0)
                return bytes;
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Aygit ozelligindeki FILETIME zaman damgasini okur. Kayit duzeni Windows surumune gore
    /// degistiginden birkac olasi yerlesimi sirayla dener; hicbiri tutmazsa null (aygit listesi
    /// yine calisir, sadece tarih bos gorunur).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ReadUsbTimestamp(ManagementClass reg, string propBase, string pid)
    {
        // Windows sürümüne göre değişen olası yerleşimler:
        // 1) {GUID}\0066 anahtarı altında "00000000" adlı değer (Win10/11'de en yaygın)
        var t = FileTimeFromBinary(RegGetBinary(reg, HKLM, propBase + "\\" + pid, "00000000"));
        // 2) {GUID}\0066 altında "Data"
        t ??= FileTimeFromBinary(RegGetBinary(reg, HKLM, propBase + "\\" + pid, "Data"));
        // 3) {GUID}\0066 altında varsayılan değer
        t ??= FileTimeFromBinary(RegGetBinary(reg, HKLM, propBase + "\\" + pid, ""));
        // 4) {GUID} altında doğrudan "0066" adlı değer
        t ??= FileTimeFromBinary(RegGetBinary(reg, HKLM, propBase, pid));
        return t;
    }

    // --- Kucuk cevirimler ---

    private static DateTimeOffset? FileTimeFromBinary(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 8)
            return null;
        try
        {
            var ticks = BitConverter.ToInt64(bytes, 0);
            if (ticks <= 0)
                return null;
            return DateTimeOffset.FromFileTime(ticks);
        }
        catch { return null; }
    }

    // Uninstall InstallDate genelde "yyyyMMdd" biciminde.
    private static DateTimeOffset? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8)
            return null;
        return DateTimeOffset.TryParseExact(raw, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }

    // USBSTOR instance / serial temizligi: sondaki "&0" gibi ekleri at.
    private static string? CleanSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        var amp = s.IndexOf('&');
        if (amp > 0 && s.Length - amp <= 3)   // sondaki "&0"
            s = s[..amp];
        return s.Length == 0 ? null : s;
    }

    private static string? ExtractSerial(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
            return null;
        var parts = pnpDeviceId.Split('\\');
        return parts.Length > 0 ? CleanSerial(parts[^1]) : null;
    }

    // "Disk&Ven_SanDisk&Prod_Cruzer&Rev_1.00" -> "SanDisk Cruzer"
    private static string DeviceClassToName(string deviceClass)
    {
        string Pick(string tag)
        {
            foreach (var part in deviceClass.Split('&'))
                if (part.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                    return part[tag.Length..].Replace('_', ' ').Trim();
            return "";
        }

        var name = (Pick("Ven_") + " " + Pick("Prod_")).Trim();
        return name.Length == 0 ? deviceClass : name;
    }

    [SupportedOSPlatform("windows")]
    private MetricsSnapshot Collect(string host, WindowsMetricsOptions options)
    {
        try
        {
            var scope = CreateScope(host, options);
            scope.Connect();

            var (cpu, cpuErr) = TryGetCpu(scope, options);
            var (memPercent, memTotal, memFree, uptime, osName) = GetOsInfo(scope, options);
            var drives = GetDrives(scope, options);

            return new MetricsSnapshot
            {
                CpuPercent = cpu,
                MemoryPercent = memPercent,
                MemoryTotalBytes = memTotal,
                MemoryFreeBytes = memFree,
                Drives = drives,
                Uptime = uptime,
                OsName = osName,
                Error = cpuErr
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI baglantisi basarisiz: {Host}", host);
            return new MetricsSnapshot { Error = Describe(ex) };
        }
    }

    [SupportedOSPlatform("windows")]
    private static ManagementScope CreateScope(string host, WindowsMetricsOptions options, string ns = "root\\cimv2")
    {
        var connection = new ConnectionOptions
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs),
            EnablePrivileges = true,
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy
        };

        // Yerel makinede kimlik bilgisi verilemez; WMI hata firlatir.
        if (!IsLocal(host) && !string.IsNullOrWhiteSpace(options.Username))
        {
            connection.Username = options.Username;
            // Parola dosyada DPAPI ile sifreli tutulur.
            connection.Password = SecretProtector.Unprotect(options.Password);
        }

        var target = IsLocal(host) ? "." : ResolveWmiTarget(host);
        return new ManagementScope(new ManagementPath($@"\\{target}\{ns}"), connection);
    }

    private static readonly ConcurrentDictionary<string, string> ResolvedHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WMI'ye IP ile baglanmak Kerberos'u devre disi birakir - IP icin SPN olmadigindan NTLM'e
    /// duser ve DCOM bunu reddedebilir ("Erisim engellendi"). Hedef bir IP ise ters DNS ile
    /// makine adini bulup onu kullaniyoruz; PTR kaydi yoksa IP ile devam edilir.
    /// </summary>
    private static string ResolveWmiTarget(string host) =>
        ResolvedHosts.GetOrAdd(host.Trim(), candidate =>
        {
            if (!IPAddress.TryParse(candidate, out var ip))
                return candidate;

            try
            {
                var entry = Dns.GetHostEntry(ip);
                return string.IsNullOrWhiteSpace(entry.HostName) ? candidate : entry.HostName;
            }
            catch
            {
                return candidate;
            }
        });

    /// <summary>
    /// Makinenin kendisini gosteren tum adresler. WMI, yerel baglantilarda kimlik bilgisi
    /// kabul etmez (0x80041064) - bu yuzden hedefin kendi IP'miz olup olmadigini bilmek sart.
    /// </summary>
    private static readonly Lazy<HashSet<string>> LocalAddresses = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".", "localhost", "127.0.0.1", "::1", Environment.MachineName
        };

        try
        {
            var hostName = Dns.GetHostName();
            set.Add(hostName);

            // FQDN (or. pdks-server.gen.local)
            try { set.Add(Dns.GetHostEntry(hostName).HostName); } catch { /* DNS yoksa atla */ }

            foreach (var address in Dns.GetHostAddresses(hostName))
                set.Add(address.ToString());
        }
        catch
        {
            // Adresler alinamazsa elimizdeki temel liste ile devam.
        }

        return set;
    });

    private static bool IsLocal(string host) =>
        string.IsNullOrWhiteSpace(host) || LocalAddresses.Value.Contains(host.Trim());

    [SupportedOSPlatform("windows")]
    private (double? Cpu, string? Error) TryGetCpu(ManagementScope scope, WindowsMetricsOptions options)
    {
        // Once performans sayaci (anlik ve dogru), olmazsa Win32_Processor.LoadPercentage.
        try
        {
            var values = Query(scope, "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'", options)
                .Select(o => ToDouble(o["PercentProcessorTime"]))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (values.Count > 0)
                return (Math.Round(values[0], 1), null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PerfFormattedData okunamadi, Win32_Processor'a dusuluyor.");
        }

        try
        {
            var loads = Query(scope, "SELECT LoadPercentage FROM Win32_Processor", options)
                .Select(o => ToDouble(o["LoadPercentage"]))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (loads.Count > 0)
                return (Math.Round(loads.Average(), 1), null);

            return (null, "CPU degeri okunamadi.");
        }
        catch (Exception ex)
        {
            return (null, $"CPU okunamadi: {Describe(ex)}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (double? Percent, long? Total, long? Free, TimeSpan? Uptime, string? OsName) GetOsInfo(
        ManagementScope scope, WindowsMetricsOptions options)
    {
        const string query =
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime, Caption FROM Win32_OperatingSystem";

        foreach (var os in Query(scope, query, options))
        {
            // Bu iki deger kilobyte cinsindendir.
            var totalKb = ToDouble(os["TotalVisibleMemorySize"]);
            var freeKb = ToDouble(os["FreePhysicalMemory"]);

            double? percent = null;
            long? totalBytes = null;
            long? freeBytes = null;

            if (totalKb is > 0 && freeKb is not null)
            {
                totalBytes = (long)(totalKb.Value * 1024);
                freeBytes = (long)(freeKb.Value * 1024);
                percent = Math.Round((totalKb.Value - freeKb.Value) * 100.0 / totalKb.Value, 1);
            }

            TimeSpan? uptime = null;
            if (os["LastBootUpTime"] is string boot && boot.Length > 0)
            {
                try
                {
                    uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(boot);
                }
                catch
                {
                    // Format bozuksa uptime'i atla.
                }
            }

            return (percent, totalBytes, freeBytes, uptime, os["Caption"]?.ToString()?.Trim());
        }

        return (null, null, null, null, null);
    }

    [SupportedOSPlatform("windows")]
    private static List<DriveUsage> GetDrives(ManagementScope scope, WindowsMetricsOptions options)
    {
        var result = new List<DriveUsage>();

        // DriveType = 3 => yerel sabit disk
        foreach (var disk in Query(scope, "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3", options))
        {
            var id = disk["DeviceID"]?.ToString();
            if (string.IsNullOrEmpty(id))
                continue;

            if (options.Drives.Count > 0 &&
                !options.Drives.Any(d => d.TrimEnd('\\').Equals(id, StringComparison.OrdinalIgnoreCase)))
                continue;

            var size = ToDouble(disk["Size"]) ?? 0;
            var free = ToDouble(disk["FreeSpace"]) ?? 0;
            if (size <= 0)
                continue;

            result.Add(new DriveUsage
            {
                Name = id,
                TotalBytes = (long)size,
                FreeBytes = (long)free
            });
        }

        return result.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string query, WindowsMetricsOptions options)
    {
        // System.IO.EnumerationOptions ile karismamasi icin tam ad kullaniliyor.
        var enumOptions = new System.Management.EnumerationOptions
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs),
            ReturnImmediately = true,
            Rewindable = false
        };

        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), enumOptions);
        using var collection = searcher.Get();

        foreach (ManagementBaseObject item in collection)
        {
            yield return item;
        }
    }

    private static double? ToDouble(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hatayi teshis edilebilir hale getirir: hangi katmanda takildigini soyleyen bir aciklama
    /// ve ham HRESULT. Kodu bilmek guvenlik duvari sorunu ile yetki sorununu ayirmanin tek yolu.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var baseEx = ex.GetBaseException();

        // ManagementStatus degerleri zaten HRESULT'tir.
        var hresult = baseEx is ManagementException me ? (int)me.ErrorCode : baseEx.HResult;

        var hint = (uint)hresult switch
        {
            0x80070005 => "Erisim reddedildi. Hesap hedefte yerel yonetici degil ya da DCOM uzaktan baslatma izni yok.",
            0x80041003 => "WMI yetkisi yok. root\\cimv2 uzerinde 'Remote Enable' izni gerekiyor.",
            0x800706BA => "RPC sunucusuna ulasilamadi. Hedefte WMI-In guvenlik duvari kurali kapali veya makine erisilemez.",
            0x800706BE => "RPC cagrisi basarisiz oldu (aglayan baglanti veya DCOM kisitlamasi).",
            0x80070776 => "Hedef adi cozulemedi.",
            0x8004100A => "WMI deposu kritik hata verdi (hedefte WMI servisi sorunlu).",
            0x80041064 => "Yerel makineye baglanirken kullanici adi/parola verilemez. Bu alanlari bos birakin.",
            0x800704F1 => "Oturum acilamadi; kimlik bilgisi reddedildi.",
            0x8001011C => "DCOM uzaktan erisim reddedildi.",
            _ => baseEx.Message
        };

        return $"{hint} (0x{hresult:X8})";
    }
}
