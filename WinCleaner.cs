// Win Cleaner by mysik — очистка мусора, настройки Windows (реклама, телеметрия, Bing…) и автозагрузка.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mysik;

[assembly: AssemblyTitle("Win Cleaner")]
[assembly: AssemblyDescription("Win Cleaner by mysik — очистка и настройка Windows")]
[assembly: AssemblyCompany("mysik")]
[assembly: AssemblyProduct("Win Cleaner by mysik")]
[assembly: AssemblyCopyright("by mysik")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WinCleaner
{
    static class Program
    {
        [STAThread]
        static void Main() { Boot.Run("Win Cleaner", "WinCleanerByMysik_SingleInstance", () => new MainWindow()); }
    }

    // ------------------------------------------------------------------ реестр

    static class Reg
    {
        public static RegistryKey Base(RegistryHive hive) { return RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); }

        public static object Get(RegistryHive hive, string path, string name)
        {
            try
            {
                using (var b = Base(hive))
                using (var k = b.OpenSubKey(path))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        public static int? Dword(RegistryHive hive, string path, string name)
        {
            var v = Get(hive, path, name);
            if (v is int) return (int)v;
            int n;
            if (v is string && int.TryParse((string)v, out n)) return n;
            return null;
        }

        public static string Str(RegistryHive hive, string path, string name)
        {
            var v = Get(hive, path, name);
            return v == null ? null : v.ToString();
        }

        public static void Set(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
        {
            using (var b = Base(hive))
            using (var k = b.CreateSubKey(path))
                k.SetValue(name, value, kind);
        }

        public static void SetDword(RegistryHive hive, string path, string name, int value) { Set(hive, path, name, value, RegistryValueKind.DWord); }
        public static void SetStr(RegistryHive hive, string path, string name, string value) { Set(hive, path, name, value, RegistryValueKind.String); }

        public static void Delete(RegistryHive hive, string path, string name)
        {
            try
            {
                using (var b = Base(hive))
                using (var k = b.OpenSubKey(path, true))
                    if (k != null) k.DeleteValue(name, false);
            }
            catch { }
        }

        public static bool KeyExists(RegistryHive hive, string path)
        {
            try
            {
                using (var b = Base(hive))
                using (var k = b.OpenSubKey(path))
                    return k != null;
            }
            catch { return false; }
        }

        public static void DeleteTree(RegistryHive hive, string path)
        {
            try { using (var b = Base(hive)) b.DeleteSubKeyTree(path, false); } catch { }
        }
    }

    static class Native
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        [StructLayout(LayoutKind.Sequential)] // раскладка 64-битной Windows
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

        public static long RecycleBinSize()
        {
            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
    }

    // ------------------------------------------------------------------ очистка

    class CleanCat
    {
        public string Name, Desc;
        public bool DefaultOn, IsRecycle, StopUpdate;
        public int MinAgeHours;
        public Func<List<string>> Dirs = () => new List<string>();
        public Func<List<string>> Files = () => new List<string>();
        public long Size;
        public CheckBox Box;
        public TextBlock SizeText;
    }

    static class Cleaner
    {
        static string Env(string n) { return Environment.GetEnvironmentVariable(n) ?? ""; }
        static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string ProgData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }

        public static List<CleanCat> Categories()
        {
            return new List<CleanCat>
            {
                new CleanCat
                {
                    Name = "Временные файлы", Desc = "Остатки установщиков и программ. Файлы младше суток не трогаем",
                    DefaultOn = true, MinAgeHours = 24,
                    Dirs = () => new List<string> { Path.GetTempPath(), Path.Combine(Win, "Temp") }
                },
                new CleanCat
                {
                    Name = "Кеш обновлений Windows", Desc = "Уже установленные обновления. Windows скачает заново, если понадобится",
                    DefaultOn = true, StopUpdate = true,
                    Dirs = () => new List<string> { Path.Combine(Win, "SoftwareDistribution", "Download") }
                },
                new CleanCat
                {
                    Name = "Кеш браузеров", Desc = "Chrome, Edge, Яндекс, Opera, Brave, Firefox. Пароли, история и вкладки остаются",
                    DefaultOn = true, Dirs = BrowserCaches
                },
                new CleanCat
                {
                    Name = "Отчёты об ошибках", Desc = "Отчёты и дампы памяти после сбоев программ",
                    DefaultOn = true,
                    Dirs = () => new List<string>
                    {
                        Path.Combine(ProgData, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(ProgData, "Microsoft", "Windows", "WER", "ReportQueue"),
                        Path.Combine(ProgData, "Microsoft", "Windows", "WER", "Temp"),
                        Path.Combine(Local, "CrashDumps"),
                        Path.Combine(Win, "Minidump")
                    },
                    Files = () => new List<string> { Path.Combine(Win, "MEMORY.DMP") }
                },
                new CleanCat
                {
                    Name = "Кеш оптимизации доставки", Desc = "Куски обновлений, которыми Windows делится с другими компьютерами",
                    DefaultOn = true,
                    Dirs = () => new List<string> { Path.Combine(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") }
                },
                new CleanCat
                {
                    Name = "Кеш миниатюр", Desc = "Превью картинок в проводнике. Создадутся заново, папки с фото первый раз откроются чуть медленнее",
                    DefaultOn = false,
                    Files = () =>
                    {
                        var dir = Path.Combine(Local, "Microsoft", "Windows", "Explorer");
                        try { return Directory.GetFiles(dir, "thumbcache_*.db").ToList(); } catch { return new List<string>(); }
                    }
                },
                new CleanCat
                {
                    Name = "Корзина", Desc = "Файлы удалятся навсегда, восстановить их будет нельзя",
                    DefaultOn = false, IsRecycle = true
                }
            };
        }

        static List<string> BrowserCaches()
        {
            var res = new List<string>();
            var chromium = new[]
            {
                Path.Combine(Local, "Google", "Chrome", "User Data"),
                Path.Combine(Local, "Microsoft", "Edge", "User Data"),
                Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"),
                Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"),
                Path.Combine(Local, "Vivaldi", "User Data")
            };
            var cacheNames = new[] { "Cache", "Code Cache", "GPUCache", "Service Worker\\CacheStorage" };
            foreach (var root in chromium)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> profiles;
                try { profiles = Directory.GetDirectories(root).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile"; }); }
                catch { continue; }
                foreach (var p in profiles)
                    foreach (var c in cacheNames) res.Add(Path.Combine(p, c));
            }
            foreach (var opera in new[] { "Opera Stable", "Opera GX Stable" })
            {
                foreach (var c in cacheNames) res.Add(Path.Combine(Roaming, "Opera Software", opera, c));
                res.Add(Path.Combine(Local, "Opera Software", opera, "Cache"));
            }
            var ff = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
            try { foreach (var p in Directory.GetDirectories(ff)) res.Add(Path.Combine(p, "cache2")); } catch { }
            return res;
        }

        // Размер всех файлов, подходящих под очистку.
        public static long Measure(CleanCat c)
        {
            if (c.IsRecycle) return Native.RecycleBinSize();
            long total = 0;
            var limit = DateTime.Now.AddHours(-c.MinAgeHours);
            foreach (var d in SafeList(c.Dirs)) Walk(d, f => { if (c.MinAgeHours == 0 || f.LastWriteTime < limit) total += f.Length; });
            foreach (var f in SafeList(c.Files)) { try { var fi = new FileInfo(f); if (fi.Exists) total += fi.Length; } catch { } }
            return total;
        }

        public static void Clean(CleanCat c)
        {
            if (c.IsRecycle) { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); return; }
            if (c.StopUpdate)
            {
                Run("net.exe", "stop wuauserv /y");
                Run("net.exe", "stop bits /y");
            }
            try
            {
                var limit = DateTime.Now.AddHours(-c.MinAgeHours);
                foreach (var d in SafeList(c.Dirs))
                {
                    Walk(d, f =>
                    {
                        if (c.MinAgeHours > 0 && f.LastWriteTime >= limit) return;
                        try { f.Attributes = FileAttributes.Normal; f.Delete(); } catch { }
                    });
                    RemoveEmptyDirs(d, true);
                }
                foreach (var f in SafeList(c.Files)) { try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { } }
            }
            finally
            {
                if (c.StopUpdate)
                {
                    Run("net.exe", "start bits");
                    Run("net.exe", "start wuauserv");
                }
            }
        }

        static List<string> SafeList(Func<List<string>> f)
        {
            try { return f() ?? new List<string>(); } catch { return new List<string>(); }
        }

        // Обход папки без падений на закрытых и «зацикленных» папках.
        static void Walk(string dir, Action<FileInfo> onFile)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(dir));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles()) { try { onFile(f); } catch { } }
                    foreach (var sub in d.EnumerateDirectories())
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                }
                catch { }
            }
        }

        static void RemoveEmptyDirs(string dir, bool keepRoot)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    RemoveEmptyDirs(sub, false);
                }
                if (!keepRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }

        public static void Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(30000);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ настройки Windows

    class Tweak
    {
        public string Name, Desc;
        public Func<bool> Get;
        public Action<bool> Set;
        public bool Explorer;     // нужен перезапуск проводника
        public bool Relogin;      // применится после перезахода
        public CheckBox Switch;
    }

    static class Tweaks
    {
        const RegistryHive CU = RegistryHive.CurrentUser;
        const RegistryHive LM = RegistryHive.LocalMachine;
        const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string MENU = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        const string HIGH = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string BALANCED = "381b4222-f694-41f0-9685-ff5bb260df2e";

        static readonly string[] AdValues =
        {
            "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-338393Enabled",
            "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled", "SubscribedContent-310093Enabled",
            "SystemPaneSuggestionsEnabled", "SilentInstalledAppsEnabled", "SoftLandingEnabled", "RotatingLockScreenOverlayEnabled"
        };

        public static bool IsWin11 { get { return Environment.OSVersion.Version.Build >= 22000; } }

        public static List<Tweak> All()
        {
            var list = new List<Tweak>
            {
                new Tweak
                {
                    Name = "Убрать рекламу и «советы»", Desc = "Рекомендации в Пуске, советы, реклама на экране блокировки и сами собой ставящиеся приложения",
                    Get = () => Reg.Dword(CU, CDM, "SystemPaneSuggestionsEnabled") == 0 && Reg.Dword(CU, CDM, "SubscribedContent-338388Enabled") == 0,
                    Set = on =>
                    {
                        foreach (var v in AdValues) Reg.SetDword(CU, CDM, v, on ? 0 : 1);
                        Reg.SetDword(CU, ADV, "Start_IrisRecommendations", on ? 0 : 1);
                        Reg.SetDword(CU, ADV, "ShowSyncProviderNotifications", on ? 0 : 1);
                    },
                    Explorer = true
                },
                new Tweak
                {
                    Name = "Отключить телеметрию", Desc = "Windows перестанет отправлять в Microsoft данные об использовании компьютера",
                    Get = () => Reg.Dword(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0,
                    Set = on =>
                    {
                        if (on)
                        {
                            Reg.SetDword(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                            Reg.SetDword(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0);
                            Cleaner.Run("sc.exe", "stop DiagTrack");
                            Cleaner.Run("sc.exe", "config DiagTrack start= disabled");
                        }
                        else
                        {
                            Reg.Delete(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
                            Reg.SetDword(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
                            Cleaner.Run("sc.exe", "config DiagTrack start= auto");
                            Cleaner.Run("sc.exe", "start DiagTrack");
                        }
                    }
                },
                new Tweak
                {
                    Name = "Убрать Bing из поиска", Desc = "Поиск в Пуске ищет только на компьютере, без результатов из интернета",
                    Get = () => Reg.Dword(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1,
                    Set = on =>
                    {
                        if (on) Reg.SetDword(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
                        else Reg.Delete(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
                        Reg.SetDword(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", on ? 0 : 1);
                    },
                    Explorer = true
                },
                new Tweak
                {
                    Name = "Отключить Copilot", Desc = "Убирает помощника Copilot из Windows",
                    Get = () => Reg.Dword(CU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,
                    Set = on =>
                    {
                        if (on) Reg.SetDword(CU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                        else Reg.Delete(CU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                        Reg.SetDword(CU, ADV, "ShowCopilotButton", on ? 0 : 1);
                    },
                    Explorer = true
                },
                new Tweak
                {
                    Name = "Отключить запись игр (Game Bar)", Desc = "Фоновая запись видео игр Xbox Game Bar. Может добавить пару FPS",
                    Get = () => Reg.Dword(CU, @"System\GameConfigStore", "GameDVR_Enabled") == 0,
                    Set = on =>
                    {
                        Reg.SetDword(CU, @"System\GameConfigStore", "GameDVR_Enabled", on ? 0 : 1);
                        Reg.SetDword(CU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on ? 0 : 1);
                    }
                },
                new Tweak
                {
                    Name = "Высокая производительность", Desc = "Схема питания без экономии. Для ПК; на ноутбуке быстрее садится батарея",
                    Get = () =>
                    {
                        string o = Capture("powercfg.exe", "/getactivescheme").ToLowerInvariant();
                        return o.Contains(HIGH) || o.Contains("высокая производительность") || o.Contains("high performance");
                    },
                    Set = on =>
                    {
                        if (!on) { Capture("powercfg.exe", "/setactive " + BALANCED); return; }
                        if (Capture("powercfg.exe", "/setactive " + HIGH).Length > 0 || !Capture("powercfg.exe", "/getactivescheme").Contains(HIGH))
                        {
                            // на части ноутбуков схема скрыта — создаём её копию
                            var m = Regex.Match(Capture("powercfg.exe", "-duplicatescheme " + HIGH), "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
                            if (m.Success) Capture("powercfg.exe", "/setactive " + m.Value);
                        }
                    }
                },
                new Tweak
                {
                    Name = "Показывать расширения файлов", Desc = "Видно, что «фото.jpg» — картинка, а «фото.jpg.exe» — программа",
                    Get = () => Reg.Dword(CU, ADV, "HideFileExt") == 0,
                    Set = on => Reg.SetDword(CU, ADV, "HideFileExt", on ? 0 : 1),
                    Explorer = true
                },
                new Tweak
                {
                    Name = "Отключить ускорение мыши", Desc = "Курсор двигается ровно на столько, на сколько двигаешь мышь. Любят в играх",
                    Get = () => Reg.Str(CU, @"Control Panel\Mouse", "MouseSpeed") == "0",
                    Set = on =>
                    {
                        Reg.SetStr(CU, @"Control Panel\Mouse", "MouseSpeed", on ? "0" : "1");
                        Reg.SetStr(CU, @"Control Panel\Mouse", "MouseThreshold1", on ? "0" : "6");
                        Reg.SetStr(CU, @"Control Panel\Mouse", "MouseThreshold2", on ? "0" : "10");
                        Native.SystemParametersInfo(0x0004, 0, on ? new[] { 0, 0, 0 } : new[] { 6, 10, 1 }, 0x3); // применить сразу
                    }
                },
                new Tweak
                {
                    Name = "Отключить залипание клавиш", Desc = "Окно после пяти нажатий Shift больше не появится",
                    Get = () => Reg.Str(CU, @"Control Panel\Accessibility\StickyKeys", "Flags") == "506",
                    Set = on => Reg.SetStr(CU, @"Control Panel\Accessibility\StickyKeys", "Flags", on ? "506" : "510"),
                    Relogin = true
                },
                new Tweak
                {
                    Name = "Быстрые меню", Desc = "Вложенные меню открываются без задержки",
                    Get = () => { int? d = Reg.Dword(CU, @"Control Panel\Desktop", "MenuShowDelay"); return d.HasValue && d.Value <= 150; },
                    Set = on => Reg.SetStr(CU, @"Control Panel\Desktop", "MenuShowDelay", on ? "100" : "400"),
                    Relogin = true
                }
            };

            if (IsWin11)
                list.Insert(4, new Tweak
                {
                    Name = "Старое меню правой кнопки", Desc = "Сразу полное контекстное меню, без «Показать дополнительные параметры»",
                    Get = () => Reg.KeyExists(CU, MENU + @"\InprocServer32"),
                    Set = on =>
                    {
                        if (on) Reg.Set(CU, MENU + @"\InprocServer32", "", "", RegistryValueKind.String);
                        else Reg.DeleteTree(CU, MENU);
                    },
                    Explorer = true
                });
            return list;
        }

        static string Capture(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return o;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ------------------------------------------------------------------ автозагрузка

    class StartupEntry
    {
        public string Name, Command, ExePath, Publisher;
        public RegistryHive ApprovedHive;
        public string ApprovedKey, ApprovedName;
        public bool Enabled;
        public CheckBox Switch;
    }

    static class Startup
    {
        const string RUN = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string APPROVED = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupEntry> Read()
        {
            var res = new List<StartupEntry>();
            ReadRun(res, RegistryHive.CurrentUser, RUN, APPROVED + "Run");
            ReadRun(res, RegistryHive.LocalMachine, RUN, APPROVED + "Run");
            ReadRun(res, RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", APPROVED + "Run32");
            ReadFolder(res, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser);
            ReadFolder(res, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine);
            return res.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void ReadRun(List<StartupEntry> res, RegistryHive hive, string path, string approved)
        {
            try
            {
                using (var b = Reg.Base(hive))
                using (var k = b.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        string cmd = Convert.ToString(k.GetValue(name));
                        var e = new StartupEntry { Name = name, Command = cmd, ApprovedHive = hive, ApprovedKey = approved, ApprovedName = name };
                        e.ExePath = ExeFromCommand(cmd);
                        Finish(e);
                        res.Add(e);
                    }
                }
            }
            catch { }
        }

        static void ReadFolder(List<StartupEntry> res, string dir, RegistryHive hive)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = new StartupEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(f), Command = f, ExePath = f,
                        ApprovedHive = hive, ApprovedKey = APPROVED + "StartupFolder", ApprovedName = fn
                    };
                    Finish(e);
                    res.Add(e);
                }
            }
            catch { }
        }

        static void Finish(StartupEntry e)
        {
            var v = Reg.Get(e.ApprovedHive, e.ApprovedKey, e.ApprovedName) as byte[];
            e.Enabled = v == null || v.Length == 0 || (v[0] & 1) == 0;
            try
            {
                if (e.ExePath != null && File.Exists(e.ExePath) && e.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var vi = FileVersionInfo.GetVersionInfo(e.ExePath);
                    e.Publisher = !string.IsNullOrWhiteSpace(vi.CompanyName) ? vi.CompanyName.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription) && e.Name.Length < 3) e.Name = vi.FileDescription.Trim();
                }
            }
            catch { }
        }

        static string ExeFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? cmd.Substring(0, i + 4) : cmd.Split(' ')[0];
        }

        // Как в Диспетчере задач: 02… — включено, 03 + время — выключено.
        public static void SetEnabled(StartupEntry e, bool on)
        {
            var data = new byte[12];
            data[0] = (byte)(on ? 2 : 3);
            if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            Reg.Set(e.ApprovedHive, e.ApprovedKey, e.ApprovedName, data, RegistryValueKind.Binary);
            e.Enabled = on;
        }

        public static ImageSource IconFor(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ic == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ окно

    class MainWindow : ShellWindow
    {
        FrameworkElement pageClean, pageTweaks, pageStartup;
        RadioButton navClean, navTweaks, navStartup;
        TextBlock cleanCaption, cleanTotal, cleanSub, cleanBtnText, startupSummary;
        StackPanel cleanList, tweakList, startupList;
        Button scanBtn, cleanBtn, restoreBtn, explorerBtn, startupRefreshBtn;

        readonly List<CleanCat> cats = Cleaner.Categories();
        List<Tweak> tweaks;
        bool scanning, cleaning, suppress, startupLoaded;

        public MainWindow()
        {
            LoadUi("Win Cleaner by mysik", 440, 760);

            pageClean = F<FrameworkElement>("PageClean");
            pageTweaks = F<FrameworkElement>("PageTweaks");
            pageStartup = F<FrameworkElement>("PageStartup");
            navClean = F<RadioButton>("NavClean");
            navTweaks = F<RadioButton>("NavTweaks");
            navStartup = F<RadioButton>("NavStartup");
            cleanCaption = F<TextBlock>("CleanCaption");
            cleanTotal = F<TextBlock>("CleanTotal");
            cleanSub = F<TextBlock>("CleanSub");
            cleanBtnText = F<TextBlock>("CleanBtnText");
            startupSummary = F<TextBlock>("StartupSummary");
            cleanList = F<StackPanel>("CleanList");
            tweakList = F<StackPanel>("TweakList");
            startupList = F<StackPanel>("StartupList");
            scanBtn = F<Button>("ScanBtn");
            cleanBtn = F<Button>("CleanBtn");
            restoreBtn = F<Button>("RestoreBtn");
            explorerBtn = F<Button>("ExplorerBtn");
            startupRefreshBtn = F<Button>("StartupRefreshBtn");

            navClean.Checked += (s, e) => ShowOnly(pageClean, pageClean, pageTweaks, pageStartup);
            navTweaks.Checked += (s, e) => ShowOnly(pageTweaks, pageClean, pageTweaks, pageStartup);
            navStartup.Checked += (s, e) =>
            {
                ShowOnly(pageStartup, pageClean, pageTweaks, pageStartup);
                if (!startupLoaded) LoadStartup();
            };

            scanBtn.Click += async (s, e) => await Scan();
            cleanBtn.Click += async (s, e) => await CleanSelected();
            restoreBtn.Click += async (s, e) => await RestorePoint();
            explorerBtn.Click += async (s, e) => await RestartExplorer();
            startupRefreshBtn.Click += (s, e) => LoadStartup();

            BuildCleanList();
            BuildTweaks();
        }

        public override async void Init()
        {
            await Scan();
        }

        // -------------------------------------------------- очистка

        void BuildCleanList()
        {
            var tick = S("Tick");
            for (int i = 0; i < cats.Count; i++)
            {
                var c = cats[i];
                if (i > 0) cleanList.Children.Add(new Border { Height = 1, Background = Br(CLine) });

                var g = Columns(Star, Auto);
                var text = new StackPanel();
                text.Children.Add(Txt(c.Name, 13.5, CText, true));
                var d = Txt(c.Desc, 11.5, CSub, false);
                d.TextWrapping = TextWrapping.Wrap;
                d.Margin = new Thickness(0, 2, 10, 0);
                text.Children.Add(d);
                g.Children.Add(text);
                c.SizeText = Txt("…", 13, CSub, true);
                c.SizeText.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(At(c.SizeText, 1));

                c.Box = new CheckBox { Style = tick, Content = g, IsChecked = c.DefaultOn, Margin = new Thickness(0, 11, 0, 11) };
                c.Box.Checked += (s, e) => UpdateTotal();
                c.Box.Unchecked += (s, e) => UpdateTotal();
                cleanList.Children.Add(c.Box);
            }
        }

        async Task Scan()
        {
            if (scanning || cleaning) return;
            scanning = true;
            scanBtn.IsEnabled = cleanBtn.IsEnabled = false;
            cleanCaption.Text = "Можно освободить";
            cleanTotal.Text = "…";
            cleanSub.Text = "Ищу ненужные файлы";
            foreach (var c in cats) { c.SizeText.Text = "…"; c.SizeText.Foreground = Br(CSub); }

            foreach (var c in cats)
            {
                var cat = c;
                cat.Size = await Task.Run(() => Cleaner.Measure(cat));
                cat.SizeText.Text = cat.Size > 0 ? FmtBytes(cat.Size) : "пусто";
                cat.SizeText.Foreground = Br(cat.Size > 100 * 1024 * 1024 ? CText : CSub);
                UpdateTotal();
            }
            scanning = false;
            scanBtn.IsEnabled = true;
            UpdateTotal();
        }

        void UpdateTotal()
        {
            if (cleaning) return;
            long sum = cats.Where(c => c.Box.IsChecked == true).Sum(c => c.Size);
            long all = cats.Sum(c => c.Size);
            cleanTotal.Text = FmtBytes(sum);
            if (!scanning)
                cleanSub.Text = sum == all ? "Отмечено всё найденное" : "Найдено всего " + FmtBytes(all) + ", отмечено " + FmtBytes(sum);
            cleanBtn.IsEnabled = !scanning && sum > 0;
            cleanBtnText.Text = sum > 0 ? "Очистить " + FmtBytes(sum) : "Нечего очищать";
        }

        async Task CleanSelected()
        {
            var chosen = cats.Where(c => c.Box.IsChecked == true && c.Size > 0).ToList();
            if (chosen.Count == 0 || cleaning) return;
            if (chosen.Any(c => c.IsRecycle))
            {
                bool yes = await Ask("Очистить корзину?", "Файлы из корзины удалятся навсегда.", "Очистить", "Отмена");
                if (!yes) return;
            }

            var running = Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } })
                .Where(n => n == "chrome" || n == "msedge" || n == "browser" || n == "opera" || n == "brave" || n == "firefox" || n == "vivaldi").Distinct().ToList();

            cleaning = true;
            scanBtn.IsEnabled = cleanBtn.IsEnabled = false;
            long before = chosen.Sum(c => c.Size);
            foreach (var c in chosen)
            {
                var cat = c;
                cleanCaption.Text = "Очищаю";
                cleanSub.Text = cat.Name + "…";
                cat.SizeText.Text = "чищу…";
                await Task.Run(() => Cleaner.Clean(cat));
                cat.Size = await Task.Run(() => Cleaner.Measure(cat));
                cat.SizeText.Text = cat.Size > 0 ? FmtBytes(cat.Size) : "пусто";
            }
            long after = chosen.Sum(c => c.Size);
            cleaning = false;
            scanBtn.IsEnabled = true;

            long freed = Math.Max(0, before - after);
            UpdateTotal();
            cleanCaption.Text = "Освобождено";
            cleanTotal.Text = FmtBytes(freed);
            cleanSub.Text = after > 0
                ? "Часть файлов сейчас занята программами" + (running.Count > 0 ? " — закрой браузер и очисти ещё раз" : "")
                : "Готово";
            Log("Очищено " + FmtBytes(freed));
            Toast("Освобождено " + FmtBytes(freed));
        }

        // -------------------------------------------------- настройки Windows

        void BuildTweaks()
        {
            tweaks = Tweaks.All();
            var sw = S("Switch");
            suppress = true;
            for (int i = 0; i < tweaks.Count; i++)
            {
                var t = tweaks[i];
                if (i > 0) tweakList.Children.Add(new Border { Height = 1, Background = Br(CLine) });
                var text = new StackPanel();
                text.Children.Add(Txt(t.Name, 13.5, CText, true));
                var d = Txt(t.Desc + (t.Relogin ? ". Сработает после перезахода в Windows" : ""), 11.5, CSub, false);
                d.TextWrapping = TextWrapping.Wrap;
                d.Margin = new Thickness(0, 2, 0, 0);
                text.Children.Add(d);
                t.Switch = new CheckBox { Style = sw, Content = text, Margin = new Thickness(0, 12, 0, 12) };
                bool state = false;
                try { state = t.Get(); } catch { }
                t.Switch.IsChecked = state;
                var tw = t;
                t.Switch.Checked += async (s, e) => await ApplyTweak(tw, true);
                t.Switch.Unchecked += async (s, e) => await ApplyTweak(tw, false);
                tweakList.Children.Add(t.Switch);
            }
            suppress = false;
        }

        async Task ApplyTweak(Tweak t, bool on)
        {
            if (suppress) return;
            t.Switch.IsEnabled = false;
            string error = null;
            await Task.Run(() => { try { t.Set(on); } catch (Exception ex) { error = ex.Message; } });
            t.Switch.IsEnabled = true;

            bool actual = on;
            try { actual = t.Get(); } catch { }
            if (error != null || actual != on)
            {
                suppress = true;
                t.Switch.IsChecked = actual;
                suppress = false;
                Toast("Не получилось: " + (error ?? "Windows не дала изменить настройку"));
                Log(t.Name + ": " + (error ?? "не применилось"));
                return;
            }
            Log(t.Name + ": " + (on ? "включено" : "выключено"));
            if (t.Explorer)
            {
                bool yes = await Ask("Перезапустить проводник?",
                    "Чтобы изменение появилось, нужно перезапустить проводник: панель задач и открытые папки моргнут на пару секунд.",
                    "Перезапустить", "Позже");
                if (yes) await RestartExplorer();
            }
            else Toast(on ? "Включено" : "Выключено");
        }

        async Task RestartExplorer()
        {
            await Task.Run(() =>
            {
                Cleaner.Run("taskkill.exe", "/f /im explorer.exe");
                Thread.Sleep(2500);
                if (Process.GetProcessesByName("explorer").Length == 0)
                {
                    // запускаем проводник с обычными правами, а не от администратора
                    string exp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Cleaner.Run("runas.exe", "/trustlevel:0x20000 " + Runner.Q(exp));
                    Thread.Sleep(1500);
                    if (Process.GetProcessesByName("explorer").Length == 0)
                        try { Process.Start(exp); } catch { }
                }
            });
            Toast("Проводник перезапущен");
        }

        async Task RestorePoint()
        {
            restoreBtn.IsEnabled = false;
            var r = await Runner.PowerShell("Checkpoint-Computer -Description 'Win Cleaner by mysik' -RestorePointType MODIFY_SETTINGS");
            restoreBtn.IsEnabled = true;
            string o = r.Value.ToLowerInvariant();
            if (o.Contains("1440"))
                await Ask("Точка уже есть", "Windows создаёт не больше одной точки в сутки, сегодняшняя уже сделана. Можно смело менять настройки.", "Понятно", null);
            else if (r.Key == 0) { Toast("Точка восстановления создана"); return; }
            else
                await Ask("Защита системы выключена",
                    "Включи её: Пуск → «Создание точки восстановления» → выбери диск C: → «Настроить» → «Включить защиту системы». Потом попробуй снова.",
                    "Понятно", null);
            Log("Точка восстановления: " + r.Value.Trim());
        }

        // -------------------------------------------------- автозагрузка

        async void LoadStartup()
        {
            startupLoaded = true;
            startupList.Children.Clear();
            startupSummary.Text = "Загружаю…";
            var entries = await Task.Run(() => Startup.Read());
            var icons = new Dictionary<StartupEntry, ImageSource>();
            await Task.Run(() => { foreach (var e in entries) icons[e] = Startup.IconFor(e.ExePath); });

            var sw = S("Switch");
            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i];
                if (i > 0) startupList.Children.Add(new Border { Height = 1, Background = Br(CLine) });

                var g = Columns(Auto, Star);
                var iconHost = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Br(CCard2), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                ImageSource img;
                if (icons.TryGetValue(en, out img) && img != null)
                    iconHost.Child = new Image { Source = img, Width = 22, Height = 22 };
                else
                    iconHost.Child = new TextBlock { Text = en.Name.Substring(0, 1).ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Foreground = Br(CSub), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(iconHost);

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Txt(en.Name, 13.5, CText, true));
                string sub = en.Publisher ?? (en.ExePath != null ? Path.GetFileName(en.ExePath) : en.Command);
                var st = Txt(sub, 11.5, CSub, false);
                st.ToolTip = en.Command;
                text.Children.Add(st);
                g.Children.Add(At(text, 1));

                en.Switch = new CheckBox { Style = sw, Content = g, IsChecked = en.Enabled, Margin = new Thickness(0, 10, 0, 10) };
                var entry = en;
                en.Switch.Checked += (s, e) => ToggleStartup(entry, true);
                en.Switch.Unchecked += (s, e) => ToggleStartup(entry, false);
                startupList.Children.Add(en.Switch);
            }
            if (entries.Count == 0)
                startupList.Children.Add(new TextBlock { Text = "В автозагрузке ничего нет", Foreground = Br(CSub), Margin = new Thickness(0, 14, 0, 14) });
            UpdateStartupSummary(entries);
        }

        void ToggleStartup(StartupEntry e, bool on)
        {
            try
            {
                Startup.SetEnabled(e, on);
                Toast(e.Name + (on ? " будет запускаться с Windows" : " больше не запускается с Windows"));
                Log("Автозагрузка: " + e.Name + " — " + (on ? "включено" : "выключено"));
            }
            catch (Exception ex) { Toast("Не получилось: " + ex.Message); }
            var all = startupList.Children.OfType<CheckBox>().Count();
            var onCount = startupList.Children.OfType<CheckBox>().Count(c => c.IsChecked == true);
            startupSummary.Text = "Запускается " + onCount + " из " + all;
        }

        void UpdateStartupSummary(List<StartupEntry> entries)
        {
            int on = entries.Count(e => e.Enabled);
            startupSummary.Text = entries.Count == 0 ? "Программы, которые запускаются вместе с Windows" : "Запускается " + on + " из " + entries.Count;
        }
    }
}
