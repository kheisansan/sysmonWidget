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

        public static void Run()
        {
            AttachConsole(-1);
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

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "SysmonWidget", "settings.ini");
            }
        }

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
                    }
                }
            }
            catch { }
            return s;
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
                    "FontScale=" + FontScale.ToString(CultureInfo.InvariantCulture)
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
        const int Pad = 12, LabelH = 18, BarH = 6, RowGap = 10;
        const int SepH = 15, NetHeaderH = 18, NetSpeedH = 22, GraphH = 36, MinGraphH = 20;
        const int ResizeBorder = 6;
        static readonly float[] FontScales = { 0.75f, 0.9f, 1.0f, 1.15f, 1.3f, 1.5f, 1.75f, 2.0f };

        readonly Settings settings;
        readonly float scale;
        readonly MetricsCollector collector = new MetricsCollector();
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        readonly List<double> upHist = new List<double>(), downHist = new List<double>();
        Thread worker;
        Snapshot snap;

        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miTopMost, miAutoStart, miBits, miBytes;
        FontWheelFilter wheelFilter;

        Font fLabel, fValue, fSmall;
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

            int w = settings.Width > 0 ? settings.Width : S(BaseWidth);
            int h = settings.Height > 0 ? settings.Height : S(ContentHeight(GraphH));
            SetSizeClamped(w, h);
            Location = InitialLocation();

            BuildMenu();
        }

        // 文字サイズ倍率も含めたレイアウト単位（余白・行の高さも文字と一緒に拡大縮小する）
        int S(int v) { return (int)Math.Round(v * scale * settings.FontScale); }

        static int ContentHeight(int graphH)
        {
            return Pad * 2 + 4 * (LabelH + BarH + RowGap) + SepH + NetHeaderH + NetSpeedH + graphH;
        }

        Size MinContentSize() { return new Size(S(MinWidth), S(ContentHeight(MinGraphH))); }

        void CreateFonts()
        {
            Font[] old = { fLabel, fValue, fSmall };
            fLabel = MakeFont("Segoe UI", 9f, FontStyle.Regular);
            fValue = MakeFont("Segoe UI Semibold", 9.5f, FontStyle.Regular);
            fSmall = MakeFont("Segoe UI", 8f, FontStyle.Regular);
            foreach (Font f in old) if (f != null) f.Dispose();
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
            int oldExtraH = Math.Max(0, ClientSize.Height - S(ContentHeight(GraphH)));
            float ratio = k / old;
            int newW = (int)Math.Round(ClientSize.Width * ratio);
            bool anchorRight = Screen.FromControl(this).WorkingArea.Left + Screen.FromControl(this).WorkingArea.Width / 2
                               < Left + Width / 2;
            int right = Right;

            settings.FontScale = k;
            CreateFonts();
            SetSizeClamped(newW, S(ContentHeight(GraphH)) + (int)Math.Round(oldExtraH * ratio));
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

            var miReset = new ToolStripMenuItem("位置とサイズをリセット", null, (s, e) =>
            {
                SetSizeClamped(S(BaseWidth), S(ContentHeight(GraphH)));
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - S(16), wa.Top + S(16));
                SaveBounds();
                Invalidate();
            });
            var miExit = new ToolStripMenuItem("終了", null, (s, e) => Close());

            menu.Items.AddRange(new ToolStripItem[]
            {
                miTopMost, miOpacity, miFont, miUnit, miAutoStart, miReset, new ToolStripSeparator(), miExit
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

            Snapshot s = snap ?? new Snapshot();
            int y = S(Pad);

            string memDetail = s.MemTotal > 0
                ? string.Format("{0:0.0} / {1:0.0} GB", s.MemUsed / 1073741824.0, s.MemTotal / 1073741824.0)
                : null;
            DrawMetric(g, "CPU", s.Cpu, null, CCpu, ref y);
            DrawMetric(g, "MEM", s.Mem, memDetail, CMem, ref y);
            DrawMetric(g, "DISK", s.Disk, null, CDisk, ref y);
            DrawMetric(g, "GPU", s.Gpu, null, CGpu, ref y);

            int left = S(Pad), right = ClientSize.Width - S(Pad);
            int sepY = y + S(SepH) / 2 - S(RowGap) / 2;
            g.DrawLine(pSep, left, sepY, right, sepY);
            y += S(SepH) - S(RowGap) / 2;

            g.DrawString("Wi-Fi", fLabel, bLabel, left, y);
            string status = !s.WifiConnected ? (snap == null ? "" : "未接続")
                : s.WifiLinkSpeed > 0 ? string.Format("リンク {0:0} Mbps", s.WifiLinkSpeed / 1e6) : "";
            DrawRight(g, status, fSmall, bDim, right, y + S(2));
            y += S(NetHeaderH);

            int half = (right - left) / 2;
            using (var bd = new SolidBrush(CDown))
                g.DrawString("↓ " + FormatSpeed(s.Down), fValue, bd, left, y);
            using (var bu = new SolidBrush(CUp))
                g.DrawString("↑ " + FormatSpeed(s.Up), fValue, bu, left + half, y);
            y += S(NetSpeedH);

            int graphH = ClientSize.Height - S(Pad) - S(4) - y;
            if (graphH > 2 && right > left)
                DrawGraph(g, new Rectangle(left, y, right - left, graphH));
            DrawResizeGrip(g);
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

        void DrawMetric(Graphics g, string label, float value, string detail, Color accent, ref int y)
        {
            int left = S(Pad), right = ClientSize.Width - S(Pad);
            g.DrawString(label, fLabel, bLabel, left, y);
            float vw = DrawRight(g, Pct(value), fValue, bValue, right, y - S(1));
            if (detail != null) DrawRight(g, detail, fSmall, bDim, right - (int)vw - S(4), y + S(2));
            y += S(LabelH);

            var track = new Rectangle(left, y, right - left, S(BarH));
            FillRounded(g, bTrack, track);
            if (value > 0)
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
                fLabel.Dispose(); fValue.Dispose(); fSmall.Dispose();
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
