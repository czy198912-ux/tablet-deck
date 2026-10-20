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
//
// Updates: unless turned off, the window asks GitHub for the latest release when it starts
// and once a day. A newer version shows a banner; "Update now" downloads the installer,
// checks it against the release's SHA-256 file and runs it quietly. The installer asks this
// window to quit (TabletDeck.Quit), installs, and starts Tablet Deck again.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    // Sent by the installer (installer/TabletDeck.iss) before it updates or removes the files.
    public static readonly int QuitMessage = RegisterWindowMessage("TabletDeck.Quit");

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
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, which GitHub requires
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
    readonly Button optionsButton = new Button();
    readonly Button editButton = new Button();
    string editorUrl = ""; // the layout editor on this PC, set when the server is ready
    bool serverReady; // the server has said "ready" and takes settings on stdin
    readonly CheckBox startWithWindows = new CheckBox();
    readonly Label tablets = new Label();
    readonly Label voice = new Label();
    readonly Label keys = new Label();
    readonly Button voiceListButton = new Button();
    readonly Label strictnessCaption = new Label();
    readonly TrackBar strictness = new TrackBar();
    readonly Label strictnessValue = new Label();
    readonly Label strictnessHelp = new Label();
    readonly Label lastHeard = new Label();
    readonly RichTextBox log = new RichTextBox();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolTip tips = new ToolTip();
    readonly System.Windows.Forms.Timer copiedTimer = new System.Windows.Forms.Timer();
    // Sends the slider's value once it has stopped moving, not on every step of a drag.
    readonly System.Windows.Forms.Timer strictnessTimer = new System.Windows.Forms.Timer();
    bool showingServerStrictness; // the slider is being set from the server, not by the player
    readonly List<string[]> voiceCommands = new List<string[]>(); // [phrase, button label]
    VoiceCommandsWindow commandsWindow;

    // ---- updates ----
    const string LatestReleaseApi = "https://api.github.com/repos/nodieCode/tablet-deck/releases/latest";
    const string ReleasesPage = "https://github.com/nodieCode/tablet-deck/releases";
    const string DownloadPrefix = "https://github.com/nodieCode/tablet-deck/releases/download/";
    const string AppKey = @"Software\Tablet Deck";
    // This window's version, e.g. "1.1.0" (AssemblyInformationalVersion, set by scripts/build.js).
    static readonly string CurrentVersion = Application.ProductVersion;
    readonly CheckBox autoUpdate = new CheckBox();
    bool showingServerAutoUpdate;
    bool checkedAtStart;
    bool checking;
    readonly System.Windows.Forms.Timer dailyCheck = new System.Windows.Forms.Timer();
    readonly Panel banner = new Panel();
    readonly Label bannerText = new Label();
    readonly Button bannerWhatsNew = new Button();
    readonly Button bannerUpdate = new Button();
    readonly Button bannerLater = new Button();
    string whatsNewUrl = "";
    string updateVersion = "", updateSetupUrl = "", updateShaUrl = "", dismissedVersion = "";
    Process installer;

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
        ClientSize = new Size(760, 590);
        MinimumSize = new Size(776, 560);
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
        StyleButton(optionsButton, "Settings", 558, 236, 90);
        optionsButton.Click += delegate { OpenSettings(); };
        tips.SetToolTip(optionsButton, "Pairing code, port, focus check, tap length, voice language and the Star Citizen keybind profile");
        // Edit layout and Restart share a place: one shows while running, the other when stopped.
        StyleButton(editButton, "Edit layout", 656, 236, 84);
        editButton.Visible = false;
        editButton.Click += delegate { OpenEditor(); };
        tips.SetToolTip(editButton, "Opens the layout editor in your browser: move buttons, change their keys, labels and icons");
        StyleButton(restartButton, "Restart", 656, 236, 84);
        restartButton.Visible = false;
        restartButton.Click += delegate { StartServer(); };

        startWithWindows.Text = "Start with Windows (in the tray)";
        startWithWindows.ForeColor = Ink;
        startWithWindows.Checked = RunsAtStartup();
        startWithWindows.CheckedChanged += delegate { SetRunAtStartup(startWithWindows.Checked); };
        Place(startWithWindows, 304, 274, 300, 22);

        autoUpdate.Text = "Check for updates automatically";
        autoUpdate.ForeColor = Ink;
        autoUpdate.Checked = true;
        autoUpdate.CheckedChanged += delegate
        {
            if (showingServerAutoUpdate) return;
            SendToServer("set autoUpdate " + (autoUpdate.Checked ? "on" : "off"));
            if (autoUpdate.Checked) CheckForUpdates(false);
        };
        Place(autoUpdate, 304, 298, 300, 22);
        tips.SetToolTip(autoUpdate, "Looks for a new version on GitHub when Tablet Deck starts and once a day. Updates only install when you click Update now.");

        tablets.Text = "Tablets connected: 0";
        Place(tablets, 20, 296, 240, 20);
        keys.ForeColor = Dim;
        Place(keys, 20, 318, 720, 20);

        voice.Text = "Voice commands: starting...";
        voice.ForeColor = Dim;
        Place(voice, 20, 350, 570, 20);
        StyleButton(voiceListButton, "Voice commands...", 600, 344, 140);
        voiceListButton.Enabled = false;
        voiceListButton.Click += delegate { ShowVoiceCommands(); };
        tips.SetToolTip(voiceListButton, "Everything you can say, and the button each one presses");

        // How sure Windows' speech recognition must be before a command counts (voiceConfidence).
        strictnessCaption.Text = "Voice strictness";
        Place(strictnessCaption, 20, 386, 120, 20);
        strictness.Minimum = 20;
        strictness.Maximum = 95;
        strictness.SmallChange = 1;
        strictness.LargeChange = 5;
        strictness.TickStyle = TickStyle.None;
        strictness.BackColor = Back;
        strictness.Enabled = false;
        strictness.ValueChanged += delegate
        {
            strictnessValue.Text = strictness.Value + "%";
            if (showingServerStrictness) return;
            strictnessTimer.Stop();
            strictnessTimer.Start();
        };
        Place(strictness, 136, 380, 300, 30);
        strictnessValue.Font = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        Place(strictnessValue, 440, 386, 50, 20);
        strictnessHelp.Text = "Lower accepts more but may mishear. Higher is stricter.";
        strictnessHelp.ForeColor = Dim;
        strictnessHelp.Font = new Font("Segoe UI", 8.25f);
        Place(strictnessHelp, 494, 380, 246, 32);
        lastHeard.ForeColor = Dim;
        Place(lastHeard, 20, 418, 720, 20);

        strictnessTimer.Interval = 400;
        strictnessTimer.Tick += delegate
        {
            strictnessTimer.Stop();
            SendToServer("set voiceConfidence " + (strictness.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
        };

        log.ReadOnly = true;
        log.BackColor = Panel;
        log.ForeColor = Ink;
        log.BorderStyle = BorderStyle.None;
        log.Font = new Font("Consolas", 9f);
        log.DetectUrls = false;
        log.WordWrap = true;
        log.ScrollBars = RichTextBoxScrollBars.Vertical;
        Place(log, 20, 446, 720, 124);
        log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        // Update banner, shown above the log when there's news; the log moves down for it.
        banner.BackColor = Color.FromArgb(20, 58, 96);
        banner.Visible = false;
        banner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bannerText.ForeColor = Ink;
        bannerText.Font = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        bannerText.Bounds = new Rectangle(10, 0, 390, 36);
        bannerText.TextAlign = ContentAlignment.MiddleLeft;
        banner.Controls.Add(bannerText);
        BannerButton(bannerWhatsNew, "What's new", 404, 96);
        bannerWhatsNew.Click += delegate { if (whatsNewUrl != "") Process.Start(whatsNewUrl); };
        BannerButton(bannerUpdate, "Update now", 506, 104);
        bannerUpdate.Click += delegate { InstallUpdate(); };
        BannerButton(bannerLater, "Later", 616, 96);
        bannerLater.Click += delegate { dismissedVersion = updateVersion; HideBanner(); };
        Place(banner, 20, 446, 720, 36);

        dailyCheck.Interval = 24 * 60 * 60 * 1000;
        dailyCheck.Tick += delegate { if (autoUpdate.Checked) CheckForUpdates(false); };
        dailyCheck.Start();

        tray.Icon = Icon;
        tray.Text = "Tablet Deck";
        tray.Visible = true;
        tray.DoubleClick += delegate { ShowFromTray(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Tablet Deck", null, delegate { ShowFromTray(); });
        menu.Items.Add("Copy link", null, delegate { CopyLink(); });
        menu.Items.Add("Edit layout", null, delegate { OpenEditor(); });
        menu.Items.Add("Check for updates now", null, delegate { CheckForUpdates(true); });
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
        ShowWhatsNewAfterUpdate();
    }

    void BannerButton(Button b, string text, int x, int w)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Accent;
        b.BackColor = Panel;
        b.ForeColor = Ink;
        b.Cursor = Cursors.Hand;
        b.Bounds = new Rectangle(x, 3, w, 30);
        banner.Controls.Add(b);
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
        serverReady = false;
        fatalText = "";
        restartButton.Visible = false;
        link = "";
        linkLabel.Text = "";
        others.Text = "";
        code.Text = "-";
        qr.Rows = null;
        lastHeard.Text = "";
        strictness.Enabled = false;
        voiceCommands.Clear();
        voiceListButton.Enabled = false;
        if (commandsWindow != null) commandsWindow.Fill();
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

    void SendToServer(string line)
    {
        var proc = server;
        if (proc == null || proc.HasExited) return;
        try
        {
            proc.StandardInput.WriteLine(line);
            proc.StandardInput.Flush();
        }
        catch (Exception) { }
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
        serverReady = false;
        // The address lines mean nothing now; the reason gets their space.
        hint.Visible = false;
        linkLabel.Text = "";
        others.Text = "";
        state.Height = codeCaption.Top - state.Top;
        SetState("Stopped: " + why, Bad);
        restartButton.Visible = true;
        editButton.Visible = false;
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
                serverReady = true;
                link = Str(ev, "link");
                editorUrl = "http://localhost:" + Str(ev, "port") + "/editor.html?t=" + Uri.EscapeDataString(Str(ev, "token"));
                editButton.Visible = true;
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
                ShowStrictness(Num(ev, "voiceConfidence"));
                showingServerAutoUpdate = true;
                autoUpdate.Checked = Bool(ev, "autoUpdate");
                showingServerAutoUpdate = false;
                if (!checkedAtStart && autoUpdate.Checked) CheckForUpdates(false);
                checkedAtStart = true;
                break;
            case "voiceConfidence":
                ShowStrictness(Num(ev, "value"));
                break;
            case "voice-commands":
                voiceCommands.Clear();
                foreach (var pair in List(ev, "commands"))
                {
                    var p = pair as object[];
                    if (p != null && p.Length == 2) voiceCommands.Add(new[] { Convert.ToString(p[0], CultureInfo.InvariantCulture), Convert.ToString(p[1], CultureInfo.InvariantCulture) });
                }
                voiceListButton.Enabled = voiceCommands.Count > 0;
                if (commandsWindow != null) commandsWindow.Fill();
                break;
            case "heard":
                string result = Str(ev, "result");
                int sure = (int)Math.Round(Num(ev, "confidence") * 100);
                if (result == "accepted")
                {
                    lastHeard.Text = "Last heard: \"" + Str(ev, "text") + "\", " + sure + "% sure. Pressed " + Str(ev, "label") + ".";
                    lastHeard.ForeColor = Good;
                }
                else if (result == "ignored")
                {
                    lastHeard.Text = "Last heard: \"" + Str(ev, "text") + "\", " + sure + "% sure. Below " + strictness.Value + "%, so it was ignored.";
                    lastHeard.ForeColor = Warn;
                }
                else
                {
                    lastHeard.Text = "Last heard: something that isn't one of the commands.";
                    lastHeard.ForeColor = Dim;
                }
                break;
            case "clients":
                tablets.Text = "Tablets connected: " + Str(ev, "count");
                break;
            case "voice":
                string s = Str(ev, "state");
                if (s == "ready")
                {
                    voice.Text = "Voice commands: ready (" + Str(ev, "phrases") + " phrases, " + Str(ev, "language") + ")"
                        + (Str(ev, "ptt") != "" ? ", push-to-talk: " + Str(ev, "ptt") : "");
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

    static double Num(Dictionary<string, object> d, string key)
    {
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return 0;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch (Exception) { return 0; }
    }

    static object[] List(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is object[] ? (object[])v : new object[0];
    }

    // Moves the slider to the server's value without sending it back.
    void ShowStrictness(double value)
    {
        int percent = Math.Max(strictness.Minimum, Math.Min(strictness.Maximum, (int)Math.Round(value * 100)));
        showingServerStrictness = true;
        strictness.Value = percent;
        strictnessValue.Text = percent + "%";
        showingServerStrictness = false;
        strictness.Enabled = true;
    }

    void ShowVoiceCommands()
    {
        if (commandsWindow == null)
        {
            commandsWindow = new VoiceCommandsWindow(voiceCommands, Icon);
            commandsWindow.FormClosed += delegate { commandsWindow = null; };
            commandsWindow.Fill();
            commandsWindow.Show(this);
        }
        else commandsWindow.Activate();
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

    // ---------- updates ----------

    // Shows the banner above the log. Buttons: "update" (What's new, Update now, Later),
    // "news" (What's new, Close), "busy" (none) or "info" (Close).
    void ShowBanner(string text, string buttons)
    {
        bannerText.Text = text;
        bannerWhatsNew.Visible = buttons == "update" || buttons == "news";
        bannerUpdate.Visible = buttons == "update";
        bannerUpdate.Enabled = true;
        bannerLater.Visible = buttons != "busy";
        bannerLater.Text = buttons == "update" ? "Later" : "Close";
        if (banner.Visible) return;
        int shift = banner.Height + banner.Height / 4;
        log.Top += shift;
        log.Height -= shift;
        banner.Visible = true;
    }

    void HideBanner()
    {
        if (!banner.Visible) return;
        int shift = banner.Height + banner.Height / 4;
        banner.Visible = false;
        log.Top -= shift;
        log.Height += shift;
    }

    static WebClient NewWebClient()
    {
        var web = new WebClient { Encoding = Encoding.UTF8 };
        web.Headers[HttpRequestHeader.UserAgent] = "TabletDeck/" + CurrentVersion; // GitHub requires one
        return web;
    }

    // "1.2.0" or "v1.2.0-beta" -> 1.2.0; null if it isn't a version.
    static Version ParseVersion(string text)
    {
        string v = (text ?? "").Trim().TrimStart('v', 'V');
        int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) v = v.Substring(0, cut);
        Version result;
        return Version.TryParse(v, out result) ? result : null;
    }

    // Asks GitHub for the latest release in the background. userAsked: report "up to date"
    // and errors too, and show a version the player dismissed with Later.
    void CheckForUpdates(bool userAsked)
    {
        if (checking || installer != null) return;
        checking = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Dictionary<string, object> release = null;
            string error = null;
            try
            {
                using (var web = NewWebClient())
                {
                    web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    release = new JavaScriptSerializer().DeserializeObject(web.DownloadString(LatestReleaseApi)) as Dictionary<string, object>;
                }
            }
            catch (Exception ex) { error = ex.Message; }
            OnUi(delegate { checking = false; OnLatestRelease(release, error, userAsked); });
        });
    }

    void OnLatestRelease(Dictionary<string, object> release, string error, bool userAsked)
    {
        if (release == null)
        {
            if (userAsked) Notify("Couldn't check for updates", error ?? "GitHub didn't answer.");
            return;
        }
        string latest = Str(release, "tag_name").TrimStart('v', 'V');
        Version remote = ParseVersion(latest), local = ParseVersion(CurrentVersion);
        if (remote == null || local == null || remote <= local)
        {
            if (userAsked) Notify("Tablet Deck is up to date", "You have version " + CurrentVersion + ", the latest.");
            return;
        }
        // The installer and its checksum, attached to the release by .github/workflows/release.yml.
        string setupName = "TabletDeck-Setup-" + latest + ".exe";
        string setupUrl = "", shaUrl = "";
        foreach (var a in List(release, "assets"))
        {
            var asset = a as Dictionary<string, object>;
            if (asset == null) continue;
            string url = Str(asset, "browser_download_url");
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
            if (Str(asset, "name") == setupName) setupUrl = url;
            else if (Str(asset, "name") == setupName + ".sha256") shaUrl = url;
        }
        if (setupUrl == "" || shaUrl == "")
        {
            if (userAsked) Notify("Update not ready yet", "Version " + latest + " is out, but its installer isn't attached yet. Try again in a few minutes.");
            return;
        }
        if (!userAsked && latest == dismissedVersion) return;
        updateVersion = latest;
        updateSetupUrl = setupUrl;
        updateShaUrl = shaUrl;
        whatsNewUrl = Str(release, "html_url") != "" ? Str(release, "html_url") : ReleasesPage;
        ShowBanner("Tablet Deck " + latest + " is available (you have " + CurrentVersion + ").", "update");
        if (!Visible) Notify("Tablet Deck " + latest + " is available", "Open Tablet Deck and click Update now.");
    }

    void Notify(string title, string text)
    {
        tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    // Downloads the installer for updateVersion, then VerifyAndInstall.
    void InstallUpdate()
    {
        if (updateSetupUrl == "") return;
        ShowBanner("Downloading Tablet Deck " + updateVersion + "...", "busy");
        string dir = UpdateFolder;
        string file = Path.Combine(dir, "TabletDeck-Setup-" + updateVersion + ".exe");
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { UpdateFailed(ex.Message); return; }
        var web = NewWebClient();
        web.DownloadProgressChanged += (s, e) => bannerText.Text = "Downloading Tablet Deck " + updateVersion + "... " + e.ProgressPercentage + "%";
        web.DownloadFileCompleted += (s, e) =>
        {
            web.Dispose();
            if (e.Error != null) UpdateFailed("the download failed (" + e.Error.Message + ")");
            else VerifyAndInstall(file);
        };
        web.DownloadFileAsync(new Uri(updateSetupUrl), file);
    }

    // Checks the download against the release's SHA-256 file, then runs the installer
    // quietly. It asks this window to quit before it replaces the files, and starts
    // Tablet Deck again afterwards. Until then the deck keeps working.
    void VerifyAndInstall(string file)
    {
        bannerText.Text = "Checking the download...";
        try
        {
            string expected;
            using (var web = NewWebClient())
                expected = web.DownloadString(updateShaUrl).Trim().Split(' ')[0].ToLowerInvariant();
            string actual;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (expected.Length != 64 || actual != expected)
            {
                File.Delete(file);
                UpdateFailed("the download didn't match its checksum, so it wasn't installed");
                return;
            }
        }
        catch (Exception ex)
        {
            UpdateFailed("couldn't check the download (" + ex.Message + ")");
            return;
        }
        bannerText.Text = "Installing Tablet Deck " + updateVersion + ". Allow the Windows prompt to continue.";
        try
        {
            installer = Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            UpdateFailed(ex.NativeErrorCode == 1223 ? "the Windows permission prompt was cancelled" : ex.Message);
            return;
        }
        // Normally the installer closes this window. If it ends first, the update didn't happen.
        installer.EnableRaisingEvents = true;
        installer.Exited += delegate
        {
            OnUi(delegate
            {
                int code = installer.ExitCode;
                installer = null;
                UpdateFailed(code == 0 ? "the installer finished without updating" : "the installer stopped (code " + code + "). Was the Windows prompt cancelled?");
            });
        };
    }

    void UpdateFailed(string why)
    {
        string text = "Update failed: " + why + (why.EndsWith("?") || why.EndsWith(".") ? "" : ".");
        ShowBanner(text, "info");
        AppendLog(text, Warn);
    }

    // Where Update now saves the installer; emptied when the window next starts.
    static readonly string UpdateFolder = Path.Combine(Path.GetTempPath(), "TabletDeck-Update");

    // After an update, offers the new version's notes once and removes the used installer.
    void ShowWhatsNewAfterUpdate()
    {
        try { if (Directory.Exists(UpdateFolder)) Directory.Delete(UpdateFolder, true); }
        catch (Exception) { }
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                string last = key.GetValue("LastVersion") as string;
                if (last != null && last != CurrentVersion)
                {
                    whatsNewUrl = ReleasesPage + "/tag/v" + CurrentVersion;
                    ShowBanner("Updated to Tablet Deck " + CurrentVersion + ".", "news");
                }
                key.SetValue("LastVersion", CurrentVersion);
            }
        }
        catch (Exception) { }
    }

    void OpenEditor()
    {
        if (editorUrl == "" || !serverReady)
        {
            Notify("Tablet Deck isn't running", "Start it first; the layout editor is part of the server.");
            return;
        }
        Process.Start(editorUrl);
    }

    // ---------- settings ----------

    string ConfigFile()
    {
        return settingsPath != "" ? settingsPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabletDeck", "config.json");
    }

    // The settings dialog. It starts from config.json; changes go to the running server
    // ("set name value" on stdin), or, while it's stopped, through TabletDeckServer.exe --set.
    void OpenSettings()
    {
        Dictionary<string, object> current = null;
        try { current = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigFile())) as Dictionary<string, object>; }
        catch (Exception) { }
        using (var dialog = new SettingsWindow(current ?? new Dictionary<string, object>(), serverArgs.Contains("--port"), Icon,
            Path.Combine(appDir, "keybinds", "TabletDeck-StarCitizen.xml")))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Changes.Count == 0) return;
            if (serverReady && server != null && !server.HasExited)
            {
                foreach (var change in dialog.Changes) SendToServer("set " + change.Key + " " + change.Value);
                if (dialog.NeedsRestart)
                {
                    AppendLog("Restarting the server for the new settings...", Dim);
                    StopServer();
                    StartServer();
                }
                return;
            }
            var pairs = new List<string>();
            // Quoted, since some values have spaces (e.g. "pttKeyName=Numpad 5"); none have quotes.
            foreach (var change in dialog.Changes) pairs.Add("\"" + change.Key + "=" + change.Value.Replace("\"", "\\\"") + "\"");
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(appDir, "TabletDeckServer.exe"), "--set " + string.Join(" ", pairs.ToArray()))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using (var proc = Process.Start(psi))
                {
                    string errors = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(10000);
                    if (errors.Trim() != "") AppendLog(errors.Trim(), Warn);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Couldn't save the settings: " + ex.Message, Warn);
                return;
            }
            if (server == null) StartServer();
        }
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
        else if (m.Msg == Program.QuitMessage) BeginInvoke(new MethodInvoker(Close));
        base.WndProc(ref m);
    }
}

// Everything the voice listener understands, with a search box: what to say and which
// button it presses. The list comes from the server, so it matches the current layout.
class VoiceCommandsWindow : Form
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    readonly List<string[]> commands;
    readonly Label note = new Label();
    readonly TextBox search = new TextBox();
    readonly ListView list = new ListView();

    public VoiceCommandsWindow(List<string[]> commands, Icon icon)
    {
        this.commands = commands;
        SuspendLayout();
        Text = "Voice commands";
        Icon = icon;
        ShowInTaskbar = false;
        BackColor = DeckWindow.Back;
        ForeColor = DeckWindow.Ink;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(520, 560);
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;

        note.Text = "Hold the mic button on the tablet and say one of these. Most Star Citizen controls are toggles, " +
            "so \"engines on\" and \"engines off\" press the same key.";
        note.ForeColor = DeckWindow.Dim;
        note.Bounds = new Rectangle(12, 10, 496, 40);
        note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        search.Bounds = new Rectangle(12, 56, 496, 24);
        search.BackColor = DeckWindow.Panel;
        search.ForeColor = DeckWindow.Ink;
        search.BorderStyle = BorderStyle.FixedSingle;
        search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        search.TextChanged += delegate { Fill(); };
        search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search, e.g. shields or quantum"); }; // EM_SETCUEBANNER

        list.Bounds = new Rectangle(12, 90, 496, 458);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BackColor = DeckWindow.Panel;
        list.ForeColor = DeckWindow.Ink;
        list.BorderStyle = BorderStyle.None;
        list.Columns.Add("Say", 270);
        list.Columns.Add("Presses", 200);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        Controls.Add(note);
        Controls.Add(search);
        Controls.Add(list);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Column widths aren't scaled with the window, so scale them for the screen's DPI.
        using (var g = CreateGraphics())
        {
            float scale = g.DpiX / 96f;
            list.Columns[0].Width = (int)(270 * scale);
            list.Columns[1].Width = (int)(200 * scale);
        }
        search.Focus();
    }

    // Shows the commands matching the search box, by phrase or button name.
    public void Fill()
    {
        string q = search.Text.Trim().ToLowerInvariant();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var c in commands)
            if (q == "" || c[0].Contains(q) || c[1].ToLowerInvariant().Contains(q))
                list.Items.Add(new ListViewItem(new[] { c[0], c[1] }));
        list.EndUpdate();
        Text = commands.Count == 0 ? "Voice commands (not running)"
            : q == "" ? "Voice commands (" + commands.Count + ")"
            : "Voice commands (" + list.Items.Count + " of " + commands.Count + ")";
    }
}

// Copies the Tablet Deck Keyboard profile (keybinds\TabletDeck-StarCitizen.xml in the
// install folder) into Star Citizen's Controls\Mappings folder, for each copy of the game
// (LIVE, PTU and so on) the player ticks. Found by searching the drives, or picked by hand.
class KeybindProfileWindow : Form
{
    static readonly string[] Channels = { "LIVE", "PTU", "EPTU", "HOTFIX", "TECH-PREVIEW" };
    const string ProfileName = "TabletDeck-StarCitizen.xml";

    readonly string profile;
    readonly List<string> games = new List<string>(); // ...\StarCitizen\<channel>, same order as list
    readonly CheckedListBox list = new CheckedListBox();
    readonly Label status = new Label();

    public KeybindProfileWindow(string profile, Icon icon)
    {
        this.profile = profile;
        SuspendLayout();
        Text = "Install keybind profile";
        Icon = icon;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DeckWindow.Back;
        ForeColor = DeckWindow.Ink;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(560, 420);

        Controls.Add(new Label
        {
            Text = "Copies the Tablet Deck Keyboard profile into Star Citizen, so buttons for actions with no default key work. Tick the copies of the game to install it into.",
            ForeColor = DeckWindow.Ink, Bounds = new Rectangle(12, 12, 536, 40),
        });
        list.Bounds = new Rectangle(12, 58, 536, 130);
        list.CheckOnClick = true;
        list.BackColor = DeckWindow.Panel;
        list.ForeColor = DeckWindow.Ink;
        list.BorderStyle = BorderStyle.None;
        list.HorizontalScrollbar = true;
        Controls.Add(list);
        MakeButton("Choose game folder...", 12, 196, 170, delegate { ChooseFolder(); });

        status.Bounds = new Rectangle(12, 236, 536, 140);
        status.ForeColor = DeckWindow.Dim;
        status.UseMnemonic = false;
        Controls.Add(status);

        var install = MakeButton("Install", 358, 382, 90, delegate { Install(); });
        var close = MakeButton("Close", 458, 382, 90, delegate { Close(); });
        AcceptButton = install;
        CancelButton = close;

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);

        if (!File.Exists(profile))
        {
            status.Text = "The profile file is missing (" + profile + "). Reinstall Tablet Deck.";
            status.ForeColor = DeckWindow.Bad;
            install.Enabled = false;
            return;
        }
        foreach (var game in FindGames()) AddGame(game);
        status.Text = games.Count == 0
            ? "Star Citizen wasn't found in the usual places. Click \"Choose game folder...\" and pick the StarCitizen folder (the one with LIVE in it)."
            : "Found " + games.Count + (games.Count == 1 ? " copy" : " copies") + " of the game. Missing one? Click \"Choose game folder...\".";
    }

    Button MakeButton(string text, int x, int y, int w, EventHandler click)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = DeckWindow.Panel, ForeColor = DeckWindow.Ink, Cursor = Cursors.Hand, Bounds = new Rectangle(x, y, w, 30) };
        b.FlatAppearance.BorderColor = DeckWindow.Accent;
        b.Click += click;
        Controls.Add(b);
        return b;
    }

    // ...\StarCitizen\<channel> folders in the usual install places, and one or two folder
    // levels down on each fixed drive (e.g. D:\Games\RSI\StarCitizen).
    static List<string> FindGames()
    {
        var found = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var places = new List<string> { drive.RootDirectory.FullName };
            foreach (var top in SubFolders(drive.RootDirectory.FullName))
            {
                if (Path.GetFileName(top).Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                places.Add(top);
                places.AddRange(SubFolders(top));
            }
            foreach (var place in places)
                foreach (var channel in Channels)
                {
                    string dir = Path.Combine(place, "StarCitizen", channel);
                    if (Directory.Exists(dir) && !found.Contains(dir)) found.Add(dir);
                }
        }
        return found;
    }

    static string[] SubFolders(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return new string[0]; } // no access, e.g. system folders
    }

    static string Target(string game)
    {
        return Path.Combine(game, "user", "client", "0", "Controls", "Mappings", ProfileName);
    }

    // Adds a game copy to the list, ticked unless it already has this exact profile.
    void AddGame(string game)
    {
        foreach (var g in games) if (string.Equals(g, game, StringComparison.OrdinalIgnoreCase)) return;
        string state;
        bool tick;
        string target = Target(game);
        if (!File.Exists(target)) { state = "not installed"; tick = true; }
        else if (SameFile(target, profile)) { state = "already up to date"; tick = false; }
        else { state = "older copy, will be replaced"; tick = true; }
        games.Add(game);
        list.Items.Add(Path.GetFileName(game) + "  -  " + game + "  (" + state + ")", tick);
    }

    static bool SameFile(string a, string b)
    {
        try { return File.ReadAllText(a).Replace("\r\n", "\n") == File.ReadAllText(b).Replace("\r\n", "\n"); }
        catch (Exception) { return false; }
    }

    // Accepts the StarCitizen folder, a channel folder inside it, or the folder above it.
    void ChooseFolder()
    {
        using (var dialog = new FolderBrowserDialog { Description = "Pick the StarCitizen folder (the one with LIVE in it).", ShowNewFolderButton = false })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string picked = dialog.SelectedPath;
            var candidates = new List<string>();
            if (Array.IndexOf(Channels, Path.GetFileName(picked).ToUpperInvariant()) >= 0) candidates.Add(picked);
            foreach (var root in new[] { picked, Path.Combine(picked, "StarCitizen") })
                foreach (var channel in Channels)
                    if (Directory.Exists(Path.Combine(root, channel))) candidates.Add(Path.Combine(root, channel));
            if (candidates.Count == 0)
            {
                status.Text = "That folder doesn't look like Star Citizen. Pick the StarCitizen folder, the one with LIVE in it.";
                status.ForeColor = DeckWindow.Warn;
                return;
            }
            foreach (var game in candidates) AddGame(game);
            status.Text = "Added. Tick the copies of the game to install the profile into, then click Install.";
            status.ForeColor = DeckWindow.Dim;
        }
    }

    void Install()
    {
        var done = new List<string>();
        var failed = new List<string>();
        for (int i = 0; i < games.Count; i++)
        {
            if (!list.GetItemChecked(i)) continue;
            try
            {
                string target = Target(games[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(profile, target, true);
                done.Add(Path.GetFileName(games[i]));
            }
            catch (Exception ex)
            {
                failed.Add(Path.GetFileName(games[i]) + ": " + ex.Message);
            }
        }
        if (done.Count == 0 && failed.Count == 0)
        {
            status.Text = "Nothing ticked. Tick the copies of the game to install the profile into.";
            status.ForeColor = DeckWindow.Warn;
            return;
        }
        var text = new StringBuilder();
        if (done.Count > 0)
        {
            text.Append("Installed into " + string.Join(", ", done.ToArray()) + ". ");
            text.Append("Now, in Star Citizen: Options > Keybindings > Control Profiles, load \"Tablet Deck Keyboard\". ");
            text.Append("This replaces your current custom binds, so note any you want to keep and set them again afterwards.");
        }
        if (failed.Count > 0)
            text.Append((done.Count > 0 ? "\n\n" : "") + "Couldn't copy into " + string.Join("; ", failed.ToArray()) +
                ". Try running Tablet Deck as administrator, or copy " + profile + " by hand.");
        status.Text = text.ToString();
        status.ForeColor = failed.Count > 0 ? DeckWindow.Warn : DeckWindow.Good;
        // Refresh the states shown in the list.
        var again = new List<string>(games);
        games.Clear();
        list.Items.Clear();
        foreach (var game in again) AddGame(game);
    }
}

// Settings that would otherwise mean editing config.json. Only what changed ends up in
// Changes, as setting name -> text value (see SETTINGS in server.js).
class SettingsWindow : Form
{
    public readonly List<KeyValuePair<string, string>> Changes = new List<KeyValuePair<string, string>>();
    public bool NeedsRestart; // port or voice changes take a server restart

    readonly Dictionary<string, object> current;
    readonly TextBox code = new TextBox();
    readonly NumericUpDown port = new NumericUpDown();
    readonly CheckBox focus = new CheckBox();
    readonly NumericUpDown tap = new NumericUpDown();
    readonly CheckBox voiceOn = new CheckBox();
    readonly ComboBox language = new ComboBox();
    readonly ComboBox pttMode = new ComboBox();
    readonly TextBox links = new TextBox();

    // config.json's links as editable lines.
    string LinksText()
    {
        object v;
        var lines = new List<string>();
        if (current.TryGetValue("links", out v) && v is object[])
            foreach (var item in (object[])v)
            {
                var l = item as Dictionary<string, object>;
                if (l == null) continue;
                lines.Add(Convert.ToString(l["name"], CultureInfo.InvariantCulture) + " | " + Convert.ToString(l["url"], CultureInfo.InvariantCulture));
            }
        return string.Join("\r\n", lines.ToArray());
    }

    // The lines as the JSON the server takes, or null (with a message) if one isn't valid.
    string LinksJson()
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var raw in links.Lines)
        {
            string line = raw.Trim();
            if (line == "") continue;
            int bar = line.IndexOf('|');
            string name = bar > 0 ? line.Substring(0, bar).Trim() : "";
            string url = bar > 0 ? line.Substring(bar + 1).Trim() : "";
            if (name == "" || name.Length > 24 || name.IndexOfAny(new[] { '<', '>', '"' }) >= 0
                || !System.Text.RegularExpressions.Regex.IsMatch(url, "^https://[^\\s\"<>]{3,300}$"))
            {
                MessageBox.Show(this, "This link isn't right:\n\n" + line + "\n\nWrite each one as: Name | https://address (names up to 24 characters).", "Tablet Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            list.Add(new Dictionary<string, string> { { "name", name }, { "url", url } });
        }
        if (list.Count > 12) { MessageBox.Show(this, "Up to 12 links, please.", "Tablet Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
        return new JavaScriptSerializer().Serialize(list);
    }
    readonly CheckBox pttSound = new CheckBox();
    Button pttButton;
    int pttKey;          // Windows virtual-key code, 0 = none
    string pttName = "";
    bool capturing;      // waiting for the player to press the push-to-talk key

    // ---------- push-to-talk key ----------

    void StartCapture()
    {
        if (capturing) { capturing = false; ShowPttKey(); return; } // a second click cancels
        capturing = true;
        pttButton.Text = "Press a key or mouse button...";
    }

    // While capturing, every key (Tab, Enter and Alt included) is the push-to-talk key; Esc cancels.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { capturing = false; ShowPttKey(); }
        else SetPttKey((int)key);
        return true;
    }

    void SetPttKey(int vk)
    {
        capturing = false;
        pttKey = vk;
        pttName = vk == 0 ? "" : KeyName(vk);
        ShowPttKey();
    }

    void ShowPttKey()
    {
        pttButton.Text = pttKey == 0 ? "None: click to set" : pttName;
    }

    static string KeyName(int vk)
    {
        if (vk == 4) return "Middle mouse";
        if (vk == 5) return "Mouse 4";
        if (vk == 6) return "Mouse 5";
        string name = ((Keys)vk).ToString();
        if (name.StartsWith("NumPad")) return "Numpad " + name.Substring(6);
        if (name.StartsWith("D") && name.Length == 2 && char.IsDigit(name[1])) return name.Substring(1);
        return System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9 +-]", "");
    }

    public SettingsWindow(Dictionary<string, object> current, bool portFromCommandLine, Icon icon, string keybindProfile)
    {
        this.current = current;
        SuspendLayout();
        Text = "Tablet Deck settings";
        Icon = icon;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DeckWindow.Back;
        ForeColor = DeckWindow.Ink;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(480, 690);

        Caption("Pairing code", 14);
        Field(code, 12, 36, 200);
        code.Font = new Font("Consolas", 11f);
        code.MaxLength = 32;
        code.Text = Get("token", "");
        AddButton("New random code", 222, 34, 150, delegate { code.Text = RandomCode(); });
        Hint("Letters and numbers. Tablets already paired keep working until they reload; new ones need the new code.", 12, 66, 456);

        Caption("Port", 108);
        Field(port, 12, 130, 90);
        port.Minimum = 1024;
        port.Maximum = 65535;
        port.Value = Math.Max(1024, Math.Min(65535, GetNumber("port", 8765)));
        port.Enabled = !portFromCommandLine;
        Hint(portFromCommandLine ? "Set with --port when Tablet Deck was started, so it can't be changed here."
            : "Changing it restarts the server. Tablets then need the new address or QR code.", 112, 128, 356);

        focus.Text = "Only press keys while Star Citizen is the window in front";
        focus.Checked = GetBool("onlyWhenGameFocused", true);
        focus.Bounds = new Rectangle(12, 176, 456, 24);
        Controls.Add(focus);

        Caption("Tap length", 212);
        Field(tap, 12, 234, 70);
        tap.Minimum = 20;
        tap.Maximum = 1000;
        tap.Value = Math.Max(20, Math.Min(1000, GetNumber("tapMs", 60)));
        Hint("milliseconds a tap holds the key. Raise it if the game misses taps.", 90, 236, 378);

        voiceOn.Text = "Voice commands";
        voiceOn.Checked = GetBool("voice", true);
        voiceOn.Bounds = new Rectangle(12, 276, 456, 24);
        voiceOn.CheckedChanged += delegate { language.Enabled = voiceOn.Checked; };
        Controls.Add(voiceOn);
        Caption("Voice language", 306);
        Field(language, 12, 328, 110);
        language.DropDownStyle = ComboBoxStyle.DropDown;
        language.FlatStyle = FlatStyle.Flat;
        language.Items.AddRange(new object[] { "en-US", "en-GB", "en-AU", "en-CA", "en-IN" });
        language.Text = Get("voiceLanguage", "en-US");
        language.Enabled = voiceOn.Checked;
        Hint("Needs that English speech recognizer installed in Windows (Settings > Time & Language > Speech).", 130, 326, 338);

        // Push-to-talk from a key or mouse button on the PC (see native/voice.cs).
        Caption("Push-to-talk key on the PC", 368);
        pttKey = GetNumber("pttKey", 0);
        pttName = Get("pttKeyName", "");
        pttButton = AddButton("", 12, 390, 190, delegate { StartCapture(); });
        pttButton.MouseDown += (s, e) =>
        {
            if (!capturing) return;
            int vk = e.Button == MouseButtons.Middle ? 4 : e.Button == MouseButtons.XButton1 ? 5 : e.Button == MouseButtons.XButton2 ? 6 : 0;
            if (vk != 0) SetPttKey(vk);
        };
        AddButton("None", 208, 390, 64, delegate { SetPttKey(0); });
        Field(pttMode, 280, 392, 188);
        pttMode.DropDownStyle = ComboBoxStyle.DropDownList;
        pttMode.FlatStyle = FlatStyle.Flat;
        pttMode.Items.AddRange(new object[] { "Hold to talk", "Press to start, again to stop" });
        pttMode.SelectedIndex = Get("pttMode", "hold") == "toggle" ? 1 : 0;
        pttSound.Text = "Play a sound when it starts and stops listening";
        pttSound.Checked = GetBool("pttSound", true);
        pttSound.Bounds = new Rectangle(12, 428, 456, 24);
        Controls.Add(pttSound);
        Hint("Pick a key Star Citizen doesn't use, like a mouse side button, a numpad key or F13-F24: the key still reaches the game.", 12, 454, 456);
        ShowPttKey();

        // Websites the tablet's "Open on PC" buttons open here, one per line: "Name | https://...".
        Caption("Links the tablet can open on this PC", 498);
        Field(links, 12, 520, 456);
        links.Multiline = true;
        links.ScrollBars = ScrollBars.Vertical;
        links.Height = 76;
        links.Font = new Font("Consolas", 9.5f);
        links.Text = LinksText();
        Hint("One per line: Name | https://address. The tablet only sees the names, so it can't open anything else.", 12, 600, 456);

        AddButton("Install keybind profile...", 12, 644, 190, delegate
        {
            using (var dialog = new KeybindProfileWindow(keybindProfile, Icon)) dialog.ShowDialog(this);
        });
        var save = AddButton("Save", 280, 644, 90, delegate { Save(); });
        var cancel = AddButton("Cancel", 378, 644, 90, delegate { DialogResult = DialogResult.Cancel; });
        AcceptButton = save;
        CancelButton = cancel;

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
    }

    void Caption(string text, int y)
    {
        Controls.Add(new Label { Text = text, ForeColor = DeckWindow.Ink, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold), Bounds = new Rectangle(12, y, 456, 20) });
    }

    void Hint(string text, int x, int y, int w)
    {
        Controls.Add(new Label { Text = text, UseMnemonic = false, ForeColor = DeckWindow.Dim, Font = new Font("Segoe UI", 8.25f), Bounds = new Rectangle(x, y, w, 34) });
    }

    void Field(Control c, int x, int y, int w)
    {
        c.BackColor = DeckWindow.Panel;
        c.ForeColor = DeckWindow.Ink;
        c.Bounds = new Rectangle(x, y, w, 26);
        Controls.Add(c);
    }

    Button AddButton(string text, int x, int y, int w, EventHandler click)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = DeckWindow.Panel, ForeColor = DeckWindow.Ink, Cursor = Cursors.Hand, Bounds = new Rectangle(x, y, w, 30) };
        b.FlatAppearance.BorderColor = DeckWindow.Accent;
        b.Click += click;
        Controls.Add(b);
        return b;
    }

    string Get(string key, string fallback)
    {
        object v;
        return current.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;
    }

    int GetNumber(string key, int fallback)
    {
        int n;
        return int.TryParse(Get(key, ""), out n) ? n : fallback;
    }

    bool GetBool(string key, bool fallback)
    {
        object v;
        return current.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
    }

    static string RandomCode()
    {
        var bytes = new byte[4];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    // Checks the fields, records what changed, and closes.
    void Save()
    {
        string newCode = code.Text.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(newCode, "^[a-z0-9]{1,32}$"))
        {
            MessageBox.Show(this, "The pairing code can only have letters and numbers (up to 32).", "Tablet Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            code.Focus();
            return;
        }
        string newLanguage = language.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(newLanguage, "^[a-z]{2}(-[A-Za-z]{2,4})?$"))
        {
            MessageBox.Show(this, "The voice language should look like en-US.", "Tablet Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            language.Focus();
            return;
        }
        Change("token", Get("token", "").Trim().ToLowerInvariant(), newCode, false);
        if (port.Enabled) Change("port", GetNumber("port", 8765).ToString(), ((int)port.Value).ToString(), true);
        Change("onlyWhenGameFocused", OnOff(GetBool("onlyWhenGameFocused", true)), OnOff(focus.Checked), false);
        Change("tapMs", GetNumber("tapMs", 60).ToString(), ((int)tap.Value).ToString(), false);
        Change("voice", OnOff(GetBool("voice", true)), OnOff(voiceOn.Checked), true);
        Change("voiceLanguage", Get("voiceLanguage", "en-US"), newLanguage, true);
        if (GetNumber("pttKey", 0) != pttKey)
        {
            Change("pttKey", GetNumber("pttKey", 0).ToString(), pttKey.ToString(), false);
            if (pttKey != 0) Change("pttKeyName", "", pttName == "" ? "Key" : pttName, false);
        }
        Change("pttMode", Get("pttMode", "hold"), pttMode.SelectedIndex == 1 ? "toggle" : "hold", false);
        Change("pttSound", OnOff(GetBool("pttSound", true)), OnOff(pttSound.Checked), false);
        if (links.Text.Trim() != LinksText().Trim())
        {
            string json = LinksJson();
            if (json == null) { Changes.Clear(); NeedsRestart = false; links.Focus(); return; }
            Change("links", "", json, false);
        }
        DialogResult = DialogResult.OK;
    }

    void Change(string name, string before, string after, bool restart)
    {
        if (before == after) return;
        Changes.Add(new KeyValuePair<string, string>(name, after));
        if (restart) NeedsRestart = true;
    }

    static string OnOff(bool on)
    {
        return on ? "on" : "off";
    }
}
