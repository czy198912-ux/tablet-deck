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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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

// The house colours and fonts (docs/DESIGN.md) and the HUD's drawing pieces: thin-lined
// panels with corner brackets and "// HEADING" titles, letter-spaced labels, rulers.
// Sizes are in 96-dpi pixels; HudView scales everything for the screen.
static class Hud
{
    public static readonly Color Back = Color.FromArgb(5, 9, 15);
    public static readonly Color Panel = Color.FromArgb(11, 23, 38);
    public static readonly Color PanelHi = Color.FromArgb(18, 37, 59);
    public static readonly Color Line = Color.FromArgb(31, 58, 88);
    public static readonly Color Accent = Color.FromArgb(79, 179, 255);
    public static readonly Color Ink = Color.FromArgb(232, 238, 245);
    public static readonly Color Dim = Color.FromArgb(138, 160, 184);
    public static readonly Color Good = Color.FromArgb(91, 227, 125);
    public static readonly Color Warn = Color.FromArgb(255, 179, 71);
    public static readonly Color Bad = Color.FromArgb(255, 107, 107);

    // Bahnschrift (Windows 10 and later) has the squared, instrument-panel look of the deck's
    // fonts; older Windows falls back to Segoe UI.
    static readonly string Display = Installed("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    static readonly StringFormat typographic = MakeTypographic();

    static bool Installed(string family)
    {
        try { using (var f = new FontFamily(family)) return f.Name == family; }
        catch (ArgumentException) { return false; }
    }

    static StringFormat MakeTypographic()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    static Font Get(string family, float px, FontStyle style)
    {
        string key = family + px + style;
        Font f;
        if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(family, px, style, GraphicsUnit.Pixel);
        return f;
    }

    public static Font Head(float px) { return Get(Display, px, FontStyle.Bold); }
    public static Font Label(float px) { return Get(Display, px, FontStyle.Regular); }
    public static Font Body(float px) { return Get("Segoe UI", px, FontStyle.Regular); }
    public static Font Mono(float px, bool bold) { return Get("Consolas", px, bold ? FontStyle.Bold : FontStyle.Regular); }

    public static Color Fade(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c); }

    // Letter-spaced text, the HUD's labels and headings; returns its width.
    public static float Spaced(Graphics g, string text, Font font, Color color, float x, float y, float spacing)
    {
        float start = x;
        using (var brush = new SolidBrush(color))
            foreach (char ch in text)
            {
                string s = ch.ToString();
                g.DrawString(s, font, brush, x, y, typographic);
                x += g.MeasureString(s, font, PointF.Empty, typographic).Width + spacing;
            }
        return x - start - (text.Length > 0 ? spacing : 0);
    }

    public static float SpacedWidth(Graphics g, string text, Font font, float spacing)
    {
        float w = 0;
        foreach (char ch in text) w += g.MeasureString(ch.ToString(), font, PointF.Empty, typographic).Width + spacing;
        return w - (text.Length > 0 ? spacing : 0);
    }

    public static float Width(Graphics g, string text, Font font)
    {
        return g.MeasureString(text, font, PointF.Empty, typographic).Width;
    }

    // One line of text, cut short with "..." when it doesn't fit.
    public static void Text(Graphics g, string text, Font font, Color color, RectangleF box)
    {
        using (var brush = new SolidBrush(color))
        using (var f = (StringFormat)typographic.Clone())
        {
            f.Trimming = StringTrimming.EllipsisCharacter;
            f.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, brush, box, f);
        }
    }

    // Text that wraps inside the box.
    public static void Wrapped(Graphics g, string text, Font font, Color color, RectangleF box)
    {
        using (var brush = new SolidBrush(color))
        using (var f = new StringFormat { Trimming = StringTrimming.EllipsisWord })
            g.DrawString(text, font, brush, box, f);
    }

    public static void Dot(Graphics g, Color color, float cx, float cy)
    {
        using (var glow = new SolidBrush(Fade(color, 60))) g.FillEllipse(glow, cx - 6, cy - 6, 12, 12);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, cx - 3.5f, cy - 3.5f, 7, 7);
    }

    // Corner brackets: short accent strokes at the four corners of a box.
    public static void Brackets(Graphics g, RectangleF r, Color color, float arm)
    {
        using (var pen = new Pen(color, 1.5f))
        {
            g.DrawLines(pen, new[] { new PointF(r.Left, r.Top + arm), new PointF(r.Left, r.Top), new PointF(r.Left + arm, r.Top) });
            g.DrawLines(pen, new[] { new PointF(r.Right - arm, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Top + arm) });
            g.DrawLines(pen, new[] { new PointF(r.Left, r.Bottom - arm), new PointF(r.Left, r.Bottom), new PointF(r.Left + arm, r.Bottom) });
            g.DrawLines(pen, new[] { new PointF(r.Right - arm, r.Bottom), new PointF(r.Right, r.Bottom), new PointF(r.Right, r.Bottom - arm) });
        }
    }

    // A row of ruler ticks, every fourth one longer.
    public static void Ruler(Graphics g, float x, float y, float width, Color color)
    {
        using (var pen = new Pen(color, 1f))
            for (int i = 0; i * 6 <= width; i++)
                g.DrawLine(pen, x + i * 6, y, x + i * 6, y + (i % 4 == 0 ? 6 : 3));
    }

    // A panel: a translucent fill the stars show through, a thin border, accent corner
    // brackets, its "// TITLE" and a ruler in the top-right corner.
    public static void PanelBox(Graphics g, RectangleF r, string title)
    {
        using (var fill = new SolidBrush(Fade(Panel, 168))) g.FillRectangle(fill, r);
        using (var border = new Pen(Line, 1f)) g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
        Brackets(g, r, Fade(Accent, 200), 9);
        float w = Spaced(g, "// " + title, Head(11), Accent, r.X + 14, r.Y + 9, 1.6f);
        using (var pen = new Pen(Fade(Line, 255), 1f)) g.DrawLine(pen, r.X + 22 + w, r.Y + 16, r.Right - 70, r.Y + 16);
        Ruler(g, r.Right - 62, r.Y + 11, 48, Fade(Accent, 110));
    }
}

// The main window's face, drawn by hand: a starfield that drifts behind the panels, shifts
// with the mouse (near stars more than far ones) and lights up along a ring sent out by a
// click, plus the window's own buttons, switches, slider and links. The owner draws the
// panels in DrawContent; that layer is cached and only redrawn after Redraw(). The stars
// move only while Running (the window is in front) and Windows' "Show animations" is on,
// so nothing animates behind Star Citizen.
class HudView : Control
{
    public enum Kind { Button, Primary, Switch, Slider, Link }

    // Something to click. Box is in 96-dpi pixels, like everything drawn here.
    public class Item
    {
        public Kind Kind;
        public RectangleF Box;
        public string Text = "", Tip = "";
        public bool Visible = true, Enabled = true, On;
        public bool Mono; // links: the address, in a monospaced font
        public int Value, Min, Max = 100;
        public MethodInvoker Click;   // buttons and links; switches after On flips
        public MethodInvoker Changed; // the slider, on every step
    }

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);

    class Star { public float X, Y, Depth, Size, Phase, Glow; public int Alpha; }
    class Pulse { public float X, Y, Age; }

    public readonly List<Item> Items = new List<Item>();
    public Action<Graphics> DrawContent;
    public float Zoom = 1f; // screen pixels per 96-dpi pixel

    readonly List<Star> stars = new List<Star>();
    readonly List<Pulse> pulses = new List<Pulse>();
    readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ToolTip tips = new ToolTip();
    readonly System.Windows.Forms.Timer tipDelay = new System.Windows.Forms.Timer { Interval = 500 };
    double lastFrame;
    float time;
    PointF mouse, look; // the mouse, and where the stars currently "look" (eases towards it)
    bool mouseInside;
    Bitmap backdrop, content;
    bool contentDirty = true;
    bool running, motion = true;
    Item hover, pressed, dragging, focused;
    bool showFocus;

    public HudView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        var random = new Random(7);
        for (int i = 0; i < 240; i++)
        {
            float depth = i % 7 == 0 ? 1f : i % 3 == 0 ? 0.6f : 0.3f;
            stars.Add(new Star
            {
                X = (float)random.NextDouble(), Y = (float)random.NextDouble(), Depth = depth,
                Size = depth >= 1f ? 2.6f : depth >= 0.6f ? 1.9f : 1.3f,
                Phase = (float)(random.NextDouble() * Math.PI * 2),
                Alpha = (int)(120 + 135 * depth * (0.5 + 0.5 * random.NextDouble())),
            });
        }
        frame.Tick += delegate { Invalidate(); };
        tipDelay.Tick += delegate
        {
            tipDelay.Stop();
            if (hover != null && hover.Tip != "") tips.Show(hover.Tip, this, (int)(mouse.X * Zoom) + 14, (int)(mouse.Y * Zoom) + 22, 6000);
        };
    }

    // Design size: the client area in 96-dpi pixels.
    public SizeF Area { get { return new SizeF(ClientSize.Width / Zoom, ClientSize.Height / Zoom); } }

    // The window is in front: animate, if Windows' animations are on.
    public bool Running
    {
        get { return running; }
        set
        {
            running = value;
            motion = AnimationsOn();
            if (running && motion) { lastFrame = clock.Elapsed.TotalSeconds; frame.Start(); }
            else { frame.Stop(); pulses.Clear(); }
            Invalidate();
        }
    }

    // "Show animations in Windows" (Settings > Accessibility > Visual effects).
    static bool AnimationsOn()
    {
        int on = 1;
        try { SystemParametersInfo(0x1042, 0, ref on, 0); } // SPI_GETCLIENTAREAANIMATION
        catch (Exception) { }
        return on != 0;
    }

    // The panels changed: draw them again on the next frame.
    public void Redraw()
    {
        contentDirty = true;
        Invalidate();
    }

    public Item Add(Kind kind, string text, MethodInvoker click)
    {
        var item = new Item { Kind = kind, Text = text, Click = click };
        Items.Add(item);
        return item;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        Redraw();
    }

    // ---------- drawing ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - lastFrame);
        lastFrame = now;
        if (running && motion) Step(dt);

        var g = e.Graphics;
        if (backdrop == null) backdrop = DrawBackdrop();
        g.DrawImageUnscaled(backdrop, 0, 0);
        g.ScaleTransform(Zoom, Zoom);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawStars(g);
        g.ResetTransform();
        if (contentDirty || content == null) DrawContentLayer();
        g.DrawImageUnscaled(content, 0, 0);
    }

    void Step(float dt)
    {
        time += dt;
        var area = Area;
        var target = mouseInside ? mouse : new PointF(area.Width / 2, area.Height / 2);
        float ease = Math.Min(1f, dt * 3f);
        look = new PointF(look.X + (target.X - look.X) * ease, look.Y + (target.Y - look.Y) * ease);
        foreach (var s in stars)
        {
            s.X -= 0.0035f * s.Depth * dt;
            if (s.X < -0.02f) s.X += 1.04f;
            s.Glow = Math.Max(0, s.Glow - dt * 1.1f);
        }
        for (int i = pulses.Count - 1; i >= 0; i--)
        {
            pulses[i].Age += dt;
            if (pulses[i].Age > 1.8f) pulses.RemoveAt(i);
        }
    }

    // The still part of the background: deep space, a soft glow and a faint grid.
    Bitmap DrawBackdrop()
    {
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Hud.Back);
            g.ScaleTransform(Zoom, Zoom);
            var area = Area;
            using (var path = new GraphicsPath())
            {
                var glow = new RectangleF(-area.Width * 0.25f, -area.Height * 0.6f, area.Width * 1.1f, area.Height * 1.4f);
                path.AddEllipse(glow);
                using (var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(46, 30, 90, 150), SurroundColors = new[] { Color.FromArgb(0, 5, 9, 15) } })
                    g.FillEllipse(brush, glow);
            }
            using (var pen = new Pen(Color.FromArgb(14, Hud.Accent), 1f))
            {
                for (float x = 0; x < area.Width; x += 40) g.DrawLine(pen, x, 0, x, area.Height);
                for (float y = 0; y < area.Height; y += 40) g.DrawLine(pen, 0, y, area.Width, y);
            }
        }
        return bmp;
    }

    void DrawStars(Graphics g)
    {
        var area = Area;
        float cx = area.Width / 2, cy = area.Height / 2;
        if (look.IsEmpty) look = new PointF(cx, cy);
        foreach (var s in stars)
        {
            float x = s.X * area.Width - (look.X - cx) * 0.035f * s.Depth;
            float y = s.Y * area.Height - (look.Y - cy) * 0.035f * s.Depth;
            foreach (var p in pulses)
            {
                float d = (float)Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
                if (Math.Abs(d - PulseRadius(p)) < 16) s.Glow = 1f;
            }
            float twinkle = motion ? 0.78f + 0.22f * (float)Math.Sin(time * (1.3 + s.Depth) + s.Phase) : 1f;
            int alpha = (int)(s.Alpha * twinkle + (255 - s.Alpha) * s.Glow);
            var color = s.Glow > 0 ? Blend(Hud.Ink, Hud.Accent, s.Glow) : (s.Depth >= 1f ? Hud.Ink : Color.FromArgb(170, 200, 230));
            float size = s.Size + s.Glow * 2.5f;
            // Near stars and lit ones get a soft halo.
            if (s.Depth >= 1f || s.Glow > 0)
                using (var halo = new SolidBrush(Hud.Fade(color, alpha / 5)))
                    g.FillEllipse(halo, x - size * 1.6f, y - size * 1.6f, size * 3.2f, size * 3.2f);
            using (var brush = new SolidBrush(Hud.Fade(color, alpha)))
                g.FillEllipse(brush, x - size / 2, y - size / 2, size, size);
        }
        foreach (var p in pulses)
        {
            float r = PulseRadius(p), fade = 1f - p.Age / 1.8f;
            using (var pen = new Pen(Hud.Fade(Hud.Accent, (int)(150 * fade)), 1.5f)) g.DrawEllipse(pen, p.X - r, p.Y - r, r * 2, r * 2);
            using (var pen = new Pen(Hud.Fade(Hud.Accent, (int)(60 * fade)), 1f)) g.DrawEllipse(pen, p.X - r * 0.8f, p.Y - r * 0.8f, r * 1.6f, r * 1.6f);
        }
    }

    static float PulseRadius(Pulse p) { return p.Age * 460f; }

    static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    void DrawContentLayer()
    {
        if (content == null || content.Width != ClientSize.Width || content.Height != ClientSize.Height)
        {
            if (content != null) content.Dispose();
            content = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        }
        using (var g = Graphics.FromImage(content))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(Zoom, Zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType needs an opaque background
            if (DrawContent != null) DrawContent(g);
            foreach (var item in Items) if (item.Visible) DrawItem(g, item);
        }
        contentDirty = false;
    }

    void DrawItem(Graphics g, Item item)
    {
        var r = item.Box;
        bool hot = item == hover && item.Enabled, down = item == pressed && hot;
        switch (item.Kind)
        {
            case Kind.Button:
            case Kind.Primary:
                bool primary = item.Kind == Kind.Primary;
                Color fill = !item.Enabled ? Hud.Fade(Hud.Panel, 220)
                    : down ? Hud.Fade(Hud.Accent, 90)
                    : primary ? Hud.Fade(Hud.Accent, hot ? 70 : 38)
                    : hot ? Hud.PanelHi : Hud.Fade(Hud.Panel, 235);
                using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, r);
                using (var pen = new Pen(!item.Enabled ? Hud.Line : primary || hot ? Hud.Accent : Hud.Line, 1f))
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                if (item.Enabled)
                    using (var pen = new Pen(Hud.Accent, 2f))
                    {
                        g.DrawLine(pen, r.X, r.Y + 1, r.X + 7, r.Y + 1);
                        g.DrawLine(pen, r.Right - 7, r.Bottom - 1, r.Right, r.Bottom - 1);
                    }
                var font = Hud.Head(11.5f);
                string text = item.Text.ToUpperInvariant();
                float w = Hud.SpacedWidth(g, text, font, 1.3f);
                Hud.Spaced(g, text, font, item.Enabled ? Hud.Ink : Hud.Fade(Hud.Dim, 140),
                    r.X + (r.Width - w) / 2, r.Y + (r.Height - font.Height) / 2 + 1, 1.3f);
                break;
            case Kind.Switch:
                var track = new RectangleF(r.X, r.Y + (r.Height - 16) / 2, 34, 16);
                using (var brush = new SolidBrush(item.On ? Hud.Fade(Hud.Accent, 60) : Hud.Fade(Hud.Panel, 235))) g.FillRectangle(brush, track);
                using (var pen = new Pen(item.On || hot ? Hud.Accent : Hud.Line, 1f)) g.DrawRectangle(pen, track.X, track.Y, track.Width, track.Height);
                using (var brush = new SolidBrush(item.On ? Hud.Accent : Hud.Dim))
                    g.FillRectangle(brush, item.On ? track.Right - 13 : track.X + 3, track.Y + 3, 10, 10);
                Hud.Text(g, item.Text, Hud.Body(13), hot ? Hud.Ink : Hud.Fade(Hud.Ink, 225), new RectangleF(r.X + 44, r.Y, r.Width - 44, r.Height));
                break;
            case Kind.Slider:
                float mid = r.Y + r.Height / 2, t = item.Max > item.Min ? (item.Value - item.Min) / (float)(item.Max - item.Min) : 0;
                float knob = r.X + 4 + t * (r.Width - 8);
                using (var pen = new Pen(Hud.Line, 2f)) g.DrawLine(pen, r.X, mid, r.Right, mid);
                using (var pen = new Pen(item.Enabled ? Hud.Accent : Hud.Dim, 2f)) g.DrawLine(pen, r.X, mid, knob, mid);
                Hud.Ruler(g, r.X, mid + 6, r.Width, Hud.Fade(Hud.Line, 255));
                using (var brush = new SolidBrush(!item.Enabled ? Hud.Dim : hot || dragging == item ? Hud.Ink : Hud.Accent))
                    g.FillRectangle(brush, knob - 3, mid - 8, 6, 16);
                break;
            case Kind.Link:
                var linkFont = item.Mono ? Hud.Mono(14, false) : Hud.Body(12.5f);
                float lw = Hud.Width(g, item.Text, linkFont);
                r.Width = Math.Min(lw + 2, item.Box.Width);
                Hud.Text(g, item.Text, linkFont, hot ? Hud.Ink : Hud.Accent, item.Box);
                if (hot) using (var pen = new Pen(Hud.Ink, 1f)) g.DrawLine(pen, r.X, r.Bottom - 2, r.Right, r.Bottom - 2);
                break;
        }
        if (item == focused && showFocus && Focused)
            using (var pen = new Pen(Hud.Accent, 1f) { DashStyle = DashStyle.Dot })
                g.DrawRectangle(pen, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
    }

    // The QR code at whole screen pixels per module, so cameras read it easily, with a white
    // margin of four modules.
    public void DrawQr(Graphics g, RectangleF box, string[] rows)
    {
        var state = g.Save();
        g.ResetTransform();
        g.SmoothingMode = SmoothingMode.None;
        int n = rows.Length;
        int side = (int)(Math.Min(box.Width, box.Height) * Zoom);
        int module = Math.Max(1, side / (n + 8));
        int size = module * (n + 8);
        int x0 = (int)(box.X * Zoom) + ((int)(box.Width * Zoom) - size) / 2, y0 = (int)(box.Y * Zoom) + ((int)(box.Height * Zoom) - size) / 2;
        g.FillRectangle(Brushes.White, x0, y0, size, size);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '1') g.FillRectangle(Brushes.Black, x0 + (x + 4) * module, y0 + (y + 4) * module, module, module);
        g.Restore(state);
    }

    // ---------- mouse and keyboard ----------

    Item ItemAt(PointF p)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var item = Items[i];
            if (item.Visible && item.Enabled && RectangleF.Inflate(item.Box, 0, item.Kind == Kind.Slider ? 6 : 0).Contains(p)) return item;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        mouse = new PointF(e.X / Zoom, e.Y / Zoom);
        mouseInside = true;
        if (dragging != null) { SlideTo(dragging, mouse.X); return; }
        var now = ItemAt(mouse);
        if (now == hover) return;
        hover = now;
        Cursor = hover != null ? Cursors.Hand : Cursors.Default;
        tips.Hide(this);
        tipDelay.Stop();
        if (hover != null && hover.Tip != "") tipDelay.Start();
        Redraw();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouseInside = false;
        tips.Hide(this);
        tipDelay.Stop();
        if (hover != null) { hover = null; Cursor = Cursors.Default; Redraw(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        showFocus = false;
        if (e.Button != MouseButtons.Left) return;
        var p = new PointF(e.X / Zoom, e.Y / Zoom);
        var item = ItemAt(p);
        tips.Hide(this);
        if (item == null)
        {
            // An empty spot sends a pulse through the stars.
            if (running && motion) pulses.Add(new Pulse { X = p.X, Y = p.Y });
            return;
        }
        pressed = focused = item;
        if (item.Kind == Kind.Slider) { dragging = item; SlideTo(item, p.X); }
        Redraw();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var p = new PointF(e.X / Zoom, e.Y / Zoom);
        var item = pressed;
        pressed = null;
        dragging = null;
        if (item != null && item.Kind != Kind.Slider && ItemAt(p) == item) Activate(item);
        Redraw();
    }

    void SlideTo(Item item, float x)
    {
        float t = Math.Max(0, Math.Min(1, (x - item.Box.X - 4) / (item.Box.Width - 8)));
        SetValue(item, item.Min + (int)Math.Round(t * (item.Max - item.Min)));
    }

    void SetValue(Item item, int value)
    {
        value = Math.Max(item.Min, Math.Min(item.Max, value));
        if (value == item.Value) return;
        item.Value = value;
        if (item.Changed != null) item.Changed();
        Redraw();
    }

    void Activate(Item item)
    {
        if (item.Kind == Kind.Switch) item.On = !item.On;
        if (item.Click != null) item.Click();
        Redraw();
    }

    // Tab and Shift+Tab move between the items, Enter or Space presses one, and the arrow
    // keys move the slider.
    protected override bool ProcessDialogKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key != Keys.Tab || (keyData & (Keys.Control | Keys.Alt)) != 0) return base.ProcessDialogKey(keyData);
        var usable = Items.FindAll(i => i.Visible && i.Enabled);
        if (usable.Count == 0) return base.ProcessDialogKey(keyData);
        int at = usable.IndexOf(focused);
        at = (keyData & Keys.Shift) != 0 ? (at <= 0 ? usable.Count - 1 : at - 1) : (at + 1) % usable.Count;
        focused = usable[at];
        showFocus = true;
        Redraw();
        return true;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (focused == null || !focused.Visible || !focused.Enabled) return;
        if (focused.Kind == Kind.Slider)
        {
            int step = e.Shift ? 5 : 1;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) SetValue(focused, focused.Value - step);
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) SetValue(focused, focused.Value + step);
        }
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Activate(focused);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Redraw(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Redraw(); }
}

class DeckWindow : Form
{
    // The house colours, for the other windows (see Hud).
    public static readonly Color Back = Hud.Back;
    public static readonly Color Panel = Hud.Panel;
    public static readonly Color Accent = Hud.Accent;
    public static readonly Color Ink = Hud.Ink;
    public static readonly Color Dim = Hud.Dim;
    public static readonly Color Good = Hud.Good;
    public static readonly Color Warn = Hud.Warn;
    public static readonly Color Bad = Hud.Bad;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Tablet Deck";
    // The window's size in 96-dpi pixels; it scales with the screen.
    const int DesignWidth = 900, DesignHeight = 640;

    readonly string appDir = Path.GetDirectoryName(Application.ExecutablePath);
    readonly string serverArgs;
    bool startHidden;
    bool quitting;
    bool trayHintShown;
    Process server;
    string link = "";
    string settingsPath = "";
    string fatalText = "";

    // What the panels show; DrawHud draws them from these.
    readonly HudView hud = new HudView();
    string[] qrRows = new string[0];
    string chip = "STARTING", stateText = "Starting...", others = "", pairingCode = "-", version = "";
    Color stateColor = Hud.Dim;
    bool dryRun, focusCheck = true;
    int tabletCount;
    DateTime runningSince = DateTime.MinValue;
    string voiceText = "Starting...", lastHeard = "";
    Color voiceColor = Hud.Dim, lastHeardColor = Hud.Dim;
    // Presses from the tablets and voice, for the telemetry panel.
    readonly Queue<DateTime> recentPresses = new Queue<DateTime>();
    int pressCount, blockedCount;
    string lastPress = "";
    // Panel boxes, set by Arrange.
    RectangleF connectBox, linkBox, telemetryBox, voiceBox, systemBox, logBox, bannerBox;

    readonly HudView.Item addressLink, copyButton, settingsButton, editButton, restartButton;
    readonly HudView.Item voiceListButton, strictness, startWithWindows, autoUpdate, folderLink, checkLink;
    readonly HudView.Item bannerWhatsNew, bannerUpdate, bannerLater;
    string editorUrl = ""; // the layout editor on this PC, set when the server is ready
    bool serverReady; // the server has said "ready" and takes settings on stdin
    readonly RichTextBox log = new RichTextBox();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer copiedTimer = new System.Windows.Forms.Timer();
    // Sends the slider's value once it has stopped moving, not on every step of a drag.
    readonly System.Windows.Forms.Timer strictnessTimer = new System.Windows.Forms.Timer();
    // Once a second: the uptime and the presses graph.
    readonly System.Windows.Forms.Timer second = new System.Windows.Forms.Timer { Interval = 1000 };
    readonly List<string[]> voiceCommands = new List<string[]>(); // [phrase, button label]
    VoiceCommandsWindow commandsWindow;

    // ---- updates ----
    const string LatestReleaseApi = "https://api.github.com/repos/nodieCode/tablet-deck/releases/latest";
    const string ReleasesPage = "https://github.com/nodieCode/tablet-deck/releases";
    const string DownloadPrefix = "https://github.com/nodieCode/tablet-deck/releases/download/";
    const string AppKey = @"Software\Tablet Deck";
    // This window's version, e.g. "1.1.0" (AssemblyInformationalVersion, set by scripts/build.js).
    static readonly string CurrentVersion = Application.ProductVersion;
    bool checkedAtStart;
    bool checking;
    readonly System.Windows.Forms.Timer dailyCheck = new System.Windows.Forms.Timer();
    bool bannerShown;
    string bannerText = "";
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
        BackColor = Hud.Back;
        ForeColor = Hud.Ink;
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.None; // HudView scales itself
        using (var g = CreateGraphics()) hud.Zoom = g.DpiX / 96f;
        ClientSize = new Size((int)(DesignWidth * hud.Zoom), (int)(DesignHeight * hud.Zoom));
        MinimumSize = SizeFromClientSize(ClientSize);
        StartPosition = FormStartPosition.CenterScreen;

        hud.Dock = DockStyle.Fill;
        hud.DrawContent = DrawHud;
        Controls.Add(hud);

        addressLink = hud.Add(HudView.Kind.Link, "", delegate { if (link != "") Process.Start(link); });
        addressLink.Mono = true;
        addressLink.Tip = "Opens the deck in this PC's browser";
        copyButton = hud.Add(HudView.Kind.Button, "Copy link", delegate { CopyLink(); });
        copyButton.Tip = "Copies the address with the pairing code, to send to a tablet";
        settingsButton = hud.Add(HudView.Kind.Button, "Settings", delegate { OpenSettings(); });
        settingsButton.Tip = "Pairing code, port, focus check, tap length, voice language and the Star Citizen keybind profile";
        // Edit layout and Restart share a place: one shows while running, the other when stopped.
        editButton = hud.Add(HudView.Kind.Primary, "Edit layout", delegate { OpenEditor(); });
        editButton.Visible = false;
        editButton.Tip = "Opens the layout editor in your browser: move buttons, change their keys, labels and icons";
        restartButton = hud.Add(HudView.Kind.Primary, "Restart", delegate { StartServer(); });
        restartButton.Visible = false;

        voiceListButton = hud.Add(HudView.Kind.Button, "Commands", delegate { ShowVoiceCommands(); });
        voiceListButton.Enabled = false;
        voiceListButton.Tip = "Everything you can say, and the button each one presses";
        // How sure Windows' speech recognition must be before a command counts (voiceConfidence).
        strictness = hud.Add(HudView.Kind.Slider, "", null);
        strictness.Min = 20;
        strictness.Max = 95;
        strictness.Value = 60;
        strictness.Enabled = false;
        strictness.Changed = delegate { strictnessTimer.Stop(); strictnessTimer.Start(); };
        strictness.Tip = "Lower accepts more but may mishear. Higher is stricter.";

        startWithWindows = hud.Add(HudView.Kind.Switch, "Start with Windows (in the tray)", null);
        startWithWindows.On = RunsAtStartup();
        startWithWindows.Click = delegate { SetRunAtStartup(startWithWindows.On); };
        autoUpdate = hud.Add(HudView.Kind.Switch, "Check for updates automatically", null);
        autoUpdate.On = true;
        autoUpdate.Click = delegate
        {
            SendToServer("set autoUpdate " + (autoUpdate.On ? "on" : "off"));
            if (autoUpdate.On) CheckForUpdates(false);
        };
        autoUpdate.Tip = "Looks for a new version on GitHub when Tablet Deck starts and once a day. Updates only install when you click Update now.";
        folderLink = hud.Add(HudView.Kind.Link, "Settings folder", delegate { OpenSettingsFolder(); });
        folderLink.Tip = "Opens the folder with config.json and your own layouts";
        checkLink = hud.Add(HudView.Kind.Link, "Check for updates now", delegate { CheckForUpdates(true); });

        // The update banner, above the panels when there's news.
        bannerWhatsNew = hud.Add(HudView.Kind.Button, "What's new", delegate { if (whatsNewUrl != "") Process.Start(whatsNewUrl); });
        bannerUpdate = hud.Add(HudView.Kind.Primary, "Update now", delegate { InstallUpdate(); });
        bannerLater = hud.Add(HudView.Kind.Button, "Later", delegate { dismissedVersion = updateVersion; HideBanner(); });
        foreach (var b in new[] { bannerWhatsNew, bannerUpdate, bannerLater }) b.Visible = false;

        strictnessTimer.Interval = 400;
        strictnessTimer.Tick += delegate
        {
            strictnessTimer.Stop();
            SendToServer("set voiceConfidence " + (strictness.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
        };

        log.ReadOnly = true;
        log.BackColor = Hud.Panel;
        log.ForeColor = Hud.Ink;
        log.BorderStyle = BorderStyle.None;
        log.Font = new Font("Consolas", 9f);
        log.DetectUrls = false;
        log.WordWrap = true;
        log.ScrollBars = RichTextBoxScrollBars.Vertical;
        log.HandleCreated += delegate { try { SetWindowTheme(log.Handle, "DarkMode_Explorer", null); } catch (Exception) { } };
        hud.Controls.Add(log);

        hud.SizeChanged += delegate { Arrange(); };
        Arrange();

        second.Tick += delegate
        {
            while (recentPresses.Count > 0 && (DateTime.Now - recentPresses.Peek()).TotalSeconds > 60) recentPresses.Dequeue();
            if (Visible && WindowState != FormWindowState.Minimized) hud.Redraw();
        };
        second.Start();

        dailyCheck.Interval = 24 * 60 * 60 * 1000;
        dailyCheck.Tick += delegate { if (autoUpdate.On) CheckForUpdates(false); };
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
        copiedTimer.Tick += delegate { copiedTimer.Stop(); copyButton.Text = "Copy link"; hud.Redraw(); };

        ResumeLayout(false);

        CreateHandle(); // lets server output reach the window even while it starts hidden
        StartServer();
        ShowWhatsNewAfterUpdate();
    }

    // A dark title bar to match (Windows 10 2004 and later use attribute 20, earlier builds 19).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try
        {
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }
        catch (Exception) { }
    }

    // The stars only move while this window is in front.
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); hud.Running = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); hud.Running = false; }

    // ---------- layout and drawing ----------

    // Places the panels and items for the window's size, in 96-dpi pixels: the header, the
    // update banner if shown, CONNECT and LINK side by side, TELEMETRY under LINK, VOICE and
    // SYSTEM below, and the log filling the rest.
    void Arrange()
    {
        var area = hud.Area;
        float w = area.Width, top = 74;
        bannerBox = new RectangleF(20, top, w - 40, 38);
        if (bannerShown) top += 50;
        connectBox = new RectangleF(20, top, 280, 300);
        linkBox = new RectangleF(316, top, w - 336, 172);
        telemetryBox = new RectangleF(316, top + 184, w - 336, 116);
        voiceBox = new RectangleF(20, top + 312, w - 356, 124);
        systemBox = new RectangleF(w - 320, top + 312, 300, 124);
        logBox = new RectangleF(20, top + 448, w - 40, Math.Max(70, area.Height - top - 448 - 16));

        float x = linkBox.X + 16, right = linkBox.Right - 16, row = linkBox.Bottom - 42;
        addressLink.Box = new RectangleF(x, linkBox.Y + 46, right - x, 20);
        editButton.Box = restartButton.Box = new RectangleF(right - 128, row, 128, 28);
        settingsButton.Box = new RectangleF(right - 128 - 10 - 104, row, 104, 28);
        copyButton.Box = new RectangleF(right - 128 - 10 - 104 - 10 - 104, row, 104, 28);

        voiceListButton.Box = new RectangleF(voiceBox.Right - 112, voiceBox.Y + 30, 96, 24);
        strictness.Box = new RectangleF(voiceBox.X + 104, voiceBox.Y + 72, 190, 14);
        startWithWindows.Box = new RectangleF(systemBox.X + 16, systemBox.Y + 32, systemBox.Width - 32, 22);
        autoUpdate.Box = new RectangleF(systemBox.X + 16, systemBox.Y + 60, systemBox.Width - 32, 22);
        folderLink.Box = new RectangleF(systemBox.X + 16, systemBox.Y + 94, 120, 18);
        checkLink.Box = new RectangleF(systemBox.X + 146, systemBox.Y + 94, systemBox.Width - 162, 18);

        float bx = bannerBox.Right - 8;
        foreach (var b in new[] { bannerLater, bannerUpdate, bannerWhatsNew })
        {
            float bw = b == bannerUpdate ? 120 : 100;
            if (b.Visible) { bx -= bw; b.Box = new RectangleF(bx, bannerBox.Y + 5, bw, 28); bx -= 8; }
        }

        float z = hud.Zoom;
        log.Bounds = new Rectangle((int)((logBox.X + 12) * z), (int)((logBox.Y + 30) * z), (int)((logBox.Width - 24) * z), (int)((logBox.Height - 40) * z));
        hud.Redraw();
    }

    void DrawHud(Graphics g)
    {
        var area = hud.Area;
        DrawHeader(g, area.Width);
        if (bannerShown)
        {
            using (var fill = new SolidBrush(Hud.Fade(Hud.Accent, 40))) g.FillRectangle(fill, bannerBox);
            using (var pen = new Pen(Hud.Accent, 1f)) g.DrawRectangle(pen, bannerBox.X, bannerBox.Y, bannerBox.Width, bannerBox.Height);
            float buttonsLeft = bannerBox.Right;
            foreach (var b in new[] { bannerWhatsNew, bannerUpdate, bannerLater }) if (b.Visible) buttonsLeft = Math.Min(buttonsLeft, b.Box.X);
            Hud.Text(g, bannerText, Hud.Body(13.5f), Hud.Ink, new RectangleF(bannerBox.X + 14, bannerBox.Y, buttonsLeft - bannerBox.X - 24, bannerBox.Height));
        }
        DrawConnect(g);
        DrawLink(g);
        DrawTelemetry(g);
        DrawVoice(g);
        Hud.PanelBox(g, systemBox, "SYSTEM");
        Hud.PanelBox(g, logBox, "LOG");
        using (var fill = new SolidBrush(Hud.Panel))
            g.FillRectangle(fill, logBox.X + 8, logBox.Y + 26, logBox.Width - 16, logBox.Height - 32);
    }

    void DrawHeader(Graphics g, float width)
    {
        // An original mark: a hexagon frame around a pointer, like a cockpit indicator.
        var hex = new PointF[6];
        for (int i = 0; i < 6; i++) hex[i] = new PointF(38 + 15 * (float)Math.Cos(Math.PI / 3 * i + Math.PI / 6), 34 + 15 * (float)Math.Sin(Math.PI / 3 * i + Math.PI / 6));
        using (var pen = new Pen(Hud.Accent, 1.5f)) g.DrawPolygon(pen, hex);
        using (var brush = new SolidBrush(Hud.Accent))
            g.FillPolygon(brush, new[] { new PointF(38, 25), new PointF(44, 40), new PointF(38, 36), new PointF(32, 40) });
        float tw = Hud.Spaced(g, "TABLET DECK", Hud.Head(24), Hud.Accent, 64, 17, 3f);
        if (version != "") Hud.Spaced(g, "V" + version, Hud.Label(11.5f), Hud.Dim, 64 + tw + 12, 29, 1.2f);
        Hud.Spaced(g, "STAR CITIZEN CONTROL DECK", Hud.Label(10.5f), Hud.Dim, 65, 45, 2.4f);

        // Status on the right: a dot and one word, the uptime under it.
        var chipFont = Hud.Head(13);
        float cw = Hud.SpacedWidth(g, chip, chipFont, 2f);
        Hud.Spaced(g, chip, chipFont, stateColor, width - 22 - cw, 20, 2f);
        Hud.Dot(g, stateColor, width - 34 - cw, 28);
        if (runningSince != DateTime.MinValue)
        {
            var up = DateTime.Now - runningSince;
            string text = "UPTIME " + ((int)up.TotalHours).ToString("00") + ":" + up.Minutes.ToString("00") + ":" + up.Seconds.ToString("00");
            float uw = Hud.SpacedWidth(g, text, Hud.Mono(11, false), 1f);
            Hud.Spaced(g, text, Hud.Mono(11, false), Hud.Dim, width - 22 - uw, 42, 1f);
        }

        using (var pen = new Pen(Hud.Line, 1f)) g.DrawLine(pen, 20, 62, width - 20, 62);
        using (var pen = new Pen(Hud.Accent, 2f)) g.DrawLine(pen, 20, 62, 150, 62);
        Hud.Ruler(g, width - 200, 64, 180, Hud.Fade(Hud.Accent, 90));
    }

    void DrawConnect(Graphics g)
    {
        var r = connectBox;
        Hud.PanelBox(g, r, "CONNECT");
        var qrBox = new RectangleF(r.X + 22, r.Y + 34, 236, 236);
        if (qrRows.Length > 0) hud.DrawQr(g, qrBox, qrRows);
        else
        {
            using (var pen = new Pen(Hud.Line, 1f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, qrBox.X, qrBox.Y, qrBox.Width, qrBox.Height);
            string text = serverReady ? "NO NETWORK FOUND" : chip == "STOPPED" ? "OFFLINE" : "WAITING FOR THE SERVER";
            float w = Hud.SpacedWidth(g, text, Hud.Label(11), 1.6f);
            Hud.Spaced(g, text, Hud.Label(11), Hud.Dim, qrBox.X + (qrBox.Width - w) / 2, qrBox.Y + qrBox.Height / 2 - 6, 1.6f);
        }
        Hud.Brackets(g, RectangleF.Inflate(qrBox, 6, 6), Hud.Accent, 14);
        string caption = "SCAN WITH YOUR TABLET'S CAMERA";
        float cw = Hud.SpacedWidth(g, caption, Hud.Label(10.5f), 1.4f);
        Hud.Spaced(g, caption, Hud.Label(10.5f), Hud.Dim, r.X + (r.Width - cw) / 2, r.Bottom - 22, 1.4f);
    }

    void DrawLink(Graphics g)
    {
        var r = linkBox;
        float x = r.X + 16, w = r.Width - 32;
        Hud.PanelBox(g, r, "LINK");
        if (serverReady)
        {
            Hud.Spaced(g, "ADDRESS", Hud.Label(10.5f), Hud.Dim, x, r.Y + 32, 1.6f);
            if (link == "") Hud.Text(g, "No network found. Connect this PC to Wi-Fi or a network.", Hud.Body(13), Hud.Warn, new RectangleF(x, r.Y + 46, w, 20));
            if (others != "") Hud.Text(g, "ALSO  " + others, Hud.Mono(11, false), Hud.Dim, new RectangleF(x, r.Y + 68, w, 16));
        }
        else Hud.Wrapped(g, stateText, Hud.Body(13.5f), stateColor, new RectangleF(x, r.Y + 34, w, 50));
        Hud.Spaced(g, "PAIRING CODE", Hud.Label(10.5f), Hud.Dim, x, r.Y + 92, 1.6f);
        Hud.Spaced(g, pairingCode, Hud.Mono(28, true), Hud.Ink, x - 1, r.Y + 106, 3f);
    }

    void DrawTelemetry(Graphics g)
    {
        var r = telemetryBox;
        Hud.PanelBox(g, r, "TELEMETRY");
        float y = r.Y + 32, h = r.Height - 46, x = r.X + 16, gap = 10;
        float total = r.Width - 32 - gap * 2;
        var tablets = new RectangleF(x, y, total * 0.24f, h);
        var keys = new RectangleF(tablets.Right + gap, y, total * 0.31f, h);
        var presses = new RectangleF(keys.Right + gap, y, r.Right - 16 - keys.Right - gap, h);

        Tile(g, tablets, "TABLETS", tabletCount.ToString(), Hud.Ink, "CONNECTED");
        string target = !serverReady ? "-" : dryRun ? "LOG ONLY" : focusCheck ? "STAR CITIZEN" : "ANY WINDOW";
        string why = !serverReady ? "" : dryRun ? "TEST MODE: NO KEYS SENT" : focusCheck ? "ONLY WHILE IT'S IN FRONT" : "FOCUS CHECK IS OFF";
        Tile(g, keys, "KEYS GO TO", target, !serverReady ? Hud.Dim : dryRun || !focusCheck ? Hud.Warn : Hud.Ink, why);

        Tile(g, presses, "PRESSES", pressCount.ToString(), Hud.Ink, lastPress != "" ? "LAST: " + lastPress.ToUpperInvariant() : "NONE YET");
        if (blockedCount > 0)
        {
            string text = blockedCount + " BLOCKED";
            float bw = Hud.SpacedWidth(g, text, Hud.Label(10), 1.2f);
            Hud.Spaced(g, text, Hud.Label(10), Hud.Warn, presses.Right - 10 - bw, presses.Y + 8, 1.2f);
        }
        // The last minute in 2-second bars.
        var graph = new RectangleF(presses.X + presses.Width * 0.45f, presses.Y + 24, presses.Width * 0.55f - 12, presses.Height - 34);
        var bars = new int[30];
        var now = DateTime.Now;
        foreach (var t in recentPresses)
        {
            int i = 29 - (int)((now - t).TotalSeconds / 2);
            if (i >= 0 && i < 30) bars[i]++;
        }
        int most = 3;
        foreach (int b in bars) most = Math.Max(most, b);
        float bar = graph.Width / 30;
        using (var brush = new SolidBrush(Hud.Fade(Hud.Accent, 200)))
            for (int i = 0; i < 30; i++)
                if (bars[i] > 0)
                {
                    float bh = Math.Max(2, graph.Height * bars[i] / most);
                    g.FillRectangle(brush, graph.X + i * bar + 1, graph.Bottom - bh, bar - 2, bh);
                }
        using (var pen = new Pen(Hud.Line, 1f)) g.DrawLine(pen, graph.X, graph.Bottom + 1, graph.Right, graph.Bottom + 1);
    }

    static void Tile(Graphics g, RectangleF r, string caption, string value, Color color, string note)
    {
        using (var fill = new SolidBrush(Hud.Fade(Hud.Back, 140))) g.FillRectangle(fill, r);
        using (var pen = new Pen(Hud.Line, 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        using (var pen = new Pen(Hud.Accent, 2f)) g.DrawLine(pen, r.X, r.Y + 6, r.X, r.Y + 20);
        Hud.Spaced(g, caption, Hud.Label(10), Hud.Dim, r.X + 10, r.Y + 8, 1.4f);
        var font = Hud.Head(value.Length > 6 ? 17 : 24);
        Hud.Text(g, value, font, color, new RectangleF(r.X + 10, r.Y + 21, r.Width - 16, 36));
        Hud.Text(g, note, Hud.Label(9.5f), Hud.Dim, new RectangleF(r.X + 10, r.Bottom - 18, r.Width - 16, 14));
    }

    void DrawVoice(Graphics g)
    {
        var r = voiceBox;
        float x = r.X + 16;
        Hud.PanelBox(g, r, "VOICE");
        Hud.Dot(g, voiceColor, x + 4, r.Y + 42);
        Hud.Text(g, voiceText, Hud.Body(13), voiceColor, new RectangleF(x + 16, r.Y + 32, voiceListButton.Box.X - x - 26, 20));
        Hud.Spaced(g, "STRICTNESS", Hud.Label(10.5f), Hud.Dim, x, r.Y + 68, 1.6f);
        Hud.Spaced(g, strictness.Value + "%", Hud.Head(13), strictness.Enabled ? Hud.Ink : Hud.Dim, strictness.Box.Right + 14, r.Y + 67, 1f);
        Hud.Wrapped(g, "Lower accepts more but may mishear. Higher is stricter.", Hud.Body(11), Hud.Dim,
            new RectangleF(strictness.Box.Right + 60, r.Y + 62, r.Right - strictness.Box.Right - 76, 32));
        Hud.Text(g, lastHeard, Hud.Body(12.5f), lastHeardColor, new RectangleF(x, r.Bottom - 28, r.Width - 32, 18));
    }

    // ---------- server ----------

    void StartServer()
    {
        serverReady = false;
        fatalText = "";
        restartButton.Visible = false;
        copyButton.Enabled = false;
        link = "";
        addressLink.Visible = false;
        others = "";
        pairingCode = "-";
        qrRows = new string[0];
        lastHeard = "";
        strictness.Enabled = false;
        voiceCommands.Clear();
        voiceListButton.Enabled = false;
        if (commandsWindow != null) commandsWindow.Fill();
        SetState("STARTING", "Starting the server...", Hud.Dim);
        voiceText = "Starting...";
        voiceColor = Hud.Dim;
        tabletCount = 0;
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
        // The address means nothing now; the reason takes its place.
        link = "";
        addressLink.Visible = false;
        copyButton.Enabled = false;
        others = "";
        qrRows = new string[0];
        runningSince = DateTime.MinValue;
        SetState("STOPPED", "Stopped: " + why, Hud.Bad);
        restartButton.Visible = true;
        editButton.Visible = false;
        tabletCount = 0;
        voiceText = "Off";
        voiceColor = Hud.Dim;
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
                addressLink.Text = link;
                addressLink.Visible = copyButton.Enabled = link != "";
                pairingCode = Str(ev, "token");
                var otherLinks = new List<string>();
                foreach (var o in List(ev, "others")) otherLinks.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                others = string.Join("  ", otherLinks.ToArray());
                var rows = new List<string>();
                foreach (var r in List(ev, "qr")) rows.Add(Convert.ToString(r, CultureInfo.InvariantCulture));
                qrRows = rows.ToArray();
                version = Str(ev, "version");
                Text = "Tablet Deck " + version;
                dryRun = Bool(ev, "dryRun");
                focusCheck = Bool(ev, "focusCheck");
                // A new pairing code or port restarts nothing, so the uptime keeps counting.
                if (runningSince == DateTime.MinValue) runningSince = DateTime.Now;
                SetState(dryRun ? "TEST MODE" : "ONLINE", dryRun ? "Running in test mode (dry run): no keys are sent" : "Running", dryRun ? Hud.Warn : Hud.Good);
                tray.Text = "Tablet Deck " + version + (dryRun ? " (test mode)" : "");
                ShowStrictness(Num(ev, "voiceConfidence"));
                autoUpdate.On = Bool(ev, "autoUpdate");
                if (!checkedAtStart && autoUpdate.On) CheckForUpdates(false);
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
                    lastHeard = "Last heard: \"" + Str(ev, "text") + "\", " + sure + "% sure. Pressed " + Str(ev, "label") + ".";
                    lastHeardColor = Hud.Good;
                }
                else if (result == "ignored")
                {
                    lastHeard = "Last heard: \"" + Str(ev, "text") + "\", " + sure + "% sure. Below " + strictness.Value + "%, so it was ignored.";
                    lastHeardColor = Hud.Warn;
                }
                else
                {
                    lastHeard = "Last heard: something that isn't one of the commands.";
                    lastHeardColor = Hud.Dim;
                }
                break;
            case "clients":
                int count;
                if (int.TryParse(Str(ev, "count"), out count)) tabletCount = count;
                break;
            case "press":
                if (Bool(ev, "blocked")) blockedCount++;
                else
                {
                    pressCount++;
                    recentPresses.Enqueue(DateTime.Now);
                    lastPress = Str(ev, "label");
                }
                break;
            case "voice":
                string s = Str(ev, "state");
                if (s == "ready")
                {
                    voiceText = "Ready: " + Str(ev, "phrases") + " phrases, " + Str(ev, "language")
                        + (Str(ev, "ptt") != "" ? ", push-to-talk: " + Str(ev, "ptt") : "");
                    voiceColor = Hud.Good;
                }
                else if (s == "off")
                {
                    voiceText = "Turned off in settings";
                    voiceColor = Hud.Dim;
                }
                else if (s == "starting")
                {
                    voiceText = "Restarting...";
                    voiceColor = Hud.Dim;
                }
                else
                {
                    voiceText = "Unavailable: " + Str(ev, "text");
                    voiceColor = Hud.Warn;
                }
                break;
            case "fatal":
                fatalText = Str(ev, "text");
                break;
        }
        hud.Redraw();
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
        int percent = Math.Max(strictness.Min, Math.Min(strictness.Max, (int)Math.Round(value * 100)));
        strictness.Value = percent;
        strictness.Enabled = true;
        hud.Redraw();
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

    // The header's one-word status, and the sentence shown while the server isn't running.
    void SetState(string word, string text, Color color)
    {
        chip = word;
        stateText = text;
        stateColor = color;
        hud.Redraw();
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
        bannerText = text;
        bannerWhatsNew.Visible = buttons == "update" || buttons == "news";
        bannerUpdate.Visible = buttons == "update";
        bannerLater.Visible = buttons != "busy";
        bannerLater.Text = buttons == "update" ? "Later" : "Close";
        if (!bannerShown && WindowState == FormWindowState.Normal) Height += (int)(50 * hud.Zoom);
        bannerShown = true;
        Arrange();
    }

    void SetBannerText(string text)
    {
        bannerText = text;
        hud.Redraw();
    }

    void HideBanner()
    {
        if (bannerShown && WindowState == FormWindowState.Normal) Height = Math.Max(MinimumSize.Height, Height - (int)(50 * hud.Zoom));
        bannerShown = false;
        foreach (var b in new[] { bannerWhatsNew, bannerUpdate, bannerLater }) b.Visible = false;
        Arrange();
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
        web.DownloadProgressChanged += (s, e) => SetBannerText("Downloading Tablet Deck " + updateVersion + "... " + e.ProgressPercentage + "%");
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
        SetBannerText("Checking the download...");
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
        SetBannerText("Installing Tablet Deck " + updateVersion + ". Allow the Windows prompt to continue.");
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
            hud.Redraw();
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
    readonly CheckBox shortEmotes = new CheckBox();
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
        ClientSize = new Size(480, 720);

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
        voiceOn.CheckedChanged += delegate { language.Enabled = shortEmotes.Enabled = voiceOn.Checked; };
        Controls.Add(voiceOn);
        Caption("Voice language", 306);
        Field(language, 12, 328, 110);
        language.DropDownStyle = ComboBoxStyle.DropDown;
        language.FlatStyle = FlatStyle.Flat;
        language.Items.AddRange(new object[] { "en-US", "en-GB", "en-AU", "en-CA", "en-IN" });
        language.Text = Get("voiceLanguage", "en-US");
        language.Enabled = voiceOn.Checked;
        Hint("Needs that English speech recognizer installed in Windows (Settings > Time & Language > Speech).", 130, 326, 338);
        shortEmotes.Text = "Emotes also answer to one word (\"wave\", not only \"emote wave\")";
        shortEmotes.Checked = GetBool("shortEmotes", false);
        shortEmotes.Enabled = voiceOn.Checked;
        shortEmotes.Bounds = new Rectangle(12, 362, 456, 24);
        Controls.Add(shortEmotes);

        // Push-to-talk from a key or mouse button on the PC (see native/voice.cs).
        Caption("Push-to-talk key on the PC", 398);
        pttKey = GetNumber("pttKey", 0);
        pttName = Get("pttKeyName", "");
        pttButton = AddButton("", 12, 420, 190, delegate { StartCapture(); });
        pttButton.MouseDown += (s, e) =>
        {
            if (!capturing) return;
            int vk = e.Button == MouseButtons.Middle ? 4 : e.Button == MouseButtons.XButton1 ? 5 : e.Button == MouseButtons.XButton2 ? 6 : 0;
            if (vk != 0) SetPttKey(vk);
        };
        AddButton("None", 208, 420, 64, delegate { SetPttKey(0); });
        Field(pttMode, 280, 422, 188);
        pttMode.DropDownStyle = ComboBoxStyle.DropDownList;
        pttMode.FlatStyle = FlatStyle.Flat;
        pttMode.Items.AddRange(new object[] { "Hold to talk", "Press to start, again to stop" });
        pttMode.SelectedIndex = Get("pttMode", "hold") == "toggle" ? 1 : 0;
        pttSound.Text = "Play a sound when it starts and stops listening";
        pttSound.Checked = GetBool("pttSound", true);
        pttSound.Bounds = new Rectangle(12, 458, 456, 24);
        Controls.Add(pttSound);
        Hint("Pick a key Star Citizen doesn't use, like a mouse side button, a numpad key or F13-F24: the key still reaches the game.", 12, 484, 456);
        ShowPttKey();

        // Websites the tablet's "Open on PC" buttons open here, one per line: "Name | https://...".
        Caption("Links the tablet can open on this PC", 528);
        Field(links, 12, 550, 456);
        links.Multiline = true;
        links.ScrollBars = ScrollBars.Vertical;
        links.Height = 76;
        links.Font = new Font("Consolas", 9.5f);
        links.Text = LinksText();
        Hint("One per line: Name | https://address. The tablet only sees the names, so it can't open anything else.", 12, 630, 456);

        AddButton("Install keybind profile...", 12, 674, 190, delegate
        {
            using (var dialog = new KeybindProfileWindow(keybindProfile, Icon)) dialog.ShowDialog(this);
        });
        var save = AddButton("Save", 280, 674, 90, delegate { Save(); });
        var cancel = AddButton("Cancel", 378, 674, 90, delegate { DialogResult = DialogResult.Cancel; });
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
        Change("shortEmotes", OnOff(GetBool("shortEmotes", false)), OnOff(shortEmotes.Checked), false);
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
