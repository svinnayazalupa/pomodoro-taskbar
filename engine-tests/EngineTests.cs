using System;
using System.IO;
using System.Text.RegularExpressions;
using PomodoroApp;

static class EngineTests
{
    static int _failed;
    static int _passed;

    static void Main()
    {
        StartShowsWorkAndCeilMinutes();
        WorkEndResetsWorkAndStartsRest();
        RestEndDecrementsRepsAndStartsWork();
        LastRestStopsAndResets();
        PauseFreezesRemaining();
        ResetRestoresConfigured();
        DisplayCeilAtBoundaries();
        FastModeUsesSeconds();
        SettingWorkRestartRemaining();
        ToggleFromIdleStartsWork();
        OverridesNeverCallThemselves();
        StripHealthNeverRecreatesForZOrder();
        SetParentRetryBudget();
        AttachFailureIsMakeBeforeBreak();
        StartOrSearchBlocksOverlayRetry();
        LogRateLimit();
        SourceHasNoZOrderRecreate();

        Console.WriteLine("passed={0} failed={1}", _passed, _failed);
        if (_failed > 0)
            Environment.Exit(1);
    }

    static void StartShowsWorkAndCeilMinutes()
    {
        long now = 1_000_000;
        var e = new PomodoroEngine(false, () => now);
        Check("idle phase", e.Phase == Phase.Idle);
        Check("idle work display", e.DisplayWorkMinutes == 25);
        Check("idle rest display", e.DisplayRestMinutes == 5);
        Check("idle reps", e.DisplayReps == 4);
        var ev = e.ToggleStartPause();
        Check("start event", ev == BoundaryKind.WorkStarted);
        Check("phase work", e.Phase == Phase.Work);
        Check("running", e.IsRunning);
        Check("display 25 at start", e.DisplayWorkMinutes == 25);
        Check("seconds 25:00", e.RemainingSecondsCeil == 25 * 60);
        now += 1; // 1 ms later still 25 and 25:00
        e.Update();
        Check("display still 25 after 1ms", e.DisplayWorkMinutes == 25);
        Check("seconds still 1500 after 1ms", e.RemainingSecondsCeil == 25 * 60);
        now += 999; // total 1000 ms
        e.Update();
        Check("after 1s title is 24:59", e.RemainingSecondsCeil == 25 * 60 - 1);
        Check("after 1s display still 25", e.DisplayWorkMinutes == 25);
        now += 59 * 1000; // total 60s
        e.Update();
        Check("after 60s display 24", e.DisplayWorkMinutes == 24);
        Check("rest still full during work", e.DisplayRestMinutes == 5);
        Check("reps unchanged during work", e.DisplayReps == 4);
    }

    static void WorkEndResetsWorkAndStartsRest()
    {
        long now = 5_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.SetWorkMinutes(25);
        e.SetRestMinutes(5);
        e.SetReps(4);
        e.ToggleStartPause();
        now += 25L * 60 * 1000;
        var ev = e.Update();
        Check("work end -> rest started", ev == BoundaryKind.RestStarted);
        Check("phase rest", e.Phase == Phase.Rest);
        Check("work counter reset", e.DisplayWorkMinutes == 25);
        Check("rest shows 5", e.DisplayRestMinutes == 5);
        Check("reps not yet decremented", e.DisplayReps == 4);
        Check("rest remaining 5:00", e.RemainingSecondsCeil == 5 * 60);
    }

    static void RestEndDecrementsRepsAndStartsWork()
    {
        long now = 7_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.SetReps(4);
        e.ToggleStartPause();
        now += 25L * 60 * 1000;
        Check("to rest", e.Update() == BoundaryKind.RestStarted);
        now += 5L * 60 * 1000;
        var ev = e.Update();
        Check("rest end -> work started", ev == BoundaryKind.WorkStarted);
        Check("phase work", e.Phase == Phase.Work);
        Check("reps decremented", e.DisplayReps == 3);
        Check("work full again", e.DisplayWorkMinutes == 25);
        Check("rest full again", e.DisplayRestMinutes == 5);
    }

    static void LastRestStopsAndResets()
    {
        long now = 9_000_000;
        var e = new PomodoroEngine(true, () => now); // fast: minute = 1 second
        e.SetWorkMinutes(2);
        e.SetRestMinutes(1);
        e.SetReps(2);

        e.ToggleStartPause();
        now += 2000;
        Check("cycle1 rest", e.Update() == BoundaryKind.RestStarted);
        now += 1000;
        Check("cycle2 work", e.Update() == BoundaryKind.WorkStarted);
        Check("reps 1", e.DisplayReps == 1);
        now += 2000;
        Check("cycle2 rest", e.Update() == BoundaryKind.RestStarted);
        now += 1000;
        var ev = e.Update();
        Check("all done", ev == BoundaryKind.AllDone);
        Check("idle after done", e.Phase == Phase.Idle);
        Check("not running", !e.IsRunning && !e.IsPaused);
        Check("reset work", e.DisplayWorkMinutes == 2);
        Check("reset rest", e.DisplayRestMinutes == 1);
        Check("reset reps", e.DisplayReps == 2);
    }

    static void PauseFreezesRemaining()
    {
        long now = 11_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.ToggleStartPause();
        now += 10 * 1000; // 10 seconds
        e.Update();
        int sec = e.RemainingSecondsCeil;
        Check("about 24:50", sec == 25 * 60 - 10);
        e.ToggleStartPause();
        Check("paused", e.IsPaused && !e.IsRunning);
        now += 60 * 1000;
        e.Update();
        Check("frozen while paused", e.RemainingSecondsCeil == sec);
        Check("display still 25", e.DisplayWorkMinutes == 25);
        e.ToggleStartPause();
        Check("resumed", e.IsRunning);
        now += 1000;
        e.Update();
        Check("resumes counting", e.RemainingSecondsCeil == sec - 1);
    }

    static void ResetRestoresConfigured()
    {
        long now = 13_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.SetWorkMinutes(40);
        e.SetRestMinutes(10);
        e.SetReps(8);
        e.ToggleStartPause();
        now += 3 * 60 * 1000;
        e.Update();
        e.Reset();
        Check("idle after reset", e.Phase == Phase.Idle);
        Check("work 40", e.DisplayWorkMinutes == 40);
        Check("rest 10", e.DisplayRestMinutes == 10);
        Check("reps 8", e.DisplayReps == 8);
        Check("not running", !e.IsRunning);
    }

    static void DisplayCeilAtBoundaries()
    {
        long now = 15_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.ToggleStartPause();
        // 1 ms into the last minute of work: remaining = 60_000 - ? wait
        // After 24 minutes, remaining = 60s, display should be 1
        now += 24L * 60 * 1000;
        e.Update();
        Check("at 1:00 display 1", e.DisplayWorkMinutes == 1);
        Check("title 01:00", e.RemainingSecondsCeil == 60);
        now += 59 * 1000; // 1 second left
        e.Update();
        Check("at 00:01 display 1", e.DisplayWorkMinutes == 1);
        Check("title 00:01", e.RemainingSecondsCeil == 1);
        now += 1000;
        var ev = e.Update();
        Check("ends after last second", ev == BoundaryKind.RestStarted);
        Check("work reset to 25 not 00", e.DisplayWorkMinutes == 25);
    }

    static void FastModeUsesSeconds()
    {
        long now = 17_000_000;
        var e = new PomodoroEngine(true, () => now);
        Check("unit is 1s", e.UnitMs == 1000);
        e.SetWorkMinutes(25);
        e.ToggleStartPause();
        Check("fast start 25", e.DisplayWorkMinutes == 25);
        Check("title is 00:25", e.RemainingSecondsCeil == 25);
        now += 1000;
        e.Update();
        Check("fast after 1s display 24", e.DisplayWorkMinutes == 24);
        Check("title 00:24", e.RemainingSecondsCeil == 24);
        now += 24 * 1000;
        var ev = e.Update();
        Check("fast 25s ends work", ev == BoundaryKind.RestStarted);
    }

    static void SettingWorkRestartRemaining()
    {
        long now = 19_000_000;
        var e = new PomodoroEngine(false, () => now);
        e.ToggleStartPause();
        now += 5 * 60 * 1000;
        e.Update();
        e.SetWorkMinutes(10);
        Check("new work 10", e.DisplayWorkMinutes == 10);
        Check("title 10:00", e.RemainingSecondsCeil == 10 * 60);
        Check("still work", e.Phase == Phase.Work);
    }

    static void ToggleFromIdleStartsWork()
    {
        long now = 21_000_000;
        var e = new PomodoroEngine(false, () => now);
        Check("start returns work", e.ToggleStartPause() == BoundaryKind.WorkStarted);
        Check("pause returns null", e.ToggleStartPause() == null);
        Check("is paused", e.IsPaused);
        Check("resume returns null", e.ToggleStartPause() == null);
        Check("running again", e.IsRunning);
    }

    // v1.4.4 regression: MainForm.OnMouseDown/Move/Up called themselves instead of
    // base.*, which overflowed the stack on the first click in window mode.
    static int CountSelfCallingOverrides(string src, out string names)
    {
        names = "";
        int count = 0;
        var head = new Regex(@"override\s+[\w<>\.]+\s+(\w+)\s*\(");
        foreach (Match m in head.Matches(src))
        {
            int open = src.IndexOf('{', m.Index);
            if (open < 0) continue;
            int depth = 0, i = open;
            for (; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) break;
            }
            string body = src.Substring(open + 1, Math.Max(0, i - open - 1));
            string name = m.Groups[1].Value;
            if (Regex.IsMatch(body, @"(?<![\w\.])" + name + @"\s*\("))
            {
                count++;
                names += name + " ";
            }
        }
        return count;
    }

    static void OverridesNeverCallThemselves()
    {
        string path = null;
        string dir = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && dir != null; up++)
        {
            string cand = Path.Combine(dir, "Pomodoro.cs");
            if (File.Exists(cand)) { path = cand; break; }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        Check("found Pomodoro.cs for source scan", path != null);
        if (path == null) return;
        string src = File.ReadAllText(path);
        string names;
        int n = CountSelfCallingOverrides(src, out names);
        if (n > 0) Console.WriteLine("  self-calling overrides: " + names);
        Check("no override calls itself (infinite recursion)", n == 0);
        string bad = "class X { protected override void OnMouseDown(MouseEventArgs e) { OnMouseDown(e); } }";
        string good = "class X { protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); } }";
        Check("scanner detects self-call", CountSelfCallingOverrides(bad, out names) == 1);
        Check("scanner accepts base call", CountSelfCallingOverrides(good, out names) == 0);
        Check("window drag has re-entrancy guard", src.Contains("_inWindowDrag"));
    }


    // v1.4.5: opening Start reorders Shell_TrayWnd children; that must only re-raise.
    static void StripHealthNeverRecreatesForZOrder()
    {
        Check("healthy top child -> none",
            StripPolicy.DecideEmbedHealth(true, true, true, true) == EmbedHealthAction.None);
        Check("not top child -> raise, not recreate",
            StripPolicy.DecideEmbedHealth(true, true, true, false) == EmbedHealthAction.Raise);
        Check("tray missing -> recover",
            StripPolicy.DecideEmbedHealth(false, true, true, true) == EmbedHealthAction.Recover);
        Check("parent dead -> recover",
            StripPolicy.DecideEmbedHealth(true, false, true, false) == EmbedHealthAction.Recover);
        Check("parent changed -> recover",
            StripPolicy.DecideEmbedHealth(true, true, false, true) == EmbedHealthAction.Recover);
    }

    static void SetParentRetryBudget()
    {
        Check("retry at 0ms", StripPolicy.ShouldRetrySetParent(0));
        Check("retry at 2850ms", StripPolicy.ShouldRetrySetParent(2850));
        Check("stop at 3000ms", !StripPolicy.ShouldRetrySetParent(3000));
        Check("stop on negative (tick wrap)", !StripPolicy.ShouldRetrySetParent(-5));
        Check("interval 100..200ms", StripPolicy.SetParentRetryIntervalMs >= 100
            && StripPolicy.SetParentRetryIntervalMs <= 200);
        Check("~20 attempts fit the budget",
            StripPolicy.SetParentRetryBudgetMs / StripPolicy.SetParentRetryIntervalMs >= 15);
    }

    static void AttachFailureIsMakeBeforeBreak()
    {
        Check("healthy old embed is kept (before direct try)",
            StripPolicy.DecideAttachFailure(false, true, false) == AttachFailAction.KeepExistingEmbed);
        Check("healthy old embed is kept (after direct try)",
            StripPolicy.DecideAttachFailure(true, true, false) == AttachFailAction.KeepExistingEmbed);
        Check("nothing showing -> try direct child first",
            StripPolicy.DecideAttachFailure(false, false, false) == AttachFailAction.TryDirectChild);
        Check("overlay showing -> still try direct child first",
            StripPolicy.DecideAttachFailure(false, false, true) == AttachFailAction.TryDirectChild);
        Check("direct failed, overlay showing -> keep overlay",
            StripPolicy.DecideAttachFailure(true, false, true) == AttachFailAction.KeepExistingOverlay);
        Check("direct failed, nothing showing -> overlay",
            StripPolicy.DecideAttachFailure(true, false, false) == AttachFailAction.FallbackOverlay);
        Check("healthy embed survives an embed recreate",
            StripPolicy.ShouldSwapAfterNewEmbeds(true, false));
        Check("explicit overlay switch replaces at once",
            !StripPolicy.ShouldSwapAfterNewEmbeds(true, true));
        Check("broken embed is not kept", !StripPolicy.ShouldSwapAfterNewEmbeds(false, false));
    }

    static void StartOrSearchBlocksOverlayRetry()
    {
        Check("xaml island is start", StripPolicy.IsStartOrSearchForeground("XamlExplorerHostIslandWindow", ""));
        Check("core window is start", StripPolicy.IsStartOrSearchForeground("Windows.UI.Core.CoreWindow", ""));
        Check("StartMenuExperienceHost", StripPolicy.IsStartOrSearchForeground("X", "StartMenuExperienceHost"));
        Check("SearchHost", StripPolicy.IsStartOrSearchForeground("", "SearchHost"));
        Check("notepad is not start", !StripPolicy.IsStartOrSearchForeground("Notepad", "notepad"));
        Check("tray click is not start", !StripPolicy.IsStartOrSearchForeground("Shell_TrayWnd", "explorer"));
        Check("retry when due and start closed",
            StripPolicy.ShouldStartOverlayRetry(true, false, true, true, true, false));
        Check("no retry while start open",
            !StripPolicy.ShouldStartOverlayRetry(true, false, true, true, true, true));
        Check("no retry when not temporary",
            !StripPolicy.ShouldStartOverlayRetry(true, false, false, true, true, false));
        Check("no retry when recovering",
            !StripPolicy.ShouldStartOverlayRetry(true, true, true, true, true, false));
        Check("no retry in overlay mode",
            !StripPolicy.ShouldStartOverlayRetry(true, false, true, false, true, false));
        Check("no retry before due",
            !StripPolicy.ShouldStartOverlayRetry(true, false, true, true, false, false));
    }

    static void LogRateLimit()
    {
        Check("first log due", StripPolicy.IsLogDue(5000, 0, 30000));
        Check("not due after 1s", !StripPolicy.IsLogDue(6000, 5000, 30000));
        Check("due after 30s", StripPolicy.IsLogDue(35000, 5000, 30000));
        Check("due across tick wrap", StripPolicy.IsLogDue(int.MinValue + 40000, int.MaxValue - 1000, 30000));
    }

    static void SourceHasNoZOrderRecreate()
    {
        string path = null;
        string dir = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && dir != null; up++)
        {
            string cand = Path.Combine(dir, "Pomodoro.cs");
            if (File.Exists(cand)) { path = cand; break; }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        if (path == null) { Check("found Pomodoro.cs", false); return; }
        string src = File.ReadAllText(path);
        Check("no 're-raise-failed' recreate", !src.Contains("\"re-raise-failed\""));
        Check("health check uses StripPolicy", src.Contains("StripPolicy.DecideEmbedHealth("));
        // v1.4.9: never couple to Explorer's input queue / UI thread.
        Check("no SetParent at all (no embedding into Shell_TrayWnd)", !src.Contains("SetParent(IntPtr hWndChild") && !src.Contains("Native.SetParent("));
        Check("no AttachThreadInput", !src.Contains("AttachThreadInput("));
        Check("no SHAppBarMessage (SendMessage to taskbar)", !src.Contains("SHAppBarMessage("));
        Check("no MoveWindow on Explorer windows", !src.Contains("MoveWindow("));
        Check("no UI Automation over the taskbar", !src.Contains("AutomationElement") && !src.Contains("System.Windows.Automation"));
        Check("no plain SendMessage to other windows", !System.Text.RegularExpressions.Regex.IsMatch(src, @"\bSendMessage\s*\("));
        Check("strip never created as a tray child", src.Contains("_directParent = IntPtr.Zero;") && src.Contains("_embedIntent = false;"));
        Check("recreate always uses top-level overlay", src.Contains("bool useOverlay = true;"));
        Check("default stripMode is overlay", src.Contains("StripDisplayMode _stripMode = StripDisplayMode.Overlay;"));
        Check("stripMode=embed migrates", src.Contains("migrated to topmost overlay"));
        Check("WantsEmbed is always false", src.Contains("public bool WantsEmbed { get { return false; } }"));
        Check("overlay is per-pixel layered", src.Contains("ex | Native.WsExLayered | Native.WsExTopmost | Native.WsExToolwindow | Native.WsExNoActivate"));
        Check("layered frame skipped when unchanged", src.Contains("if (key == _lastFrameKey && _layerBmp != null)"));
        Check("background alpha 1 kept", src.Contains("Color.FromArgb(1, 0, 0, 0)"));
        Check("unlit alpha 14/11 kept", src.Contains("Color.FromArgb(light ? 14 : 11, MainForm.WorkLit)"));
        Check("thin segments 0.72 kept", src.Contains("layered ? 0.72f : 1f"));
        Check("10-minute resource log", src.Contains("600000u") && src.Contains("GetGuiResources(self, 0)"));
        Check("fast topmost re-assert while Start/Search open", src.Contains("_fastRaise.Interval = 50;") && src.Contains("Native.SwpAsyncwindowpos"));
        Check("winevent hook covers OBJECT_SHOW..REORDER out of context", src.Contains("Native.EventObjectShow, Native.EventObjectReorder") && src.Contains("Native.WinEventOutOfContext"));
        Check("owner-dock to Shell_TrayWnd via GWLP_HWNDPARENT (not WS_CHILD)", src.Contains("Native.SetOwner(Handle, tray);") && src.Contains("SetWindowLongPtrW"));
        Check("owner-dock survives Explorer restart", src.Contains("\"taskbar-owner-gone\""));
        Check("re-seat uses SWP_NOOWNERZORDER", src.Contains("Native.SwpNoownerzorder | Native.SwpAsyncwindowpos"));
        Check("no BringWindowToTop on the strip (activation dismissed the menu)", !src.Contains("BringWindowToTop(Handle)"));
        Check("z-order work paused while menu open", src.Contains("get { return _host.IsMenuOpen || _dragging; }") && src.Contains("_menuOpen = true;") && src.Contains("_menuOpen = false;"));
        Check("failed topmost re-assert backs off", src.Contains("_lastAssertFailed && (uint)(tnow - _lastAssertAt) < 2000u"));
        Check("lost owner-dock recreates strip", src.Contains("\"owner-lost\""));
        Check("menu takes foreground on Opened", src.Contains("_menu.Opened += MenuOnOpened;") && src.Contains("Native.SetForegroundWindow(_menu.Handle)"));
        Check("owner dropped while menu open, restored after", src.Contains("public void OnMenuOpening()") && src.Contains("DockOwnerToTaskbar(\"menu-closed\")"));
        Check("no strip recreate while taskbar in a raised band", src.Contains("Native.BandOf(tray) <= 1 && !IsStartLikeForeground"));
        Check("start log says v1.5.0", src.Contains("\"start v1.5.0 "));
    }
    static void Check(string name, bool ok)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ok  {0}", name);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL  {0}", name);
        }
    }
}
