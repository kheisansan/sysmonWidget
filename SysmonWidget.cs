using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysmonWidget
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest.Run();
                return;
            }
            if (args.Length > 0 && args[0] == "--loopback-test")
            {
                SelfTest.AttachParent();
                int fails = GmailClient.RunLoopbackSelfTest();
                Console.WriteLine(fails == 0 ? "loopback ok" : "loopback FAIL " + fails);
                Environment.ExitCode = fails == 0 ? 0 : 1;
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "SysmonWidget_SingleInstance", out created))
            {
                if (!created) return;
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    static class SelfTest
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        public static void AttachParent() { try { AttachConsole(-1); } catch { } }

        public static void Run()
        {
            AttachParent();
            var collector = new MetricsCollector();
            for (int i = 0; i < 4; i++)
            {
                var sw = Stopwatch.StartNew();
                Snapshot s = collector.Sample();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] CPU={1:0.0} MEM={2:0.0} ({3:0.0}/{4:0.0}GB) DISK={5:0.0} GPU={6:0.0} WIFI={7}({8}) DOWN={9:0}B/s UP={10:0}B/s  ({11}ms)",
                    i, s.Cpu, s.Mem, s.MemUsed / 1073741824.0, s.MemTotal / 1073741824.0, s.Disk, s.Gpu,
                    s.WifiConnected, s.WifiName, s.Down, s.Up, sw.ElapsedMilliseconds));
                Thread.Sleep(1000);
            }
        }
    }

    class Snapshot
    {
        public float Cpu = -1, Mem = -1, Disk = -1, Gpu = -1;
        public ulong MemUsed, MemTotal;
        public double Up = -1, Down = -1;
        public bool WifiConnected;
        public string WifiName;
        public long WifiLinkSpeed;
    }

    /// <summary>カテゴリ全インスタンスを一括で読み、前回サンプルとの差分から値を計算する</summary>
    class CategorySampler
    {
        readonly string category, counter;
        Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();

        public CategorySampler(string category, string counter)
        {
            this.category = category;
            this.counter = counter;
        }

        public Dictionary<string, float> Sample()
        {
            var result = new Dictionary<string, float>();
            var next = new Dictionary<string, CounterSample>();
            InstanceDataCollectionCollection all = new PerformanceCounterCategory(category).ReadCategory();
            InstanceDataCollection col = all[counter];
            if (col != null)
            {
                foreach (InstanceData d in col.Values)
                {
                    CounterSample cur = d.Sample;
                    next[d.InstanceName] = cur;
                    CounterSample old;
                    if (prev.TryGetValue(d.InstanceName, out old))
                        result[d.InstanceName] = CounterSample.Calculate(old, cur);
                }
            }
            prev = next;
            return result;
        }
    }

    class MetricsCollector
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        readonly PerformanceCounter cpuCounter;
        readonly CategorySampler diskSampler = new CategorySampler("PhysicalDisk", "% Idle Time");
        readonly CategorySampler gpuSampler = new CategorySampler("GPU Engine", "Utilization Percentage");
        bool gpuDisabled, diskDisabled;
        int gpuFailures, diskFailures;

        readonly Stopwatch clock = Stopwatch.StartNew();
        NetworkInterface wifi;
        int wifiRefreshCountdown;
        string prevWifiId;
        long prevSent, prevRecv;
        double prevTime;

        public MetricsCollector()
        {
            cpuCounter = TryCreate("Processor Information", "% Processor Utility", "_Total")
                      ?? TryCreate("Processor", "% Processor Time", "_Total");
        }

        static PerformanceCounter TryCreate(string cat, string name, string inst)
        {
            try
            {
                var pc = new PerformanceCounter(cat, name, inst, true);
                pc.NextValue();
                return pc;
            }
            catch { return null; }
        }

        public Snapshot Sample()
        {
            var s = new Snapshot();
            try { if (cpuCounter != null) s.Cpu = Clamp(cpuCounter.NextValue()); } catch { }
            try { SampleMemory(s); } catch { }
            if (!diskDisabled)
            {
                try { s.Disk = SampleDisk(); diskFailures = 0; }
                catch { if (++diskFailures >= 5) diskDisabled = true; }
            }
            if (!gpuDisabled)
            {
                try { s.Gpu = SampleGpu(); gpuFailures = 0; }
                catch { if (++gpuFailures >= 5) gpuDisabled = true; }
            }
            try { SampleNet(s); } catch { wifi = null; }
            return s;
        }

        static float Clamp(float v)
        {
            if (float.IsNaN(v) || v < 0) return 0;
            return v > 100 ? 100 : v;
        }

        static void SampleMemory(Snapshot s)
        {
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return;
            s.MemTotal = m.ullTotalPhys;
            s.MemUsed = m.ullTotalPhys - m.ullAvailPhys;
            s.Mem = m.ullTotalPhys == 0 ? 0 : (float)(s.MemUsed * 100.0 / m.ullTotalPhys);
        }

        // 一番忙しい物理ディスクのアクティブ時間（タスクマネージャーと同じ 100 - %Idle）
        float SampleDisk()
        {
            Dictionary<string, float> values = diskSampler.Sample();
            if (values.Count == 0) return -1;
            float max = 0;
            foreach (var kv in values)
            {
                if (kv.Key == "_Total") continue;
                max = Math.Max(max, Clamp(100f - kv.Value));
            }
            return max;
        }

        // エンジン（luid+phys+eng）ごとに全プロセス分を合計し、その最大値を GPU 使用率とする
        float SampleGpu()
        {
            Dictionary<string, float> values = gpuSampler.Sample();
            if (values.Count == 0) return -1;
            var perEngine = new Dictionary<string, float>();
            foreach (var kv in values)
            {
                int li = kv.Key.IndexOf("luid_", StringComparison.Ordinal);
                string key = li >= 0 ? kv.Key.Substring(li) : kv.Key;
                float cur;
                perEngine.TryGetValue(key, out cur);
                perEngine[key] = cur + kv.Value;
            }
            float max = 0;
            foreach (float v in perEngine.Values) max = Math.Max(max, v);
            return Clamp(max);
        }

        void SampleNet(Snapshot s)
        {
            if (wifi == null || --wifiRefreshCountdown <= 0)
            {
                wifi = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                    n.OperationalStatus == OperationalStatus.Up);
                wifiRefreshCountdown = 5;
            }
            if (wifi == null)
            {
                prevWifiId = null;
                return;
            }

            IPInterfaceStatistics st = wifi.GetIPStatistics();
            long sent = st.BytesSent, recv = st.BytesReceived;
            double now = clock.Elapsed.TotalSeconds;

            s.WifiConnected = true;
            s.WifiName = wifi.Name;
            s.WifiLinkSpeed = wifi.Speed;

            if (prevWifiId == wifi.Id)
            {
                double dt = now - prevTime;
                if (dt > 0)
                {
                    s.Up = Math.Max(0, (sent - prevSent) / dt);
                    s.Down = Math.Max(0, (recv - prevRecv) / dt);
                }
            }
            prevWifiId = wifi.Id;
            prevSent = sent;
            prevRecv = recv;
            prevTime = now;
        }
    }

    class Settings
    {
        public const float MinFontScale = 0.6f, MaxFontScale = 2.5f;

        public int X = int.MinValue, Y = int.MinValue;
        public int Width, Height;
        public bool TopMost = true;
        public bool Bits = true;
        public double Opacity = 0.9;
        public float FontScale = 1.0f;

        public bool ShowClock;
        public bool ClockAnalog; // false = digital
        public bool ShowPomodoro;
        public int PomodoroWorkMin = 25;
        public int PomodoroBreakMin = 5;
        public bool ShowGmail;
        public int GmailCount = 5;
        public int GmailIntervalSec = 60; // 更新間隔（秒）

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "SysmonWidget", "settings.ini");
            }
        }

        public bool AnySidePanel { get { return ShowClock || ShowPomodoro || ShowGmail; } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int i; double d; bool b;
                    switch (k)
                    {
                        case "X": if (int.TryParse(v, out i)) s.X = i; break;
                        case "Y": if (int.TryParse(v, out i)) s.Y = i; break;
                        case "Width": if (int.TryParse(v, out i) && i > 0) s.Width = i; break;
                        case "Height": if (int.TryParse(v, out i) && i > 0) s.Height = i; break;
                        case "FontScale":
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                                s.FontScale = (float)Math.Max(MinFontScale, Math.Min(MaxFontScale, d));
                            break;
                        case "TopMost": if (bool.TryParse(v, out b)) s.TopMost = b; break;
                        case "Bits": if (bool.TryParse(v, out b)) s.Bits = b; break;
                        case "Opacity":
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                                s.Opacity = Math.Max(0.3, Math.Min(1.0, d));
                            break;
                        case "ShowClock": if (bool.TryParse(v, out b)) s.ShowClock = b; break;
                        case "ClockAnalog": if (bool.TryParse(v, out b)) s.ClockAnalog = b; break;
                        case "ShowPomodoro": if (bool.TryParse(v, out b)) s.ShowPomodoro = b; break;
                        case "PomodoroWorkMin":
                            if (int.TryParse(v, out i) && i >= 1 && i <= 120) s.PomodoroWorkMin = i;
                            break;
                        case "PomodoroBreakMin":
                            if (int.TryParse(v, out i) && i >= 1 && i <= 60) s.PomodoroBreakMin = i;
                            break;
                        case "ShowGmail": if (bool.TryParse(v, out b)) s.ShowGmail = b; break;
                        case "GmailCount":
                            if (int.TryParse(v, out i) && i >= 3 && i <= 10) s.GmailCount = i;
                            break;
                        case "GmailIntervalSec":
                            if (int.TryParse(v, out i) && i >= 15 && i <= 3600) s.GmailIntervalSec = i;
                            break;
                        // 旧版で ini に平文保存していた分は Credential Manager へ移す
                        case "GmailClientId":
                        case "GmailClientSecret":
                            break;
                    }
                }
                if (MigratePlainGmailCredentials())
                    s.Save(); // ini から平文キーを落とす
            }
            catch { }
            return s;
        }

        // 以前の settings.ini の Client ID / Secret を Credential Manager に移す。要再保存なら true。
        static bool MigratePlainGmailCredentials()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                string id = null, secret = null;
                bool hadPlain = false;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "GmailClientId")
                    {
                        hadPlain = true;
                        if (v.Length > 0) id = v;
                    }
                    else if (k == "GmailClientSecret")
                    {
                        hadPlain = true;
                        secret = v;
                    }
                }
                if (!hadPlain) return false;
                string existingId, existingSecret;
                GmailAppCredentials.Load(out existingId, out existingSecret);
                if (string.IsNullOrEmpty(existingId) && !string.IsNullOrEmpty(id))
                    GmailAppCredentials.Save(id, secret ?? "");
                return true;
            }
            catch { return false; }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "X=" + X, "Y=" + Y, "Width=" + Width, "Height=" + Height,
                    "TopMost=" + TopMost, "Bits=" + Bits,
                    "Opacity=" + Opacity.ToString(CultureInfo.InvariantCulture),
                    "FontScale=" + FontScale.ToString(CultureInfo.InvariantCulture),
                    "ShowClock=" + ShowClock,
                    "ClockAnalog=" + ClockAnalog,
                    "ShowPomodoro=" + ShowPomodoro,
                    "PomodoroWorkMin=" + PomodoroWorkMin,
                    "PomodoroBreakMin=" + PomodoroBreakMin,
                    "ShowGmail=" + ShowGmail,
                    "GmailCount=" + GmailCount,
                    "GmailIntervalSec=" + GmailIntervalSec
                });
            }
            catch { }
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "SysmonWidget";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(Name) != null;
        }

        public static void Set(bool enable)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
            }
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int n);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int HistLen = 60;
        const int BaseWidth = 240, MinWidth = 190;
        const int RightColMin = 168, PanelGap = 10;
        const int Pad = 12, LabelH = 18, BarH = 6, RowGap = 10;
        const int SepH = 15, NetHeaderH = 18, NetSpeedH = 22, GraphH = 36, MinGraphH = 20;
        const int ClockDigitalH = 66, ClockAnalogH = 120, PomoH = 72, GmailRowH = 34, GmailHeaderH = 20;
        const int ResizeBorder = 6;
        static readonly float[] FontScales = { 0.75f, 0.9f, 1.0f, 1.15f, 1.3f, 1.5f, 1.75f, 2.0f };
        static readonly int[] WorkMins = { 15, 20, 25, 30, 45, 50, 60 };
        static readonly int[] BreakMins = { 3, 5, 10, 15, 20 };
        static readonly int[] GmailIntervals = { 15, 30, 60, 120, 300, 600 };

        readonly Settings settings;
        readonly float scale;
        readonly MetricsCollector collector = new MetricsCollector();
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        readonly List<double> upHist = new List<double>(), downHist = new List<double>();
        readonly GmailClient gmail = new GmailClient();
        Thread worker, gmailWorker;
        Snapshot snap;
        System.Windows.Forms.Timer uiTimer;
        CancellationTokenSource gmailAuthCancel;

        // ポモドーロ: 0=停止 1=作業 2=休憩（一時停止時は paused=true）
        int pomoPhase;
        bool pomoPaused;
        DateTime pomoDeadlineUtc;
        int pomoRemainSec;

        Rectangle pomoToggleRect;
        GmailMailItem[] gmailItems = new GmailMailItem[0];
        string gmailStatus = "";
        DateTime gmailNextFetchUtc = DateTime.MinValue;
        int gmailFetchGen, gmailAuthGen;

        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miTopMost, miAutoStart, miBits, miBytes;
        ToolStripMenuItem miClock, miClockDigital, miClockAnalog;
        ToolStripMenuItem miPomo, miGmail;
        FontWheelFilter wheelFilter;

        Font fLabel, fValue, fSmall, fClock, fPomo, fMailTitle, fMailFrom;
        readonly Brush bLabel = new SolidBrush(Color.FromArgb(170, 170, 180));
        readonly Brush bValue = new SolidBrush(Color.FromArgb(240, 240, 245));
        readonly Brush bDim = new SolidBrush(Color.FromArgb(120, 120, 130));
        readonly Brush bTrack = new SolidBrush(Color.FromArgb(55, 55, 62));
        readonly Pen pSep = new Pen(Color.FromArgb(60, 60, 68));
        static readonly Color CCpu = Color.FromArgb(77, 166, 255);
        static readonly Color CMem = Color.FromArgb(178, 120, 255);
        static readonly Color CDisk = Color.FromArgb(80, 210, 140);
        static readonly Color CGpu = Color.FromArgb(255, 170, 60);
        static readonly Color CHigh = Color.FromArgb(240, 80, 90);
        static readonly Color CDown = Color.FromArgb(77, 200, 255);
        static readonly Color CUp = Color.FromArgb(255, 140, 80);

        public WidgetForm()
        {
            settings = Settings.Load();

            // 自分で DPI と文字倍率を掛ける。WinForms の自動スケールが重なると、
            // 文字サイズもウィンドウサイズも指定どおりにならなくなる。
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 32);
            TopMost = settings.TopMost;
            Opacity = settings.Opacity;
            Text = "SysmonWidget";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            CreateFonts();
            gmail.LoadStoredToken();

            int w = settings.Width > 0 ? settings.Width : PreferredWidth();
            int h = settings.Height > 0 ? settings.Height : PreferredHeight();
            SetSizeClamped(w, h);
            Location = InitialLocation();

            BuildMenu();
            StartUiTimer();
        }

        // 文字サイズ倍率も含めたレイアウト単位（余白・行の高さも文字と一緒に拡大縮小する）
        int S(int v) { return (int)Math.Round(v * scale * settings.FontScale); }

        static int MetricsContentHeight(int graphH)
        {
            return Pad * 2 + 4 * (LabelH + BarH + RowGap) + SepH + NetHeaderH + NetSpeedH + graphH;
        }

        int SidePanelsMinHeight()
        {
            int h = Pad * 2;
            if (settings.ShowClock) h += (settings.ClockAnalog ? ClockAnalogH : ClockDigitalH) + 8;
            if (settings.ShowPomodoro) h += PomoH + 8;
            if (settings.ShowGmail) h += GmailHeaderH + settings.GmailCount * GmailRowH + 8;
            return Math.Max(h, MetricsContentHeight(MinGraphH));
        }

        int PreferredWidth()
        {
            int w = BaseWidth;
            if (settings.AnySidePanel) w += PanelGap + RightColMin;
            return S(w);
        }

        int PreferredHeight()
        {
            int metrics = MetricsContentHeight(GraphH);
            int side = settings.AnySidePanel ? SidePanelsMinHeight() : metrics;
            return S(Math.Max(metrics, side));
        }

        Size MinContentSize()
        {
            int w = MinWidth;
            if (settings.AnySidePanel) w += PanelGap + RightColMin;
            int h = settings.AnySidePanel
                ? Math.Max(MetricsContentHeight(MinGraphH), SidePanelsMinHeight())
                : MetricsContentHeight(MinGraphH);
            return new Size(S(w), S(h));
        }

        int LeftColWidth()
        {
            if (!settings.AnySidePanel) return ClientSize.Width;
            int gap = S(PanelGap);
            int minRight = S(RightColMin);
            int maxLeft = Math.Max(S(MinWidth), ClientSize.Width - gap - minRight);
            return Math.Max(S(MinWidth), Math.Min(S(BaseWidth), maxLeft));
        }

        int RightColX() { return LeftColWidth() + S(PanelGap); }
        int RightColWidth() { return Math.Max(0, ClientSize.Width - RightColX()); }

        void CreateFonts()
        {
            Font[] old = { fLabel, fValue, fSmall, fClock, fPomo, fMailTitle, fMailFrom };
            fLabel = MakeFont("Segoe UI", 9f, FontStyle.Regular);
            fValue = MakeFont("Segoe UI Semibold", 9.5f, FontStyle.Regular);
            fSmall = MakeFont("Segoe UI", 8f, FontStyle.Regular);
            fClock = MakeFont("Segoe UI Semibold", 22f, FontStyle.Regular);
            fPomo = MakeFont("Segoe UI Semibold", 18f, FontStyle.Regular);
            fMailTitle = MakeFont("Segoe UI Semibold", 9.5f, FontStyle.Regular);
            fMailFrom = MakeFont("Segoe UI", 7.5f, FontStyle.Regular);
            foreach (Font f in old) if (f != null) f.Dispose();
        }

        void StartUiTimer()
        {
            if (uiTimer != null) return;
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += (s, e) => OnUiTick();
            uiTimer.Start();
        }

        void OnUiTick()
        {
            bool need = settings.ShowClock || settings.ShowPomodoro || settings.ShowGmail;
            if (!need) return;
            UpdatePomodoroRemain();
            if (settings.ShowGmail) EnsureGmailFetch(false);
            Invalidate();
        }

        void EnsureSideWidth()
        {
            Size min = MinContentSize();
            if (ClientSize.Width < min.Width || ClientSize.Height < min.Height)
                SetSizeClamped(Math.Max(ClientSize.Width, min.Width), Math.Max(ClientSize.Height, min.Height));
        }

        // ポイント指定だと GDI の DPI と S() の DPI がずれて、文字だけ変わらないことがある。
        // レイアウトと同じ倍率でピクセルサイズにする。
        Font MakeFont(string family, float pt, FontStyle style)
        {
            float px = Math.Max(1f, pt * settings.FontScale * Math.Max(scale, 1f) * 96f / 72f);
            try { return new Font(family, px, style, GraphicsUnit.Pixel); }
            catch
            {
                try { return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel); }
                catch { return new Font(FontFamily.GenericSansSerif, px, FontStyle.Regular, GraphicsUnit.Pixel); }
            }
        }

        void QueueStep(int dir)
        {
            if (!IsHandleCreated) { StepFontScale(dir); return; }
            BeginInvoke(new Action(delegate { StepFontScale(dir); }));
        }

        void QueueScale(float k)
        {
            if (!IsHandleCreated) { SetFontScale(k); return; }
            BeginInvoke(new Action(delegate { SetFontScale(k); }));
        }

        void SetSizeClamped(int w, int h)
        {
            Size min = MinContentSize();
            MinimumSize = Size.Empty;
            ClientSize = new Size(Math.Max(min.Width, w), Math.Max(min.Height, h));
            MinimumSize = min;
        }

        void SetFontScale(float k)
        {
            k = Math.Max(Settings.MinFontScale, Math.Min(Settings.MaxFontScale, k));
            float old = settings.FontScale;
            if (Math.Abs(k - old) < 0.001f) return;

            // 幅と、グラフに割り当てた余分な高さは倍率に比例させて保つ
            int oldExtraH = Math.Max(0, ClientSize.Height - PreferredHeight());
            float ratio = k / old;
            int newW = (int)Math.Round(ClientSize.Width * ratio);
            bool anchorRight = Screen.FromControl(this).WorkingArea.Left + Screen.FromControl(this).WorkingArea.Width / 2
                               < Left + Width / 2;
            int right = Right;

            settings.FontScale = k;
            CreateFonts();
            SetSizeClamped(newW, PreferredHeight() + (int)Math.Round(oldExtraH * ratio));
            if (anchorRight) Left = right - Width;

            SaveBounds();
            Invalidate();
        }

        void StepFontScale(int dir)
        {
            float cur = settings.FontScale;
            float next = dir > 0 ? FontScales.FirstOrDefault(f => f > cur + 0.001f)
                                 : FontScales.LastOrDefault(f => f < cur - 0.001f);
            if (next > 0) SetFontScale(next);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00040000; // WS_THICKFRAME: これがないと端のヒットテストではサイズ変更が始まらない
                cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW: タスクバー / Alt+Tab に出さない
                return cp;
            }
        }

        Point InitialLocation()
        {
            var saved = new Point(settings.X, settings.Y);
            if (settings.X != int.MinValue && settings.Y != int.MinValue &&
                Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(saved, new Size(S(40), S(40))))))
                return saved;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - Width - S(16), wa.Top + S(16));
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();

            miTopMost = new ToolStripMenuItem("常に手前に表示", null, (s, e) =>
            {
                TopMost = settings.TopMost = !settings.TopMost;
                miTopMost.Checked = settings.TopMost;
                settings.Save();
            });
            miTopMost.Checked = settings.TopMost;

            var miOpacity = new ToolStripMenuItem("透明度");
            foreach (int pct in new[] { 100, 90, 80, 70, 60, 50 })
            {
                double val = pct / 100.0;
                var item = new ToolStripMenuItem(pct + "%", null, (s, e) =>
                {
                    Opacity = settings.Opacity = val;
                    settings.Save();
                });
                miOpacity.DropDownItems.Add(item);
            }
            miOpacity.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripMenuItem it in miOpacity.DropDownItems)
                    it.Checked = it.Text == (int)Math.Round(settings.Opacity * 100) + "%";
            };

            var miUnit = new ToolStripMenuItem("速度の単位");
            miBits = new ToolStripMenuItem("bps（ビット/秒）", null, (s, e) => SetUnit(true));
            miBytes = new ToolStripMenuItem("B/s（バイト/秒）", null, (s, e) => SetUnit(false));
            miUnit.DropDownItems.Add(miBits);
            miUnit.DropDownItems.Add(miBytes);
            miBits.Checked = settings.Bits;
            miBytes.Checked = !settings.Bits;

            miAutoStart = new ToolStripMenuItem("Windows 起動時に自動起動", null, (s, e) =>
            {
                try
                {
                    AutoStart.Set(!AutoStart.IsEnabled());
                    miAutoStart.Checked = AutoStart.IsEnabled();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("自動起動の設定に失敗しました: " + ex.Message, "SysmonWidget");
                }
            });
            menu.Opening += (s, e) => { try { miAutoStart.Checked = AutoStart.IsEnabled(); } catch { } };

            var miFont = new ToolStripMenuItem("文字サイズ");
            miFont.DropDownItems.Add(new ToolStripMenuItem("大きく", null, (s, e) => QueueStep(1))
            {
                ShortcutKeyDisplayString = "Ctrl+ホイール↑"
            });
            miFont.DropDownItems.Add(new ToolStripMenuItem("小さく", null, (s, e) => QueueStep(-1))
            {
                ShortcutKeyDisplayString = "Ctrl+ホイール↓"
            });
            miFont.DropDownItems.Add(new ToolStripSeparator());
            foreach (float f in FontScales)
            {
                float val = f;
                var item = new ToolStripMenuItem((int)Math.Round(f * 100) + "%", null, (s, e) => QueueScale(val));
                item.Tag = val;
                miFont.DropDownItems.Add(item);
            }
            miFont.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripItem it in miFont.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is float)
                        mi.Checked = Math.Abs((float)mi.Tag - settings.FontScale) < 0.001f;
                }
            };

            miClock = new ToolStripMenuItem("時計");
            var miClockShow = new ToolStripMenuItem("表示する", null, (s, e) => ToggleClock(!settings.ShowClock));
            miClockDigital = new ToolStripMenuItem("デジタル", null, (s, e) => SetClockAnalog(false));
            miClockAnalog = new ToolStripMenuItem("アナログ", null, (s, e) => SetClockAnalog(true));
            miClock.DropDownItems.Add(miClockShow);
            miClock.DropDownItems.Add(new ToolStripSeparator());
            miClock.DropDownItems.Add(miClockDigital);
            miClock.DropDownItems.Add(miClockAnalog);
            miClock.DropDownOpening += (s, e) =>
            {
                miClockShow.Checked = settings.ShowClock;
                miClockDigital.Checked = !settings.ClockAnalog;
                miClockAnalog.Checked = settings.ClockAnalog;
                miClockDigital.Enabled = miClockAnalog.Enabled = settings.ShowClock;
            };

            miPomo = new ToolStripMenuItem("ポモドーロ");
            var miPomoShow = new ToolStripMenuItem("表示する", null, (s, e) => TogglePomodoroPanel(!settings.ShowPomodoro));
            var miPomoStart = new ToolStripMenuItem("開始 / 一時停止", null, (s, e) => TogglePomodoroRun());
            var miPomoReset = new ToolStripMenuItem("リセット", null, (s, e) => ResetPomodoro());
            var miPomoWork = new ToolStripMenuItem("作業時間");
            foreach (int m in WorkMins)
            {
                int val = m;
                miPomoWork.DropDownItems.Add(new ToolStripMenuItem(val + " 分", null, (s, e) =>
                {
                    settings.PomodoroWorkMin = val;
                    settings.Save();
                    if (pomoPhase == 1 && !pomoPaused) RestartPomodoroPhase();
                }) { Tag = val });
            }
            miPomoWork.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripItem it in miPomoWork.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is int) mi.Checked = (int)mi.Tag == settings.PomodoroWorkMin;
                }
            };
            var miPomoBreak = new ToolStripMenuItem("休憩時間");
            foreach (int m in BreakMins)
            {
                int val = m;
                miPomoBreak.DropDownItems.Add(new ToolStripMenuItem(val + " 分", null, (s, e) =>
                {
                    settings.PomodoroBreakMin = val;
                    settings.Save();
                    if (pomoPhase == 2 && !pomoPaused) RestartPomodoroPhase();
                }) { Tag = val });
            }
            miPomoBreak.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripItem it in miPomoBreak.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is int) mi.Checked = (int)mi.Tag == settings.PomodoroBreakMin;
                }
            };
            miPomo.DropDownItems.Add(miPomoShow);
            miPomo.DropDownItems.Add(new ToolStripSeparator());
            miPomo.DropDownItems.Add(miPomoStart);
            miPomo.DropDownItems.Add(miPomoReset);
            miPomo.DropDownItems.Add(miPomoWork);
            miPomo.DropDownItems.Add(miPomoBreak);
            miPomo.DropDownOpening += (s, e) =>
            {
                miPomoShow.Checked = settings.ShowPomodoro;
                miPomoStart.Enabled = miPomoReset.Enabled = settings.ShowPomodoro;
                miPomoWork.Enabled = miPomoBreak.Enabled = settings.ShowPomodoro;
            };

            miGmail = new ToolStripMenuItem("Gmail");
            var miGmailShow = new ToolStripMenuItem("表示する", null, (s, e) => ToggleGmail(!settings.ShowGmail));
            var miGmailConnect = new ToolStripMenuItem("接続…", null, (s, e) => BeginGmailAuth());
            var miGmailCred = new ToolStripMenuItem("Client ID を設定…", null, (s, e) => EditGmailCredentials());
            var miGmailRefresh = new ToolStripMenuItem("今すぐ更新", null, (s, e) => EnsureGmailFetch(true));
            var miGmailCount = new ToolStripMenuItem("表示件数");
            for (int n = 3; n <= 10; n++)
            {
                int val = n;
                miGmailCount.DropDownItems.Add(new ToolStripMenuItem(val + " 件", null, (s, e) =>
                {
                    settings.GmailCount = val;
                    settings.Save();
                    EnsureSideWidth();
                    EnsureGmailFetch(true);
                    Invalidate();
                }) { Tag = val });
            }
            miGmailCount.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripItem it in miGmailCount.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is int) mi.Checked = (int)mi.Tag == settings.GmailCount;
                }
            };
            var miGmailInterval = new ToolStripMenuItem("更新間隔");
            foreach (int sec in GmailIntervals)
            {
                int val = sec;
                miGmailInterval.DropDownItems.Add(new ToolStripMenuItem(FormatGmailInterval(val), null, (s, e) =>
                {
                    settings.GmailIntervalSec = val;
                    settings.Save();
                    gmailNextFetchUtc = DateTime.MinValue;
                    EnsureGmailFetch(true);
                }) { Tag = val });
            }
            miGmailInterval.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripItem it in miGmailInterval.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is int) mi.Checked = (int)mi.Tag == settings.GmailIntervalSec;
                }
            };
            miGmail.DropDownItems.Add(miGmailShow);
            miGmail.DropDownItems.Add(new ToolStripSeparator());
            miGmail.DropDownItems.Add(miGmailConnect);
            miGmail.DropDownItems.Add(miGmailCred);
            miGmail.DropDownItems.Add(miGmailRefresh);
            miGmail.DropDownItems.Add(miGmailCount);
            miGmail.DropDownItems.Add(miGmailInterval);
            miGmail.DropDownOpening += (s, e) =>
            {
                miGmailShow.Checked = settings.ShowGmail;
                bool on = settings.ShowGmail;
                miGmailConnect.Enabled = on;
                miGmailRefresh.Enabled = on && gmail.IsAuthorized;
                miGmailCount.Enabled = on;
                miGmailInterval.Enabled = on;
            };

            var miReset = new ToolStripMenuItem("位置とサイズをリセット", null, (s, e) =>
            {
                SetSizeClamped(PreferredWidth(), PreferredHeight());
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - S(16), wa.Top + S(16));
                SaveBounds();
                Invalidate();
            });
            var miExit = new ToolStripMenuItem("終了", null, (s, e) => Close());

            menu.Items.AddRange(new ToolStripItem[]
            {
                miTopMost, miOpacity, miFont, miUnit, miClock, miPomo, miGmail, miAutoStart,
                miReset, new ToolStripSeparator(), miExit
            });
            ContextMenuStrip = menu;

            tray = new NotifyIcon
            {
                Icon = MakeTrayIcon(),
                Text = "SysmonWidget",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                Visible = !Visible;
                if (Visible) Activate();
            };
        }

        void SetUnit(bool bits)
        {
            settings.Bits = bits;
            miBits.Checked = bits;
            miBytes.Checked = !bits;
            settings.Save();
            Invalidate();
        }

        void ToggleClock(bool on)
        {
            settings.ShowClock = on;
            settings.Save();
            EnsureSideWidth();
            Invalidate();
        }

        void SetClockAnalog(bool analog)
        {
            settings.ClockAnalog = analog;
            settings.Save();
            EnsureSideWidth();
            Invalidate();
        }

        void TogglePomodoroPanel(bool on)
        {
            settings.ShowPomodoro = on;
            if (!on) ResetPomodoro();
            settings.Save();
            EnsureSideWidth();
            Invalidate();
        }

        void ToggleGmail(bool on)
        {
            settings.ShowGmail = on;
            settings.Save();
            EnsureSideWidth();
            if (on)
            {
                gmailNextFetchUtc = DateTime.MinValue;
                EnsureGmailFetch(true);
            }
            else
            {
                // 非表示の間は更新もトークン再発行もしない
                Interlocked.Increment(ref gmailFetchGen);
                gmailNextFetchUtc = DateTime.MaxValue;
                gmail.AbortRequests();
                if (gmailAuthCancel != null)
                {
                    try { gmailAuthCancel.Cancel(); } catch { }
                }
            }
            Invalidate();
        }

        void UpdatePomodoroRemain()
        {
            if (pomoPhase == 0)
            {
                pomoRemainSec = settings.PomodoroWorkMin * 60;
                return;
            }
            if (pomoPaused) return;
            int left = (int)Math.Ceiling((pomoDeadlineUtc - DateTime.UtcNow).TotalSeconds);
            if (left <= 0)
            {
                if (pomoPhase == 1)
                {
                    pomoPhase = 2;
                    pomoDeadlineUtc = DateTime.UtcNow.AddMinutes(settings.PomodoroBreakMin);
                    pomoRemainSec = settings.PomodoroBreakMin * 60;
                }
                else
                {
                    pomoPhase = 1;
                    pomoDeadlineUtc = DateTime.UtcNow.AddMinutes(settings.PomodoroWorkMin);
                    pomoRemainSec = settings.PomodoroWorkMin * 60;
                }
            }
            else pomoRemainSec = left;
        }

        void TogglePomodoroRun()
        {
            if (!settings.ShowPomodoro) return;
            if (pomoPhase == 0)
            {
                pomoPhase = 1;
                pomoPaused = false;
                pomoDeadlineUtc = DateTime.UtcNow.AddMinutes(settings.PomodoroWorkMin);
                pomoRemainSec = settings.PomodoroWorkMin * 60;
            }
            else if (pomoPaused)
            {
                pomoPaused = false;
                pomoDeadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, pomoRemainSec));
            }
            else
            {
                UpdatePomodoroRemain();
                pomoPaused = true;
            }
            Invalidate();
        }

        void ResetPomodoro()
        {
            pomoPhase = 0;
            pomoPaused = false;
            pomoRemainSec = settings.PomodoroWorkMin * 60;
            Invalidate();
        }

        void RestartPomodoroPhase()
        {
            int mins = pomoPhase == 2 ? settings.PomodoroBreakMin : settings.PomodoroWorkMin;
            pomoDeadlineUtc = DateTime.UtcNow.AddMinutes(mins);
            pomoRemainSec = mins * 60;
            pomoPaused = false;
            Invalidate();
        }

        void EditGmailCredentials()
        {
            string curId, curSecret;
            GmailAppCredentials.Load(out curId, out curSecret);
            using (var dlg = new Form())
            {
                dlg.Text = "Gmail OAuth 設定";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.ClientSize = new Size(440, 176);
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                var l1 = new Label { Text = "Client ID", Left = 12, Top = 16, Width = 100 };
                var t1 = new TextBox { Left = 120, Top = 12, Width = 300, Text = curId };
                var l2 = new Label { Text = "Client Secret", Left = 12, Top = 52, Width = 100 };
                var t2 = new TextBox { Left = 120, Top = 48, Width = 300, Text = curSecret, UseSystemPasswordChar = true };
                var tip = new Label
                {
                    Left = 12, Top = 84, Width = 410, Height = 40,
                    Text = "Google Cloud の Desktop OAuth クライアントを入力。\nCredential Manager に保存します（settings.ini には書きません）。"
                };
                var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Left = 250, Top = 136, Width = 80 };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 340, Top = 136, Width = 80 };
                dlg.Controls.AddRange(new Control[] { l1, t1, l2, t2, tip, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string id = (t1.Text ?? "").Trim();
                string secret = (t2.Text ?? "").Trim();
                if (string.IsNullOrEmpty(id)) GmailAppCredentials.Delete();
                else GmailAppCredentials.Save(id, secret);
                // 旧 ini の平文を消す
                settings.Save();
            }
        }

        void BeginGmailAuth()
        {
            string cid, secret;
            GmailAppCredentials.Load(out cid, out secret);
            if (string.IsNullOrEmpty(cid))
            {
                EditGmailCredentials();
                GmailAppCredentials.Load(out cid, out secret);
                if (string.IsNullOrEmpty(cid)) return;
            }
            if (gmailAuthCancel != null)
            {
                try { gmailAuthCancel.Cancel(); } catch { }
            }
            gmailAuthCancel = new CancellationTokenSource();
            int gen = Interlocked.Increment(ref gmailAuthGen);
            gmailStatus = "ブラウザで認証中…";
            Invalidate();
            CancellationToken token = gmailAuthCancel.Token;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = gmail.AuthorizeInteractive(cid, secret, token);
                string err = gmail.LastError;
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (gen != gmailAuthGen) return;
                        gmailStatus = ok ? "接続済み" : (err != null && err.Length > 0 ? err : "認証失敗");
                        if (ok && settings.ShowGmail) EnsureGmailFetch(true);
                        Invalidate();
                    }));
                }
            });
        }

        static string FormatGmailInterval(int sec)
        {
            if (sec < 60) return sec + " 秒";
            if (sec % 60 == 0) return (sec / 60) + " 分";
            return (sec / 60) + " 分 " + (sec % 60) + " 秒";
        }

        void EnsureGmailFetch(bool force)
        {
            if (!settings.ShowGmail) return;
            if (!gmail.IsAuthorized)
            {
                gmailStatus = "未接続";
                return;
            }
            if (!force && DateTime.UtcNow < gmailNextFetchUtc) return;
            if (gmailWorker != null && gmailWorker.IsAlive) return;
            int interval = Math.Max(15, Math.Min(3600, settings.GmailIntervalSec));
            gmailNextFetchUtc = DateTime.UtcNow.AddSeconds(interval);
            gmailStatus = "更新中…";
            string cid, secret;
            GmailAppCredentials.Load(out cid, out secret);
            int count = settings.GmailCount;
            int gen = gmailFetchGen;
            gmailWorker = new Thread(() =>
            {
                Func<bool> wanted = () => gen == Interlocked.CompareExchange(ref gmailFetchGen, 0, 0) && settings.ShowGmail;
                if (!wanted()) return;
                bool ok = gmail.RefreshInbox(cid, secret, count, wanted);
                if (!wanted()) return;
                GmailMailItem[] items = gmail.Snapshot();
                string err = gmail.LastError;
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (gen != gmailFetchGen || !settings.ShowGmail) return;
                        if (ok) { gmailItems = items; gmailStatus = ""; }
                        else gmailStatus = string.IsNullOrEmpty(err) ? "取得失敗" : err;
                        Invalidate();
                    }));
                }
            }) { IsBackground = true, Name = "GmailFetch" };
            gmailWorker.Start();
        }

        static Icon MakeTrayIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(28, 28, 32));
                    using (var b1 = new SolidBrush(CCpu)) g.FillRectangle(b1, 2, 8, 3, 6);
                    using (var b2 = new SolidBrush(CMem)) g.FillRectangle(b2, 6, 4, 3, 10);
                    using (var b3 = new SolidBrush(CGpu)) g.FillRectangle(b3, 10, 2, 3, 12);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (wheelFilter == null)
            {
                wheelFilter = new FontWheelFilter(this);
                Application.AddMessageFilter(wheelFilter);
            }
            int round = 2; // DWMWCP_ROUND（Windows 11 の角丸）
            try { DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MetricsWorker" };
            worker.Start();
        }

        void WorkerLoop()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                long start = sw.ElapsedMilliseconds;
                Snapshot s = null;
                try { s = collector.Sample(); } catch { }
                if (s != null && IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke(new Action<Snapshot>(OnSnapshot), s); }
                    catch (InvalidOperationException) { return; }
                }
                int wait = 1000 - (int)(sw.ElapsedMilliseconds - start);
                if (stopEvent.WaitOne(Math.Max(100, wait))) return;
            }
        }

        void OnSnapshot(Snapshot s)
        {
            snap = s;
            Push(upHist, s.Up);
            Push(downHist, s.Down);
            if (tray != null)
            {
                string tip = string.Format("CPU {0}  MEM {1}  GPU {2}", Pct(s.Cpu), Pct(s.Mem), Pct(s.Gpu));
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            Invalidate();
        }

        static void Push(List<double> list, double v)
        {
            list.Add(Math.Max(0, v));
            if (list.Count > HistLen) list.RemoveAt(0);
        }

        static string Pct(float v) { return v < 0 ? "--" : string.Format("{0:0}%", v); }

        string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 0) return "--";
            if (settings.Bits)
            {
                double b = bytesPerSec * 8;
                if (b < 1e3) return string.Format("{0:0} bps", b);
                if (b < 1e6) return string.Format("{0:0.0} Kbps", b / 1e3);
                if (b < 1e9) return string.Format("{0:0.0} Mbps", b / 1e6);
                return string.Format("{0:0.00} Gbps", b / 1e9);
            }
            if (bytesPerSec < 1024) return string.Format("{0:0} B/s", bytesPerSec);
            if (bytesPerSec < 1048576) return string.Format("{0:0.0} KB/s", bytesPerSec / 1024);
            if (bytesPerSec < 1073741824) return string.Format("{0:0.0} MB/s", bytesPerSec / 1048576);
            return string.Format("{0:0.00} GB/s", bytesPerSec / 1073741824);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (settings.ShowPomodoro && pomoToggleRect.Contains(e.Location))
            {
                TogglePomodoroRun();
                return;
            }
            // 端やグリップはサイズ変更。内側は移動。
            // NCHITTEST がクライアント扱いに戻しても、ここでも同じ判定にする。
            int ht = HitTestClient(e.Location);
            if (ht == 1) ht = 2; // HTCAPTION
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)ht, IntPtr.Zero);
            SaveBounds();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) != 0 && e.Delta != 0) StepFontScale(e.Delta > 0 ? 1 : -1);
        }

        int HitTestClient(Point pt)
        {
            int edge = Math.Max(ResizeBorder, (int)Math.Round(8 * Math.Max(scale, 1f)));
            int grip = Math.Max(edge, (int)Math.Round(14 * Math.Max(scale, 1f)));
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return 1;
            if (pt.X >= w - grip && pt.Y >= h - grip) return 17; // HTBOTTOMRIGHT
            bool l = pt.X < edge, r = pt.X >= w - edge;
            bool t = pt.Y < edge, b = pt.Y >= h - edge;
            if (t && l) return 13; // HTTOPLEFT
            if (t && r) return 14; // HTTOPRIGHT
            if (t) return 12;       // HTTOP
            if (b && l) return 16;  // HTBOTTOMLEFT
            if (b && r) return 17;  // HTBOTTOMRIGHT
            if (b) return 15;       // HTBOTTOM
            if (l) return 10;       // HTLEFT
            if (r) return 11;       // HTRIGHT
            return 1;               // HTCLIENT
        }

        void ApplyResizeCursor(Point pt)
        {
            Cursor c;
            switch (HitTestClient(pt))
            {
                case 10: case 11: c = Cursors.SizeWE; break;
                case 12: case 15: c = Cursors.SizeNS; break;
                case 13: case 17: c = Cursors.SizeNWSE; break;
                case 14: case 16: c = Cursors.SizeNESW; break;
                default: c = Cursors.Default; break;
            }
            // Cursor プロパティへ代入すると、ポインタが窓の中にある間
            // WM_SETCURSOR を同期送信する。このハンドラからだと無限再帰になる。
            SetCursor(c.Handle);
        }

        // 枠なしウィンドウは、ヒットテストでサイズ用の端を返しても OS がリサイズを始めない。
        // クリックはクライアントのまま受けて、移動と同じ SendMessage でサイズ変更を始める。
        // WS_THICKFRAME の見た目の枠は WM_NCCALCSIZE で消す。
        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x83, WM_SETCURSOR = 0x20;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                if (IsHandleCreated && IsZoomed(Handle))
                {
                    RECT rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    int pad = GetSystemMetrics(92); // SM_CXPADDEDBORDER
                    int fx = GetSystemMetrics(32) + pad; // SM_CXFRAME
                    int fy = GetSystemMetrics(33) + pad; // SM_CYFRAME
                    rc.Left += fx; rc.Top += fy; rc.Right -= fx; rc.Bottom -= fy;
                    Marshal.StructureToPtr(rc, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_SETCURSOR && IsHandleCreated)
            {
                Point pt = PointToClient(Cursor.Position);
                if (HitTestClient(pt) != 1)
                {
                    ApplyResizeCursor(pt);
                    m.Result = (IntPtr)1;
                    return;
                }
            }

            base.WndProc(ref m);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveBounds();
        }

        void SaveBounds()
        {
            settings.X = Left;
            settings.Y = Top;
            settings.Width = ClientSize.Width;
            settings.Height = ClientSize.Height;
            settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (wheelFilter != null)
            {
                Application.RemoveMessageFilter(wheelFilter);
                wheelFilter = null;
            }
            if (uiTimer != null) { uiTimer.Stop(); uiTimer.Dispose(); uiTimer = null; }
            if (gmailAuthCancel != null) { try { gmailAuthCancel.Cancel(); } catch { } }
            stopEvent.Set();
            SaveBounds();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            int leftW = LeftColWidth();
            DrawMetricsColumn(g, leftW);
            if (settings.AnySidePanel)
            {
                int sx = RightColX();
                g.DrawLine(pSep, sx - S(PanelGap) / 2, S(Pad), sx - S(PanelGap) / 2, ClientSize.Height - S(Pad));
                DrawSideColumn(g, sx, RightColWidth());
            }
            DrawResizeGrip(g);
        }

        void DrawMetricsColumn(Graphics g, int colW)
        {
            Snapshot s = snap ?? new Snapshot();
            int y = S(Pad);
            int left = S(Pad), right = colW - S(Pad);

            string memDetail = s.MemTotal > 0
                ? string.Format("{0:0.0} / {1:0.0} GB", s.MemUsed / 1073741824.0, s.MemTotal / 1073741824.0)
                : null;
            DrawMetric(g, left, right, "CPU", s.Cpu, null, CCpu, ref y);
            DrawMetric(g, left, right, "MEM", s.Mem, memDetail, CMem, ref y);
            DrawMetric(g, left, right, "DISK", s.Disk, null, CDisk, ref y);
            DrawMetric(g, left, right, "GPU", s.Gpu, null, CGpu, ref y);

            int sepY = y + S(SepH) / 2 - S(RowGap) / 2;
            g.DrawLine(pSep, left, sepY, right, sepY);
            y += S(SepH) - S(RowGap) / 2;

            g.DrawString("Wi-Fi", fLabel, bLabel, left, y);
            string status = !s.WifiConnected ? (snap == null ? "" : "未接続")
                : s.WifiLinkSpeed > 0 ? string.Format("リンク {0:0} Mbps", s.WifiLinkSpeed / 1e6) : "";
            DrawRight(g, status, fSmall, bDim, right, y + S(2));
            y += S(NetHeaderH);

            int half = Math.Max(0, (right - left) / 2);
            using (var bd = new SolidBrush(CDown))
                g.DrawString("↓ " + FormatSpeed(s.Down), fValue, bd, left, y);
            using (var bu = new SolidBrush(CUp))
                g.DrawString("↑ " + FormatSpeed(s.Up), fValue, bu, left + half, y);
            y += S(NetSpeedH);

            int graphH = ClientSize.Height - S(Pad) - S(4) - y;
            if (graphH > 2 && right > left)
                DrawGraph(g, new Rectangle(left, y, right - left, graphH));
        }

        void DrawSideColumn(Graphics g, int x, int w)
        {
            int y = S(Pad);
            int left = x, right = x + w - S(Pad);
            int innerW = Math.Max(0, right - left);
            pomoToggleRect = Rectangle.Empty;

            if (settings.ShowClock)
            {
                int ch = S(settings.ClockAnalog ? ClockAnalogH : ClockDigitalH);
                DrawClock(g, new Rectangle(left, y, innerW, ch));
                y += ch + S(8);
            }
            if (settings.ShowPomodoro)
            {
                int ph = S(PomoH);
                DrawPomodoro(g, new Rectangle(left, y, innerW, ph));
                y += ph + S(8);
            }
            if (settings.ShowGmail)
            {
                int gh = ClientSize.Height - S(Pad) - y;
                if (gh > S(GmailHeaderH))
                    DrawGmail(g, new Rectangle(left, y, innerW, gh));
            }
        }

        void DrawClock(Graphics g, Rectangle r)
        {
            DateTime now = DateTime.Now;
            g.DrawString("時計", fSmall, bDim, r.X, r.Y);
            if (settings.ClockAnalog)
            {
                int size = Math.Min(r.Width - S(8), r.Height - S(18));
                if (size < 8) return;
                var face = new Rectangle(r.X + (r.Width - size) / 2, r.Y + S(16), size, size);
                using (var pen = new Pen(Color.FromArgb(90, 90, 100), Math.Max(1f, scale)))
                    g.DrawEllipse(pen, face);
                float cx = face.X + face.Width / 2f, cy = face.Y + face.Height / 2f, rad = face.Width / 2f;
                DrawHand(g, cx, cy, rad * 0.55f, (now.Hour % 12 + now.Minute / 60f) * 30f, 2.5f * scale, bValue);
                DrawHand(g, cx, cy, rad * 0.75f, now.Minute * 6f + now.Second * 0.1f, 2f * scale, bValue);
                using (var sec = new SolidBrush(CHigh))
                    DrawHand(g, cx, cy, rad * 0.85f, now.Second * 6f, 1.2f * scale, sec);
            }
            else
            {
                string time = now.ToString("HH:mm:ss");
                string date = now.ToString("yyyy/MM/dd ddd", CultureInfo.GetCultureInfo("ja-JP"));
                float timeY = r.Y + S(14);
                float tw = g.MeasureString(time, fClock).Width;
                g.DrawString(time, fClock, bValue, r.X + Math.Max(0, (r.Width - tw) / 2f), timeY);
                // 数字にはディセンダが無いので、日付はベースライン直下から描く（行の高さで並べると間延びする）
                FontFamily ff = fClock.FontFamily;
                float ascent = fClock.Size * ff.GetCellAscent(fClock.Style) / ff.GetEmHeight(fClock.Style);
                float dw = g.MeasureString(date, fSmall).Width;
                g.DrawString(date, fSmall, bDim, r.X + Math.Max(0, (r.Width - dw) / 2f), timeY + ascent + S(3));
            }
        }

        static void DrawHand(Graphics g, float cx, float cy, float length, float deg, float width, Brush brush)
        {
            double rad = (deg - 90) * Math.PI / 180.0;
            var tip = new PointF(cx + (float)(Math.Cos(rad) * length), cy + (float)(Math.Sin(rad) * length));
            using (var pen = new Pen(brush, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx, cy, tip.X, tip.Y);
        }

        void DrawPomodoro(Graphics g, Rectangle r)
        {
            UpdatePomodoroRemain();
            string phase = pomoPhase == 0 ? "停止" : pomoPhase == 1 ? (pomoPaused ? "作業 一時停止" : "作業中")
                : (pomoPaused ? "休憩 一時停止" : "休憩中");
            g.DrawString("ポモドーロ · " + phase, fSmall, bDim, r.X, r.Y);
            string mmss = string.Format("{0:00}:{1:00}", Math.Max(0, pomoRemainSec) / 60, Math.Max(0, pomoRemainSec) % 60);
            float tw = g.MeasureString(mmss, fPomo).Width;
            g.DrawString(mmss, fPomo, bValue, r.X + Math.Max(0, (r.Width - tw) / 2f), r.Y + S(16));

            string btn = pomoPhase == 0 ? "開始" : (pomoPaused ? "再開" : "一時停止");
            SizeF bs = g.MeasureString(btn, fSmall);
            int bw = (int)bs.Width + S(16), bh = S(20);
            pomoToggleRect = new Rectangle(r.X + (r.Width - bw) / 2, r.Bottom - bh - S(2), bw, bh);
            using (var bg = new SolidBrush(Color.FromArgb(55, 55, 62)))
                FillRounded(g, bg, pomoToggleRect);
            float bx = pomoToggleRect.X + (pomoToggleRect.Width - bs.Width) / 2f;
            float by = pomoToggleRect.Y + (pomoToggleRect.Height - bs.Height) / 2f;
            g.DrawString(btn, fSmall, bValue, bx, by);
        }

        void DrawGmail(Graphics g, Rectangle r)
        {
            g.DrawString("Gmail", fSmall, bDim, r.X, r.Y);
            if (!string.IsNullOrEmpty(gmailStatus))
                DrawRight(g, Ellipsize(g, gmailStatus, fSmall, r.Width / 2), fSmall, bDim, r.Right, r.Y);
            int y = r.Y + S(GmailHeaderH);
            int rowH = S(GmailRowH);
            int shown = 0;
            GmailMailItem[] items = gmailItems ?? new GmailMailItem[0];
            for (int i = 0; i < items.Length && y + rowH <= r.Bottom; i++)
            {
                GmailMailItem m = items[i];
                string from = Ellipsize(g, m.From ?? "", fMailFrom, r.Width);
                string sub = Ellipsize(g, m.Subject ?? "", fMailTitle, r.Width);
                g.DrawString(from, fMailFrom, bDim, r.X, y);
                g.DrawString(sub, fMailTitle, bValue, r.X, y + S(12));
                y += rowH;
                shown++;
            }
            if (shown == 0)
            {
                string tip = gmail.IsAuthorized ? "メールなし / 取得待ち" : "メニューから接続";
                g.DrawString(tip, fSmall, bDim, r.X, y);
            }
        }

        static string Ellipsize(Graphics g, string text, Font f, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return "";
            if (g.MeasureString(text, f).Width <= maxWidth) return text;
            const string ell = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(text.Substring(0, mid) + ell, f).Width <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return lo <= 0 ? ell : text.Substring(0, lo) + ell;
        }

        void DrawResizeGrip(Graphics g)
        {
            float d = Math.Max(1.5f, 1.5f * scale), gap = 4 * scale;
            float x0 = ClientSize.Width - 4 * scale, y0 = ClientSize.Height - 4 * scale;
            using (var b = new SolidBrush(Color.FromArgb(90, 90, 100)))
            {
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j <= i; j++)
                        g.FillEllipse(b, x0 - j * gap - d, y0 - (i - j) * gap - d, d, d);
            }
        }

        void DrawMetric(Graphics g, int left, int right, string label, float value, string detail, Color accent, ref int y)
        {
            g.DrawString(label, fLabel, bLabel, left, y);
            float vw = DrawRight(g, Pct(value), fValue, bValue, right, y - S(1));
            if (detail != null) DrawRight(g, detail, fSmall, bDim, right - (int)vw - S(4), y + S(2));
            y += S(LabelH);

            var track = new Rectangle(left, y, Math.Max(0, right - left), S(BarH));
            FillRounded(g, bTrack, track);
            if (value > 0 && track.Width > 0)
            {
                int fw = Math.Max(track.Height, (int)(track.Width * value / 100f));
                using (var b = new SolidBrush(value >= 90 ? CHigh : accent))
                    FillRounded(g, b, new Rectangle(track.X, track.Y, fw, track.Height));
            }
            y += S(BarH) + S(RowGap);
        }

        static float DrawRight(Graphics g, string text, Font f, Brush b, int right, int y)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float w = g.MeasureString(text, f).Width;
            g.DrawString(text, f, b, right - w, y);
            return w;
        }

        static void FillRounded(Graphics g, Brush b, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int d = Math.Min(r.Height, r.Width);
            if (d <= 1) { g.FillRectangle(b, r); return; }
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        void DrawGraph(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Color.FromArgb(36, 36, 42))) g.FillRectangle(bg, r);
            if (downHist.Count < 2) return;

            double max = 1024;
            foreach (double v in downHist) max = Math.Max(max, v);
            foreach (double v in upHist) max = Math.Max(max, v);

            float step = r.Width / (float)(HistLen - 1);
            Func<List<double>, PointF[]> toPoints = list =>
            {
                var pts = new PointF[list.Count];
                int offset = HistLen - list.Count;
                for (int i = 0; i < list.Count; i++)
                    pts[i] = new PointF(r.X + (offset + i) * step, r.Bottom - 1 - (float)(list[i] / max * (r.Height - 2)));
                return pts;
            };

            PointF[] down = toPoints(downHist);
            var area = new List<PointF>(down);
            area.Add(new PointF(down[down.Length - 1].X, r.Bottom));
            area.Add(new PointF(down[0].X, r.Bottom));
            using (var fill = new SolidBrush(Color.FromArgb(70, CDown))) g.FillPolygon(fill, area.ToArray());
            using (var pd = new Pen(CDown, Math.Max(1f, scale))) g.DrawLines(pd, down);
            using (var pu = new Pen(CUp, Math.Max(1f, scale))) g.DrawLines(pu, toPoints(upHist));

            g.DrawString(FormatSpeed(max), fSmall, bDim, r.X + S(2), r.Y + S(1));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (wheelFilter != null) Application.RemoveMessageFilter(wheelFilter);
                if (uiTimer != null) { uiTimer.Dispose(); uiTimer = null; }
                if (fLabel != null) fLabel.Dispose();
                if (fValue != null) fValue.Dispose();
                if (fSmall != null) fSmall.Dispose();
                if (fClock != null) fClock.Dispose();
                if (fPomo != null) fPomo.Dispose();
                if (fMailTitle != null) fMailTitle.Dispose();
                if (fMailFrom != null) fMailFrom.Dispose();
                bLabel.Dispose(); bValue.Dispose(); bDim.Dispose(); bTrack.Dispose(); pSep.Dispose();
                if (menu != null) menu.Dispose();
            }
            base.Dispose(disposing);
        }

        // フォーカスがなくても、ウィジェットの上で Ctrl+ホイールなら文字サイズを変える。
        sealed class FontWheelFilter : IMessageFilter
        {
            readonly WidgetForm owner;
            public FontWheelFilter(WidgetForm owner) { this.owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_MOUSEWHEEL = 0x020A;
                if (m.Msg != WM_MOUSEWHEEL) return false;
                if ((Control.ModifierKeys & Keys.Control) == 0) return false;
                if (owner.IsDisposed || !owner.Visible) return false;
                if (owner.menu != null && owner.menu.Visible) return false;

                long wp = m.WParam.ToInt64();
                int delta = (short)((wp >> 16) & 0xFFFF);
                if (delta == 0) return false;
                long lp64 = m.LParam.ToInt64();
                int lp = unchecked((int)lp64);
                var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                if (!owner.Bounds.Contains(screen)) return false;
                owner.StepFontScale(delta > 0 ? 1 : -1);
                return true;
            }
        }
    }
}
