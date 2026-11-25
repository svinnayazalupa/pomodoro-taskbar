// Pomodoro — a compact timer that lives on the Windows taskbar.
// Build:  dotnet build -c Release
// Test:   Pomodoro.exe /fast   (each minute lasts one second)
using System;
using System.Globalization;
using System.IO;

#if WINFORMS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
#endif

namespace PomodoroApp
{
    public enum Phase
    {
        Idle = 0,
        Work = 1,
        Rest = 2
    }

    public enum BoundaryKind
    {
        WorkStarted = 0,
        RestStarted = 1,
        AllDone = 2
    }

    /// <summary>
    /// Timer logic with no UI. Time can be injected (for tests).
    /// </summary>
    public sealed class PomodoroEngine
    {
        public const int DefaultWorkMinutes = 25;
        public const int DefaultRestMinutes = 5;
        public const int DefaultReps = 4;

        readonly bool _fast;
        readonly Func<long> _clock;

        int _workMin = DefaultWorkMinutes;
        int _restMin = DefaultRestMinutes;
        int _repsCfg = DefaultReps;

        long _remainWorkMs;
        long _remainRestMs;
        int _remainReps;
        long _stampMs;
        bool _paused;

        public PomodoroEngine(bool fast)
            : this(fast, null)
        {
        }

        public PomodoroEngine(bool fast, Func<long> clock)
        {
            _fast = fast;
            _clock = clock ?? new Func<long>(DefaultClock);
            Reset();
        }

        public static long DefaultClock()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public bool IsFast { get { return _fast; } }

        /// <summary>Length of one timer "minute" in milliseconds.</summary>
        public int UnitMs { get { return _fast ? 1000 : 60000; } }

        public int WorkMinutes { get { return _workMin; } }
        public int RestMinutes { get { return _restMin; } }
        public int RepsConfigured { get { return _repsCfg; } }

        public Phase Phase { get; private set; }

        public bool IsPaused { get { return _paused && Phase != Phase.Idle; } }

        public bool IsRunning { get { return Phase != Phase.Idle && !_paused; } }

        public int RemainingReps
        {
            get { return _remainReps; }
        }

        public long RemainingWorkMs
        {
            get { SyncRemaining(); return _remainWorkMs; }
        }

        public long RemainingRestMs
        {
            get { SyncRemaining(); return _remainRestMs; }
        }

        /// <summary>
        /// Minutes shown on the work indicator: rounded up while work is running;
        /// otherwise the configured value (reset after a session ends).
        /// </summary>
        public int DisplayWorkMinutes
        {
            get
            {
                SyncRemaining();
                if (Phase == Phase.Work)
                    return MsToDisplayMinutes(_remainWorkMs);
                return _workMin;
            }
        }

        public int DisplayRestMinutes
        {
            get
            {
                SyncRemaining();
                if (Phase == Phase.Rest)
                    return MsToDisplayMinutes(_remainRestMs);
                return _restMin;
            }
        }

        public int DisplayReps
        {
            get { return _remainReps; }
        }

        /// <summary>Seconds left in the current phase, rounded up (for an mm:ss title).</summary>
        public int RemainingSecondsCeil
        {
            get
            {
                SyncRemaining();
                long ms = CurrentPhaseMs();
                if (ms <= 0) return 0;
                return (int)((ms + 999) / 1000);
            }
        }

        /// <summary>Progress of the current phase: 0 at the start, 1 at the end. 0 when idle.</summary>
        public double PhaseProgress
        {
            get
            {
                SyncRemaining();
                long total = CurrentPhaseTotalMs();
                if (Phase == Phase.Idle || total <= 0) return 0;
                long rem = CurrentPhaseMs();
                double p = 1.0 - (double)rem / (double)total;
                if (p < 0) return 0;
                if (p > 1) return 1;
                return p;
            }
        }

        public long CurrentPhaseTotalMs()
        {
            if (Phase == Phase.Rest) return (long)_restMin * UnitMs;
            if (Phase == Phase.Work) return (long)_workMin * UnitMs;
            return (long)_workMin * UnitMs;
        }

        long CurrentPhaseMs()
        {
            if (Phase == Phase.Rest) return _remainRestMs;
            if (Phase == Phase.Work) return _remainWorkMs;
            return _remainWorkMs;
        }

        int MsToDisplayMinutes(long ms)
        {
            if (ms <= 0) return 0;
            int unit = UnitMs;
            return (int)((ms + unit - 1) / unit);
        }

        long Now()
        {
            return _clock();
        }

        void SyncRemaining()
        {
            if (Phase == Phase.Idle || _paused) return;
            long now = Now();
            long elapsed = now - _stampMs;
            if (elapsed <= 0) return;
            _stampMs = now;
            if (Phase == Phase.Work)
                _remainWorkMs = Math.Max(0L, _remainWorkMs - elapsed);
            else if (Phase == Phase.Rest)
                _remainRestMs = Math.Max(0L, _remainRestMs - elapsed);
        }

        /// <summary>
        /// Advance time and handle a session boundary (at most one transition per call).
        /// </summary>
        public BoundaryKind? Update()
        {
            if (Phase == Phase.Idle || _paused) return null;
            SyncRemaining();
            if (Phase == Phase.Work && _remainWorkMs <= 0)
            {
                _remainWorkMs = (long)_workMin * UnitMs;
                _remainRestMs = (long)_restMin * UnitMs;
                Phase = Phase.Rest;
                _paused = false;
                _stampMs = Now();
                return BoundaryKind.RestStarted;
            }
            if (Phase == Phase.Rest && _remainRestMs <= 0)
            {
                _remainReps = _remainReps - 1;
                _remainWorkMs = (long)_workMin * UnitMs;
                _remainRestMs = (long)_restMin * UnitMs;
                if (_remainReps <= 0)
                {
                    Reset();
                    return BoundaryKind.AllDone;
                }
                Phase = Phase.Work;
                _paused = false;
                _stampMs = Now();
                return BoundaryKind.WorkStarted;
            }
            return null;
        }

        /// <summary>
        /// Start / pause / resume. Starting from idle returns WorkStarted.
        /// </summary>
        public BoundaryKind? ToggleStartPause()
        {
            if (Phase != Phase.Idle && !_paused)
            {
                SyncRemaining();
                _paused = true;
                return null;
            }
            if (Phase == Phase.Idle)
            {
                Phase = Phase.Work;
                _remainWorkMs = (long)_workMin * UnitMs;
                _remainRestMs = (long)_restMin * UnitMs;
                _remainReps = _repsCfg;
                _paused = false;
                _stampMs = Now();
                return BoundaryKind.WorkStarted;
            }
            _paused = false;
            _stampMs = Now();
            return null;
        }

        public void Reset()
        {
            Phase = Phase.Idle;
            _paused = false;
            _remainWorkMs = (long)_workMin * UnitMs;
            _remainRestMs = (long)_restMin * UnitMs;
            _remainReps = _repsCfg;
            _stampMs = Now();
        }

        public void SetWorkMinutes(int minutes)
        {
            if (minutes < 1) minutes = 1;
            if (minutes > 99) minutes = 99;
            SyncRemaining();
            _workMin = minutes;
            _remainWorkMs = (long)minutes * UnitMs;
            if (Phase == Phase.Work && !_paused)
                _stampMs = Now();
        }

        public void SetRestMinutes(int minutes)
        {
            if (minutes < 1) minutes = 1;
            if (minutes > 99) minutes = 99;
            SyncRemaining();
            _restMin = minutes;
            _remainRestMs = (long)minutes * UnitMs;
            if (Phase == Phase.Rest && !_paused)
                _stampMs = Now();
        }

        public void SetReps(int reps)
        {
            if (reps < 1) reps = 1;
            if (reps > 99) reps = 99;
            _repsCfg = reps;
            _remainReps = reps;
        }
    }

    /// <summary>What to do with an embedded strip after a periodic health check.</summary>
    public enum EmbedHealthAction
    {
        None,
        /// <summary>Strip is fine but not the top tray child: SetWindowPos(HWND_TOP) and move on.</summary>
        Raise,
        /// <summary>Parent is really gone or changed: recover (recreate on the new taskbar).</summary>
        Recover,
    }

    /// <summary>What to do when every attempt to embed a fresh strip failed.</summary>
    public enum AttachFailAction
    {
        /// <summary>Try once more with a window created directly as a child of the tray.</summary>
        TryDirectChild,
        /// <summary>The previous embedded strip is still healthy: keep it, drop the new one.</summary>
        KeepExistingEmbed,
        /// <summary>A temporary overlay is already showing: keep it and retry later.</summary>
        KeepExistingOverlay,
        /// <summary>Nothing usable is on screen: show a temporary overlay.</summary>
        FallbackOverlay,
    }

    /// <summary>Pure decisions for the taskbar strip, kept testable without WinForms.</summary>
    public static class StripPolicy
    {
        public const int SetParentRetryIntervalMs = 150;
        public const int SetParentRetryBudgetMs = 3000;
        public const int RaiseLogIntervalMs = 30000;

        /// <summary>
        /// Z-order alone never justifies recreating a working embedded strip: opening
        /// Start/Search reorders Shell_TrayWnd children (XAML island etc.). Only a lost
        /// parent does.
        /// </summary>
        public static EmbedHealthAction DecideEmbedHealth(bool trayFound, bool parentAlive,
            bool parentIsTray, bool isTopChild)
        {
            if (!trayFound || !parentAlive || !parentIsTray) return EmbedHealthAction.Recover;
            if (!isTopChild) return EmbedHealthAction.Raise;
            return EmbedHealthAction.None;
        }

        /// <summary>Keep retrying SetParent on the same fresh hidden HWND within the budget.</summary>
        public static bool ShouldRetrySetParent(int elapsedMs)
        {
            return elapsedMs >= 0 && elapsedMs < SetParentRetryBudgetMs;
        }

        /// <summary>Make-before-break: never replace a working embed with an overlay.</summary>
        public static AttachFailAction DecideAttachFailure(bool triedDirectChild,
            bool existingEmbedHealthy, bool existingOverlayShowing)
        {
            if (existingEmbedHealthy) return AttachFailAction.KeepExistingEmbed;
            if (!triedDirectChild) return AttachFailAction.TryDirectChild;
            if (existingOverlayShowing) return AttachFailAction.KeepExistingOverlay;
            return AttachFailAction.FallbackOverlay;
        }

        /// <summary>
        /// A recreate is allowed to destroy the old strip up front only when it is not a
        /// working embed (or the user explicitly asked for overlay).
        /// </summary>
        public static bool ShouldSwapAfterNewEmbeds(bool oldIsHealthyEmbed, bool targetOverlay)
        {
            return oldIsHealthyEmbed && !targetOverlay;
        }

        /// <summary>Start, Search and their XAML hosts. While one is foreground, do not retry embedding.</summary>
        public static bool IsStartOrSearchForeground(string cls, string proc)
        {
            if (!string.IsNullOrEmpty(cls))
            {
                if (cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Core.CoreWindow"
                    || cls == "DV2ControlHost" || cls == "StartMenu"
                    || cls.IndexOf("StartMenu", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            if (!string.IsNullOrEmpty(proc))
            {
                string p = proc.ToLowerInvariant();
                if (p == "startmenuexperiencehost" || p == "searchhost" || p == "searchapp"
                    || p == "shellexperiencehost" || p == "shellhost")
                    return true;
            }
            return false;
        }

        public static bool ShouldStartOverlayRetry(bool taskbarMode, bool recovering,
            bool temporaryOverlay, bool wantsEmbed, bool due, bool startOrSearchOpen)
        {
            if (!taskbarMode || recovering || !temporaryOverlay || !wantsEmbed || !due) return false;
            return !startOrSearchOpen;
        }

        /// <summary>Rate limit for a repeated log line.</summary>
        public static bool IsLogDue(int nowTick, int lastTick, int intervalMs)
        {
            if (lastTick == 0) return true;
            return unchecked((uint)(nowTick - lastTick)) >= (uint)intervalMs;
        }
    }

#if WINFORMS
    static class Program
    {
        public const string MutexName = @"Local\Chernikov.Pomodoro";
        public const string ShowEventName = @"Local\Chernikov.Pomodoro.Show";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        static Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            bool fast = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a != null)
                    {
                        a = a.Trim();
                        if (string.Equals(a, "/fast", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(a, "-fast", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(a, "--fast", StringComparison.OrdinalIgnoreCase))
                        {
                            fast = true;
                        }
                    }
                }
            }

            // Per-monitor v2. Do not call SetProcessDPIAware — it would downgrade awareness.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }
            // Mixed DPI hosting lets a PMv2 child live inside explorer's taskbar window.
            Native.EnterMixedDpiHosting();

            try { SetCurrentProcessExplicitAppUserModelID("Chernikov.Pomodoro"); }
            catch { }

            AppLog.Info("start v1.5.0 fast=" + fast + " os=" + Environment.OSVersion +
                        " build=" + OsInfo.Build + " win11=" + OsInfo.IsWindows11);

            bool owned = false;
            try
            {
                _mutex = new Mutex(false, MutexName);
                try
                {
                    owned = _mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                    AppLog.Info("took abandoned mutex from a previous process");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("mutex create", ex);
                owned = true;
            }

            if (!owned)
            {
                AppLog.Info("already running; signaling the existing instance to show itself");
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                        ev.Set();
                }
                catch (Exception ex)
                {
                    AppLog.Error("failed to signal existing instance", ex);
                }
                return;
            }

            EventWaitHandle showEvent = null;
            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch (Exception ex)
            {
                AppLog.Error("show event create", ex);
                try { showEvent = new EventWaitHandle(false, EventResetMode.AutoReset); }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    AppLog.Error("UI thread exception (continuing)", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Exception ux = e.ExceptionObject as Exception;
                    if (ux != null) AppLog.Error("unhandled exception terminating=" + e.IsTerminating, ux);
                    else AppLog.Error("unhandled non-exception object terminating=" + e.IsTerminating);
                };
            }
            catch { }
            try
            {
                Application.Run(new MainForm(fast, showEvent));
            }
            finally
            {
                try { if (showEvent != null) showEvent.Dispose(); } catch { }
                try { if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); } } catch { }
                AppLog.Info("exit");
            }
        }
    }

    static class AppLog
    {
        static readonly object Sync = new object();
        static string _path;

        public static string FilePath
        {
            get
            {
                if (_path != null) return _path;
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Pomodoro");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                _path = Path.Combine(dir, "log.txt");
                return _path;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex != null ? ": " + ex.GetType().Name + " " + ex.Message : ""));
        }

        static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = FilePath;
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 1024 * 1024)
                        {
                            string dir = Path.GetDirectoryName(path);
                            string old = Path.Combine(dir, "log.old.txt");
                            try { if (File.Exists(old)) File.Delete(old); }
                            catch { }
                            try { File.Move(path, old); }
                            catch
                            {
                                try { File.Copy(path, old, true); File.Delete(path); }
                                catch { }
                            }
                        }
                    }
                    catch
                    {
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }

    #region Native interop

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }

        public Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZEL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    enum TaskbarEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    enum EmbedKind
    {
        None = 0,
        Overlay = 1,
        ClassicReBar = 2,
        ShellTray = 3
    }

    /// <summary>Where the taskbar strip sits: the empty area left of Start, or the gap left of the tray.</summary>
    enum StripZone
    {
        Right = 0,
        Left = 1
    }

    /// <summary>How the strip is shown on the taskbar: a child of the tray, or a topmost overlay.</summary>
    enum StripDisplayMode
    {
        Embed = 0,
        Overlay = 1
    }

    static class Native
    {
        public const int GwlStyle = -16;
        public const int GwlExStyle = -20;
        public const int WsChild = 0x40000000;
        public const int WsPopup = unchecked((int)0x80000000);
        public const int WsExToolwindow = 0x00000080;
        public const int WsExAppwindow = 0x00040000;
        public const int WsExTopmost = 0x00000008;
        public const int WsExNoActivate = 0x08000000;
        public const int WsExLayered = 0x00080000;
        public const int WsExNoRedirectionBitmap = 0x00200000;
        public const int WsVisible = 0x10000000;
        public const int WsClipSiblings = 0x04000000;
        public const int WsCaption = 0x00C00000;
        public const int WsSysMenu = 0x00080000;
        public const int WsThickframe = 0x00040000;
        public const int WsBorder = 0x00800000;
        public const int DpiHostingBehaviorDefault = 0;
        public const int DpiHostingBehaviorMixed = 1;
        public const uint WmNull = 0;
        public const uint PwRenderFullContent = 2;
        public const int AsfwAny = -1;

        public const uint SwpNosize = 0x0001;
        public const uint SwpNomove = 0x0002;
        public const uint SwpNozorder = 0x0004;
        public const uint SwpNoactivate = 0x0010;
        public const uint SwpShowwindow = 0x0040;
        public const uint SwpAsyncwindowpos = 0x4000;
        public const uint EventObjectShow = 0x8002;

        // Read-only z-band query (diagnostics: tells whether Explorer moved the taskbar
        // into a band a normal topmost window cannot climb over).
        [DllImport("user32.dll")]
        public static extern bool GetWindowBand(IntPtr hWnd, out uint band);

        public static uint BandOf(IntPtr hwnd)
        {
            uint b = 0;
            try { if (!GetWindowBand(hwnd, out b)) b = 0; }
            catch { b = 0; }
            return b;
        }
        public const uint SwpFramechanged = 0x0020;

        public static readonly IntPtr HwndTopmost = new IntPtr(-1);
        public static readonly IntPtr HwndNoTopmost = new IntPtr(-2);
        public static readonly IntPtr HwndTop = new IntPtr(0);
        public const uint UlwAlpha = 0x00000002;
        public const uint LwaAlpha = 0x00000002;
        public const byte AcSrcOver = 0x00;
        public const byte AcSrcAlpha = 0x01;
        public const uint Srccopy = 0x00CC0020;
        public const uint DibRgbColors = 0;
        public const int BiRgb = 0;

        public const uint AbeLeft = 0;
        public const uint AbeTop = 1;
        public const uint AbeRight = 2;
        public const uint AbeBottom = 3;
        public const uint AbmGetTaskbarPos = 5;

        public const int SwShownoactivate = 4;
        public const int SwHide = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        public static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);


        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public const uint SwpNoownerzorder = 0x0200;

        /// <summary>Owner (GWLP_HWNDPARENT) only — never WS_CHILD / SetParent.</summary>
        public static void SetOwner(IntPtr hwnd, IntPtr owner)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlHwndParent, owner);
            else SetWindowLong(hwnd, GwlHwndParent, owner.ToInt32());
        }

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);


        [DllImport("ntdll.dll")]
        public static extern int RtlGetVersion(ref OSVERSIONINFOEX lpVersionInformation);

        [DllImport("kernel32.dll")]
        public static extern void SetLastError(uint dwErrCode);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        public const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public const uint EventSystemForeground = 0x0003;
        public const uint EventSystemMenusStart = 0x0004;
        public const uint EventSystemMenuEnd = 0x0005;
        public const uint EventObjectHide = 0x8003;
        public const uint EventObjectReorder = 0x8004;
        public const uint ObjidWindow = 0;
        public const uint WinEventOutOfContext = 0;
        public const uint WinEventSkipOwnProcess = 0x0002;
        public const uint GwHwndPrev = 3;
        public const uint GwChild = 5;
        public const uint GwOwner = 4;
        public const int GwlHwndParent = -8;
        public const uint ProcessQueryLimitedInformation = 0x1000;

        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);


        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern int SetThreadDpiHostingBehavior(int value);

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZEL psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        // pptDst / hdcDst may be NULL for a layered child (position already set).
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "UpdateLayeredWindow")]
        public static extern bool UpdateLayeredWindowOpt(IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst,
            ref SIZEL psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
            out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        public static string GetWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            try { GetClassName(hwnd, sb, sb.Capacity); }
            catch { return ""; }
            return sb.ToString();
        }

        public struct ForegroundState
        {
            public IntPtr Hwnd;
            public string ClassName;
            public string ProcessName;
            public Rectangle Rect;
            public bool CoversMonitor;
            public bool IsShellChrome;
            public bool IsFullscreenApp;
        }

        public static ForegroundState ReadForeground()
        {
            var s = new ForegroundState();
            s.ClassName = "";
            s.ProcessName = "";
            try
            {
                s.Hwnd = GetForegroundWindow();
                if (s.Hwnd == IntPtr.Zero) return s;
                s.ClassName = GetWindowClass(s.Hwnd);
                s.ProcessName = GetProcessName(s.Hwnd);
                s.Rect = GetRect(s.Hwnd);
                s.IsShellChrome = IsShellChromeWindow(s.Hwnd, s.ClassName, s.ProcessName);
                if (s.Rect.IsEmpty) return s;

                IntPtr mon = MonitorFromWindow(s.Hwnd, MonitorDefaultToNearest);
                if (mon == IntPtr.Zero) return s;
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return s;
                Rectangle monR = mi.rcMonitor.ToRectangle();
                s.CoversMonitor = s.Rect.Left <= monR.Left + 2 && s.Rect.Top <= monR.Top + 2
                    && s.Rect.Right >= monR.Right - 2 && s.Rect.Bottom >= monR.Bottom - 2;
                // Start / Search / Widgets / notification center often cover the monitor
                // but must not hide the overlay.
                s.IsFullscreenApp = s.CoversMonitor && !s.IsShellChrome;
            }
            catch
            {
            }
            return s;
        }

        public static bool IsForegroundFullscreen()
        {
            return ReadForeground().IsFullscreenApp;
        }

        public static string GetProcessName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                IntPtr proc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (proc != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(520);
                        uint size = (uint)sb.Capacity;
                        if (QueryFullProcessImageName(proc, 0, sb, ref size))
                        {
                            string path = sb.ToString();
                            if (!string.IsNullOrEmpty(path))
                                return Path.GetFileNameWithoutExtension(path) ?? "";
                        }
                    }
                    finally
                    {
                        CloseHandle(proc);
                    }
                }
                try
                {
                    using (System.Diagnostics.Process dp = System.Diagnostics.Process.GetProcessById((int)pid))
                    {
                        if (dp != null && !string.IsNullOrEmpty(dp.ProcessName))
                            return dp.ProcessName;
                    }
                }
                catch
                {
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        public static bool IsShellChromeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            return IsShellChromeWindow(hwnd, GetWindowClass(hwnd), GetProcessName(hwnd));
        }

        public static bool IsShellChromeWindow(IntPtr hwnd, string cls, string proc)
        {
            if (IsShellChromeClass(cls)) return true;
            if (IsShellChromeProcess(proc)) return true;
            // Process name can fail; these classes are used by Start/Search hosts.
            if (string.IsNullOrEmpty(proc) && IsLikelyShellHostClass(cls)) return true;
            return false;
        }

        static bool IsLikelyShellHostClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (cls == "Windows.UI.Core.CoreWindow") return true;
            if (cls == "XamlExplorerHostIslandWindow") return true;
            if (cls.IndexOf("StartMenu", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool IsShellChromeClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
            if (cls == "Progman" || cls == "WorkerW" || cls == "ForegroundStaging") return true;
            if (cls == "NotifyIconOverflowWindow") return true;
            if (cls == "DV2ControlHost" || cls == "StartMenu") return true;
            return false;
        }

        public static bool IsShellChromeProcess(string proc)
        {
            if (string.IsNullOrEmpty(proc)) return false;
            string p = proc.ToLowerInvariant();
            return p == "explorer"
                || p == "startmenuexperiencehost"
                || p == "searchhost"
                || p == "searchapp"
                || p == "shellexperiencehost"
                || p == "shellhost"
                || p == "widgets"
                || p == "widgetboard"
                || p == "windows.widgets"
                || p == "textinputhost";
        }

        /// <summary>True if <paramref name="upper"/> is above <paramref name="lower"/> in the z-order.</summary>
        public static bool IsWindowAbove(IntPtr upper, IntPtr lower)
        {
            if (upper == IntPtr.Zero || lower == IntPtr.Zero || upper == lower) return false;
            try
            {
                int guard = 0;
                for (IntPtr w = GetWindow(lower, GwHwndPrev);
                     w != IntPtr.Zero && guard < 4096;
                     w = GetWindow(w, GwHwndPrev), guard++)
                {
                    if (w == upper) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool IsTopChild(IntPtr parent, IntPtr child)
        {
            if (parent == IntPtr.Zero || child == IntPtr.Zero) return false;
            try { return GetWindow(parent, GwChild) == child; }
            catch { return false; }
        }

        public static bool IsTaskbarForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                string cls = GetWindowClass(fg);
                return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
            }
            catch
            {
                return false;
            }
        }

        public static Rectangle GetRect(IntPtr hwnd)
        {
            RECT rc;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rc))
                return Rectangle.Empty;
            return rc.ToRectangle();
        }

        public static Point ScreenToClientPoint(IntPtr hwnd, Point screen)
        {
            POINT p;
            p.X = screen.X;
            p.Y = screen.Y;
            ScreenToClient(hwnd, ref p);
            return new Point(p.X, p.Y);
        }

        public static void RestorePopupStyle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            int style = GetWindowLong(hwnd, GwlStyle);
            style = (style | WsPopup) & ~WsChild;
            SetWindowLong(hwnd, GwlStyle, style);
            int ex = GetWindowLong(hwnd, GwlExStyle);
            ex |= WsExToolwindow | WsExTopmost | WsExNoActivate;
            ex &= ~WsExAppwindow;
            ex &= ~WsExLayered;
            SetWindowLong(hwnd, GwlExStyle, ex);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate | SwpFramechanged | SwpShowwindow);
        }

        public static void ApplyChildStyle(IntPtr hwnd)
        {
            ApplyChildStyle(hwnd, true);
        }

        public static void ApplyChildStyle(IntPtr hwnd, bool layered)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            int style = GetWindowLong(hwnd, GwlStyle);
            style |= WsChild | WsClipSiblings;
            style &= ~WsPopup;
            style &= ~WsCaption;
            style &= ~WsThickframe;
            style &= ~WsSysMenu;
            SetWindowLong(hwnd, GwlStyle, style);
            int ex = GetWindowLong(hwnd, GwlExStyle);
            // WS_EX_LAYERED child is what makes a GDI window actually composite
            // onto a WS_EX_NOREDIRECTIONBITMAP (DirectComposition / XAML) tray.
            ex |= WsExToolwindow | WsExNoActivate;
            if (layered) ex |= WsExLayered;
            else ex &= ~WsExLayered;
            ex &= ~WsExTopmost;
            ex &= ~WsExAppwindow;
            SetWindowLong(hwnd, GwlExStyle, ex);
            // SetWindowLong alone does not drop WS_EX_TOPMOST — need HWND_NOTOPMOST.
            SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate | SwpFramechanged);
        }

        static bool _mixedDpiHosting;
        static int _priorDpiHosting;

        public static void EnterMixedDpiHosting()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch { }
            if (_mixedDpiHosting) return;
            try
            {
                _priorDpiHosting = SetThreadDpiHostingBehavior(DpiHostingBehaviorMixed);
                _mixedDpiHosting = true;
                AppLog.Info("dpi hosting=mixed (was " + _priorDpiHosting.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex)
            {
                AppLog.Error("SetThreadDpiHostingBehavior", ex);
            }
        }

        public static void LeaveMixedDpiHosting()
        {
            if (!_mixedDpiHosting) return;
            try { SetThreadDpiHostingBehavior(_priorDpiHosting); }
            catch { }
            _mixedDpiHosting = false;
        }

        public static void FocusForPopup(IntPtr hwnd)
        {
            // v1.4.9: no AttachThreadInput to the foreground thread (often Explorer).
            // The strip is our own top-level window and the click gave us foreground rights.
            if (hwnd == IntPtr.Zero) return;
            try { SetForegroundWindow(hwnd); }
            catch { }
        }

        public static string ProbePaint(IntPtr hwnd)
        {
            try
            {
                Rectangle r = GetRect(hwnd);
                if (hwnd == IntPtr.Zero || r.IsEmpty || r.Width < 2 || r.Height < 2)
                    return "skip empty";
                int w = Math.Min(r.Width, 200);
                int h = Math.Min(r.Height, 80);
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try
                        {
                            ok = PrintWindow(hwnd, hdc, PwRenderFullContent);
                            if (!ok) ok = PrintWindow(hwnd, hdc, 0);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                        if (!ok) return "PrintWindow=false";
                    }
                    int hits = 0, samples = 0;
                    for (int y = 0; y < h; y += 3)
                    {
                        for (int x = 0; x < w; x += 3)
                        {
                            samples++;
                            Color c = bmp.GetPixel(x, y);
                            if (c.A > 0 && (c.R + c.G + c.B) > 0) hits++;
                        }
                    }
                    return "PrintWindow=true painted=" + hits.ToString(CultureInfo.InvariantCulture)
                        + "/" + samples.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                return "PrintWindow error " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static bool UpdateLayeredFromBitmap(IntPtr hwnd, Bitmap bmp, int destX, int destY)
        {
            if (hwnd == IntPtr.Zero || bmp == null) return false;
            int w = bmp.Width;
            int h = bmp.Height;
            if (w < 1 || h < 1) return false;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                var bi = new BITMAPINFO();
                bi.biSize = Marshal.SizeOf(typeof(BITMAPINFO));
                bi.biWidth = w;
                bi.biHeight = -h; // top-down
                bi.biPlanes = 1;
                bi.biBitCount = 32;
                bi.biCompression = BiRgb;

                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                IntPtr bits;
                dib = CreateDIBSection(memDc, ref bi, DibRgbColors, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                old = SelectObject(memDc, dib);

                // Lock as straight BGRA, then premultiply into the DIB.
                // UpdateLayeredWindow + AC_SRC_ALPHA requires premultiplied alpha;
                // non-premultiplied edges bloom and look thick.
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    CopyStraightToPremultiplied(data.Scan0, data.Stride, bits, w * 4, w, h);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                var dst = new POINT();
                dst.X = destX;
                dst.Y = destY;
                var src = new POINT();
                src.X = 0;
                src.Y = 0;
                var size = new SIZEL();
                size.cx = w;
                size.cy = h;
                var blend = new BLENDFUNCTION();
                blend.BlendOp = AcSrcOver;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;

                // Child layered windows (Win8+): hdcDst must be NULL; pptDst is
                // parent-client or NULL (keep current position). Screen coords +
                // a screen DC produced ERROR_INVALID_PARAMETER (87) on Win11 26H2.
                SetLastError(0);
                if (UpdateLayeredWindowOpt(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, UlwAlpha))
                    return true;
                SetLastError(0);
                if (UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, UlwAlpha))
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (old != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static void CopyStraightToPremultiplied(IntPtr src, int srcStride, IntPtr dst, int dstStride, int w, int h)
        {
            if (src == IntPtr.Zero || dst == IntPtr.Zero || w < 1 || h < 1) return;
            int srcLen = srcStride * h;
            byte[] buf = new byte[srcLen];
            Marshal.Copy(src, buf, 0, srcLen);
            int rowBytes = w * 4;
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                int so = y * srcStride;
                for (int x = 0; x < w; x++)
                {
                    int i = so + x * 4;
                    int d = x * 4;
                    byte b = buf[i];
                    byte g = buf[i + 1];
                    byte r = buf[i + 2];
                    byte a = buf[i + 3];
                    if (a == 0)
                    {
                        row[d] = 0;
                        row[d + 1] = 0;
                        row[d + 2] = 0;
                        row[d + 3] = 0;
                    }
                    else if (a == 255)
                    {
                        row[d] = b;
                        row[d + 1] = g;
                        row[d + 2] = r;
                        row[d + 3] = 255;
                    }
                    else
                    {
                        row[d] = (byte)((b * a + 127) / 255);
                        row[d + 1] = (byte)((g * a + 127) / 255);
                        row[d + 2] = (byte)((r * a + 127) / 255);
                        row[d + 3] = a;
                    }
                }
                Marshal.Copy(row, 0, new IntPtr(dst.ToInt64() + (long)y * dstStride), rowBytes);
            }
        }

        public static bool SetLayeredOpaque(IntPtr hwnd)
        {
            try
            {
                return SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>BitBlt the screen rect and look for the strip's digit colors.</summary>
        public static bool ScreenCheckVisible(Rectangle rect, out int hits, out int samples)
        {
            hits = 0;
            samples = 0;
            if (rect.Width < 2 || rect.Height < 2) return false;
            int w = Math.Min(rect.Width, 400);
            int h = Math.Min(rect.Height, 120);
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                var bi = new BITMAPINFO();
                bi.biSize = Marshal.SizeOf(typeof(BITMAPINFO));
                bi.biWidth = w;
                bi.biHeight = -h;
                bi.biPlanes = 1;
                bi.biBitCount = 32;
                bi.biCompression = BiRgb;
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                memDc = CreateCompatibleDC(screenDc);
                IntPtr bits;
                dib = CreateDIBSection(memDc, ref bi, DibRgbColors, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                old = SelectObject(memDc, dib);
                if (!BitBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, Srccopy))
                    return false;

                int stride = w * 4;
                for (int y = 0; y < h; y += 2)
                {
                    for (int x = 0; x < w; x += 2)
                    {
                        samples++;
                        int i = y * stride + x * 4;
                        byte b = Marshal.ReadByte(bits, i);
                        byte g = Marshal.ReadByte(bits, i + 1);
                        byte r = Marshal.ReadByte(bits, i + 2);
                        if (LooksLikeDigitPixel(r, g, b)) hits++;
                    }
                }
                return hits >= 8;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (old != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static bool LooksLikeDigitPixel(byte r, byte g, byte b)
        {
            // WorkLit 255,70,48 — tomato red
            if (r >= 160 && r > g + 40 && r > b + 40 && g < 160) return true;
            // RestLit 46,214,122 — green
            if (g >= 150 && g > r + 30 && g > b) return true;
            // RepsLit 228,210,128 — yellow
            if (r >= 160 && g >= 140 && b < 200 && (r + g) > (b * 2 + 40)) return true;
            return false;
        }
    }

    static class OsInfo
    {
        static int _build = -1;

        public static int Build
        {
            get
            {
                if (_build >= 0) return _build;
                try
                {
                    var info = new OSVERSIONINFOEX();
                    info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
                    if (Native.RtlGetVersion(ref info) == 0)
                    {
                        _build = info.dwBuildNumber;
                        return _build;
                    }
                }
                catch
                {
                }
                _build = Environment.OSVersion.Version.Build;
                return _build;
            }
        }

        public static bool IsWindows11 { get { return Build >= 22000; } }

        public static bool IsTaskbarLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object v = key.GetValue("SystemUsesLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    sealed class TaskbarSnapshot
    {
        public IntPtr Tray;
        public IntPtr ReBar;
        public IntPtr TaskList;
        public IntPtr Notify;
        public IntPtr Start;
        public Rectangle TrayRect;
        public Rectangle ReBarRect;
        public Rectangle TaskListRect;
        public Rectangle NotifyRect;
        public Rectangle StartRect;
        public Rectangle AppsRect;
        public string AppsSource;
        public Rectangle WidgetsRect;
        public string WidgetsSource;
        public TaskbarEdge Edge = TaskbarEdge.Bottom;
        public bool IsWin11Style;
        public bool HasXamlIsland;
        public bool IsHorizontal
        {
            get { return Edge == TaskbarEdge.Top || Edge == TaskbarEdge.Bottom; }
        }

        public bool IsValid
        {
            get { return Tray != IntPtr.Zero && !TrayRect.IsEmpty && TrayRect.Width > 0 && TrayRect.Height > 0; }
        }
    }

    static class TaskbarFinder
    {
        public static TaskbarSnapshot Capture()
        {
            var s = new TaskbarSnapshot();
            s.Tray = Native.FindWindow("Shell_TrayWnd", null);
            if (s.Tray == IntPtr.Zero) return s;

            // v1.4.9: no SHAppBarMessage (it blocks on Explorer via a sent message). GetWindowRect only.
            Rectangle abmRect = Rectangle.Empty;

            Rectangle raw = Native.GetRect(s.Tray);
            s.TrayRect = !abmRect.IsEmpty ? abmRect : raw;
            if (s.TrayRect.IsEmpty) s.TrayRect = raw;
            if (abmRect.IsEmpty)
                s.Edge = ReadEdge(s.Tray, s.TrayRect);

            s.ReBar = Native.FindWindowEx(s.Tray, IntPtr.Zero, "ReBarWindow32", null);
            if (s.ReBar == IntPtr.Zero)
                s.ReBar = Native.FindWindowEx(s.Tray, IntPtr.Zero, "WorkerW", null);

            if (s.ReBar != IntPtr.Zero)
            {
                s.ReBarRect = Native.GetRect(s.ReBar);
                s.TaskList = Native.FindWindowEx(s.ReBar, IntPtr.Zero, "MSTaskSwWClass", null);
                if (s.TaskList == IntPtr.Zero)
                    s.TaskList = Native.FindWindowEx(s.ReBar, IntPtr.Zero, "MSTaskListWClass", null);
                if (s.TaskList != IntPtr.Zero)
                    s.TaskListRect = Native.GetRect(s.TaskList);
            }

            s.Notify = Native.FindWindowEx(s.Tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (s.Notify != IntPtr.Zero)
                s.NotifyRect = Native.GetRect(s.Notify);

            s.Start = Native.FindWindowEx(s.Tray, IntPtr.Zero, "Start", null);
            if (s.Start != IntPtr.Zero)
                s.StartRect = Native.GetRect(s.Start);

            bool hasBridge =
                Native.FindWindowEx(s.Tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero ||
                Native.FindWindowEx(s.Tray, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null) != IntPtr.Zero;
            s.HasXamlIsland = hasBridge;
            s.IsWin11Style = hasBridge || OsInfo.IsWindows11;

            AppsAreaFinder.Fill(s);
            return s;
        }

        public static bool IsTrayReady(out TaskbarSnapshot snap)
        {
            snap = Capture();
            if (snap == null || !snap.IsValid) return false;
            if (snap.TrayRect.Width < 16 || snap.TrayRect.Height < 8) return false;
            if (OsInfo.IsWindows11 && !snap.HasXamlIsland) return false;
            return true;
        }

        static TaskbarEdge ReadEdge(IntPtr tray, Rectangle trayRect)
        {
            if (trayRect.IsEmpty) return TaskbarEdge.Bottom;
            if (trayRect.Width >= trayRect.Height)
                return trayRect.Top <= 80 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
            return trayRect.Left <= 80 ? TaskbarEdge.Left : TaskbarEdge.Right;
        }
    }

    static class AppsAreaFinder
    {
        const int CacheMs = 1000;
        const int LogEveryMs = 10000;

        static IntPtr _cachedTray;
        static Rectangle _cachedRect;
        static string _cachedHow = "";
        static int _cachedAt;
        static string _lastLog = "";
        static int _lastLogAt;

        public static void Fill(TaskbarSnapshot s)
        {
            Rectangle r;
            string how;
            Resolve(s, out r, out how);
            s.AppsRect = r;
            s.AppsSource = how;
            FillWidgets(s);

            int now = Environment.TickCount;
            string line = "apps src=" + how + " rect=" + Fmt(r)
                + " tray=" + Fmt(s.TrayRect)
                + " notify=" + Fmt(s.NotifyRect)
                + " start=" + Fmt(s.StartRect)
                + " widgets=" + Fmt(s.WidgetsRect)
                + (string.IsNullOrEmpty(s.WidgetsSource) ? "" : " widgetsSrc=" + s.WidgetsSource);
            bool changed = line != _lastLog;
            bool due = (uint)(now - _lastLogAt) >= (uint)LogEveryMs;
            if (changed && due)
            {
                _lastLog = line;
                _lastLogAt = now;
                AppLog.Info(line);
            }
        }

        static void Resolve(TaskbarSnapshot s, out Rectangle rect, out string how)
        {
            int now = Environment.TickCount;
            if (s.Tray == _cachedTray && !_cachedRect.IsEmpty && (uint)(now - _cachedAt) < (uint)CacheMs)
            {
                rect = _cachedRect;
                how = _cachedHow;
                return;
            }

            if (IsTightAppsRect(s.TaskListRect, s))
            {
                Remember(s.Tray, s.TaskListRect, "MSTaskList/hwnd", now, out rect, out how);
                return;
            }

            Rectangle byClass = FindByClassName(s);
            if (IsTightAppsRect(byClass, s))
            {
                Remember(s.Tray, byClass, "MSTaskListWClass", now, out rect, out how);
                return;
            }

            Rectangle byUia;
            string uiaHow;
            if (TryFindByUia(s, out byUia, out uiaHow) && IsTightAppsRect(byUia, s))
            {
                Remember(s.Tray, byUia, uiaHow, now, out rect, out how);
                return;
            }

            // Safe zone: never left of (taskbar center + estimated half-width of the apps cluster).
            int half = EstimatedHalfApps(s);
            Rectangle fb;
            if (s.IsHorizontal)
            {
                int cx = s.TrayRect.Left + s.TrayRect.Width / 2;
                fb = new Rectangle(cx - half, s.TrayRect.Top, half * 2, s.TrayRect.Height);
            }
            else
            {
                int cy = s.TrayRect.Top + s.TrayRect.Height / 2;
                fb = new Rectangle(s.TrayRect.Left, cy - half, s.TrayRect.Width, half * 2);
            }
            Remember(s.Tray, fb, "fallback center±" + half, now, out rect, out how);
        }

        static void Remember(IntPtr tray, Rectangle r, string how, int now, out Rectangle rect, out string outHow)
        {
            _cachedTray = tray;
            _cachedRect = r;
            _cachedHow = how;
            _cachedAt = now;
            rect = r;
            outHow = how;
        }

        static int EstimatedHalfApps(TaskbarSnapshot s)
        {
            int thick = s.IsHorizontal ? s.TrayRect.Height : s.TrayRect.Width;
            if (thick < 24) thick = 40;
            // ~8 icon slots around the centered cluster.
            return Math.Max(160, thick * 8);
        }

        // Tight = the real icon cluster, not a band that spans almost the whole taskbar.
        static bool IsTightAppsRect(Rectangle r, TaskbarSnapshot s)
        {
            if (r.IsEmpty || r.Width < 8 || r.Height < 8) return false;
            Rectangle tray = s.TrayRect;
            if (tray.IsEmpty) return false;
            Rectangle hit = Rectangle.Intersect(r, Rectangle.Inflate(tray, 16, 16));
            if (hit.Width < 8 || hit.Height < 8) return false;
            if (s.IsHorizontal)
            {
                if (r.Width > tray.Width * 7 / 10) return false;
                if (!s.NotifyRect.IsEmpty && r.Right > s.NotifyRect.Left + 8) return false;
            }
            else
            {
                if (r.Height > tray.Height * 7 / 10) return false;
                if (!s.NotifyRect.IsEmpty && r.Bottom > s.NotifyRect.Top + 8) return false;
            }
            return true;
        }

        static Rectangle FindByClassName(TaskbarSnapshot s)
        {
            if (s.Tray == IntPtr.Zero) return Rectangle.Empty;
            Rectangle best = Rectangle.Empty;
            Native.EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr lParam)
            {
                string cls = Native.GetWindowClass(hwnd);
                if (cls == "MSTaskListWClass" || cls == "MSTaskSwWClass")
                {
                    Rectangle r = Native.GetRect(hwnd);
                    if (IsTightAppsRect(r, s))
                    {
                        if (best.IsEmpty)
                            best = r;
                        else
                            best = Rectangle.Union(best, r);
                    }
                }
                return true;
            };
            try { Native.EnumChildWindows(s.Tray, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            return best;
        }

        // v1.4.9: UI Automation over Shell_TrayWnd removed. FindAll(Descendants) on
        // Explorer's taskbar ran every ~1 s, leaked client-side UIA memory (GBs over
        // days) and made synchronous cross-process calls into Explorer's UI thread.
        static bool TryFindByUia(TaskbarSnapshot s, out Rectangle rect, out string how)
        {
            rect = Rectangle.Empty;
            how = "uia-disabled";
            return false;
        }

        static Rectangle _cachedWidgets;
        static string _cachedWidgetsHow = "";
        static IntPtr _cachedWidgetsTray;
        static int _cachedWidgetsAt;

        static void FillWidgets(TaskbarSnapshot s)
        {
            int now = Environment.TickCount;
            if (s.Tray == _cachedWidgetsTray && (uint)(now - _cachedWidgetsAt) < (uint)CacheMs)
            {
                s.WidgetsRect = _cachedWidgets;
                s.WidgetsSource = _cachedWidgetsHow;
                return;
            }

            Rectangle r;
            string how;
            if (!TryFindWidgets(s, out r, out how))
            {
                r = Rectangle.Empty;
                how = "";
            }
            _cachedWidgetsTray = s.Tray;
            _cachedWidgets = r;
            _cachedWidgetsHow = how;
            _cachedWidgetsAt = now;
            s.WidgetsRect = r;
            s.WidgetsSource = how;
        }

        static bool TryFindWidgets(TaskbarSnapshot s, out Rectangle rect, out string how)
        {
            rect = Rectangle.Empty;
            how = "";
            if (s.Tray == IntPtr.Zero) return false;

            Rectangle best = Rectangle.Empty;
            string bestHow = "";
            Native.EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr lParam)
            {
                string cls = Native.GetWindowClass(hwnd);
                if (!ClassLooksLikeWidgets(cls)) return true;
                Rectangle wr = Native.GetRect(hwnd);
                if (!IsTaskbarChromeButton(wr, s)) return true;
                if (best.IsEmpty || wr.Left < best.Left)
                {
                    best = wr;
                    bestHow = "hwnd " + cls;
                }
                return true;
            };
            try { Native.EnumChildWindows(s.Tray, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            if (!best.IsEmpty)
            {
                rect = best;
                how = bestHow;
                return true;
            }

            Rectangle uia;
            string uiaHow;
            if (TryFindWidgetsByUia(s, out uia, out uiaHow))
            {
                rect = uia;
                how = uiaHow;
                return true;
            }
            return false;
        }

        static bool ClassLooksLikeWidgets(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            return cls.IndexOf("WidgetsButton", StringComparison.OrdinalIgnoreCase) >= 0
                || cls.Equals("Widgets", StringComparison.OrdinalIgnoreCase)
                || (cls.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0
                    && cls.IndexOf("WidgetBoard", StringComparison.OrdinalIgnoreCase) < 0
                    && cls.IndexOf("WidgetHost", StringComparison.OrdinalIgnoreCase) < 0);
        }

        static bool IsTaskbarChromeButton(Rectangle r, TaskbarSnapshot s)
        {
            if (r.IsEmpty || s.TrayRect.IsEmpty) return false;
            if (Rectangle.Intersect(r, Rectangle.Inflate(s.TrayRect, 16, 16)).IsEmpty) return false;
            int thick = s.IsHorizontal ? s.TrayRect.Height : s.TrayRect.Width;
            if (thick < 24) thick = 40;
            int maxSide = thick * 4;
            if (s.IsHorizontal)
                return r.Width <= maxSide && r.Height <= thick + 16 && r.Width >= 8;
            return r.Height <= maxSide && r.Width <= thick + 16 && r.Height >= 8;
        }

        static bool TryFindWidgetsByUia(TaskbarSnapshot s, out Rectangle rect, out string how)
        {
            rect = Rectangle.Empty;
            how = "";
            return false;
        }

        static bool NameLooksLikeWidgets(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim();
            if (n.Equals("Widgets", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("Виджеты", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.IndexOf("Мини-приложения", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("Widgets", StringComparison.OrdinalIgnoreCase) >= 0 && n.Length <= 32) return true;
            return false;
        }



        static string Fmt(Rectangle r)
        {
            if (r.IsEmpty) return "empty";
            return r.ToString();
        }
    }

    static class StripLayout
    {
        public const int Margin = 4;

        public static Rectangle EnsureNotify(TaskbarSnapshot snap)
        {
            Rectangle tray = snap.TrayRect;
            Rectangle notify = snap.NotifyRect;
            bool horizontal = snap.IsHorizontal;
            if (!notify.IsEmpty && notify.Width > 1 && notify.Height > 1)
                return notify;
            int fallback = horizontal
                ? Math.Max(88, (int)(tray.Width * 0.18))
                : Math.Max(88, (int)(tray.Height * 0.18));
            if (horizontal)
                return new Rectangle(tray.Right - fallback, tray.Top, fallback, tray.Height);
            return new Rectangle(tray.Left, tray.Bottom - fallback, tray.Width, fallback);
        }

        public static void Axis(TaskbarSnapshot snap, Rectangle r, out int lead, out int trail)
        {
            if (snap.IsHorizontal)
            {
                lead = r.Left;
                trail = r.Right;
            }
            else
            {
                lead = r.Top;
                trail = r.Bottom;
            }
        }

        // Left empty zone: [taskbar lead + margin, start lead − margin],
        // pushed past the Widgets button when that button sits in this area.
        public static bool TryLeftZone(TaskbarSnapshot snap, out int min, out int max)
        {
            min = 0;
            max = 0;
            Rectangle tray = snap.TrayRect;
            if (tray.IsEmpty) return false;
            int margin = Margin;
            int trayLead, trayTrail, startLead, startTrail;
            Axis(snap, tray, out trayLead, out trayTrail);
            min = trayLead + margin;

            Rectangle start = snap.StartRect;
            if (start.IsEmpty && !snap.AppsRect.IsEmpty)
            {
                // Approximate Start as one icon slot immediately before the apps cluster.
                int thick = snap.IsHorizontal ? Math.Max(24, tray.Height) : Math.Max(24, tray.Width);
                if (snap.IsHorizontal)
                    start = new Rectangle(snap.AppsRect.Left - thick, tray.Top, thick, tray.Height);
                else
                    start = new Rectangle(tray.Left, snap.AppsRect.Top - thick, tray.Width, thick);
            }
            if (start.IsEmpty) return false;
            Axis(snap, start, out startLead, out startTrail);
            max = startLead - margin;

            Rectangle widgets = snap.WidgetsRect;
            if (!widgets.IsEmpty)
            {
                int wLead, wTrail;
                Axis(snap, widgets, out wLead, out wTrail);
                // Only shrink the zone if Widgets sits at the leading end (typical Win11 left corner).
                if (wTrail + margin > min && wLead < max)
                    min = Math.Max(min, wTrail + margin);
            }
            return max > min;
        }

        public static bool TryRightZone(TaskbarSnapshot snap, out int min, out int max)
        {
            min = 0;
            max = 0;
            Rectangle tray = snap.TrayRect;
            if (tray.IsEmpty) return false;
            int margin = Margin;
            int trayLead, trayTrail;
            Axis(snap, tray, out trayLead, out trayTrail);
            min = trayLead + margin;

            Rectangle notify = EnsureNotify(snap);
            int nLead, nTrail;
            Axis(snap, notify, out nLead, out nTrail);
            max = nLead - margin;
            if (max > trayTrail - margin) max = trayTrail - margin;

            if (!snap.AppsRect.IsEmpty)
            {
                int aLead, aTrail;
                Axis(snap, snap.AppsRect, out aLead, out aTrail);
                min = Math.Max(min, aTrail + margin);
            }
            else if (!snap.StartRect.IsEmpty)
            {
                int sLead, sTrail;
                Axis(snap, snap.StartRect, out sLead, out sTrail);
                if (sTrail < trayLead + (trayTrail - trayLead) / 3)
                    min = Math.Max(min, sTrail + margin);
            }
            return max > min;
        }

        public static bool Fits(int min, int max, int extent)
        {
            return max - min >= extent;
        }

        public static int ClampPos(int pos, int extent, int min, int max)
        {
            if (max - min < extent)
                return max - extent;
            if (pos < min) return min;
            if (pos + extent > max) return max - extent;
            return pos;
        }

        public static int PosFromPlacement(StripZone zone, int offset, int trayLead, int notifyLead)
        {
            if (zone == StripZone.Left)
                return trayLead + offset;
            return notifyLead + offset;
        }

        public static int OffsetFromPos(StripZone zone, int pos, int trayLead, int notifyLead)
        {
            if (zone == StripZone.Left)
                return pos - trayLead;
            return pos - notifyLead;
        }

        // How far the strip must move to lie fully inside [min, max].
        public static int SnapDistance(int pos, int extent, int min, int max)
        {
            if (!Fits(min, max, extent)) return int.MaxValue;
            int clamped = ClampPos(pos, extent, min, max);
            return Math.Abs(clamped - pos);
        }

        public static StripZone NearestZone(int pos, int extent,
            bool leftOk, int leftMin, int leftMax,
            bool rightOk, int rightMin, int rightMax,
            StripZone prefer)
        {
            int dLeft = leftOk ? SnapDistance(pos, extent, leftMin, leftMax) : int.MaxValue;
            int dRight = rightOk ? SnapDistance(pos, extent, rightMin, rightMax) : int.MaxValue;
            if (dLeft == int.MaxValue && dRight == int.MaxValue)
                return prefer;
            if (dLeft < dRight) return StripZone.Left;
            if (dRight < dLeft) return StripZone.Right;
            return prefer;
        }
    }

    static class Autostart
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Pomodoro";

        public static string DesiredValue()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        AppLog.Error("autostart: cannot open HKCU Run key");
                        return;
                    }
                    if (enabled)
                    {
                        string want = DesiredValue();
                        object existing = key.GetValue(ValueName);
                        string have = existing == null ? null : Convert.ToString(existing, CultureInfo.InvariantCulture);
                        if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue(ValueName, want, RegistryValueKind.String);
                            AppLog.Info("autostart set Run=" + want + (have == null ? " (new)" : " (was " + have + ")"));
                        }
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                        AppLog.Info("autostart removed Run value");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("autostart apply enabled=" + enabled, ex);
            }
        }
    }

    /// <summary>
    /// v1.4.9: embedding into Explorer windows is gone. A cross-process SetParent into
    /// Shell_TrayWnd attaches our input queue to Explorer's, so any stall in this process
    /// froze the taskbar (event 1002 "explorer.exe stopped interacting"). This stub keeps
    /// the call sites compiling; every path ends as a top-level topmost overlay.
    /// </summary>
    sealed class TaskbarEmbedder
    {
        public EmbedKind Kind { get; private set; }

        public IntPtr CurrentParent { get { return IntPtr.Zero; } }

        public void Detach(IntPtr hwnd)
        {
            Kind = EmbedKind.None;
        }

        public void RestoreTaskList()
        {
        }

        public EmbedKind Attach(IntPtr hwnd, TaskbarSnapshot snap, StripDisplayMode mode)
        {
            Kind = EmbedKind.Overlay;
            if (hwnd != IntPtr.Zero) Native.RestorePopupStyle(hwnd);
            return Kind;
        }

        public bool RetryShellTray(IntPtr hwnd, IntPtr tray, int attempt, bool verbose)
        {
            return false;
        }

        public bool AdoptDirectChild(IntPtr hwnd, IntPtr tray)
        {
            return false;
        }

        public void ShrinkTaskList(TaskbarSnapshot snap, int reserve, bool horizontal)
        {
        }
    }

    #endregion

    sealed class MainForm : Form
    {
        public static readonly Color Bg = Color.FromArgb(16, 16, 18);
        static readonly Color Border = Color.FromArgb(42, 42, 46);
        public static readonly Color WorkLit = Color.FromArgb(255, 70, 48);
        public static readonly Color WorkUnlit = Color.FromArgb(48, 18, 16);
        public static readonly Color WorkDim = Color.FromArgb(118, 42, 34);

        public static readonly Color RestLit = Color.FromArgb(46, 214, 122);
        public static readonly Color RestUnlit = Color.FromArgb(14, 40, 26);
        public static readonly Color RestDim = Color.FromArgb(28, 110, 68);

        public static readonly Color RepsLit = Color.FromArgb(228, 210, 128);
        public static readonly Color RepsUnlit = Color.FromArgb(42, 38, 22);

        static readonly int[] WorkChoices = { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 90 };
        static readonly int[] RestChoices = { 1, 2, 3, 5, 10, 15, 20, 30 };
        static readonly int[] RepsChoices = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 20 };

        readonly PomodoroEngine _engine;
        readonly System.Windows.Forms.Timer _timer;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _startItem;
        readonly ToolStripMenuItem _topmostItem;
        readonly ToolStripMenuItem _taskbarModeItem;
        readonly ToolStripMenuItem _windowModeItem;
        readonly ToolStripMenuItem _displayMenu;
        readonly ToolStripMenuItem _embedModeItem;
        readonly ToolStripMenuItem _overlayModeItem;
        readonly ToolStripMenuItem _lockItem;
        readonly ToolStripMenuItem _autostartItem;
        readonly ToolStripMenuItem _workMenu;
        readonly ToolStripMenuItem _restMenu;
        readonly ToolStripMenuItem _repsMenu;
        readonly SoundPlayer _player = new SoundPlayer();
        readonly uint _msgTaskbarCreated;

        Point _dragOffset;
        bool _dragging;
        bool _moved;
        bool _inWindowDrag;
        bool _settingsLoaded;
        bool _taskbarMode = true;
        bool _locked = true;
        bool _autostart = true;
        bool _allowVisible;
        int _stripOffset;
        StripZone _stripZone = StripZone.Right;
        bool _legacyRightOffset;
        StripDisplayMode _stripMode = StripDisplayMode.Overlay;
        bool _stripModeManual;
        bool _temporaryOverlay;
        int _temporarySince;
        int _nextEmbedRetryAt;
        bool _recovering;
        string _recoveryReason = "";
        int _recoveryStartAt;
        int _recoveryNextAt;
        int _recoveryDelayMs;
        bool _recreatingStrip;
        bool _recreatePending;
        TaskbarStrip _pendingStrip;
        string _pendingReason = "";
        IntPtr _pendingOldHwnd;
        int _lastRetryDeferLogAt;
        System.IO.MemoryStream _soundStream;

        const int TbpfNoProgress = 0;
        const int TbpfNormal = 2;
        const int TbpfPaused = 8;

        ITaskbarList3 _taskbar;
        bool _taskbarReady;

        TaskbarStrip _strip;
        NotifyIcon _notify;
        Icon _trayIcon;
        Icon _appIcon;
        int _lastTrayKey = int.MinValue;

        readonly EventWaitHandle _showEvent;
        Thread _showWaiter;
        volatile bool _stopWaiter;
        bool _readyStarted;
        System.Windows.Forms.Timer _safety;
        DateTime _stripDeadline;
        bool _safetyArmed;
        bool _balloonShown;
        Native.WinEventDelegate _fgDelegate;
        IntPtr _hookSystem;
        IntPtr _hookObject;
        int _winEventPosted;

        public MainForm(bool fast, EventWaitHandle showEvent)
        {
            _showEvent = showEvent;
            _engine = new PomodoroEngine(fast);
            try { _msgTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated"); }
            catch { _msgTaskbarCreated = 0; }

            SuspendLayout();
            Text = "Pomodoro";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            ShowIcon = true;
            MaximizeBox = false;
            MinimizeBox = true;
            ControlBox = true;
            DoubleBuffered = true;
            BackColor = Bg;
            ForeColor = Color.Gainsboro;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(300, 76);
            MinimumSize = new Size(220, 56);
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            try
            {
                _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                Icon = _appIcon;
            }
            catch
            {
            }

            _startItem = new ToolStripMenuItem("Start");
            _startItem.Click += delegate { ToggleRun(); };
            var resetItem = new ToolStripMenuItem("Reset");
            resetItem.Click += delegate { ResetTimer(); };

            _workMenu = BuildIntMenu("Work", WorkChoices, " min", delegate(int v) { ApplyWork(v); });
            _restMenu = BuildIntMenu("Rest", RestChoices, " min", delegate(int v) { ApplyRest(v); });
            _repsMenu = BuildIntMenu("Reps", RepsChoices, "", delegate(int v) { ApplyReps(v); });

            _taskbarModeItem = new ToolStripMenuItem("On the taskbar");
            _taskbarModeItem.Click += delegate
            {
                if (!_taskbarMode) SetTaskbarMode(true, true);
            };
            _windowModeItem = new ToolStripMenuItem("As a window");
            _windowModeItem.Click += delegate
            {
                if (_taskbarMode) SetTaskbarMode(false, true);
            };

            _embedModeItem = new ToolStripMenuItem("Embed in the taskbar (recommended)");
            _embedModeItem.Click += delegate { SetStripMode(StripDisplayMode.Embed, true); };
            _overlayModeItem = new ToolStripMenuItem("Over the taskbar");
            _overlayModeItem.Click += delegate { SetStripMode(StripDisplayMode.Overlay, true); };
            _displayMenu = new ToolStripMenuItem("Display mode");
            _displayMenu.DropDownItems.Add(_embedModeItem);
            _displayMenu.DropDownItems.Add(_overlayModeItem);

            _topmostItem = new ToolStripMenuItem("Always on top");
            _topmostItem.Click += delegate
            {
                if (_taskbarMode) return;
                TopMost = !TopMost;
                _topmostItem.Checked = TopMost;
                AppLog.Info("topmost=" + TopMost);
                SaveSettings();
            };

            _lockItem = new ToolStripMenuItem("Lock position");
            _lockItem.CheckOnClick = false;
            _lockItem.Click += delegate
            {
                _locked = !_locked;
                _lockItem.Checked = _locked;
                _dragging = false;
                _moved = false;
                AppLog.Info("locked=" + _locked);
                SaveSettings();
            };

            _autostartItem = new ToolStripMenuItem("Start with Windows");
            _autostartItem.CheckOnClick = false;
            _autostartItem.Click += delegate
            {
                _autostart = !_autostart;
                _autostartItem.Checked = _autostart;
                Autostart.Apply(_autostart);
                SaveSettings();
            };

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { Close(); };

            _menu = new ContextMenuStrip();
            _menu.Renderer = new DarkMenuRenderer();
            _menu.ShowImageMargin = true;
            _menu.ShowCheckMargin = true;
            _menu.Items.Add(_startItem);
            _menu.Items.Add(resetItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_workMenu);
            _menu.Items.Add(_restMenu);
            _menu.Items.Add(_repsMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_taskbarModeItem);
            _menu.Items.Add(_windowModeItem);
            _menu.Items.Add(_displayMenu);
            _displayMenu.Available = false; // v1.4.9: only one strip mode is left
            _menu.Items.Add(_topmostItem);
            _menu.Items.Add(_lockItem);
            _menu.Items.Add(_autostartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);
            _menu.Opening += MenuOnOpening;
            _menu.Opened += MenuOnOpened;
            _menu.Closed += MenuOnClosed;
            ContextMenuStrip = _menu;

            LoadSettings();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 100;
            _timer.Tick += delegate { OnTick(); };
            _timer.Start();

            HandleCreated += delegate { EnsureReady(); };
            Load += delegate { EnsureReady(); };
            FormClosing += OnMainClosing;
            KeyDown += OnKeyDown;
            ResumeLayout(false);
        }

        public PomodoroEngine Engine { get { return _engine; } }

        public int StripOffset { get { return _stripOffset; } }

        public StripZone StripZone { get { return _stripZone; } }

        public bool LegacyRightOffset { get { return _legacyRightOffset; } }

        public bool IsLocked { get { return _locked; } }

        // v1.4.9.3: while the context menu is open, no z-order / activation work at all.
        bool _menuOpen;
        public bool IsMenuOpen { get { return _menuOpen; } }

        public bool IsTaskbarMode { get { return _taskbarMode; } }

        public StripDisplayMode StripMode { get { return _stripMode; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WsExAppwindow;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_taskbarMode && !_allowVisible)
            {
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message m)
        {
            if (_msgTaskbarCreated != 0 && m.Msg == (int)_msgTaskbarCreated)
                BeginInvoke(new Action(OnExplorerTaskbarCreated));
            base.WndProc(ref m);
        }

        void OnExplorerTaskbarCreated()
        {
            AppLog.Info("TaskbarCreated — waiting for the new taskbar");
            RecreateNotifyIcon();
            if (_taskbarMode)
                BeginTrayRecovery("TaskbarCreated");
        }

        void EnsureReady()
        {
            if (_readyStarted) return;
            _readyStarted = true;
            OnFormReady();
        }

        void OnFormReady()
        {
            _settingsLoaded = true;
            try
            {
                AppLog.Info("ready handle=" + Handle.ToString("X") + " taskbarMode=" + _taskbarMode
                    + " locked=" + _locked + " autostart=" + _autostart
                    + " stripMode=" + _stripMode
                    + " dpi=" + DeviceDpi + " lightTaskbar=" + OsInfo.IsTaskbarLight());
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    Screen s = screens[i];
                    AppLog.Info("screen[" + i + "] bounds=" + s.Bounds + " work=" + s.WorkingArea
                        + " primary=" + s.Primary);
                }
                var snap = TaskbarFinder.Capture();
                AppLog.Info("taskbar tray=" + snap.TrayRect + " notify=" + snap.NotifyRect
                    + " edge=" + snap.Edge + " win11style=" + snap.IsWin11Style
                    + " start=" + snap.StartRect
                    + " widgets=" + snap.WidgetsRect
                    + " apps=" + snap.AppsRect + " appsSrc=" + snap.AppsSource
                    + " zone=" + _stripZone + " offset=" + _stripOffset);
            }
            catch (Exception ex)
            {
                AppLog.Error("ready diagnostics", ex);
            }

            Autostart.Apply(_autostart);
            SaveSettings();

            InitTaskbarProgress();
            InitNotifyIcon();
            SetTaskbarMode(_taskbarMode, false);
            StartShowWaiter();
            HookForeground();
            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }
            catch (Exception ex)
            {
                AppLog.Error("DisplaySettingsChanged subscribe", ex);
            }
            RefreshVisuals();
        }

        void StartShowWaiter()
        {
            if (_showEvent == null) return;
            _showWaiter = new Thread(ShowWaiterLoop);
            _showWaiter.IsBackground = true;
            _showWaiter.Name = "PomodoroShow";
            _showWaiter.Start();
        }

        void ShowWaiterLoop()
        {
            while (!_stopWaiter)
            {
                try
                {
                    if (_showEvent.WaitOne(400))
                    {
                        if (_stopWaiter) break;
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new Action(OnShowExistingInstance));
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Error("show waiter", ex);
                    break;
                }
            }
        }

        void OnShowExistingInstance()
        {
            AppLog.Info("show request from a second instance");
            InitNotifyIcon();
            if (_taskbarMode)
            {
                RequestRecreateStrip("second-instance", null);
                if (!IsStripVisibleAndPlaced())
                {
                    AppLog.Info("second instance: strip still not visible, showing window");
                    SetTaskbarMode(false, false);
                }
            }
            else
            {
                _allowVisible = true;
                ShowInTaskbar = true;
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }

        void HookForeground()
        {
            try
            {
                _fgDelegate = OnWinEvent;
                uint flags = Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess;
                // 0x0003–0x0005: FOREGROUND, MENUSTART, MENUEND
                _hookSystem = Native.SetWinEventHook(
                    Native.EventSystemForeground, Native.EventSystemMenuEnd,
                    IntPtr.Zero, _fgDelegate, 0, 0, flags);
                // 0x8003–0x8004: OBJECT_HIDE, OBJECT_REORDER (Start / taskbar z-order fights)
                _hookObject = Native.SetWinEventHook(
                    Native.EventObjectShow, Native.EventObjectReorder,
                    IntPtr.Zero, _fgDelegate, 0, 0, flags);
                AppLog.Info("winevent hooks system=" + _hookSystem.ToString("X")
                    + " object=" + _hookObject.ToString("X"));
            }
            catch (Exception ex)
            {
                AppLog.Error("SetWinEventHook", ex);
            }
        }

        void UnhookForeground()
        {
            UnhookOne(ref _hookSystem);
            UnhookOne(ref _hookObject);
        }

        static void UnhookOne(ref IntPtr hook)
        {
            if (hook == IntPtr.Zero) return;
            try { Native.UnhookWinEvent(hook); }
            catch { }
            hook = IntPtr.Zero;
        }

        void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!ShouldHandleWinEvent(eventType, hwnd, idObject)) return;
            if (Interlocked.CompareExchange(ref _winEventPosted, 1, 0) != 0)
                return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    Interlocked.Exchange(ref _winEventPosted, 0);
                    HandleForegroundChange();
                }));
            }
            catch
            {
                Interlocked.Exchange(ref _winEventPosted, 0);
            }
        }

        static bool ShouldHandleWinEvent(uint eventType, IntPtr hwnd, int idObject)
        {
            if (eventType == Native.EventSystemForeground)
                return true;
            if (eventType == Native.EventSystemMenusStart)
                return false;
            // MENUEND / OBJECT_HIDE / OBJECT_REORDER: Start host, search, tray, etc.
            if (eventType == Native.EventObjectShow || eventType == Native.EventObjectHide || eventType == Native.EventObjectReorder)
            {
                if (idObject != 0 && idObject != unchecked((int)Native.ObjidWindow))
                    return false;
            }
            if (hwnd == IntPtr.Zero) return false;
            return Native.IsShellChromeWindow(hwnd);
        }

        void HandleForegroundChange()
        {
            if (_taskbarMode && _strip != null && !_strip.IsDisposed)
                _strip.OnForegroundChanged();
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                if (_taskbarMode && _strip != null)
                    _strip.Reposition(true);
            }));
        }

        void InitTaskbarProgress()
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
                if (t == null) return;
                object obj = Activator.CreateInstance(t);
                _taskbar = obj as ITaskbarList3;
                if (_taskbar == null) return;
                _taskbar.HrInit();
                _taskbarReady = true;
            }
            catch
            {
                _taskbar = null;
                _taskbarReady = false;
            }
        }

        void InitNotifyIcon()
        {
            if (_notify != null)
            {
                try { if (!_notify.Visible) _notify.Visible = true; } catch { }
                return;
            }
            try
            {
                _notify = new NotifyIcon();
                _notify.Text = "Pomodoro";
                _notify.ContextMenuStrip = _menu;
                _notify.MouseUp += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left)
                        ToggleRun();
                };
                UpdateTrayIcon(true);
                // Visible must be set after a valid Icon, on the UI thread.
                _notify.Visible = true;
                AppLog.Info("notify icon visible=" + _notify.Visible + " hasIcon=" + (_notify.Icon != null));
            }
            catch (Exception ex)
            {
                AppLog.Error("InitNotifyIcon", ex);
            }
        }

        void RecreateNotifyIcon()
        {
            if (_notify != null)
            {
                try { _notify.Visible = false; } catch { }
                try { _notify.Dispose(); } catch { }
                _notify = null;
            }
            _lastTrayKey = int.MinValue;
            InitNotifyIcon();
        }

        static ToolStripMenuItem BuildIntMenu(string title, int[] values, string suffix, Action<int> apply)
        {
            var root = new ToolStripMenuItem(title);
            for (int i = 0; i < values.Length; i++)
            {
                int v = values[i];
                var item = new ToolStripMenuItem(v.ToString("00", CultureInfo.InvariantCulture) + suffix);
                item.Tag = v;
                item.Click += delegate { apply(v); };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        void MenuOnOpened(object sender, EventArgs e)
        {
            // The dropdown is a visible window of ours and we just received the click,
            // so we may take foreground. Without this the menu sees "app not active"
            // and closes at once with AppFocusChange.
            IntPtr before = Native.GetForegroundWindow();
            bool ok = false;
            try { if (_menu.IsHandleCreated) ok = Native.SetForegroundWindow(_menu.Handle); }
            catch { }
            IntPtr after = Native.GetForegroundWindow();
            AppLog.Info("menu opened fgBefore=" + Native.GetWindowClass(before)
                + " setFg=" + ok + " fgAfter=" + Native.GetWindowClass(after)
                + " ours=" + (_menu.IsHandleCreated && after == _menu.Handle));
        }

        void MenuOnOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _menuOpen = true;
            if (_strip != null && !_strip.IsDisposed) _strip.OnMenuOpening();
            if (_engine.IsRunning)
                _startItem.Text = "Pause";
            else if (_engine.IsPaused)
                _startItem.Text = "Resume";
            else
                _startItem.Text = "Start";

            SyncModeMenu();
            _lockItem.Checked = _locked;
            _autostartItem.Checked = _autostart;

            PrepareForegroundForMenu();

            SyncMenuChecks(_workMenu, _engine.WorkMinutes);
            SyncMenuChecks(_restMenu, _engine.RestMinutes);
            SyncMenuChecks(_repsMenu, _engine.RepsConfigured);
        }

        void MenuOnClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            _menuOpen = false;
            AppLog.Info("menu closed reason=" + e.CloseReason);
            if (_strip != null && !_strip.IsDisposed) _strip.OnMenuClosed();
            try { Native.PostMessage(Handle, Native.WmNull, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        public void PrepareForegroundForMenu()
        {
            // A child of explorer is not a foreground window; without this the
            // context menu may fail to appear or refuse to dismiss.
            Native.FocusForPopup(Handle);
        }

        public bool TemporaryOverlay { get { return _temporaryOverlay; } }

        public bool WantsEmbed { get { return false; } } // v1.4.9: never embed into Explorer

        public void FallbackToOverlay(string reason)
        {
            AppLog.Info("auto-fallback to overlay (temporary, not saved): " + reason);
            _temporaryOverlay = true;
            _temporarySince = Environment.TickCount;
            _nextEmbedRetryAt = _temporarySince + 5000;
            RequestRecreateStrip("temporary-overlay: " + reason, true);
        }

        public void ClearTemporaryOverlay()
        {
            if (!_temporaryOverlay) return;
            _temporaryOverlay = false;
            _temporarySince = 0;
            AppLog.Info("temporary overlay cleared; embed is healthy again");
        }

        public bool ShouldRetryEmbed()
        {
            if (!_temporaryOverlay || _stripMode != StripDisplayMode.Embed) return false;
            return unchecked((int)(Environment.TickCount - _nextEmbedRetryAt)) >= 0;
        }

        public void MarkEmbedRetry()
        {
            uint elapsed = (uint)(Environment.TickCount - _temporarySince);
            int delay = elapsed < 60000u ? 5000 : 30000;
            _nextEmbedRetryAt = Environment.TickCount + delay;
        }

        public void NoteEmbedFailed(string reason)
        {
            if (!_temporaryOverlay)
            {
                _temporaryOverlay = true;
                _temporarySince = Environment.TickCount;
                AppLog.Info("auto-fallback to overlay (temporary, not saved): " + reason);
            }
            else
                AppLog.Info("embed retry failed, still temporary overlay: " + reason);
            MarkEmbedRetry();
        }

        void SetStripMode(StripDisplayMode mode, bool save)
        {
            mode = StripDisplayMode.Overlay; // v1.4.9: embed mode removed
            _stripModeManual = true;
            _temporaryOverlay = false;
            _temporarySince = 0;
            bool same = _stripMode == mode;
            _stripMode = mode;
            if (!same)
                AppLog.Info("stripMode=" + mode + " (manual)");
            SyncModeMenu();
            if (save) SaveSettings();
            if (_taskbarMode)
                RequestRecreateStrip(same ? "strip-mode-refresh" : "strip-mode-manual",
                    mode == StripDisplayMode.Overlay);
        }

        void SyncModeMenu()
        {
            if (_taskbarModeItem != null) _taskbarModeItem.Checked = _taskbarMode;
            if (_windowModeItem != null) _windowModeItem.Checked = !_taskbarMode;
            if (_displayMenu != null) _displayMenu.Enabled = _taskbarMode;
            if (_embedModeItem != null)
                _embedModeItem.Checked = _taskbarMode && _stripMode == StripDisplayMode.Embed;
            if (_overlayModeItem != null)
                _overlayModeItem.Checked = _taskbarMode && _stripMode == StripDisplayMode.Overlay;
            if (_topmostItem != null)
            {
                _topmostItem.Enabled = !_taskbarMode;
                _topmostItem.Checked = !_taskbarMode && TopMost;
            }
        }

        static void SyncMenuChecks(ToolStripMenuItem root, int current)
        {
            if (root == null) return;
            foreach (ToolStripItem raw in root.DropDownItems)
            {
                ToolStripMenuItem it = raw as ToolStripMenuItem;
                if (it == null || it.Tag == null) continue;
                it.Checked = (int)it.Tag == current;
            }
        }

        void ApplyWork(int minutes)
        {
            _engine.SetWorkMinutes(minutes);
            SaveSettings();
            RefreshVisuals();
        }

        void ApplyRest(int minutes)
        {
            _engine.SetRestMinutes(minutes);
            SaveSettings();
            RefreshVisuals();
        }

        void ApplyReps(int reps)
        {
            _engine.SetReps(reps);
            SaveSettings();
            RefreshVisuals();
        }

        public void ToggleRun()
        {
            BoundaryKind? ev = _engine.ToggleStartPause();
            if (ev.HasValue) PlayBoundary(ev.Value);
            RefreshVisuals();
        }

        void ResetTimer()
        {
            _engine.Reset();
            RefreshVisuals();
        }

        public void SetStripPlacement(StripZone zone, int offset, bool clearLegacy)
        {
            if (_stripZone == zone && _stripOffset == offset && (!clearLegacy || !_legacyRightOffset))
                return;
            _stripZone = zone;
            _stripOffset = offset;
            if (clearLegacy) _legacyRightOffset = false;
            SaveSettings();
        }

        void SetTaskbarMode(bool enabled, bool save)
        {
            AppLog.Info("mode switch " + (_taskbarMode ? "taskbar/" + _stripMode : "window")
                + " -> " + (enabled ? "taskbar/" + _stripMode : "window"));
            _taskbarMode = enabled;
            AppLog.Info("SetTaskbarMode enabled=" + enabled + " save=" + save);
            SyncModeMenu();
            if (enabled)
            {
                _allowVisible = false;
                ShowInTaskbar = false;
                Hide();
                RecreateStripCore("taskbar-mode", null);
                MaybeShowTrayBalloon();
            }
            else
            {
                CancelStripSafety();
                _recovering = false;
                DestroyStrip();
                _dragging = false;
                _moved = false;
                if (!IsOnAnyScreen(new Rectangle(Location, Size)))
                {
                    AppLog.Info("window position off-screen, placing default");
                    PlaceDefault();
                }
                _allowVisible = true;
                ShowInTaskbar = true;
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
            if (save) SaveSettings();
            RefreshVisuals();
        }


        public void BeginTrayRecovery(string reason)
        {
            if (!_taskbarMode) return;
            if (_recovering)
            {
                AppLog.Info("tray recovery already running reason=" + _recoveryReason
                    + " new=" + reason);
                return;
            }
            _recovering = true;
            _recoveryReason = reason ?? "";
            _recoveryStartAt = Environment.TickCount;
            _recoveryDelayMs = 500;
            _recoveryNextAt = Environment.TickCount + 500;
            AppLog.Info("tray recovery begin reason=" + _recoveryReason + " wait=500ms");
            // Drop the old HWND immediately — re-parenting it later will not composite.
            DestroyStrip();
        }

        void TickTrayRecovery()
        {
            if (!_recovering || !_taskbarMode) return;
            if (unchecked((int)(Environment.TickCount - _recoveryNextAt)) < 0) return;
            uint elapsed = (uint)(Environment.TickCount - _recoveryStartAt);
            TaskbarSnapshot snap;
            if (TaskbarFinder.IsTrayReady(out snap))
            {
                AppLog.Info("tray ready after " + elapsed.ToString(CultureInfo.InvariantCulture)
                    + "ms reason=" + _recoveryReason
                    + " tray=" + snap.Tray.ToString("X")
                    + " xaml=" + snap.HasXamlIsland);
                _recovering = false;
                RecreateStripCore("tray-ready: " + _recoveryReason, null);
                return;
            }
            if (elapsed >= 30000u)
            {
                AppLog.Info("tray not ready after 30s reason=" + _recoveryReason);
                _recovering = false;
                if (WantsEmbed)
                    FallbackToOverlay("tray not ready after 30s: " + _recoveryReason);
                else
                    RecreateStripCore("tray-timeout", true);
                return;
            }
            AppLog.Info("tray not ready yet reason=" + _recoveryReason
                + " elapsed=" + elapsed.ToString(CultureInfo.InvariantCulture)
                + " next=" + _recoveryDelayMs.ToString(CultureInfo.InvariantCulture) + "ms");
            _recoveryDelayMs = Math.Min(Math.Max(500, _recoveryDelayMs * 2), 8000);
            _recoveryNextAt = Environment.TickCount + _recoveryDelayMs;
        }

        void TickEmbedRetry()
        {
            if (_recovering || !_taskbarMode || !ShouldRetryEmbed()) return;
            if (_pendingStrip != null) return; // an embed attempt is already in flight
            Native.ForegroundState fg = Native.ReadForeground();
            bool startOpen = StripPolicy.IsStartOrSearchForeground(fg.ClassName, fg.ProcessName);
            if (!StripPolicy.ShouldStartOverlayRetry(_taskbarMode, _recovering, _temporaryOverlay,
                    WantsEmbed, true, startOpen))
            {
                // Start/Search is open: wait until it closes (checked again next tick).
                int now = Environment.TickCount;
                if (StripPolicy.IsLogDue(now, _lastRetryDeferLogAt, 10000))
                {
                    _lastRetryDeferLogAt = now == 0 ? 1 : now;
                    AppLog.Info("embed retry deferred: Start/Search open fg=" + fg.ClassName + "/" + fg.ProcessName);
                }
                return;
            }
            MarkEmbedRetry();
            if (TaskbarFinder.IsTrayReady(out _))
            {
                AppLog.Info("retrying embed after temporary overlay");
                RequestRecreateStrip("retry-embed", false);
            }
            else
                AppLog.Info("embed retry deferred: tray not ready");
        }

        public void RequestRecreateStrip(string reason, bool? overlay)
        {
            if (!_taskbarMode || IsDisposed) return;
            if (_recreatePending || _recreatingStrip) return;
            _recreatePending = true;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    _recreatePending = false;
                    RecreateStripCore(reason, overlay);
                }));
            }
            catch
            {
                _recreatePending = false;
                RecreateStripCore(reason, overlay);
            }
        }

        void RecreateStripCore(string reason, bool? overlay)
        {
            if (!_taskbarMode || IsDisposed) return;
            if (_recreatingStrip) return;
            _recreatingStrip = true;
            try
            {
                IntPtr oldHwnd = IntPtr.Zero;
                if (_strip != null && !_strip.IsDisposed && _strip.IsHandleCreated)
                    oldHwnd = _strip.Handle;
                bool useOverlay = true; // v1.4.9: always a top-level overlay, never a child of Explorer
                DestroyPendingStrip();
                if (useOverlay)
                {
                    // Explicit overlay (menu / invisible embed / tray timeout): replace now.
                    DestroyStrip();
                    _strip = new TaskbarStrip(this);
                    _strip.ContextMenuStrip = _menu;
                    _strip.AttachOnce(true);
                    IntPtr neuO = _strip.IsHandleCreated ? _strip.Handle : IntPtr.Zero;
                    AppLog.Info("strip recreated reason=" + reason
                        + " overlay=True oldHwnd=" + oldHwnd.ToString("X")
                        + " newHwnd=" + neuO.ToString("X"));
                    ArmStripSafety();
                    return;
                }

                // Embed, make-before-break: the current strip (embedded or overlay) keeps
                // showing until the new HWND has really landed in the tray.
                bool oldHealthy = _strip != null && _strip.IsHealthyEmbed;
                bool keepOld = _strip != null && !_strip.IsDisposed
                    && (StripPolicy.ShouldSwapAfterNewEmbeds(oldHealthy, false)
                        || _strip.Kind == EmbedKind.Overlay);
                if (!keepOld)
                    DestroyStrip();
                _pendingReason = reason;
                _pendingOldHwnd = oldHwnd;
                TaskbarStrip neu = new TaskbarStrip(this, true, IntPtr.Zero);
                neu.ContextMenuStrip = _menu;
                _pendingStrip = neu;
                AppLog.Info("strip recreate begin reason=" + reason
                    + " keepOld=" + keepOld + " oldHealthyEmbed=" + oldHealthy
                    + " oldHwnd=" + oldHwnd.ToString("X"));
                neu.AttachOnce(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("strip recreate reason=" + reason, ex);
            }
            finally
            {
                _recreatingStrip = false;
            }
        }

        /// <summary>Called by a pending strip once it embedded, or after every retry failed.</summary>
        public void OnStripAttachResult(TaskbarStrip strip, bool ok)
        {
            if (strip == null) return;
            if (strip != _pendingStrip || !_taskbarMode || IsDisposed)
            {
                AppLog.Info("stale attach result ok=" + ok + "; dropping that strip");
                DisposeStripObject(strip);
                return;
            }
            IntPtr neuHwnd = strip.IsHandleCreated ? strip.Handle : IntPtr.Zero;
            if (ok)
            {
                _pendingStrip = null;
                TaskbarStrip old = _strip;
                _strip = strip;
                if (old != null && old != strip)
                    DisposeStripObject(old);
                AppLog.Info("strip recreated reason=" + _pendingReason
                    + " overlay=False oldHwnd=" + _pendingOldHwnd.ToString("X")
                    + " newHwnd=" + neuHwnd.ToString("X")
                    + (strip.IsDirectChild ? " directChild=True" : "")
                    + (old != null ? " (make-before-break)" : ""));
                ArmStripSafety();
                return;
            }

            bool existingHealthy = _strip != null && _strip.IsHealthyEmbed;
            bool existingOverlay = _strip != null && !_strip.IsDisposed && _strip.Kind == EmbedKind.Overlay;
            AttachFailAction action = StripPolicy.DecideAttachFailure(
                strip.IsDirectChild, existingHealthy, existingOverlay);
            AppLog.Info("embed attach failed reason=" + _pendingReason
                + " directChild=" + strip.IsDirectChild
                + " existingHealthyEmbed=" + existingHealthy
                + " existingOverlay=" + existingOverlay
                + " -> " + action);
            _pendingStrip = null;
            DisposeStripObject(strip);
            string reason = _pendingReason;
            BeginInvoke(new Action(delegate { ApplyAttachFailure(action, reason); }));
        }

        void ApplyAttachFailure(AttachFailAction action, string reason)
        {
            if (!_taskbarMode || IsDisposed) return;
            switch (action)
            {
                case AttachFailAction.KeepExistingEmbed:
                    AppLog.Info("keeping the existing embedded strip");
                    return;
                case AttachFailAction.TryDirectChild:
                {
                    TaskbarSnapshot snap = TaskbarFinder.Capture();
                    if (snap != null && snap.IsValid && snap.Tray != IntPtr.Zero)
                    {
                        AppLog.Info("embed: trying a strip created directly as a tray child");
                        TaskbarStrip direct = null;
                        try
                        {
                            direct = new TaskbarStrip(this, true, snap.Tray);
                            direct.ContextMenuStrip = _menu;
                            _pendingStrip = direct;
                            direct.AttachOnce(false);
                            return;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("direct-child strip", ex);
                            if (direct != null && _pendingStrip == direct)
                            {
                                _pendingStrip = null;
                                DisposeStripObject(direct);
                            }
                        }
                    }
                    if (_strip != null && _strip.IsHealthyEmbed) return;
                    if (_strip != null && !_strip.IsDisposed && _strip.Kind == EmbedKind.Overlay)
                    {
                        NoteEmbedFailed("embed + direct child failed: " + reason);
                        return;
                    }
                    goto case AttachFailAction.FallbackOverlay;
                }
                case AttachFailAction.KeepExistingOverlay:
                    NoteEmbedFailed("embed retries failed, overlay kept: " + reason);
                    return;
                case AttachFailAction.FallbackOverlay:
                default:
                    NoteEmbedFailed("embed failed after retries: " + reason);
                    RecreateStripCore("temporary-overlay: attach failed", true);
                    return;
            }
        }

        void DestroyPendingStrip()
        {
            TaskbarStrip p = _pendingStrip;
            _pendingStrip = null;
            if (p != null) DisposeStripObject(p);
        }

        static void DisposeStripObject(TaskbarStrip strip)
        {
            if (strip == null) return;
            try
            {
                strip.PrepareDestroy();
                strip.ContextMenuStrip = null;
                strip.AllowClose = true;
                if (!strip.IsDisposed)
                    strip.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("destroy strip", ex);
            }
        }

        void DestroyStrip()
        {
            DestroyPendingStrip();
            TaskbarStrip strip = _strip;
            _strip = null;
            DisposeStripObject(strip);
        }

        private void MaybeShowTrayBalloon()
        {
            if (_balloonShown || _notify == null)
            {
                return;
            }
            _balloonShown = true;
            try
            {
                _notify.BalloonTipTitle = "Pomodoro";
                _notify.BalloonTipText = "The timer is on the taskbar. The icon is in the notification area.";
                _notify.BalloonTipIcon = ToolTipIcon.Info;
                _notify.ShowBalloonTip(4000);
            }
            catch (Exception ex)
            {
                AppLog.Error("balloon", ex);
            }
        }

        private void ArmStripSafety()
        {
            _stripDeadline = DateTime.UtcNow.AddSeconds(2.0);
            _safetyArmed = true;
            if (_safety == null)
            {
                _safety = new System.Windows.Forms.Timer();
                _safety.Interval = 200;
                _safety.Tick += delegate
                {
                    CheckStripSafety();
                };
            }
            _safety.Start();
        }

        private void CancelStripSafety()
        {
            _safetyArmed = false;
            if (_safety != null)
            {
                _safety.Stop();
            }
        }

        private void CheckStripSafety()
        {
            if (!_safetyArmed || !_taskbarMode)
            {
                CancelStripSafety();
            }
            else if (Native.IsForegroundFullscreen())
            {
                _stripDeadline = DateTime.UtcNow.AddSeconds(2.0);
            }
            else if (IsStripVisibleAndPlaced())
            {
                AppLog.Info("safety: strip is visible at " + FormatRect(_strip.LastScreenBounds));
                CancelStripSafety();
            }
            else if (!(DateTime.UtcNow < _stripDeadline))
            {
                AppLog.Error("safety: strip not placed within 2s; showing the floating window");
                CancelStripSafety();
                SetTaskbarMode(false, false);
            }
        }

        public bool IsStripVisibleAndPlaced()
        {
            if (_strip == null || _strip.IsDisposed)
            {
                return false;
            }
            if (!_strip.IsHandleCreated)
            {
                return false;
            }
            return _strip.IsPlacedOnTaskbar;
        }

        private static string FormatRect(Rectangle r)
        {
            return r.ToString();
        }

        int _resLogAt;

        void LogResourcesIfDue()
        {
            int now = Environment.TickCount;
            if (_resLogAt != 0 && (uint)(now - _resLogAt) < 600000u) return;
            _resLogAt = now == 0 ? 1 : now;
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    IntPtr self = Native.GetCurrentProcess();
                    AppLog.Info("resources private=" + (p.PrivateMemorySize64 / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB"
                        + " ws=" + (p.WorkingSet64 / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB"
                        + " managed=" + (GC.GetTotalMemory(false) / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB"
                        + " handles=" + p.HandleCount.ToString(CultureInfo.InvariantCulture)
                        + " gdi=" + Native.GetGuiResources(self, 0).ToString(CultureInfo.InvariantCulture)
                        + " user=" + Native.GetGuiResources(self, 1).ToString(CultureInfo.InvariantCulture)
                        + " cpu=" + p.TotalProcessorTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("resources", ex);
            }
        }

        private void OnTick()
        {
            LogResourcesIfDue();
            BoundaryKind? ev = _engine.Update();
            if (ev.HasValue) PlayBoundary(ev.Value);
            if (_taskbarMode)
            {
                TickTrayRecovery();
                TickEmbedRetry();
            }
            RefreshVisuals();
        }

        public void RefreshVisuals()
        {
            if (!IsDisposed)
            {
                UpdateTitle();
                UpdateTaskbar();
                if (Visible)
                {
                    Invalidate();
                }
                if (_strip != null && !_strip.IsDisposed)
                {
                    _strip.Present();
                }
                UpdateTrayIcon(false);
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleRun();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.R && e.Modifiers == Keys.None)
            {
                ResetTimer();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && (_engine.IsRunning || _engine.IsPaused))
            {
                ResetTimer();
                e.Handled = true;
            }
        }

        private void UpdateTitle()
        {
            Text = BuildStatusText(false);
        }

        public string BuildStatusText(bool forTray)
        {
            string text = "Pomodoro";
            if (_engine.Phase == Phase.Idle)
            {
                if (!_engine.IsFast)
                {
                    return text;
                }
                return text + "  ·  fast";
            }
            int remainingSecondsCeil = _engine.RemainingSecondsCeil;
            string text2 = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", remainingSecondsCeil / 60, remainingSecondsCeil % 60);
            string text3 = ((_engine.Phase == Phase.Rest) ? "Rest" : "Work");
            string text4 = text2 + " — " + text3;
            if (_engine.IsPaused)
            {
                text4 += " (paused)";
            }
            if (forTray && text4.Length > 63)
            {
                text4 = text4.Substring(0, 63);
            }
            return text4;
        }

        private void UpdateTaskbar()
        {
            if (!_taskbarReady || _taskbar == null || !IsHandleCreated || _taskbarMode)
            {
                return;
            }
            try
            {
                if (_engine.Phase == Phase.Idle)
                {
                    _taskbar.SetProgressState(Handle, 0);
                    return;
                }
                long num = _engine.CurrentPhaseTotalMs();
                long num2 = ((_engine.Phase != Phase.Rest) ? _engine.RemainingWorkMs : _engine.RemainingRestMs);
                if (num <= 0)
                {
                    num = 1L;
                }
                if (num2 < 0)
                {
                    num2 = 0L;
                }
                if (num2 > num)
                {
                    num2 = num;
                }
                ulong ullCompleted = (ulong)(num - num2);
                if (_engine.IsPaused)
                {
                    _taskbar.SetProgressState(Handle, 8);
                }
                else if (_engine.Phase == Phase.Rest)
                {
                    _taskbar.SetProgressState(Handle, 8);
                }
                else
                {
                    _taskbar.SetProgressState(Handle, 2);
                }
                _taskbar.SetProgressValue(Handle, ullCompleted, (ulong)num);
            }
            catch
            {
            }
        }

        private void UpdateTrayIcon(bool force)
        {
            if (_notify == null)
            {
                return;
            }
            int num;
            int num2;
            if (_engine.Phase == Phase.Work)
            {
                num = _engine.DisplayWorkMinutes;
                num2 = 1;
            }
            else if (_engine.Phase == Phase.Rest)
            {
                num = _engine.DisplayRestMinutes;
                num2 = 2;
            }
            else
            {
                num = _engine.DisplayWorkMinutes;
                num2 = 0;
            }
            if (_engine.IsPaused)
            {
                num2 += 10;
            }
            int num3 = num + num2 * 1000;
            if (!force && num3 == _lastTrayKey)
            {
                try
                {
                    _notify.Text = BuildStatusText(true);
                    return;
                }
                catch
                {
                    return;
                }
            }
            _lastTrayKey = num3;
            Color fg = ((num2 % 10 == 1) ? WorkLit : ((num2 % 10 == 2) ? RestLit : RepsLit));
            Icon val = TrayGlyph.Create(num, fg, _engine.IsPaused, OsInfo.IsTaskbarLight());
            Icon trayIcon = _trayIcon;
            _trayIcon = val;
            try
            {
                _notify.Icon = val;
                _notify.Text = BuildStatusText(true);
            }
            catch
            {
            }
            if (trayIcon == null || trayIcon == _appIcon)
            {
                return;
            }
            try
            {
                trayIcon.Dispose();
            }
            catch
            {
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // v1.4.4: this used to call itself instead of base -> infinite recursion / STACK OVERFLOW
            // on the first click in window mode. Must call the base implementation.
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = !_locked;
                _moved = false;
                _dragOffset = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_taskbarMode || _locked || !_dragging || e.Button != MouseButtons.Left) return;
            if (_inWindowDrag) return; // re-entrancy guard: setting Location must not nest moves
            int dx = e.X - _dragOffset.X;
            int dy = e.Y - _dragOffset.Y;
            if (!_moved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                _moved = true;
                AppLog.Info("window drag start at " + Location.X.ToString(CultureInfo.InvariantCulture)
                    + "," + Location.Y.ToString(CultureInfo.InvariantCulture));
            }
            if (!_moved || (dx == 0 && dy == 0)) return;
            _inWindowDrag = true;
            try
            {
                Location = new Point(Location.X + dx, Location.Y + dy);
            }
            finally
            {
                _inWindowDrag = false;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool moved = _moved;
                bool wasDragging = _dragging;
                _dragging = false;
                _moved = false;
                if (!moved)
                {
                    ToggleRun();
                }
                else if (wasDragging)
                {
                    AppLog.Info("window drag end at " + Location.X.ToString(CultureInfo.InvariantCulture)
                        + "," + Location.Y.ToString(CultureInfo.InvariantCulture));
                    SaveSettings();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Bg);
            Pen val = new Pen(Border);
            try
            {
                graphics.DrawRectangle(val, 0, 0, Width - 1, Height - 1);
            }
            finally
            {
                val.Dispose();
            }
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int num = Math.Max(8, width / 32);
            int num2 = Math.Max(6, height / 14);
            int num3 = (width - num * 2) / 3;
            int displayWorkMinutes = _engine.DisplayWorkMinutes;
            int displayRestMinutes = _engine.DisplayRestMinutes;
            int displayReps = _engine.DisplayReps;
            bool flag = _engine.Phase == Phase.Work;
            bool flag2 = _engine.Phase == Phase.Rest;
            DrawColumn(graphics, num, num2, num3, height - num2 * 2, displayWorkMinutes, flag ? WorkLit : WorkDim, WorkUnlit, flag);
            DrawColumn(graphics, num + num3, num2, num3, height - num2 * 2, displayRestMinutes, flag2 ? RestLit : RestDim, RestUnlit, flag2);
            DrawColumn(graphics, num + num3 * 2, num2, num3, height - num2 * 2, displayReps, RepsLit, RepsUnlit, false);
            if (_engine.IsFast)
            {
                SolidBrush val2 = new SolidBrush(Color.FromArgb(160, 80, 60));
                try
                {
                    graphics.FillRectangle(val2, 1, 1, 2, height - 2);
                }
                finally
                {
                    val2.Dispose();
                }
            }
        }

        private void DrawColumn(Graphics g, int x, int y, int colW, int colH, int value, Color lit, Color unlit, bool withBar)
        {
            string text = value.ToString("00", CultureInfo.InvariantCulture);
            int num = Math.Max(3, colH / 16);
            int num2 = Math.Max(28, colH - num - 6);
            int num3 = (int)Math.Max(16.0, Math.Min((colW - 6) / 2, (double)num2 * 0.58));
            int num4 = Math.Max(3, num3 / 8);
            int num5 = num3 * 2 + num4;
            int num6 = x + (colW - num5) / 2;
            int num7 = y + Math.Max(0, (colH - num - 4 - num2) / 2);
            if (text.Length < 2)
            {
                text = "0" + text;
            }
            if (text.Length > 2)
            {
                text = text.Substring(text.Length - 2);
            }
            SevenSeg.DrawDigit(g, num6, num7, num3, num2, text[0] - 48, lit, unlit);
            SevenSeg.DrawDigit(g, num6 + num3 + num4, num7, num3, num2, text[1] - 48, lit, unlit);
            int num8 = num7 + num2 + 3;
            int num9 = num5;
            int num10 = num6;
            SolidBrush val = new SolidBrush(Color.FromArgb(32, 32, 36));
            try
            {
                FillRoundRect(g, val, num10, num8, num9, num, (float)num / 2f);
            }
            finally
            {
                val.Dispose();
            }
            if (!withBar || (!_engine.IsRunning && !_engine.IsPaused))
            {
                return;
            }
            double phaseProgress = _engine.PhaseProgress;
            int num11 = (int)Math.Round((double)num9 * phaseProgress);
            if (num11 < 0)
            {
                num11 = 0;
            }
            if (num11 > num9)
            {
                num11 = num9;
            }
            if (num11 > 0)
            {
                SolidBrush val2 = new SolidBrush(lit);
                try
                {
                    FillRoundRect(g, val2, num10, num8, Math.Max(num11, num), num, (float)num / 2f);
                }
                finally
                {
                    val2.Dispose();
                }
            }
            if (Environment.TickCount / 500 % 2 == 0 && _engine.IsRunning)
            {
                int num12 = Math.Max(3, num);
                SolidBrush val3 = new SolidBrush(lit);
                try
                {
                    g.FillEllipse(val3, num10 + num9 + 5, num8 + (num - num12) / 2, num12, num12);
                }
                finally
                {
                    val3.Dispose();
                }
            }
        }

        private static void FillRoundRect(Graphics g, Brush b, int x, int y, int w, int h, float r)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }
            if (r * 2f > (float)h)
            {
                r = (float)h / 2f;
            }
            if (r * 2f > (float)w)
            {
                r = (float)w / 2f;
            }
            GraphicsPath val = new GraphicsPath();
            try
            {
                float num = r * 2f;
                if (num <= 0.5f)
                {
                    g.FillRectangle(b, x, y, w, h);
                    return;
                }
                val.AddArc((float)x, (float)y, num, num, 180f, 90f);
                val.AddArc((float)(x + w) - num, (float)y, num, num, 270f, 90f);
                val.AddArc((float)(x + w) - num, (float)(y + h) - num, num, num, 0f, 90f);
                val.AddArc((float)x, (float)(y + h) - num, num, num, 90f, 90f);
                val.CloseFigure();
                g.FillPath(b, val);
            }
            finally
            {
                val.Dispose();
            }
        }

        private void PlayBoundary(BoundaryKind kind)
        {
            try
            {
                byte[] buffer;
                if (kind == BoundaryKind.RestStarted)
                    buffer = Tone.RestChime();
                else if (kind == BoundaryKind.AllDone)
                    buffer = Tone.DoneChime();
                else
                    buffer = Tone.WorkChime();
                if (_soundStream != null)
                {
                    try
                    {
                        _player.Stop();
                    }
                    catch
                    {
                    }
                    _soundStream.Dispose();
                    _soundStream = null;
                }
                _soundStream = new MemoryStream(buffer, writable: false);
                _player.Stream = _soundStream;
                _player.Play();
            }
            catch
            {
                try
                {
                    SystemSounds.Beep.Play();
                }
                catch
                {
                }
            }
        }

        private static string SettingsDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pomodoro");
        }

        private static string SettingsPath()
        {
            return Path.Combine(SettingsDir(), "settings.ini");
        }

        private void LoadSettings()
        {
            int workMinutes = 25;
            int restMinutes = 5;
            int reps = 4;
            bool flag = false;
            bool flag2 = true;
            bool autostart = true;
            bool locked = true;
            bool flag5 = false;
            bool flag6 = false;
            int stripOffset = 0;
            StripZone stripZone = StripZone.Right;
            StripDisplayMode stripDisplayMode = StripDisplayMode.Overlay;
            bool flag7 = false;
            int? num = null;
            int? num2 = null;
            try
            {
                string path = SettingsPath();
                if (File.Exists(path))
                {
                    string[] array = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = 0; i < array.Length; i++)
                    {
                        string text = array[i].Trim();
                        if (text.Length == 0 || text[0] == '#' || text[0] == ';')
                        {
                            continue;
                        }
                        int num3 = text.IndexOf('=');
                        if (num3 <= 0)
                        {
                            continue;
                        }
                        string text2 = text.Substring(0, num3).Trim().ToLowerInvariant();
                        string text3 = text.Substring(num3 + 1).Trim();
                        if (text2 == "work" && int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                        {
                            workMinutes = result;
                            continue;
                        }
                        if (text2 == "rest" && int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                        {
                            restMinutes = result;
                            continue;
                        }
                        if (text2 == "reps" && int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                        {
                            reps = result;
                            continue;
                        }
                        switch (text2)
                        {
                        case "topmost":
                            flag = text3 == "1" || string.Equals(text3, "true", StringComparison.OrdinalIgnoreCase);
                            continue;
                        case "taskbar":
                            flag2 = text3 != "0" && !string.Equals(text3, "false", StringComparison.OrdinalIgnoreCase);
                            continue;
                        case "offset":
                            if (int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                            {
                                flag6 = true;
                                stripOffset = result;
                                continue;
                            }
                            break;
                        }
                        switch (text2)
                        {
                        case "zone":
                        {
                            flag5 = true;
                            int num4;
                            switch (text3.ToLowerInvariant())
                            {
                            default:
                                num4 = 0;
                                break;
                            case "left":
                            case "l":
                            case "1":
                                num4 = 1;
                                break;
                            }
                            stripZone = (StripZone)num4;
                            continue;
                        }
                        case "autostart":
                            autostart = text3 != "0" && !string.Equals(text3, "false", StringComparison.OrdinalIgnoreCase);
                            continue;
                        case "locked":
                            locked = text3 != "0" && !string.Equals(text3, "false", StringComparison.OrdinalIgnoreCase);
                            continue;
                        case "stripmode":
                            switch (text3.ToLowerInvariant())
                            {
                            case "overlay":
                            case "over":
                            case "top":
                            case "0":
                                stripDisplayMode = StripDisplayMode.Overlay;
                                break;
                            default:
                                AppLog.Info("settings: stripMode=" + text3 + " migrated to topmost overlay (v1.4.9)");
                                stripDisplayMode = StripDisplayMode.Overlay;
                                break;
                            }
                            continue;
                        case "stripmodemanual":
                            flag7 = text3 == "1" || string.Equals(text3, "true", StringComparison.OrdinalIgnoreCase);
                            continue;
                        case "x":
                            if (int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                            {
                                num = result;
                                continue;
                            }
                            break;
                        }
                        if (text2 == "y" && int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                        {
                            num2 = result;
                        }
                    }
                }
            }
            catch
            {
            }
            _engine.SetWorkMinutes(workMinutes);
            _engine.SetRestMinutes(restMinutes);
            _engine.SetReps(reps);
            TopMost = flag;
            _topmostItem.Checked = flag;
            _taskbarMode = flag2;
            _autostart = autostart;
            _locked = locked;
            _stripZone = (flag5 ? stripZone : StripZone.Right);
            _legacyRightOffset = !flag5 && flag6;
            _stripOffset = stripOffset;
            stripDisplayMode = StripDisplayMode.Overlay;
            _stripMode = stripDisplayMode;
            _stripModeManual = flag7;
            if (_autostartItem != null) _autostartItem.Checked = _autostart;
            if (_lockItem != null) _lockItem.Checked = _locked;
            SyncModeMenu();
            ShowInTaskbar = !flag2;
            if (num.HasValue && num2.HasValue)
            {
                Rectangle rect = new Rectangle(num.Value, num2.Value, Width, Height);
                if (IsOnAnyScreen(rect))
                {
                    Location = rect.Location;
                }
                else
                {
                    PlaceDefault();
                }
            }
            else
            {
                PlaceDefault();
            }
        }

        private void PlaceDefault()
        {
            Rectangle rectangle = ((Screen.PrimaryScreen != null) ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 800, 600));
            Location = new Point(rectangle.Right - Width - 16, rectangle.Bottom - Height - 16);
        }

        private static bool IsOnAnyScreen(Rectangle rect)
        {
            Rectangle rect2 = new Rectangle(rect.X + 8, rect.Y + 8, Math.Max(40, rect.Width - 16), Math.Max(40, rect.Height - 16));
            Screen[] allScreens = Screen.AllScreens;
            for (int i = 0; i < allScreens.Length; i++)
            {
                if (allScreens[i].WorkingArea.IntersectsWith(rect2))
                {
                    return true;
                }
            }
            return false;
        }

        private void SaveSettings()
        {
            if (!_settingsLoaded)
            {
                return;
            }
            try
            {
                string path = SettingsDir();
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("work=" + _engine.WorkMinutes.ToString(CultureInfo.InvariantCulture));
                stringBuilder.AppendLine("rest=" + _engine.RestMinutes.ToString(CultureInfo.InvariantCulture));
                stringBuilder.AppendLine("reps=" + _engine.RepsConfigured.ToString(CultureInfo.InvariantCulture));
                stringBuilder.AppendLine("topmost=" + (TopMost ? "1" : "0"));
                stringBuilder.AppendLine("taskbar=" + (_taskbarMode ? "1" : "0"));
                stringBuilder.AppendLine("autostart=" + (_autostart ? "1" : "0"));
                stringBuilder.AppendLine("locked=" + (_locked ? "1" : "0"));
                stringBuilder.AppendLine("stripMode=overlay");
                stringBuilder.AppendLine("stripModeManual=" + (_stripModeManual ? "1" : "0"));
                stringBuilder.AppendLine("zone=" + ((_stripZone == StripZone.Left) ? "left" : "right"));
                stringBuilder.AppendLine("offset=" + _stripOffset.ToString(CultureInfo.InvariantCulture));
                stringBuilder.AppendLine("x=" + Location.X.ToString(CultureInfo.InvariantCulture));
                stringBuilder.AppendLine("y=" + Location.Y.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(SettingsPath(), stringBuilder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private void OnMainClosing(object sender, FormClosingEventArgs e)
        {
            _stopWaiter = true;
            try
            {
                if (_showEvent != null)
                {
                    _showEvent.Set();
                }
            }
            catch
            {
            }
            CancelStripSafety();
            SaveSettings();
            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }
            catch
            {
            }
            try
            {
                UnhookForeground();
            }
            catch
            {
            }
            DestroyStrip();
            if (_notify != null)
            {
                try
                {
                    _notify.Visible = false;
                }
                catch
                {
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stopWaiter = true;
                try
                {
                    UnhookForeground();
                }
                catch
                {
                }
                if (_safety != null)
                {
                    _safety.Dispose();
                    _safety = null;
                }
                if (_timer != null)
                {
                    _timer.Dispose();
                }
                if (_menu != null)
                {
                    _menu.Dispose();
                }
                if (_notify != null)
                {
                    try
                    {
                        _notify.Visible = false;
                    }
                    catch
                    {
                    }
                    _notify.Dispose();
                    _notify = null;
                }
                if (_trayIcon != null)
                {
                    try
                    {
                        _trayIcon.Dispose();
                    }
                    catch
                    {
                    }
                    _trayIcon = null;
                }
                if (_appIcon != null)
                {
                    try
                    {
                        _appIcon.Dispose();
                    }
                    catch
                    {
                    }
                    _appIcon = null;
                }
                try
                {
                    _player.Stop();
                }
                catch
                {
                }
                _player.Dispose();
                if (_soundStream != null)
                {
                    _soundStream.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }


    /// <summary>
    /// Live digits on the taskbar. The HWND is bound once (embed or overlay);
    /// after that the host must dispose this form and create a new one.
    /// </summary>
    sealed class TaskbarStrip : Form
    {
        readonly MainForm _host;
        readonly TaskbarEmbedder _embedder = new TaskbarEmbedder();
        readonly System.Windows.Forms.Timer _sync;
        readonly ToolTip _tip;

        EmbedKind _kind;
        TaskbarSnapshot _snap;
        bool _verticalStack;
        bool _dragging;
        bool _moved;
        Point _dragStartCursor;
        int _dragStartPos;
        Size _desired;
        int _dragPos;
        bool _hiddenForFullscreen;
        bool _loggedPlace;
        int _syncTick;
        int _lastZLogAt;
        int _zOrderSuppressed;
        string _lastZLog = "";
        string _lastFsLog = "";
        int _lastFsLogAt;
        bool _paintProbed;
        bool _layeredPerPixel;
        bool _layeredOpaque;
        bool _ulwOkLogged;
        Bitmap _layerBmp;
        int _screenCheckStart;
        int _screenCheckLastAt;
        int _screenCheckFails;
        bool _screenCheckDone = true;
        int _lastParentCheckAt;
        bool _deferShow;
        bool _attachedOnce;
        readonly bool _embedIntent;
        readonly IntPtr _directParent;
        System.Windows.Forms.Timer _attachRetry;
        int _attachStart;
        int _attachAttempt;
        int _lastRaiseLogAt;
        int _raiseSuppressed;
        bool _attachReported;

        /// <summary>True for a strip created directly as WS_CHILD of the tray (no SetParent).</summary>
        public bool IsDirectChild { get { return _directParent != IntPtr.Zero; } }

        public EmbedKind Kind { get { return _kind; } }

        /// <summary>Embedded and its parent is alive and still ours.</summary>
        public bool IsHealthyEmbed
        {
            get
            {
                if (IsDisposed || !IsHandleCreated || !IsEmbedded) return false;
                IntPtr parent = _embedder.CurrentParent;
                return parent != IntPtr.Zero && Native.IsWindow(parent) && Native.GetParent(Handle) == parent;
            }
        }

        public bool AllowClose { get; set; }

        public bool IsPlacedOnTaskbar { get; private set; }

        public Rectangle LastScreenBounds { get; private set; }

        public TaskbarStrip(MainForm host) : this(host, false, IntPtr.Zero)
        {
        }

        public TaskbarStrip(MainForm host, bool embedIntent, IntPtr directParent)
        {
            _host = host;
            // v1.4.9: never created inside / re-parented into Explorer windows.
            _embedIntent = false;
            _directParent = IntPtr.Zero;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            UpdateStyles();
            ApplyThemeColors();
            Text = "Pomodoro";
            Size = new Size(140, 36);

            _tip = new ToolTip();
            _tip.ShowAlways = true;

            _sync = new System.Windows.Forms.Timer();
            _sync.Interval = 250;
            _sync.Tick += delegate { OnSync(); };
            _sync.Start();

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown)
                    e.Cancel = true;
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WsExToolwindow | Native.WsExNoActivate;
                cp.ExStyle &= ~Native.WsExAppwindow;
                if (_directParent != IntPtr.Zero)
                {
                    // Created straight into the tray: no SetParent at all.
                    cp.Parent = _directParent;
                    cp.Style = (cp.Style | Native.WsChild | Native.WsClipSiblings)
                        & ~(Native.WsPopup | Native.WsCaption | Native.WsThickframe
                            | Native.WsSysMenu | Native.WsVisible);
                    cp.ExStyle |= Native.WsExLayered;
                    cp.ExStyle &= ~Native.WsExTopmost;
                }
                else if (_embedIntent)
                {
                    // Will be embedded: hidden, never TOPMOST before SetParent.
                    cp.Style = ((cp.Style | Native.WsPopup) & ~Native.WsChild) & ~Native.WsVisible;
                    cp.ExStyle &= ~Native.WsExTopmost;
                }
                else
                {
                    cp.ExStyle |= Native.WsExTopmost;
                    cp.Style = (cp.Style | Native.WsPopup) & ~Native.WsChild;
                }
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        const int WmContextMenu = 0x007B;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmContextMenu)
            {
                try { _host.PrepareForegroundForMenu(); }
                catch { }
            }
            base.WndProc(ref m);
        }

        public void ShowInactive()
        {
            if (!IsHandleCreated) CreateHandle();
            ApplyThemeColors();
            Native.ShowWindow(Handle, Native.SwShownoactivate);
        }

        void ApplyThemeColors()
        {
            bool light = OsInfo.IsTaskbarLight();
            BackColor = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        }

        public void PrepareDestroy()
        {
            try { if (_sync != null) _sync.Stop(); } catch { }
            StopFastRaise();
            StopAttachRetry();
            _attachReported = true;
            DisposeLayerBitmap();
            try { _embedder.RestoreTaskList(); } catch { }
        }


        bool IsEmbedded
        {
            get { return _kind == EmbedKind.ClassicReBar || _kind == EmbedKind.ShellTray; }
        }

        /// <summary>
        /// Bind this freshly created HWND exactly once. Re-parenting a used window
        /// does not composite on Win11; callers must dispose and construct a new strip.
        /// Order: create hidden → styles → SetParent → layered → client position → show → HWND_TOP.
        /// </summary>
        public void AttachOnce(bool overlay)
        {
            if (_attachedOnce)
            {
                AppLog.Error("AttachOnce called twice on hwnd="
                    + (IsHandleCreated ? Handle.ToString("X") : "0")
                    + " — caller must create a new strip window");
                return;
            }
            _attachedOnce = true;
            _deferShow = true;
            if (!IsHandleCreated) CreateHandle();
            ApplyThemeColors();
            IsPlacedOnTaskbar = false;
            _loggedPlace = false;
            _paintProbed = false;
            _hiddenForFullscreen = false;
            _ulwOkLogged = false;
            _lastParentCheckAt = 0;
            _dragging = false;
            _moved = false;
            _snap = TaskbarFinder.Capture();
            _desired = MeasureSize(_snap);
            _verticalStack = _snap != null && _snap.IsValid && !_snap.IsHorizontal;

            AppLog.Info("attach-once overlay=" + overlay
                + " hwnd=" + Handle.ToString("X")
                + " win11=" + OsInfo.IsWindows11
                + " build=" + OsInfo.Build
                + " tray=" + ((_snap != null) ? _snap.Tray.ToString("X") : "0"));

            if (overlay)
            {
                _kind = EmbedKind.Overlay;
                TopMost = true;
                Native.RestorePopupStyle(Handle);
                // Top-level, topmost, tool, no-activate, per-pixel-alpha layered window.
                int ex = Native.GetWindowLong(Handle, Native.GwlExStyle);
                Native.SetWindowLong(Handle, Native.GwlExStyle,
                    ex | Native.WsExLayered | Native.WsExTopmost | Native.WsExToolwindow | Native.WsExNoActivate);
                DockOwnerToTaskbar("attach");
                _layeredPerPixel = true;
                _layeredOpaque = false;
                _screenCheckDone = true;
                _lastFrameKey = null;
                DisposeLayerBitmap();
                AppLog.Info("strip path=overlay (top-level layered topmost, owner-docked to Shell_TrayWnd, no WS_CHILD/SetParent)");
            }
            else
            {
                if (_directParent != IntPtr.Zero)
                {
                    bool adopted = _embedder.AdoptDirectChild(Handle, _directParent);
                    _kind = adopted ? EmbedKind.ShellTray : EmbedKind.Overlay;
                }
                else
                    _kind = _embedder.Attach(Handle, _snap, StripDisplayMode.Embed);
                if (_kind == EmbedKind.ClassicReBar && _snap != null && _snap.IsHorizontal)
                    _embedder.ShrinkTaskList(_snap, _desired.Width + 4, true);
                else if (_kind == EmbedKind.ClassicReBar)
                    _embedder.ShrinkTaskList(_snap, _desired.Height + 4, false);
                if (_kind == EmbedKind.Overlay)
                {
                    // Not visible yet. Keep this fresh HWND hidden and retry SetParent on it;
                    // the host keeps whatever strip is currently showing (make-before-break).
                    _kind = EmbedKind.None;
                    if (_directParent != IntPtr.Zero)
                    {
                        AppLog.Info("direct-child strip did not land in the tray");
                        ReportAttach(false);
                        return;
                    }
                    StartAttachRetry();
                    return;
                }
                AppLog.Info("strip path=" + _kind + (IsDirectChild ? " (direct child)" : ""));
            }

            FinishShow();
            if (!overlay) ReportAttach(true);
        }

        void StartAttachRetry()
        {
            _attachStart = Environment.TickCount;
            _attachAttempt = 0;
            if (_attachRetry == null)
            {
                _attachRetry = new System.Windows.Forms.Timer();
                _attachRetry.Interval = StripPolicy.SetParentRetryIntervalMs;
                _attachRetry.Tick += delegate { TickAttachRetry(); };
            }
            AppLog.Info("embed: retrying SetParent every "
                + StripPolicy.SetParentRetryIntervalMs.ToString(CultureInfo.InvariantCulture)
                + "ms for up to " + StripPolicy.SetParentRetryBudgetMs.ToString(CultureInfo.InvariantCulture)
                + "ms on hwnd=" + Handle.ToString("X"));
            _attachRetry.Start();
        }

        void StopAttachRetry()
        {
            try { if (_attachRetry != null) _attachRetry.Stop(); }
            catch { }
        }

        void TickAttachRetry()
        {
            if (IsDisposed || !IsHandleCreated || _attachReported)
            {
                StopAttachRetry();
                return;
            }
            _attachAttempt++;
            int elapsed = unchecked(Environment.TickCount - _attachStart);
            TaskbarSnapshot snap = TaskbarFinder.Capture();
            IntPtr tray = (snap != null && snap.IsValid) ? snap.Tray : IntPtr.Zero;
            bool verbose = _attachAttempt <= 2 || (_attachAttempt % 5) == 0;
            if (tray != IntPtr.Zero && _embedder.RetryShellTray(Handle, tray, _attachAttempt, verbose))
            {
                StopAttachRetry();
                _snap = snap;
                _desired = MeasureSize(snap);
                _verticalStack = !snap.IsHorizontal;
                _kind = EmbedKind.ShellTray;
                AppLog.Info("strip path=ShellTray after " + _attachAttempt.ToString(CultureInfo.InvariantCulture)
                    + " retries, " + elapsed.ToString(CultureInfo.InvariantCulture) + "ms");
                FinishShow();
                ReportAttach(true);
                return;
            }
            if (!StripPolicy.ShouldRetrySetParent(elapsed))
            {
                StopAttachRetry();
                AppLog.Info("embed: SetParent retries exhausted attempts="
                    + _attachAttempt.ToString(CultureInfo.InvariantCulture)
                    + " elapsed=" + elapsed.ToString(CultureInfo.InvariantCulture) + "ms");
                ReportAttach(false);
            }
        }

        void ReportAttach(bool ok)
        {
            if (_attachReported) return;
            _attachReported = true;
            _host.OnStripAttachResult(this, ok);
        }

        void FinishShow()
        {
            if (_kind == EmbedKind.ShellTray)
            {
                _layeredPerPixel = true;
                _layeredOpaque = false;
                _screenCheckDone = false;
                _screenCheckFails = 0;
                _screenCheckLastAt = 0;
                _screenCheckStart = Environment.TickCount;
            }

            Reposition(true);
            _deferShow = false;
            Native.ShowWindow(Handle, Native.SwShownoactivate);
            if (IsEmbedded)
                RaiseChildZOrder();
            Present();
        }

        public void Reposition(bool force)
        {
            if (!IsHandleCreated) return;
            var snap = TaskbarFinder.Capture();
            if (snap == null || !snap.IsValid)
            {
                IsPlacedOnTaskbar = false;
                if (Visible) Visible = false;
                if (force) AppLog.Error("reposition: taskbar not found");
                return;
            }

            if (_hiddenForFullscreen)
            {
                IsPlacedOnTaskbar = true; // still considered "placed" so the safety net stays quiet
                return;
            }

            if (!_deferShow && !Visible)
                Native.ShowWindow(Handle, Native.SwShownoactivate);

            if (_kind != EmbedKind.Overlay && _kind != EmbedKind.None && !_dragging)
            {
                if (!Native.IsWindow(_embedder.CurrentParent) || Native.GetParent(Handle) != _embedder.CurrentParent)
                {
                    AppLog.Info("reposition: parent gone or changed parent="
                        + _embedder.CurrentParent.ToString("X")
                        + " getParent=" + Native.GetParent(Handle).ToString("X")
                        + " parentAlive=" + Native.IsWindow(_embedder.CurrentParent));
                    _host.BeginTrayRecovery("parent-destroyed");
                    return;
                }
            }

            _snap = snap;
            _verticalStack = !snap.IsHorizontal;
            Size size = MeasureSize(snap);
            _desired = size;

            bool dragging = !_host.IsLocked && _dragging && _moved;
            StripZone usedZone;
            int usedOffset;
            bool clearLegacy;
            Rectangle screen = ComputeScreenBounds(
                snap, size, _host.StripZone, _host.StripOffset, _host.LegacyRightOffset,
                dragging, _dragPos,
                out usedZone, out usedOffset, out clearLegacy);
            if (!dragging && (usedZone != _host.StripZone || usedOffset != _host.StripOffset || clearLegacy))
                _host.SetStripPlacement(usedZone, usedOffset, clearLegacy);
            if (screen.IsEmpty)
            {
                IsPlacedOnTaskbar = false;
                AppLog.Error("reposition: empty strip bounds");
                return;
            }

            Point clientLoc = Point.Empty;
            IntPtr parent = IntPtr.Zero;
            if (IsEmbedded)
            {
                parent = _embedder.CurrentParent;
                if (parent == IntPtr.Zero) parent = snap.Tray;
                clientLoc = Native.ScreenToClientPoint(parent, screen.Location);
                uint flags = Native.SwpNoactivate | (_deferShow ? 0u : Native.SwpShowwindow);
                Native.SetWindowPos(Handle, Native.HwndTop,
                    clientLoc.X, clientLoc.Y, screen.Width, screen.Height, flags);
            }
            else
            {
                uint flags = Native.SwpNoactivate | Native.SwpNoownerzorder | (_deferShow ? 0u : Native.SwpShowwindow);
                if (ZOrderPaused) flags |= Native.SwpNozorder;
                Native.SetWindowPos(Handle, Native.HwndTopmost,
                    screen.X, screen.Y, screen.Width, screen.Height, flags);
                // Overlay only: if the tray still sits above us, climb back (rate-limited).
                if (!ZOrderPaused && !Native.IsWindowAbove(Handle, snap.Tray))
                    AssertTopmost("place", false);
            }

            // Size is set via SetWindowPos above. Assigning ClientSize on an
            // embedded child can re-apply a stale screen Location as client coords.

            bool moved = LastScreenBounds != screen;
            LastScreenBounds = screen;
            IsPlacedOnTaskbar = screen.Width >= 8 && screen.Height >= 8 && screen.IntersectsWith(snap.TrayRect);
            if (force || !_loggedPlace || moved)
            {
                _loggedPlace = true;
                Rectangle actual = Native.GetRect(Handle);
                bool vis = Native.IsWindowVisible(Handle);
                AppLog.Info("placed kind=" + _kind
                    + " parent=" + parent.ToString("X")
                    + (IsEmbedded ? (" client=" + clientLoc) : "")
                    + " tray=" + snap.TrayRect
                    + " notify=" + snap.NotifyRect
                    + " start=" + snap.StartRect
                    + " apps=" + snap.AppsRect
                    + " appsSrc=" + snap.AppsSource
                    + " widgets=" + snap.WidgetsRect
                    + " strip=" + screen
                    + " actual=" + actual
                    + " visible=" + vis
                    + " inTray=" + actual.IntersectsWith(snap.TrayRect)
                    + " zone=" + _host.StripZone
                    + " offset=" + _host.StripOffset
                    + " ok=" + IsPlacedOnTaskbar);
                if (IsEmbedded && !_paintProbed)
                {
                    _paintProbed = true;
                    AppLog.Info("embed paint " + Native.ProbePaint(Handle));
                }
            }

            if (_layeredPerPixel)
                Present();
        }

        public void OnForegroundChanged()
        {
            // An embedded child already lives in the taskbar band — do not hide
            // when Start/Search/the bar is clicked. Just keep it above the XAML island.
            if (IsEmbedded)
            {
                RaiseChildZOrder();
                return;
            }
            ApplyFullscreenAndZOrder("event", true);
            MaybeStartFastRaise();
        }

        // v1.4.9: while Start/Search (or the taskbar itself) is foreground, re-assert
        // HWND_TOPMOST every 50 ms on our own window only. Nothing is sent to Explorer.
        System.Windows.Forms.Timer _fastRaise;
        int _fastRaiseStart;
        int _fastRaiseTicks;
        int _fastRaiseAboveTicks;
        bool _fastRaiseLogged;
        IntPtr _fastRaiseCappedFg;

        static bool IsStartLikeForeground(Native.ForegroundState fg)
        {
            if (fg.ClassName == "Shell_TrayWnd" || fg.ClassName == "Shell_SecondaryTrayWnd") return true;
            string p = (fg.ProcessName ?? "").ToLowerInvariant();
            return p == "startmenuexperiencehost" || p == "searchhost" || p == "searchapp"
                || p == "shellexperiencehost" || p == "shellhost";
        }

        void MaybeStartFastRaise()
        {
            if (IsDisposed || !IsHandleCreated || IsEmbedded || _hiddenForFullscreen) return;
            if (ZOrderPaused) return;
            Native.ForegroundState fg = Native.ReadForeground();
            if (!IsStartLikeForeground(fg)) return;
            if (_fastRaise == null)
            {
                _fastRaise = new System.Windows.Forms.Timer();
                _fastRaise.Interval = 50;
                _fastRaise.Tick += delegate { TickFastRaise(); };
            }
            if (_fastRaise.Enabled) return;
            if (fg.Hwnd == _fastRaiseCappedFg) return; // already ran 60 s for this window
            _fastRaiseCappedFg = IntPtr.Zero;
            _fastRaiseStart = Environment.TickCount;
            _fastRaiseTicks = 0;
            _fastRaiseAboveTicks = 0;
            _fastRaiseLogged = false;
            RaiseTopmostNow();
            _fastRaise.Start();
        }

        void RaiseTopmostNow()
        {
            Native.SetWindowPos(Handle, Native.HwndTopmost, 0, 0, 0, 0,
                Native.SwpNomove | Native.SwpNosize | Native.SwpNoactivate | Native.SwpShowwindow
                | Native.SwpNoownerzorder | Native.SwpAsyncwindowpos);
        }

        // Owner-dock (as NetSpeedTray does): a top-level topmost window OWNED by
        // Shell_TrayWnd. Owned windows always sit above their owner, so when Start/Search
        // raises the taskbar the strip rises with it. No WS_CHILD, no SetParent.
        IntPtr _ownerTray;
        int _ownerCheckAt;

        void DockOwnerToTaskbar(string reason)
        {
            if (!IsHandleCreated) return;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            if (Native.GetWindow(Handle, Native.GwOwner) == tray) { _ownerTray = tray; return; }
            Native.SetOwner(Handle, tray);
            IntPtr now = Native.GetWindow(Handle, Native.GwOwner);
            bool ok = now == tray;
            _ownerTray = ok ? tray : IntPtr.Zero;
            if (ok) _ownerFails = 0; else _ownerFails++;
            if (ok != _ownerLastOk || _ownerFails == 1 || reason == "attach")
                AppLog.Info("owner-dock reason=" + reason + " tray=" + tray.ToString("X")
                    + " owner=" + now.ToString("X") + " ok=" + ok
                    + " bandStrip=" + Native.BandOf(Handle).ToString(CultureInfo.InvariantCulture)
                    + " bandTray=" + Native.BandOf(tray).ToString(CultureInfo.InvariantCulture));
            _ownerLastOk = ok;
            // Owner link lost and cannot be restored on this HWND (seen after Search moved
            // the taskbar to another band): build a fresh strip, at most once a minute.
            // Only when the taskbar is back in its normal band (1): while Start/Search keeps
            // it in a higher band a fresh window could not be docked either and would hide.
            if (!ok && _ownerFails >= 3 && reason != "attach" && reason != "menu-closed"
                && Native.BandOf(tray) <= 1 && !IsStartLikeForeground(Native.ReadForeground()))
            {
                int t = Environment.TickCount;
                if (_ownerRecreateAt == 0 || (uint)(t - _ownerRecreateAt) >= 60000u)
                {
                    _ownerRecreateAt = t == 0 ? 1 : t;
                    _ownerFails = 0;
                    AppLog.Info("owner-dock lost; recreating the strip window");
                    _host.RequestRecreateStrip("owner-lost", true);
                }
            }
        }

        /// <summary>Menu open: drop the cross-process owner so activation of our menu is not
        /// tied to Explorer's taskbar (the menu was closing instantly with AppFocusChange).</summary>
        public void OnMenuOpening()
        {
            StopFastRaise();
            if (!IsHandleCreated) return;
            if (Native.GetWindow(Handle, Native.GwOwner) != IntPtr.Zero)
            {
                Native.SetOwner(Handle, IntPtr.Zero);
                _ownerTray = IntPtr.Zero;
                _ownerUndockedForMenu = true;
            }
        }

        public void OnMenuClosed()
        {
            if (!IsHandleCreated) return;
            if (_ownerUndockedForMenu)
            {
                _ownerUndockedForMenu = false;
                DockOwnerToTaskbar("menu-closed");
            }
        }

        bool _ownerUndockedForMenu;
        int _ownerFails;
        bool _ownerLastOk = true;
        static int _ownerRecreateAt;

        /// <summary>False when the taskbar owner died (Explorer restart) and recovery started.</summary>
        bool CheckOwnerDock()
        {
            if (_ownerTray != IntPtr.Zero && !Native.IsWindow(_ownerTray))
            {
                _ownerTray = IntPtr.Zero;
                AppLog.Info("owner-dock: taskbar owner is gone; recreating strip");
                _host.BeginTrayRecovery("taskbar-owner-gone");
                return false;
            }
            int now = Environment.TickCount;
            if (!ZOrderPaused && (uint)(now - _ownerCheckAt) >= 2000u)
            {
                _ownerCheckAt = now;
                DockOwnerToTaskbar("recheck");
            }
            return true;
        }

        void StopFastRaise()
        {
            try { if (_fastRaise != null) _fastRaise.Stop(); } catch { }
        }

        void TickFastRaise()
        {
            if (IsDisposed || !IsHandleCreated || _hiddenForFullscreen) { StopFastRaise(); return; }
            if (ZOrderPaused) { StopFastRaise(); return; }
            Native.ForegroundState fg = Native.ReadForeground();
            bool startLike = IsStartLikeForeground(fg);
            RaiseTopmostNow();
            _fastRaiseTicks++;
            IntPtr tray = (_snap != null && _snap.Tray != IntPtr.Zero) ? _snap.Tray : IntPtr.Zero;
            bool above = tray == IntPtr.Zero || Native.IsWindowAbove(Handle, tray);
            if (above) _fastRaiseAboveTicks++;
            uint elapsed = (uint)(Environment.TickCount - _fastRaiseStart);
            if (!_fastRaiseLogged && (elapsed >= 600u || !startLike))
            {
                _fastRaiseLogged = true;
                AppLog.Info("start-raise fg=" + fg.ClassName + "/" + fg.ProcessName
                    + " above=" + above
                    + " aboveTicks=" + _fastRaiseAboveTicks.ToString(CultureInfo.InvariantCulture)
                    + "/" + _fastRaiseTicks.ToString(CultureInfo.InvariantCulture)
                    + " bandStrip=" + Native.BandOf(Handle).ToString(CultureInfo.InvariantCulture)
                    + " bandTray=" + (tray != IntPtr.Zero ? Native.BandOf(tray).ToString(CultureInfo.InvariantCulture) : "-")
                    + " bandFg=" + Native.BandOf(fg.Hwnd).ToString(CultureInfo.InvariantCulture));
            }
            // Keep going while Start/Search is open (cap 60 s), plus ~0.5 s after it closes.
            if (!startLike && _fastRaiseTicks > 10) StopFastRaise();
            else if (elapsed >= 60000u) { _fastRaiseCappedFg = fg.Hwnd; StopFastRaise(); }
        }

        void RaiseChildZOrder()
        {
            if (!IsHandleCreated || !IsEmbedded) return;
            Native.SetWindowPos(Handle, Native.HwndTop, 0, 0, 0, 0,
                Native.SwpNomove | Native.SwpNosize | Native.SwpNoactivate | Native.SwpShowwindow);
        }

        // Re-check fullscreen (Start/Search/Widgets must not count) and put the
        // overlay back above Shell_TrayWnd when it has slipped behind.
        void ApplyFullscreenAndZOrder(string reason, bool reposition)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (IsEmbedded) return;
            Native.ForegroundState fg = Native.ReadForeground();
            if (fg.IsFullscreenApp)
            {
                if (Visible || !_hiddenForFullscreen)
                {
                    _hiddenForFullscreen = true;
                    if (Visible) Visible = false;
                    LogFullscreen("hide", reason, fg);
                }
                return;
            }

            if (fg.IsShellChrome)
                LogFullscreen("skip-chrome", reason, fg);
            else if (fg.CoversMonitor)
                LogFullscreen("covers-but-not-app", reason, fg);

            if (_hiddenForFullscreen)
            {
                _hiddenForFullscreen = false;
                LogFullscreen("show", reason, fg);
                ShowInactive();
                Reposition(false);
            }
            else if (reposition && _kind != EmbedKind.ClassicReBar)
            {
                Reposition(false);
            }

            AssertTopmost(reason, false);
        }

        public void AssertTopmost()
        {
            AssertTopmost("call", false);
        }

        void AssertTopmost(string reason, bool force)
        {
            if (!IsHandleCreated || IsEmbedded) return;
            if (_hiddenForFullscreen) return;
            if (ZOrderPaused) return;

            IntPtr tray = IntPtr.Zero;
            if (_snap != null && _snap.Tray != IntPtr.Zero)
                tray = _snap.Tray;
            else
            {
                try { tray = Native.FindWindow("Shell_TrayWnd", null); }
                catch { }
            }

            bool wasAbove = tray == IntPtr.Zero || Native.IsWindowAbove(Handle, tray);
            if (wasAbove && !force) return;
            // Back off when the last re-assert did not help (e.g. after Start moved the
            // taskbar to another z-band): at most every 2 s instead of every 250 ms.
            int tnow = Environment.TickCount;
            if (!force && _lastAssertFailed && (uint)(tnow - _lastAssertAt) < 2000u) return;
            _lastAssertAt = tnow;

            uint flags = Native.SwpNomove | Native.SwpNosize | Native.SwpNoactivate | Native.SwpShowwindow
                | Native.SwpNoownerzorder | Native.SwpAsyncwindowpos;
            Native.SetWindowPos(Handle, Native.HwndTopmost, 0, 0, 0, 0, flags);
            // v1.4.9.3: no BringWindowToTop — it activates the strip and that is what
            // dismissed the context menu the instant it opened.

            bool nowAbove = tray == IntPtr.Zero || Native.IsWindowAbove(Handle, tray);
            _lastAssertFailed = !nowAbove;
            LogZOrder(reason, wasAbove, nowAbove, tray);
        }

        int _lastAssertAt;
        bool _lastAssertFailed;

        bool ZOrderPaused
        {
            get { return _host.IsMenuOpen || _dragging; }
        }

        void LogZOrder(string reason, bool wasAbove, bool nowAbove, IntPtr tray)
        {
            int now = Environment.TickCount;
            string line = "z-order assert reason=" + reason
                + " wasAbove=" + wasAbove
                + " nowAbove=" + nowAbove
                + " tray=" + tray.ToString("X");
            bool due = (uint)(now - _lastZLogAt) >= 10000u;
            if (!due)
            {
                _zOrderSuppressed++;
                _lastZLog = line;
                return;
            }
            if (_zOrderSuppressed > 0)
            {
                line += " suppressed=" + _zOrderSuppressed.ToString(CultureInfo.InvariantCulture);
                _zOrderSuppressed = 0;
            }
            _lastZLog = line;
            _lastZLogAt = now;
            AppLog.Info(line);
        }

        void LogFullscreen(string decision, string reason, Native.ForegroundState fg)
        {
            string line = "fullscreen " + decision
                + " reason=" + reason
                + " cls=" + fg.ClassName
                + " proc=" + fg.ProcessName
                + " rect=" + (fg.Rect.IsEmpty ? "empty" : fg.Rect.ToString())
                + " covers=" + fg.CoversMonitor
                + " chrome=" + fg.IsShellChrome
                + " hwnd=" + fg.Hwnd.ToString("X");
            bool changed = line != _lastFsLog;
            bool important = decision == "hide" || decision == "show";
            if (important)
            {
                _lastFsLog = line;
                _lastFsLogAt = Environment.TickCount;
                AppLog.Info(line);
                return;
            }
            bool due = (uint)(Environment.TickCount - _lastFsLogAt) >= 10000u;
            if (!changed || !due) return;
            _lastFsLog = line;
            _lastFsLogAt = Environment.TickCount;
            AppLog.Info(line);
        }

        bool VerifyEmbedParent()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            IntPtr parent = Native.GetParent(Handle);
            IntPtr expected = _embedder.CurrentParent;
            if (expected == IntPtr.Zero) expected = tray;
            bool alive = Native.IsWindow(expected);
            bool isTop = Native.IsTopChild(tray, Handle);
            EmbedHealthAction action = StripPolicy.DecideEmbedHealth(
                tray != IntPtr.Zero, alive, parent == tray && expected == tray, isTop);
            if (action == EmbedHealthAction.Recover)
            {
                AppLog.Info("parent check failed parent=" + parent.ToString("X")
                    + " tray=" + tray.ToString("X")
                    + " expected=" + expected.ToString("X")
                    + " parentAlive=" + alive);
                _host.BeginTrayRecovery("parent-mismatch");
                return false;
            }
            if (action == EmbedHealthAction.Raise)
            {
                // Start/Search reorder the tray's children (XAML island). The strip is a
                // working child; just ask for HWND_TOP and leave it alone — never recreate.
                Native.SetWindowPos(Handle, Native.HwndTop, 0, 0, 0, 0,
                    Native.SwpNomove | Native.SwpNosize | Native.SwpNoactivate);
                int now = Environment.TickCount;
                if (StripPolicy.IsLogDue(now, _lastRaiseLogAt, StripPolicy.RaiseLogIntervalMs))
                {
                    AppLog.Info("strip not top tray child; raised (no recreate) nowTop="
                        + Native.IsTopChild(tray, Handle)
                        + (_raiseSuppressed > 0 ? " suppressed=" + _raiseSuppressed.ToString(CultureInfo.InvariantCulture) : ""));
                    _lastRaiseLogAt = now == 0 ? 1 : now;
                    _raiseSuppressed = 0;
                }
                else
                    _raiseSuppressed++;
            }
            return true;
        }

        void OnSync()
        {
            if (IsDisposed || !_host.IsTaskbarMode || _kind == EmbedKind.None) return;
            if (_kind == EmbedKind.Overlay && !CheckOwnerDock()) return;
            _syncTick++;
            if (_dragging)
                goto Tip;
            else if (IsEmbedded)
            {
                if (_lastParentCheckAt == 0
                    || unchecked((int)(Environment.TickCount - _lastParentCheckAt)) >= 2000)
                {
                    _lastParentCheckAt = Environment.TickCount;
                    if (!VerifyEmbedParent())
                        goto Tip;
                }
                if ((_syncTick % 2) == 0)
                    Reposition(false);
                Present();
                RunScreenCheck();
            }
            else
            {
                // Overlay: recover from a false fullscreen hide and re-assert
                // z-order only when the strip is actually behind the taskbar.
                ApplyFullscreenAndZOrder("timer", false);
                MaybeStartFastRaise();
                if ((_syncTick % 2) == 0 && !_hiddenForFullscreen)
                    Reposition(false);
            }
            Tip:
            try
            {
                string tip = _host.BuildStatusText(false);
                if (_tip.GetToolTip(this) != tip)
                    _tip.SetToolTip(this, tip);
            }
            catch
            {
            }
        }

        Size MeasureSize(TaskbarSnapshot snap)
        {
            int thickness = 40;
            bool vertical = false;
            if (snap != null && snap.IsValid)
            {
                vertical = !snap.IsHorizontal;
                thickness = vertical ? snap.TrayRect.Width : snap.TrayRect.Height;
            }
            if (thickness < 24) thickness = 24;
            int pad = Math.Max(2, thickness / 24);
            int body = Math.Max(18, thickness - pad * 2);

            if (vertical)
            {
                int digitH = Math.Max(12, ((body - 8) / 3) * 2 / 3);
                int digitW = Math.Max(7, (int)(digitH * 0.62));
                int pairW = digitW * 2 + Math.Max(2, digitW / 8);
                int width = Math.Min(snap != null && snap.IsValid ? Math.Max(20, snap.TrayRect.Width - 2) : pairW + 6, pairW + 6);
                int height = digitH * 3 + 10;
                return new Size(Math.Max(22, width), height);
            }
            else
            {
                // Digits at ~2/3 of the old full-height size, so they don't fill the taskbar.
                // Strip height hugs the digits + a thin progress bar; ComputeScreenBounds centers it.
                int bar = 2;
                int fullDigit = Math.Max(16, body - bar - 2);
                int digitH = Math.Max(12, fullDigit * 2 / 3);
                int digitW = Math.Max(7, (int)(digitH * 0.58));
                int gap = Math.Max(2, digitW / 8);
                int groupGap = Math.Max(5, digitW / 2);
                int pairW = digitW * 2 + gap;
                int width = pairW * 3 + groupGap * 2 + 8;
                int height = Math.Max(16, digitH + bar + 6);
                return new Size(width, height);
            }
        }

        static Rectangle ComputeScreenBounds(
            TaskbarSnapshot snap, Size size,
            StripZone zone, int offset, bool legacyRight,
            bool dragging, int dragPos,
            out StripZone usedZone, out int usedOffset, out bool clearLegacy)
        {
            usedZone = zone;
            usedOffset = offset;
            clearLegacy = false;

            Rectangle tray = snap.TrayRect;
            Rectangle notify = StripLayout.EnsureNotify(snap);
            Rectangle start = snap.StartRect;
            bool horizontal = snap.IsHorizontal;
            int extent = horizontal ? size.Width : size.Height;
            int margin = StripLayout.Margin;

            int trayLead, trayTrail, notifyLead;
            StripLayout.Axis(snap, tray, out trayLead, out trayTrail);
            StripLayout.Axis(snap, notify, out notifyLead, out _);

            int leftMin, leftMax, rightMin, rightMax;
            bool leftOk = StripLayout.TryLeftZone(snap, out leftMin, out leftMax)
                && StripLayout.Fits(leftMin, leftMax, extent);
            bool rightOk = StripLayout.TryRightZone(snap, out rightMin, out rightMax)
                && StripLayout.Fits(rightMin, rightMax, extent);

            // Along-bar travel limits while dragging: the whole bar minus the notify area.
            int travelMin = trayLead + margin;
            int travelMax = notifyLead - margin;
            if (travelMax > trayTrail - margin) travelMax = trayTrail - margin;

            int pos;
            if (dragging)
            {
                pos = dragPos;
                if (travelMax - travelMin >= extent)
                    pos = StripLayout.ClampPos(pos, extent, travelMin, travelMax);
                else
                    pos = travelMax - extent;
            }
            else
            {
                if (legacyRight)
                {
                    // v1.3.0: pos = (notifyLead − margin − extent) + offset
                    pos = (notifyLead - margin - extent) + offset;
                    zone = StripZone.Right;
                    clearLegacy = true;
                }
                else
                {
                    pos = StripLayout.PosFromPlacement(zone, offset, trayLead, notifyLead);
                }

                bool zoneOk = (zone == StripZone.Left) ? leftOk : rightOk;
                if (!zoneOk)
                {
                    // This zone shrank too much (apps/widgets/start moved). Switch if the other fits.
                    StripZone other = (zone == StripZone.Left) ? StripZone.Right : StripZone.Left;
                    bool otherOk = (other == StripZone.Left) ? leftOk : rightOk;
                    if (otherOk)
                    {
                        AppLog.Info("zone " + zone + " too narrow (" +
                            ((zone == StripZone.Left) ? (leftMax - leftMin) : (rightMax - rightMin)) +
                            "px), moving to " + other);
                        zone = other;
                    }
                }

                if (zone == StripZone.Left && leftOk)
                    pos = StripLayout.ClampPos(pos, extent, leftMin, leftMax);
                else if (zone == StripZone.Right && rightOk)
                    pos = StripLayout.ClampPos(pos, extent, rightMin, rightMax);
                else if (rightOk)
                {
                    zone = StripZone.Right;
                    pos = StripLayout.ClampPos(pos, extent, rightMin, rightMax);
                }
                else if (leftOk)
                {
                    zone = StripZone.Left;
                    pos = StripLayout.ClampPos(pos, extent, leftMin, leftMax);
                }
                else
                {
                    // Neither zone fits: stay adjacent to the tray.
                    zone = StripZone.Right;
                    pos = notifyLead - margin - extent;
                }

                usedZone = zone;
                usedOffset = StripLayout.OffsetFromPos(zone, pos, trayLead, notifyLead);
            }

            if (horizontal)
            {
                int x = pos;
                Rectangle band = tray;
                if (!start.IsEmpty && start.Height >= size.Height && start.Height < tray.Height)
                    band = new Rectangle(tray.X, start.Y, tray.Width, start.Height);
                int y = band.Top + (band.Height - size.Height) / 2;
                if (y < tray.Top + 1) y = tray.Top + 1;
                if (y + size.Height > tray.Bottom - 1) y = tray.Bottom - size.Height - 1;
                return new Rectangle(x, y, size.Width, size.Height);
            }
            else
            {
                int y = pos;
                int x = tray.Left + (tray.Width - size.Width) / 2;
                if (x < tray.Left + 1) x = tray.Left + 1;
                if (x + size.Width > tray.Right - 1) x = tray.Right - size.Width - 1;
                return new Rectangle(x, y, size.Width, size.Height);
            }
        }

        // After a drag, snap the free position into the nearest zone that still fits the strip.
        void SnapAfterDrag()
        {
            if (_snap == null || !_snap.IsValid) return;
            Size size = _desired.IsEmpty ? MeasureSize(_snap) : _desired;
            bool horizontal = _snap.IsHorizontal;
            int extent = horizontal ? size.Width : size.Height;
            int pos = _dragPos;

            int leftMin, leftMax, rightMin, rightMax;
            bool leftOk = StripLayout.TryLeftZone(_snap, out leftMin, out leftMax)
                && StripLayout.Fits(leftMin, leftMax, extent);
            bool rightOk = StripLayout.TryRightZone(_snap, out rightMin, out rightMax)
                && StripLayout.Fits(rightMin, rightMax, extent);

            StripZone zone = StripLayout.NearestZone(
                pos, extent, leftOk, leftMin, leftMax, rightOk, rightMin, rightMax, _host.StripZone);

            if (zone == StripZone.Left && leftOk)
                pos = StripLayout.ClampPos(pos, extent, leftMin, leftMax);
            else if (zone == StripZone.Right && rightOk)
                pos = StripLayout.ClampPos(pos, extent, rightMin, rightMax);
            else if (rightOk)
            {
                zone = StripZone.Right;
                pos = StripLayout.ClampPos(pos, extent, rightMin, rightMax);
            }
            else if (leftOk)
            {
                zone = StripZone.Left;
                pos = StripLayout.ClampPos(pos, extent, leftMin, leftMax);
            }

            int trayLead, notifyLead;
            StripLayout.Axis(_snap, _snap.TrayRect, out trayLead, out _);
            StripLayout.Axis(_snap, StripLayout.EnsureNotify(_snap), out notifyLead, out _);
            int offset = StripLayout.OffsetFromPos(zone, pos, trayLead, notifyLead);
            _host.SetStripPlacement(zone, offset, true);
            AppLog.Info("snap zone=" + zone + " offset=" + offset
                + " pos=" + pos
                + " left=[" + leftMin + "," + leftMax + "] ok=" + leftOk
                + " right=[" + rightMin + "," + rightMax + "] ok=" + rightOk);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_host.IsLocked)
            {
                // Locked: click still toggles start/pause; never start a drag.
                _dragging = false;
                _moved = false;
                Capture = false;
                return;
            }
            _dragging = true;
            _moved = false;
            _dragStartCursor = Cursor.Position;
            bool vertical = _snap != null && _snap.IsValid && !_snap.IsHorizontal;
            _dragStartPos = vertical ? LastScreenBounds.Y : LastScreenBounds.X;
            if (LastScreenBounds.IsEmpty && _snap != null && _snap.IsValid)
                _dragStartPos = vertical ? _snap.TrayRect.Top : _snap.TrayRect.Left;
            _dragPos = _dragStartPos;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_host.IsLocked)
            {
                _dragging = false;
                _moved = false;
                return;
            }
            if (!_dragging || e.Button != MouseButtons.Left) return;
            int dx = Cursor.Position.X - _dragStartCursor.X;
            int dy = Cursor.Position.Y - _dragStartCursor.Y;
            if (!_moved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
                _moved = true;
            if (_moved)
            {
                int delta = (_snap != null && _snap.IsValid && !_snap.IsHorizontal) ? dy : dx;
                _dragPos = _dragStartPos + delta;
                // Keep the existing parent; only SetWindowPos in client coords.
                Reposition(false);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_host.IsLocked)
            {
                _dragging = false;
                _moved = false;
                _host.ToggleRun();
                return;
            }
            bool wasDrag = _moved;
            if (wasDrag)
                SnapAfterDrag();
            _dragging = false;
            _moved = false;
            if (!wasDrag)
                _host.ToggleRun();
            else
                Reposition(true);
        }

        public void Present()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_kind != EmbedKind.None && _layeredPerPixel)
            {
                if (PublishLayered())
                {
                    if (!_ulwOkLogged)
                    {
                        _ulwOkLogged = true;
                        AppLog.Info("UpdateLayeredWindow ok (top-level, hdcDst=NULL)");
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLog.Info("UpdateLayeredWindow failed err=" + err + "; trying LWA_ALPHA 255");
                    _layeredPerPixel = false;
                    _layeredOpaque = Native.SetLayeredOpaque(Handle);
                    if (_layeredOpaque)
                    {
                        ApplyThemeColors();
                        Invalidate();
                    }
                }
                return;
            }
            if (_kind != EmbedKind.None && Visible)
                Invalidate();
        }

        bool PublishLayered()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (!LastScreenBounds.IsEmpty)
            {
                w = LastScreenBounds.Width;
                h = LastScreenBounds.Height;
            }
            if (w < 8 || h < 8) return false;

            // v1.4.9: repaint/upload only when what is displayed actually changes.
            string key = BuildFrameKey(w, h);
            if (key == _lastFrameKey && _layerBmp != null)
                return true;

            if (_layerBmp == null || _layerBmp.Width != w || _layerBmp.Height != h)
            {
                DisposeLayerBitmap();
                // Straight alpha; premultiplied when uploaded to UpdateLayeredWindow.
                _layerBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }

            using (Graphics g = Graphics.FromImage(_layerBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: invisible but hit-testable (alpha 0 lets clicks fall through)
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                PaintStrip(g, w, h, true);
            }

            // Child layered windows want pptDst in parent client coords (or NULL).
            int x = 0, y = 0;
            IntPtr parent = IsEmbedded ? Native.GetParent(Handle) : IntPtr.Zero;
            Point screen = LastScreenBounds.Location;
            if (LastScreenBounds.IsEmpty)
            {
                Rectangle r = Native.GetRect(Handle);
                screen = r.Location;
            }
            if (parent != IntPtr.Zero)
            {
                Point client = Native.ScreenToClientPoint(parent, screen);
                x = client.X;
                y = client.Y;
            }
            else
            {
                x = screen.X;
                y = screen.Y;
            }
            bool ok = Native.UpdateLayeredFromBitmap(Handle, _layerBmp, x, y);
            _lastFrameKey = ok ? key : null;
            return ok;
        }

        string _lastFrameKey;

        string BuildFrameKey(int w, int h)
        {
            PomodoroEngine e = _host.Engine;
            bool running = e.IsRunning;
            int progress = (e.IsRunning || e.IsPaused) ? (int)Math.Round(e.PhaseProgress * w) : -1;
            int blink = running ? (Environment.TickCount / 500) % 2 : 0;
            return w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture)
                + "|" + (OsInfo.IsTaskbarLight() ? "L" : "D")
                + (_verticalStack ? "V" : "H")
                + "|" + ((int)e.Phase).ToString(CultureInfo.InvariantCulture)
                + "|" + e.DisplayWorkMinutes.ToString(CultureInfo.InvariantCulture)
                + "|" + e.DisplayRestMinutes.ToString(CultureInfo.InvariantCulture)
                + "|" + e.DisplayReps.ToString(CultureInfo.InvariantCulture)
                + "|" + (running ? "R" : "") + (e.IsPaused ? "P" : "") + (e.IsFast ? "F" : "")
                + "|" + progress.ToString(CultureInfo.InvariantCulture)
                + "|" + blink.ToString(CultureInfo.InvariantCulture);
        }

        void RunScreenCheck()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_screenCheckDone || _kind != EmbedKind.ShellTray) return;
            if (_hiddenForFullscreen || !Visible || _dragging) return;
            Native.ForegroundState fg = Native.ReadForeground();
            if (fg.IsShellChrome || fg.IsFullscreenApp)
            {
                _screenCheckStart = Environment.TickCount;
                _screenCheckFails = 0;
                _screenCheckLastAt = 0;
                return;
            }
            int now = Environment.TickCount;
            if (_screenCheckLastAt == 0)
            {
                if ((uint)(now - _screenCheckStart) < 1000u) return;
            }
            else if ((uint)(now - _screenCheckLastAt) < 1000u)
                return;
            _screenCheckLastAt = now;
            Rectangle r = LastScreenBounds.IsEmpty ? Native.GetRect(Handle) : LastScreenBounds;
            int hits, samples;
            bool ok = Native.ScreenCheckVisible(r, out hits, out samples);
            AppLog.Info("screen-check visible=" + ok
                + " hits=" + hits.ToString(CultureInfo.InvariantCulture)
                + "/" + samples.ToString(CultureInfo.InvariantCulture)
                + " fail=" + _screenCheckFails.ToString(CultureInfo.InvariantCulture)
                + " rect=" + r);
            if (ok)
            {
                _screenCheckDone = true;
                _screenCheckFails = 0;
                _host.ClearTemporaryOverlay();
                return;
            }
            _screenCheckFails++;
            if (_screenCheckFails >= 3 && (uint)(now - _screenCheckStart) >= 3000u)
            {
                _screenCheckDone = true;
                _host.FallbackToOverlay("screen-check failed 3/3 hits="
                    + hits.ToString(CultureInfo.InvariantCulture) + "/"
                    + samples.ToString(CultureInfo.InvariantCulture));
            }
        }

        void DisposeLayerBitmap()
        {
            if (_layerBmp != null)
            {
                try { _layerBmp.Dispose(); }
                catch { }
                _layerBmp = null;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            PaintStrip(g, ClientSize.Width, ClientSize.Height, false);
        }

        void PaintStrip(Graphics g, int w, int h, bool layered)
        {
            PomodoroEngine engine = _host.Engine;
            bool light = OsInfo.IsTaskbarLight();
            Color bg;
            Color workLit, restLit, repsLit, workUnlit, restUnlit, repsUnlit;
            if (layered)
            {
                // v1.4.6: fully transparent background (no plate). Unlit segments
                // stay at ~10% alpha so they don't read as a full 8 over the taskbar.
                bg = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
                workLit = engine.Phase == Phase.Work ? MainForm.WorkLit : Color.FromArgb(light ? 140 : 120, MainForm.WorkLit);
                restLit = engine.Phase == Phase.Rest ? MainForm.RestLit : Color.FromArgb(light ? 140 : 120, MainForm.RestLit);
                repsLit = MainForm.RepsLit;
                workUnlit = Color.FromArgb(light ? 14 : 11, MainForm.WorkLit);
                restUnlit = Color.FromArgb(light ? 14 : 11, MainForm.RestLit);
                repsUnlit = Color.FromArgb(light ? 14 : 11, MainForm.RepsLit);
            }
            else
            {
                bg = BackColor;
                g.Clear(bg);
                workLit = engine.Phase == Phase.Work ? MainForm.WorkLit : Blend(bg, MainForm.WorkLit, light ? 55 : 45);
                restLit = engine.Phase == Phase.Rest ? MainForm.RestLit : Blend(bg, MainForm.RestLit, light ? 55 : 45);
                repsLit = MainForm.RepsLit;
                workUnlit = Blend(bg, MainForm.WorkLit, light ? 14 : 16);
                restUnlit = Blend(bg, MainForm.RestLit, light ? 14 : 16);
                repsUnlit = Blend(bg, MainForm.RepsLit, light ? 14 : 16);
            }

            int work = engine.DisplayWorkMinutes;
            int rest = engine.DisplayRestMinutes;
            int reps = engine.DisplayReps;

            if (_verticalStack)
            {
                int rowH = (h - 4) / 3;
                PaintPair(g, 2, 2, w - 4, rowH - 1, work, workLit, workUnlit, engine.Phase == Phase.Work, engine, bg, layered);
                PaintPair(g, 2, 2 + rowH, w - 4, rowH - 1, rest, restLit, restUnlit, engine.Phase == Phase.Rest, engine, bg, layered);
                PaintPair(g, 2, 2 + rowH * 2, w - 4, rowH - 1, reps, repsLit, repsUnlit, false, engine, bg, layered);
            }
            else
            {
                int pad = 4;
                int inner = w - pad * 2;
                int groupGap = Math.Max(5, inner / 28);
                int colW = (inner - groupGap * 2) / 3;
                PaintPair(g, pad, 2, colW, h - 4, work, workLit, workUnlit, engine.Phase == Phase.Work, engine, bg, layered);
                PaintPair(g, pad + colW + groupGap, 2, colW, h - 4, rest, restLit, restUnlit, engine.Phase == Phase.Rest, engine, bg, layered);
                PaintPair(g, pad + (colW + groupGap) * 2, 2, colW, h - 4, reps, repsLit, repsUnlit, false, engine, bg, layered);
            }

            if (engine.IsFast)
            {
                using (var b = new SolidBrush(Color.FromArgb(180, 80, 50)))
                    g.FillRectangle(b, 0, 0, 2, h);
            }
        }

        static void PaintPair(Graphics g, int x, int y, int cw, int ch, int value,
            Color lit, Color unlit, bool active, PomodoroEngine engine, Color bg, bool layered)
        {
            string text = value.ToString("00", CultureInfo.InvariantCulture);
            if (text.Length > 2) text = text.Substring(text.Length - 2);

            int barH = ch >= 22 ? 2 : 0;
            int digitH = Math.Max(12, ch - barH - 3);
            int digitW = Math.Max(7, Math.Min((cw - 4) / 2, (int)(digitH * 0.62)));
            int gap = Math.Max(1, digitW / 8);
            int pairW = digitW * 2 + gap;
            int dx = x + (cw - pairW) / 2;
            int dy = y + Math.Max(0, (ch - barH - 2 - digitH) / 2);

            // Layered path: ~28% thinner segments, no glow (glow blooms on alpha).
            float thick = layered ? 0.72f : 1f;
            bool glow = !layered;
            SevenSeg.DrawDigit(g, dx, dy, digitW, digitH, text[0] - '0', lit, unlit, thick, glow);
            SevenSeg.DrawDigit(g, dx + digitW + gap, dy, digitW, digitH, text[1] - '0', lit, unlit, thick, glow);

            if (barH > 0)
            {
                int barY = dy + digitH + 1;
                int drawH = layered ? 1 : barH;
                if (layered)
                {
                    using (var track = new SolidBrush(Color.FromArgb(28, lit)))
                        g.FillRectangle(track, dx, barY, pairW, drawH);
                }
                else
                {
                    using (var track = new SolidBrush(Blend(bg, lit, 18)))
                        g.FillRectangle(track, dx, barY, pairW, barH);
                }
                if (active && (engine.IsRunning || engine.IsPaused))
                {
                    int fw = (int)Math.Round(pairW * engine.PhaseProgress);
                    if (fw > 0)
                    {
                        using (var fill = new SolidBrush(lit))
                            g.FillRectangle(fill, dx, barY, Math.Min(fw, pairW), drawH);
                    }
                    if (engine.IsRunning && ((Environment.TickCount / 500) % 2) == 0)
                    {
                        using (var db = new SolidBrush(lit))
                            g.FillRectangle(db, dx + pairW + 2, barY, barH, barH);
                    }
                }
            }
        }

        static Color Blend(Color a, Color b, int percentB)
        {
            if (percentB < 0) percentB = 0;
            if (percentB > 100) percentB = 100;
            int r = a.R + (b.R - a.R) * percentB / 100;
            int g = a.G + (b.G - a.G) * percentB / 100;
            int bl = a.B + (b.B - a.B) * percentB / 100;
            return Color.FromArgb(r, g, bl);
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PrepareDestroy();
                try { if (_sync != null) _sync.Dispose(); } catch { }
                try { if (_fastRaise != null) _fastRaise.Dispose(); } catch { }
                try { if (_tip != null) _tip.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    static class TrayGlyph
    {
        static readonly byte[][] Glyphs =
        {
            new byte[] { 7, 5, 5, 5, 7 },
            new byte[] { 2, 6, 2, 2, 7 },
            new byte[] { 7, 1, 7, 4, 7 },
            new byte[] { 7, 1, 7, 1, 7 },
            new byte[] { 5, 5, 7, 1, 1 },
            new byte[] { 7, 4, 7, 1, 7 },
            new byte[] { 7, 4, 7, 5, 7 },
            new byte[] { 7, 1, 1, 1, 1 },
            new byte[] { 7, 5, 7, 5, 7 },
            new byte[] { 7, 5, 7, 1, 7 }
        };

        public static Icon Create(int minutes, Color fg, bool paused, bool light)
        {
            if (minutes < 0) minutes = 0;
            if (minutes > 99) minutes = 99;
            int tens = minutes / 10;
            int ones = minutes % 10;
            Color bg = light ? Color.FromArgb(240, 240, 242) : Color.FromArgb(28, 28, 32);
            Color ink = paused ? Color.FromArgb(180, fg) : fg;

            using (var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.None;
                    g.PixelOffsetMode = PixelOffsetMode.None;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    using (var bb = new SolidBrush(bg))
                        g.FillEllipse(bb, 0, 0, 15, 15);
                    DrawGlyph(g, 3, 3, tens, ink);
                    DrawGlyph(g, 9, 3, ones, ink);
                    if (paused)
                    {
                        using (var p = new Pen(ink, 1))
                            g.DrawEllipse(p, 0, 0, 15, 15);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon clone = (Icon)tmp.Clone();
                tmp.Dispose();
                DestroyIcon(h);
                return clone;
            }
        }

        static void DrawGlyph(Graphics g, int x, int y, int digit, Color color)
        {
            if (digit < 0 || digit > 9) digit = 0;
            byte[] rows = Glyphs[digit];
            using (var b = new SolidBrush(color))
            {
                for (int row = 0; row < 5; row++)
                {
                    int bits = rows[row];
                    for (int col = 0; col < 3; col++)
                    {
                        if ((bits & (1 << (2 - col))) != 0)
                            g.FillRectangle(b, x + col * 2, y + row * 2, 2, 2);
                    }
                }
            }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }

    static class SevenSeg
    {
        static readonly int[] Map =
        {
            0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F
        };

        public static void DrawDigit(Graphics g, int x, int y, int w, int h, int digit, Color lit, Color unlit)
        {
            DrawDigit(g, x, y, w, h, digit, lit, unlit, 1f, true);
        }

        public static void DrawDigit(Graphics g, int x, int y, int w, int h, int digit, Color lit, Color unlit,
            float thicknessScale, bool glow)
        {
            if (digit < 0 || digit > 9) digit = 0;
            int mask = Map[digit];
            if (thicknessScale < 0.2f) thicknessScale = 0.2f;
            if (thicknessScale > 1.5f) thicknessScale = 1.5f;
            float tBase = Math.Max(2f, Math.Min(w * 0.18f, h * 0.12f));
            float t = Math.Max(1.5f, tBase * thicknessScale);
            // Keep the inter-segment gap from the unscaled thickness.
            float gap = Math.Max(1.0f, tBase * 0.22f);
            float x0 = x + 1;
            float y0 = y + 1;
            float x1 = x + w - 1;
            float y1 = y + h - 1;
            float mid = (y0 + y1) / 2f;

            DrawAll(g, unlit, 0x7F, x0, y0, x1, y1, mid, t, gap, false);
            DrawAll(g, lit, mask, x0, y0, x1, y1, mid, t, gap, glow);
        }

        static void DrawAll(Graphics g, Color color, int mask,
            float x0, float y0, float x1, float y1, float mid, float t, float gap, bool glow)
        {
            if (mask == 0) return;
            bool glowing = glow && color.R + color.G + color.B > 180;
            using (var pathA = HSeg(x0 + t * 0.55f, x1 - t * 0.55f, y0, t, gap))
            using (var pathG = HSeg(x0 + t * 0.55f, x1 - t * 0.55f, mid - t * 0.5f, t, gap))
            using (var pathD = HSeg(x0 + t * 0.55f, x1 - t * 0.55f, y1 - t, t, gap))
            using (var pathF = VSeg(x0, y0 + t * 0.55f, mid - gap * 0.5f, t, gap))
            using (var pathB = VSeg(x1 - t, y0 + t * 0.55f, mid - gap * 0.5f, t, gap))
            using (var pathE = VSeg(x0, mid + gap * 0.5f, y1 - t * 0.55f, t, gap))
            using (var pathC = VSeg(x1 - t, mid + gap * 0.5f, y1 - t * 0.55f, t, gap))
            {
                GraphicsPath[] segs = { pathA, pathB, pathC, pathD, pathE, pathF, pathG };
                for (int i = 0; i < 7; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    FillSeg(g, segs[i], color, glowing && t >= 3f);
                }
            }
        }

        static void FillSeg(Graphics g, GraphicsPath path, Color color, bool glow)
        {
            if (glow)
            {
                Color soft = Color.FromArgb(70, color);
                using (var gp = (GraphicsPath)path.Clone())
                using (var pen = new Pen(soft, 2.2f))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, gp);
                }
            }
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        static GraphicsPath HSeg(float left, float right, float top, float t, float gap)
        {
            float l = left + gap;
            float r = right - gap;
            float d = t * 0.5f;
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(l, top + d),
                new PointF(l + d, top),
                new PointF(r - d, top),
                new PointF(r, top + d),
                new PointF(r - d, top + t),
                new PointF(l + d, top + t)
            });
            return p;
        }

        static GraphicsPath VSeg(float left, float top, float bottom, float t, float gap)
        {
            float a = top + gap;
            float btm = bottom - gap;
            float d = t * 0.5f;
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(left + d, a),
                new PointF(left + t, a + d),
                new PointF(left + t, btm - d),
                new PointF(left + d, btm),
                new PointF(left, btm - d),
                new PointF(left, a + d)
            });
            return p;
        }
    }

    static class Tone
    {
        const int SampleRate = 22050;

        public static byte[] WorkChime()
        {
            return Render(new[] { 784.0, 1046.5 }, new[] { 90, 140 }, new[] { 40, 55 }, 40);
        }

        public static byte[] RestChime()
        {
            return Render(new[] { 523.25, 392.0 }, new[] { 110, 180 }, new[] { 35, 50 }, 35);
        }

        public static byte[] DoneChime()
        {
            return Render(new[] { 523.25, 659.25, 783.99 }, new[] { 110, 110, 220 }, new[] { 32, 32, 48 }, 40);
        }

        static byte[] Render(double[] freqs, int[] durMs, int[] amps, int gapMs)
        {
            int totalMs = 0;
            for (int i = 0; i < durMs.Length; i++)
            {
                totalMs += durMs[i];
                if (i < durMs.Length - 1) totalMs += gapMs;
            }
            int samples = Math.Max(1, SampleRate * totalMs / 1000);
            var pcm = new short[samples];
            int cursor = 0;
            for (int i = 0; i < freqs.Length; i++)
            {
                int n = Math.Max(1, SampleRate * durMs[i] / 1000);
                WriteTone(pcm, cursor, n, freqs[i], amps[i]);
                cursor += n;
                if (i < freqs.Length - 1)
                    cursor += Math.Max(0, SampleRate * gapMs / 1000);
            }
            return ToWav(pcm);
        }

        static void WriteTone(short[] dst, int offset, int count, double freq, int amplitude)
        {
            if (count <= 0) return;
            double fadeIn = Math.Min(count * 0.12, SampleRate * 0.008);
            double fadeOut = Math.Min(count * 0.35, SampleRate * 0.05);
            if (fadeIn < 2) fadeIn = 2;
            if (fadeOut < 4) fadeOut = 4;
            for (int i = 0; i < count; i++)
            {
                int idx = offset + i;
                if (idx < 0 || idx >= dst.Length) break;
                double env = 1.0;
                if (i < fadeIn) env = i / fadeIn;
                if (i > count - fadeOut) env = Math.Min(env, (count - i) / fadeOut);
                double s = Math.Sin(2.0 * Math.PI * freq * i / SampleRate);
                s = s * 0.88 + Math.Sin(4.0 * Math.PI * freq * i / SampleRate) * 0.12;
                int v = (int)Math.Round(s * env * amplitude * 200.0);
                if (v > short.MaxValue) v = short.MaxValue;
                if (v < short.MinValue) v = short.MinValue;
                dst[idx] = (short)v;
            }
        }

        static byte[] ToWav(short[] pcm)
        {
            int dataSize = pcm.Length * 2;
            var ms = new System.IO.MemoryStream(44 + dataSize);
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(SampleRate);
            bw.Write(SampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            for (int i = 0; i < pcm.Length; i++)
                bw.Write(pcm[i]);
            bw.Flush();
            return ms.ToArray();
        }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarListCoclass
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled)
                e.TextColor = Color.FromArgb(230, 230, 234);
            else
                e.TextColor = Color.FromArgb(120, 120, 126);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Color.FromArgb(58, 58, 64)))
                e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Color.FromArgb(26, 26, 30)))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0)
                r = new Rectangle(e.Item.ContentRectangle.X, e.Item.ContentRectangle.Y, 16, 16);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.FromArgb(255, 110, 70), 1.8f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                int x = r.X, y = r.Y, w = r.Width, h = r.Height;
                g.DrawLines(p, new[]
                {
                    new Point(x + w * 2 / 10, y + h / 2),
                    new Point(x + w * 4 / 10, y + h * 72 / 100),
                    new Point(x + w * 82 / 100, y + h * 28 / 100)
                });
            }
        }
    }

    sealed class DarkColorTable : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(26, 26, 30);
        static readonly Color Sel = Color.FromArgb(52, 52, 60);
        static readonly Color Line = Color.FromArgb(58, 58, 64);

        public override Color MenuItemSelected { get { return Sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
        public override Color MenuItemPressedGradientBegin { get { return Sel; } }
        public override Color MenuItemPressedGradientMiddle { get { return Sel; } }
        public override Color MenuItemPressedGradientEnd { get { return Sel; } }
        public override Color MenuItemBorder { get { return Line; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color ImageMarginRevealedGradientBegin { get { return Bg; } }
        public override Color ImageMarginRevealedGradientMiddle { get { return Bg; } }
        public override Color ImageMarginRevealedGradientEnd { get { return Bg; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Line; } }
        public override Color MenuStripGradientBegin { get { return Bg; } }
        public override Color MenuStripGradientEnd { get { return Bg; } }
        public override Color ToolStripBorder { get { return Line; } }
        public override Color ToolStripContentPanelGradientBegin { get { return Bg; } }
        public override Color ToolStripContentPanelGradientEnd { get { return Bg; } }
        public override Color CheckBackground { get { return Bg; } }
        public override Color CheckSelectedBackground { get { return Sel; } }
        public override Color CheckPressedBackground { get { return Sel; } }
        public override Color ButtonSelectedBorder { get { return Line; } }
        public override Color ButtonSelectedGradientBegin { get { return Sel; } }
        public override Color ButtonSelectedGradientEnd { get { return Sel; } }
        public override Color ButtonSelectedHighlight { get { return Sel; } }
        public override Color ButtonSelectedHighlightBorder { get { return Line; } }
    }
#endif
}
