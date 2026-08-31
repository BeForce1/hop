// hop - press a hotkey, every clickable thing gets a letter, type it to click.
// A Windows take on Homerow (macOS, $30). One file, no dependencies, no installer.
//
//   build:  .\build.ps1
//   run:    hop.exe              Ctrl+Alt+Space to label, Esc to cancel
//           hop.exe --dump 3     wait 3s, then print what it finds in the focused window

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Hop {

static class Native {
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, System.Text.StringBuilder n, int max);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    public const uint VK_SPACE = 0x20;
    public const uint LEFTDOWN = 0x2, LEFTUP = 0x4;
    public const int WM_HOTKEY = 0x312;
}

class Target {
    public AutomationElement El;
    public System.Windows.Rect Box;
    public string Label = "";
    public string Kind = "";
    public string Name = "";
}

static class Finder {
    // Custom is deliberately absent: in Electron apps it matches thousands of nodes
    // and buries the real controls. ponytail: add it behind a flag if something is missing.
    static readonly ControlType[] Kinds = {
        ControlType.Button, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.MenuItem, ControlType.TabItem, ControlType.ListItem, ControlType.Edit,
        ControlType.ComboBox, ControlType.SplitButton, ControlType.TreeItem
    };

    // Scrollbar arrows and troughs are ControlType.Button parented to a ScrollBar.
    // The trough is the worst offender: one 32x1697 "target" that scrolls, not clicks.
    static readonly System.Text.RegularExpressions.Regex ScrollPart =
        new System.Text.RegularExpressions.Regex(
            @"^(Vertical|Horizontal)(Small|Large)(Increase|Decrease)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Home row first, but w/a/s/d are deliberately absent: they steer the green
    // selection instead. 22 single-key labels, 484 two-key ones.
    public const string Alpha = "fghjklqertyuiopzxcvbnm";
    public const int Cap = 22 * 22;

    // Chrome, Edge and every Electron app use this window class. Measured on a cold
    // Chrome: the FIRST UIA query returns browser chrome only - 13 elements, zero page
    // content - and is itself what makes the renderer build its tree. ~250ms later the
    // same query returns 33 elements and 20 links.
    static bool IsChromium(IntPtr hwnd) {
        var sb = new System.Text.StringBuilder(256);
        try { Native.GetClassName(hwnd, sb, sb.Capacity); } catch { return false; }
        return sb.ToString().StartsWith("Chrome_WidgetWin");
    }

    // Trigger on an empty document, not on a low count. A count threshold misfires on
    // small windows that are already awake - one measured at 19 targets, fully loaded -
    // and would tax every press. ControlType.Document is present either way, so it is
    // the document being *empty* that means "not built yet".
    static bool PageLooksEmpty(IntPtr hwnd, List<Target> ts) {
        AutomationElement root = null;
        try { root = AutomationElement.FromHandle(hwnd); } catch { return false; }
        if (root == null) return false;
        AutomationElement doc = null;
        try {
            doc = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        } catch { }
        if (doc == null) return false;               // not a page; nothing to wait for
        System.Windows.Rect r;
        try { r = doc.Current.BoundingRectangle; } catch { return false; }
        foreach (var t in ts)
            if (r.Contains(t.Box.Left + t.Box.Width / 2, t.Box.Top + t.Box.Height / 2)) return false;
        return true;
    }

    public const int WakeMs = 250;

    public static List<Target> Find(IntPtr hwnd, int cap, int budgetMs, out long ms) {
        var sw = Stopwatch.StartNew();
        var found = Scan(hwnd, cap, budgetMs, sw);

        // ponytail: one retry, not a loop. Two passes is all the measurement showed was
        // needed, and a loop here would stall the overlay on a genuinely blank page.
        if (IsChromium(hwnd) && PageLooksEmpty(hwnd, found)) {
            System.Threading.Thread.Sleep(WakeMs);
            // Keep whichever pass found more. The two scans share one stopwatch, so a
            // first pass that ate the whole budget would leave the retry with none and
            // return nothing - never let the retry make the answer worse.
            var again = Scan(hwnd, cap, budgetMs, sw);
            if (again.Count > found.Count) found = again;
        }

        Label(found);
        ms = sw.ElapsedMilliseconds;
        return found;
    }

    static List<Target> Scan(IntPtr hwnd, int cap, int budgetMs, Stopwatch sw) {
        var found = new List<Target>();
        AutomationElement root = null;
        try { root = AutomationElement.FromHandle(hwnd); } catch { }
        if (root == null) return found;

        Condition kinds = new OrCondition(Kinds.Select(k =>
            (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, k)).ToArray());
        var cond = new AndCondition(kinds,
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));

        // Without a CacheRequest every property read is a separate cross-process
        // call and a busy window takes seconds. This is the whole performance story.
        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.AutomationIdProperty);

        using (cache.Activate()) {
            AutomationElementCollection all = null;
            try { all = root.FindAll(TreeScope.Descendants, cond); } catch { }
            if (all == null) return found;

            var seen = new HashSet<string>();
            foreach (AutomationElement e in all) {
                // budget is measured from the top of Find, so a retry cannot double it
                if (sw.ElapsedMilliseconds > budgetMs || found.Count >= cap) break;
                System.Windows.Rect r;
                try { r = e.Cached.BoundingRectangle; } catch { continue; }
                if (r.Width < 2 || r.Height < 2 || double.IsInfinity(r.Width)) continue;

                // Overlapping duplicates are common (a link wrapping its own text).
                string key = ((int)r.Left) + "," + ((int)r.Top) + "," + ((int)r.Width) + "," + ((int)r.Height);
                if (!seen.Add(key)) continue;

                // ponytail: AutomationId, not the parent chain - a TreeWalker call per
                // element is the cross-process cost the CacheRequest exists to avoid.
                // These ids are XAML/WPF and not localised. Add ClassName=="RepeatButton"
                // if a Win32 scrollbar ever slips through.
                string aid = "";
                try { aid = e.Cached.AutomationId ?? ""; } catch { }
                if (ScrollPart.IsMatch(aid)) continue;

                string name = "", kind = "";
                try { name = e.Cached.Name ?? ""; } catch { }
                try { kind = e.Cached.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                found.Add(new Target { El = e, Box = r, Name = name, Kind = kind });
            }
        }

        return found.OrderBy(t => (int)t.Box.Top / 24).ThenBy(t => t.Box.Left).ToList();
    }

    static void Label(List<Target> ts) {
        bool single = ts.Count <= Alpha.Length;
        for (int i = 0; i < ts.Count; i++)
            ts[i].Label = single
                ? Alpha[i].ToString()
                : "" + Alpha[i / Alpha.Length] + Alpha[i % Alpha.Length];
    }

    public static void Activate(Target t, IntPtr owner) {
        Native.SetForegroundWindow(owner);
        object p;
        try {
            if (t.Kind == "Edit") { t.El.SetFocus(); return; }
            if (t.El.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return; }
            if (t.El.TryGetCurrentPattern(TogglePattern.Pattern, out p)) { ((TogglePattern)p).Toggle(); return; }
            if (t.El.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p)) {
                var ec = (ExpandCollapsePattern)p;
                if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
                return;
            }
            if (t.El.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return; }
        } catch { }

        // ponytail: last resort, a real click at the centre. Works when nothing else does.
        int x = (int)(t.Box.Left + t.Box.Width / 2), y = (int)(t.Box.Top + t.Box.Height / 2);
        Native.SetCursorPos(x, y);
        Native.mouse_event(Native.LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Native.mouse_event(Native.LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}

class Overlay : Form {
    readonly List<Target> all;
    readonly IntPtr owner;
    string typed = "";

    int sel = -1;   // index into `all`; -1 until a direction key is pressed

    static readonly Font Face = new Font("Consolas", 11f, FontStyle.Bold);
    static readonly Color Fill = Color.FromArgb(255, 232, 92);
    static readonly Color Pick = Color.FromArgb(88, 222, 120);
    static readonly Color Dim = Color.FromArgb(150, 150, 150);

    // One brush each, not one per target per repaint. 484 labels repainting on every
    // keystroke made that thousands of throwaway GDI+ objects for three colours.
    static readonly Brush FillB = new SolidBrush(Fill);
    static readonly Brush PickB = new SolidBrush(Pick);
    static readonly Brush DimB = new SolidBrush(Dim);

    static double Cx(Target t) { return t.Box.Left + t.Box.Width / 2; }
    static double Cy(Target t) { return t.Box.Top + t.Box.Height / 2; }

    // Nearest target in the requested direction, heavily preferring the same lane.
    void Steer(int dx, int dy) {
        var pool = all.Where(t => t.Label.StartsWith(typed)).ToList();
        if (pool.Count == 0) return;
        if (sel < 0 || !pool.Contains(all[sel])) { sel = all.IndexOf(pool[0]); Invalidate(); return; }

        var cur = all[sel];
        Target best = null;
        double bestCost = double.MaxValue;
        foreach (var t in pool) {
            if (t == cur) continue;
            double ax = Cx(t) - Cx(cur), ay = Cy(t) - Cy(cur);
            double along = ax * dx + ay * dy;
            if (along <= 1) continue;                          // wrong way
            double perp = Math.Abs(ax * dy - ay * dx);
            double cost = along + perp * 3;                    // 3x penalty for drifting sideways
            if (cost < bestCost) { bestCost = cost; best = t; }
        }
        if (best != null) { sel = all.IndexOf(best); Invalidate(); }
    }

    public Overlay(List<Target> targets, IntPtr ownerWindow) {
        all = targets; owner = ownerWindow;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;   // solid label boxes only, so no AA fringing
        KeyPreview = true;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        for (int i = 0; i < all.Count; i++) {
            var t = all[i];
            bool live = t.Label.StartsWith(typed);
            bool picked = (i == sel);
            string text = t.Label.ToUpper();
            var size = g.MeasureString(text, Face);
            int w = (int)size.Width + 8, h = (int)size.Height + 2;
            int x = (int)t.Box.Left - Bounds.Left, y = (int)t.Box.Top - Bounds.Top;

            // outline the whole control, not just its label, so you can see what you're on
            if (picked) {
                using (var pen = new Pen(Pick, 2f))
                    g.DrawRectangle(pen, x, y, (int)t.Box.Width, (int)t.Box.Height);
            }

            g.FillRectangle(picked ? PickB : (live ? FillB : DimB), x, y, w, h);
            g.DrawRectangle(Pens.Black, x, y, w, h);
            // typed prefix in grey, the part still to press in black
            if (live && typed.Length > 0) {
                var pre = text.Substring(0, typed.Length);
                g.DrawString(pre, Face, Brushes.DimGray, x + 4, y + 1);
                g.DrawString(text.Substring(typed.Length), Face, Brushes.Black,
                    x + 4 + g.MeasureString(pre, Face).Width - 3, y + 1);
            } else {
                g.DrawString(text, Face, Brushes.Black, x + 4, y + 1);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        switch (e.KeyCode) {
            case Keys.Escape: Close(); return;
            case Keys.Back:
                if (typed.Length > 0) { typed = typed.Substring(0, typed.Length - 1); Invalidate(); }
                e.Handled = true; return;

            case Keys.W: case Keys.Up:    Steer(0, -1); e.Handled = true; return;
            case Keys.S: case Keys.Down:  Steer(0, 1);  e.Handled = true; return;
            case Keys.A: case Keys.Left:  Steer(-1, 0); e.Handled = true; return;
            case Keys.D: case Keys.Right: Steer(1, 0);  e.Handled = true; return;

            case Keys.Enter: case Keys.Space:
                e.Handled = true;
                if (sel >= 0) { var t = all[sel]; Close(); Finder.Activate(t, owner); }
                return;
        }

        char c = char.ToLower((char)e.KeyValue);
        if (Finder.Alpha.IndexOf(c) < 0) return;

        typed += c;
        sel = -1;                     // typing and steering are separate modes
        var hit = all.FirstOrDefault(t => t.Label == typed);
        if (hit != null) { Close(); Finder.Activate(hit, owner); return; }
        if (!all.Any(t => t.Label.StartsWith(typed))) typed = "";   // dead end, start over
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e) { Close(); }
}

class Combo {
    public readonly uint Mods; public readonly uint Vk; public readonly string Name;
    public Combo(uint m, uint v, string n) { Mods = m; Vk = v; Name = n; }
}

class Host : Form {
    const int HotkeyId = 0xB0B;
    NotifyIcon tray;
    bool showing;
    string hotkey = "?";

    // Ctrl+Alt+Space is the nice one but it does get taken. Fall through rather than
    // failing: whichever registers first wins, and the tray says which.
    static readonly Combo[] Candidates = {
        new Combo(Native.MOD_CONTROL | Native.MOD_ALT,   Native.VK_SPACE, "Ctrl+Alt+Space"),
        new Combo(Native.MOD_CONTROL | Native.MOD_ALT,   0x48,            "Ctrl+Alt+H"),
        new Combo(Native.MOD_CONTROL | Native.MOD_ALT,   0xBA,            "Ctrl+Alt+;"),
        new Combo(Native.MOD_CONTROL | Native.MOD_SHIFT, Native.VK_SPACE, "Ctrl+Shift+Space"),
        new Combo(Native.MOD_ALT     | Native.MOD_SHIFT, 0x48,            "Alt+Shift+H"),
    };

    public Host() {
        // A hidden window is still a window: it needs a handle for RegisterHotKey.
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        Size = new Size(1, 1);

        tray = new NotifyIcon {
            Icon = SystemIcons.Application,
            Text = "hop - Ctrl+Alt+Space",
            Visible = true,
            ContextMenu = new ContextMenu(new[] { new MenuItem("Exit", (s, e) => Close()) })
        };

        // ponytail: no MessageBox. A modal dialog from a tray app blocks the whole
        // thing and there is nothing the user can usefully do about it anyway.
        foreach (var c in Candidates)
            if (Native.RegisterHotKey(Handle, HotkeyId, c.Mods, c.Vk)) { hotkey = c.Name; break; }

        if (hotkey == "?") {
            tray.Text = "hop - no hotkey available";
            tray.ShowBalloonTip(6000, "hop", "Every candidate hotkey is already taken.", ToolTipIcon.Error);
        } else {
            tray.Text = "hop - " + hotkey;
            tray.ShowBalloonTip(3000, "hop ready", hotkey + " to label the focused window", ToolTipIcon.Info);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId && !showing) Fire();
        base.WndProc(ref m);
    }

    void Fire() {
        var target = Native.GetForegroundWindow();
        long ms;
        var found = Finder.Find(target, Finder.Cap, 1500, out ms);
        if (found.Count == 0) { tray.Text = "hop " + hotkey + " - nothing found (" + ms + "ms)"; return; }
        tray.Text = "hop " + hotkey + " - " + found.Count + " targets (" + ms + "ms)";

        showing = true;
        using (var o = new Overlay(found, target)) { o.ShowDialog(); }
        showing = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        Native.UnregisterHotKey(Handle, HotkeyId);
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}

static class Program {
    [STAThread]
    static void Main(string[] args) {
        Native.SetProcessDPIAware();   // UIA reports physical pixels; without this labels land wrong

        if (args.Length > 0 && args[0] == "--dump") {
            int wait = args.Length > 1 ? int.Parse(args[1]) : 0;
            if (wait > 0) {
                Console.WriteLine("focus the window you want, waiting " + wait + "s...");
                System.Threading.Thread.Sleep(wait * 1000);
            }
            long ms;
            var hwnd = Native.GetForegroundWindow();
            var found = Finder.Find(hwnd, Finder.Cap, 5000, out ms);
            Console.WriteLine("hwnd " + hwnd + " -> " + found.Count + " clickable in " + ms + "ms");
            foreach (var t in found)
                Console.WriteLine("  {0,-3} {1,-12} [{2},{3} {4}x{5}] {6}",
                    t.Label, t.Kind, (int)t.Box.Left, (int)t.Box.Top,
                    (int)t.Box.Width, (int)t.Box.Height,
                    t.Name.Length > 48 ? t.Name.Substring(0, 48) : t.Name);
            return;
        }

        Application.EnableVisualStyles();
        Application.Run(new Host());
    }
}
}
