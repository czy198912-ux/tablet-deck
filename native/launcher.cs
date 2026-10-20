// Tablet Deck's app window. It starts TabletDeckServer.exe (server.js packed by
// scripts/build.js) hidden, with --ui, and shows its address, pairing code, QR code,
// status and log instead of a console window. Minimizing hides it to a tray icon; closing
// it stops the server, which releases any held keys first.
//
//   TabletDeck.exe [--minimized] [server options such as --dry-run or --port 8791]
//
// The server reports status as "@ui {json}" lines on stdout (see ui() in server.js); every
// other line goes to the log. "quit" on its stdin stops it. Only the installed app uses
// this window; running from source keeps the console.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Sent to every window when Tablet Deck is opened a second time; the running copy shows itself.
    public static readonly int ShowWindowMessage = RegisterWindowMessage("TabletDeck.ShowWindow");

    [STAThread]
    static void Main(string[] args)
    {
        bool first;
        using (var mutex = new Mutex(true, "Local\\TabletDeck.Window", out first))
        {
            if (!first)
            {
                PostMessage((IntPtr)0xffff, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero); // HWND_BROADCAST
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DeckWindow(args));
            GC.KeepAlive(mutex);
        }
    }
}

// The QR code, drawn from the server's rows of "1" (dark) and "0".
class QrView : Control
{
    string[] rows = new string[0];

    public QrView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public string[] Rows
    {
        set { rows = value ?? new string[0]; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DeckWindow.Panel);
        if (rows.Length == 0)
        {
            TextRenderer.DrawText(g, "QR code appears here\nonce the server is running", Font, ClientRectangle, DeckWindow.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        // Whole pixels per module and a 4-module white margin, so phone cameras read it easily.
        int n = rows.Length;
        int module = Math.Max(1, Math.Min(Width, Height) / (n + 8));
        int size = module * (n + 8);
        int x0 = (Width - size) / 2, y0 = (Height - size) / 2;
        g.FillRectangle(Brushes.White, x0, y0, size, size);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '1')
                    g.FillRectangle(Brushes.Black, x0 + (x + 4) * module, y0 + (y + 4) * module, module, module);
    }
}

class DeckWindow : Form
{
    // The deck's HUD colours (README banner and default theme).
    public static readonly Color Back = Color.FromArgb(11, 23, 38);
    public static readonly Color Panel = Color.FromArgb(18, 37, 59);
    public static readonly Color Accent = Color.FromArgb(79, 179, 255);
    public static readonly Color Ink = Color.FromArgb(232, 238, 245);
    public static readonly Color Dim = Color.FromArgb(138, 160, 184);
    public static readonly Color Good = Color.FromArgb(91, 227, 125);
    public static readonly Color Warn = Color.FromArgb(255, 179, 71);
    public static readonly Color Bad = Color.FromArgb(255, 107, 107);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Tablet Deck";

    readonly string appDir = Path.GetDirectoryName(Application.ExecutablePath);
    readonly string serverArgs;
    bool startHidden;
    bool quitting;
    bool trayHintShown;
    Process server;
    string link = "";
    string settingsPath = "";
    string fatalText = "";

    readonly QrView qr = new QrView();
    readonly Label title = new Label();
    readonly Label state = new Label();
    readonly Label hint = new Label();
    readonly LinkLabel linkLabel = new LinkLabel();
    readonly Label others = new Label();
    readonly Label codeCaption = new Label();
    readonly Label code = new Label();
    readonly Button copyButton = new Button();
    readonly Button settingsButton = new Button();
    readonly Button restartButton = new Button();
    readonly CheckBox startWithWindows = new CheckBox();
    readonly Label tablets = new Label();
    readonly Label voice = new Label();
    readonly Label keys = new Label();
    readonly RichTextBox log = new RichTextBox();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolTip tips = new ToolTip();
    readonly System.Windows.Forms.Timer copiedTimer = new System.Windows.Forms.Timer();

    public DeckWindow(string[] args)
    {
        var passOn = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "--minimized") startHidden = true;
            else passOn.Add(arg.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg);
        }
        serverArgs = ("--ui " + string.Join(" ", passOn)).Trim();

        SuspendLayout();
        Text = "Tablet Deck";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Back;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(776, 480);
        StartPosition = FormStartPosition.CenterScreen;

        Place(qr, 20, 20, 260, 260);

        title.Text = "TABLET DECK";
        title.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
        title.ForeColor = Accent;
        Place(title, 300, 14, 440, 40);

        state.Text = "Starting...";
        state.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        state.ForeColor = Dim;
        Place(state, 302, 54, 440, 40);

        hint.Text = "Scan the QR code with your tablet's camera, or open:";
        hint.ForeColor = Dim;
        Place(hint, 302, 98, 440, 20);

        linkLabel.Font = new Font("Consolas", 10.5f);
        linkLabel.LinkColor = Accent;
        linkLabel.ActiveLinkColor = Ink;
        linkLabel.LinkClicked += delegate { if (link != "") Process.Start(link); };
        Place(linkLabel, 302, 120, 440, 22);
        tips.SetToolTip(linkLabel, "Opens the deck in this PC's browser");

        others.ForeColor = Dim;
        others.Font = new Font("Segoe UI", 8.25f);
        Place(others, 302, 142, 440, 18);

        codeCaption.Text = "Pairing code";
        codeCaption.ForeColor = Dim;
        Place(codeCaption, 302, 166, 440, 20);

        code.Font = new Font("Consolas", 22f, FontStyle.Bold);
        code.Text = "-";
        Place(code, 300, 186, 440, 40);

        StyleButton(copyButton, "Copy link", 302, 236, 110);
        copyButton.Click += delegate { CopyLink(); };
        StyleButton(settingsButton, "Settings folder", 420, 236, 130);
        settingsButton.Click += delegate { OpenSettingsFolder(); };
        StyleButton(restartButton, "Restart", 558, 236, 90);
        restartButton.Visible = false;
        restartButton.Click += delegate { StartServer(); };

        startWithWindows.Text = "Start with Windows (in the tray)";
        startWithWindows.ForeColor = Ink;
        startWithWindows.Checked = RunsAtStartup();
        startWithWindows.CheckedChanged += delegate { SetRunAtStartup(startWithWindows.Checked); };
        Place(startWithWindows, 304, 274, 300, 22);

        tablets.Text = "Tablets connected: 0";
        Place(tablets, 20, 296, 240, 20);
        voice.Text = "Voice commands: starting...";
        voice.ForeColor = Dim;
        Place(voice, 20, 318, 720, 20);
        keys.ForeColor = Dim;
        Place(keys, 20, 340, 720, 20);

        log.ReadOnly = true;
        log.BackColor = Panel;
        log.ForeColor = Ink;
        log.BorderStyle = BorderStyle.None;
        log.Font = new Font("Consolas", 9f);
        log.DetectUrls = false;
        log.WordWrap = true;
        log.ScrollBars = RichTextBoxScrollBars.Vertical;
        Place(log, 20, 368, 720, 132);
        log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        tray.Icon = Icon;
        tray.Text = "Tablet Deck";
        tray.Visible = true;
        tray.DoubleClick += delegate { ShowFromTray(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Tablet Deck", null, delegate { ShowFromTray(); });
        menu.Items.Add("Copy link", null, delegate { CopyLink(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, delegate { Close(); });
        tray.ContextMenuStrip = menu;

        copiedTimer.Interval = 1500;
        copiedTimer.Tick += delegate { copiedTimer.Stop(); copyButton.Text = "Copy link"; };

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);

        CreateHandle(); // lets server output reach the window even while it starts hidden
        StartServer();
    }

    void Place(Control c, int x, int y, int w, int h)
    {
        c.Bounds = new Rectangle(x, y, w, h);
        Controls.Add(c);
    }

    void StyleButton(Button b, string text, int x, int y, int w)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Accent;
        b.BackColor = Panel;
        b.ForeColor = Ink;
        b.Cursor = Cursors.Hand;
        Place(b, x, y, w, 30);
    }

    // ---------- server ----------

    void StartServer()
    {
        fatalText = "";
        restartButton.Visible = false;
        link = "";
        linkLabel.Text = "";
        others.Text = "";
        code.Text = "-";
        qr.Rows = null;
        hint.Visible = true;
        state.Height = hint.Top - state.Top;
        SetState("Starting...", Dim);
        voice.Text = "Voice commands: starting...";
        voice.ForeColor = Dim;
        tablets.Text = "Tablets connected: 0";
        string exe = Path.Combine(appDir, "TabletDeckServer.exe");
        if (!File.Exists(exe))
        {
            Stopped(exe + " is missing. Reinstall Tablet Deck.");
            return;
        }
        var psi = new ProcessStartInfo(exe, serverArgs)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnUi(delegate { OnLine(e.Data, false); }); };
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) OnUi(delegate { OnLine(e.Data, true); }); };
        proc.Exited += delegate { OnUi(delegate { OnServerExit(proc); }); };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Stopped("Couldn't start the server: " + ex.Message);
            return;
        }
        server = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
    }

    void OnUi(MethodInvoker action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    // Asks the server to release held keys and stop; ends it if it doesn't within 3 s.
    void StopServer()
    {
        var proc = server;
        server = null;
        if (proc == null || proc.HasExited) return;
        try
        {
            proc.StandardInput.WriteLine("quit");
            proc.StandardInput.Flush();
        }
        catch (Exception) { }
        if (!proc.WaitForExit(3000))
        {
            try { proc.Kill(); } catch (Exception) { }
        }
    }

    void OnServerExit(Process proc)
    {
        if (quitting || proc != server) return;
        server = null;
        Stopped(fatalText != "" ? fatalText : "The server stopped (exit code " + proc.ExitCode + "). See the log below.");
        ShowFromTray();
    }

    void Stopped(string why)
    {
        // The address lines mean nothing now; the reason gets their space.
        hint.Visible = false;
        linkLabel.Text = "";
        others.Text = "";
        state.Height = codeCaption.Top - state.Top;
        SetState("Stopped: " + why, Bad);
        restartButton.Visible = true;
        tablets.Text = "Tablets connected: 0";
        voice.Text = "Voice commands: off";
        voice.ForeColor = Dim;
        tray.Text = "Tablet Deck (stopped)";
    }

    void OnLine(string line, bool error)
    {
        if (line.StartsWith("@ui "))
        {
            Dictionary<string, object> ev = null;
            try { ev = new JavaScriptSerializer().DeserializeObject(line.Substring(4)) as Dictionary<string, object>; }
            catch (Exception) { }
            if (ev != null) OnEvent(ev);
            return;
        }
        if (line.Trim() == "") return;
        AppendLog(line, error ? Warn : Ink);
    }

    void OnEvent(Dictionary<string, object> ev)
    {
        switch (Str(ev, "t"))
        {
            case "ready":
                link = Str(ev, "link");
                settingsPath = Str(ev, "settings");
                linkLabel.Text = link != "" ? link : "No network found. Connect this PC to Wi-Fi or a network.";
                code.Text = Str(ev, "token");
                var otherLinks = new List<string>();
                foreach (var o in List(ev, "others")) otherLinks.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                others.Text = otherLinks.Count > 0 ? "Also: " + string.Join("  ", otherLinks.ToArray()) : "";
                var rows = new List<string>();
                foreach (var r in List(ev, "qr")) rows.Add(Convert.ToString(r, CultureInfo.InvariantCulture));
                qr.Rows = rows.ToArray();
                string version = Str(ev, "version");
                Text = "Tablet Deck " + version;
                bool dryRun = Bool(ev, "dryRun");
                SetState(dryRun ? "Running in test mode (dry run): no keys are sent" : "Running", dryRun ? Warn : Good);
                keys.Text = dryRun ? "Button presses are only logged below."
                    : Bool(ev, "focusCheck") ? "Keys are only sent while Star Citizen is the window in front."
                    : "Keys go to whatever window is in front.";
                tray.Text = "Tablet Deck " + version + (dryRun ? " (test mode)" : "");
                break;
            case "clients":
                tablets.Text = "Tablets connected: " + Str(ev, "count");
                break;
            case "voice":
                string s = Str(ev, "state");
                if (s == "ready")
                {
                    voice.Text = "Voice commands: ready (" + Str(ev, "phrases") + " phrases, " + Str(ev, "language") + ")";
                    voice.ForeColor = Good;
                }
                else if (s == "off")
                {
                    voice.Text = "Voice commands: turned off in settings";
                    voice.ForeColor = Dim;
                }
                else if (s == "starting")
                {
                    voice.Text = "Voice commands: restarting...";
                    voice.ForeColor = Dim;
                }
                else
                {
                    voice.Text = "Voice commands: unavailable (" + Str(ev, "text") + ")";
                    voice.ForeColor = Warn;
                }
                tips.SetToolTip(voice, voice.Text);
                break;
            case "fatal":
                fatalText = Str(ev, "text");
                break;
        }
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
    }

    static bool Bool(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    static object[] List(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is object[] ? (object[])v : new object[0];
    }

    void SetState(string text, Color color)
    {
        state.Text = text;
        state.ForeColor = color;
    }

    void AppendLog(string line, Color color)
    {
        // Keep the last 500 lines.
        if (log.Lines.Length > 500)
        {
            log.ReadOnly = false;
            log.Select(0, log.GetFirstCharIndexFromLine(100));
            log.SelectedText = "";
            log.ReadOnly = true;
        }
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = color;
        log.AppendText(line + "\n");
        log.ScrollToCaret();
    }

    // ---------- buttons ----------

    void CopyLink()
    {
        if (link == "") return;
        try
        {
            Clipboard.SetText(link);
            copyButton.Text = "Copied!";
            copiedTimer.Stop();
            copiedTimer.Start();
        }
        catch (ExternalException) { }
    }

    void OpenSettingsFolder()
    {
        string dir = settingsPath != "" ? Path.GetDirectoryName(settingsPath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabletDeck");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", "\"" + dir + "\"");
    }

    static bool RunsAtStartup()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(RunValue) != null;
    }

    static void SetRunAtStartup(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --minimized");
            else key.DeleteValue(RunValue, false);
        }
    }

    // ---------- tray and window ----------

    void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // "Start with Windows" opens straight into the tray.
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden)
        {
            startHidden = false;
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized) return;
        Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray.ShowBalloonTip(3000, "Tablet Deck is still running", "Double-click the tray icon to open it, or right-click it to quit.", ToolTipIcon.Info);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window quits: the server releases held keys and stops.
        quitting = true;
        StopServer();
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Program.ShowWindowMessage) ShowFromTray();
        base.WndProc(ref m);
    }
}
