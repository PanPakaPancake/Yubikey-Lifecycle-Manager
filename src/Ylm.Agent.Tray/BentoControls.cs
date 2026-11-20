using System.Drawing.Drawing2D;

namespace Ylm.Agent.Tray;

/// <summary>
/// Bausteine im Design „Bento“ der Weboberfläche: Nachtblau mit Raster und Leuchtkanten (dunkel) bzw. helles Violett,
/// runde Karten, Verlaufsknöpfe, Schalter statt Häkchen, Werte in Monospace.
/// </summary>
internal class BentoForm : Form
{
    public BentoForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        BackColor = Theme.P.Bg;
        ForeColor = Theme.P.Ink;
        Font = Theme.Ui();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.Clear(p.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Leuchten oben rechts (violett) und unten links (cyan) wie der Seitenhintergrund
        Glow(g, new RectangleF(Width * .45f, -Height * .55f, Width * 1.1f, Height * 1.1f), Theme.Alpha(p.Violet, p.IsDark ? 60 : 30));
        Glow(g, new RectangleF(-Width * .5f, Height * .55f, Width * 1.0f, Height * .9f), Theme.Alpha(p.Cyan, p.IsDark ? 32 : 14));
        if (!p.IsDark) return;
        using var grid = new Pen(Color.FromArgb(13, 148, 163, 184));
        var step = LogicalToDeviceUnits(32);
        for (var x = 0; x < Width; x += step) g.DrawLine(grid, x, 0, x, Height);
        for (var y = 0; y < Height; y += step) g.DrawLine(grid, 0, y, Width, y);
    }

    private static void Glow(Graphics g, RectangleF r, Color c)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(r);
        using var brush = new PathGradientBrush(path) { CenterColor = c, SurroundColors = [Color.Transparent] };
        g.FillPath(brush, path);
    }
}

/// <summary>Karte mit runden Ecken und Leuchtkante in der Bereichsfarbe.</summary>
internal sealed class Card : FlowLayoutPanel
{
    public Color Accent { get; set; }

    public Card(Color? accent = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Accent = accent ?? Theme.P.Accent;
        BackColor = Color.Transparent;
        ForeColor = Theme.P.Ink;
        Padding = new Padding(18, 16, 18, 14);
        Margin = new Padding(0, 0, 0, 12);
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    /// <summary>Farbe, die Kinder als Hintergrund nehmen.</summary>
    public static Color Fill => Theme.P.Surface;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        var radius = LogicalToDeviceUnits(18);
        using var path = Theme.Rounded(r, radius);
        if (!p.IsDark)
        {
            // weicher Schatten
            using var shadowPath = Theme.Rounded(new RectangleF(r.X, r.Y + 3, r.Width, r.Height), radius);
            using var shadow = new SolidBrush(Color.FromArgb(14, 30, 27, 46));
            g.FillPath(shadow, shadowPath);
        }
        using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
        if (p.IsDark)
        {
            // Bereichsfarbe schimmert von oben links in die Karte
            using var tint = new LinearGradientBrush(new PointF(0, 0), new PointF(Width * .9f, Height), Theme.Alpha(Accent, 26), Color.Transparent);
            g.FillPath(tint, path);
        }
        using (var border = new Pen(p.IsDark ? Theme.Alpha(Accent, 80) : p.Line, 1f)) g.DrawPath(border, path);
        if (p.IsDark)
        {
            using var shine = new LinearGradientBrush(new PointF(r.X + radius, 0), new PointF(r.Right - radius, 0), Color.Transparent, Color.Transparent)
            {
                InterpolationColors = new ColorBlend { Colors = [Color.Transparent, Theme.Alpha(Accent, 170), Color.Transparent], Positions = [0, .35f, 1] },
            };
            using var pen = new Pen(shine, 1.2f);
            g.DrawLine(pen, r.X + radius, r.Y, r.Right - radius, r.Y);
        }
    }
}

/// <summary>Kleine Überschrift wie „ABDECKUNG ZIELGRUPPE“: Monospace, Versalien, gesperrt, optional mit Punkt.</summary>
internal sealed class Eyebrow : Label
{
    public Eyebrow(string text, Color? color = null, bool dot = false)
    {
        Text = (dot ? "● " : "") + text.ToUpperInvariant();
        Font = Theme.P.IsDark ? Theme.Mono(7.5f, FontStyle.Bold) : Theme.Ui(7.5f, FontStyle.Bold);
        ForeColor = color ?? Theme.P.Muted;
        AutoSize = true;
        Margin = new Padding(0, 0, 0, 8);
        BackColor = Color.Transparent;
    }
}

internal static class Txt
{
    public static Label Body(string text, int width, Color? color = null, Font? font = null) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color ?? Theme.P.Ink, Font = font ?? Theme.Ui(),
        Margin = new Padding(0, 0, 0, 6), BackColor = Color.Transparent,
    };

    public static Label Muted(string text, int width) => Body(text, width, Theme.P.Muted, Theme.Ui(8.75f));

    public static Label Value(string text, int width) => Body(text, width, Theme.P.Ink, Theme.Mono(9f));
}

internal enum ButtonKind { Primary, Secondary }

/// <summary>Knopf mit runden Ecken; Primär mit Verlauf (violett → cyan), sekundär als Fläche mit Kante.</summary>
internal sealed class BentoButton : Button
{
    private bool _hover, _down;
    public ButtonKind Kind { get; }

    public BentoButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = text;
        Kind = kind;
        Font = Theme.Ui(9.25f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        AutoSize = false;
        Margin = new Padding(0, 4, 8, 0);
        var size = TextRenderer.MeasureText(text, Font);
        Size = new Size(size.Width + 34, 34);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        if (_down) r.Inflate(-1, -1);
        using var path = Theme.Rounded(r, LogicalToDeviceUnits(12));
        Color text;
        if (!Enabled)
        {
            using var fill = new SolidBrush(p.Surface2);
            g.FillPath(fill, path);
            using var border = new Pen(p.Line);
            g.DrawPath(border, path);
            text = p.Muted;
        }
        else if (Kind == ButtonKind.Primary)
        {
            var a = _hover ? Theme.Mix(p.Accent, Color.White, .12f) : p.Accent;
            var b = _hover ? Theme.Mix(p.Accent2, Color.White, .12f) : p.Accent2;
            using var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(Width, Height), a, b);
            if (p.IsDark)
            {
                using var glowPath = Theme.Rounded(RectangleF.Inflate(r, 0, 0), LogicalToDeviceUnits(12));
                using var glow = new Pen(Theme.Alpha(p.Accent2, _hover ? 140 : 70), 3f);
                g.DrawPath(glow, glowPath);
            }
            g.FillPath(fill, path);
            text = p.AccentInk;
        }
        else
        {
            using var fill = new SolidBrush(_hover ? Theme.Mix(p.Surface2, p.Accent, .08f) : p.Surface2);
            g.FillPath(fill, path);
            using var border = new Pen(_hover ? Theme.Alpha(p.Accent, 200) : (p.IsDark ? Theme.Mix(p.Line, p.Accent2, .2f) : p.Line));
            g.DrawPath(border, path);
            text = p.Ink;
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Alpha(p.Accent2, 200), 1.5f) { DashStyle = DashStyle.Solid };
            using var fp = Theme.Rounded(RectangleF.Inflate(r, -2.5f, -2.5f), LogicalToDeviceUnits(10));
            g.DrawPath(focus, fp);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Schalter (wie in der Weboberfläche) mit Titel und optionaler Beschreibung darunter.</summary>
internal sealed class Toggle : Control
{
    private readonly string? _description;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private bool _checked, _hover;
    private float _pos;
    private readonly int _width;

    public event EventHandler? CheckedChanged;

    public Toggle(string text, string? description = null, int width = 420)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = text;
        _description = description;
        _width = width;
        Font = Theme.Ui(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 2, 0, 10);
        Size = new Size(width, Measure());
        _anim.Tick += (_, _) =>
        {
            var target = _checked ? 1f : 0f;
            _pos += Math.Sign(target - _pos) * .2f;
            if (Math.Abs(target - _pos) < .2f) { _pos = target; _anim.Stop(); }
            Invalidate();
        };
    }

    public string? Description { get => _description; }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (Theme.Animations && IsHandleCreated && Visible) _anim.Start(); else { _pos = value ? 1 : 0; Invalidate(); }
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Setzt den Zustand ohne CheckedChanged (Anzeige aus dem Dienst).</summary>
    public void SetSilently(bool value)
    {
        _checked = value;
        _pos = value ? 1 : 0;
        Invalidate();
    }

    private int Measure()
    {
        var h = TextRenderer.MeasureText(Text, Font, new Size(_width - 58, 0), TextFormatFlags.WordBreak).Height;
        if (_description is not null) h += 2 + TextRenderer.MeasureText(_description, Theme.Ui(8.75f), new Size(_width - 58, 0), TextFormatFlags.WordBreak).Height;
        return Math.Max(LogicalToDeviceUnits(24), h);
    }

    protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = !Checked; } base.OnClick(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space && Enabled) Checked = !Checked; base.OnKeyUp(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 2, LogicalToDeviceUnits(40), LogicalToDeviceUnits(22));
        using var trackPath = Theme.Rounded(track, track.Height / 2);
        var off = p.Surface2;
        if (_pos > 0)
        {
            using var on = new LinearGradientBrush(track, Enabled ? p.Accent : p.Muted, Enabled ? p.Accent2 : p.Line, 0f);
            using var offBrush = new SolidBrush(off);
            g.FillPath(offBrush, trackPath);
            var clip = g.Clip;
            g.SetClip(new RectangleF(track.X, track.Y, track.Width * Math.Max(_pos, .001f) + track.Height / 2, track.Height));
            g.FillPath(on, trackPath);
            g.Clip = clip;
        }
        else
        {
            using var offBrush = new SolidBrush(off);
            g.FillPath(offBrush, trackPath);
        }
        using (var border = new Pen(_hover && Enabled ? Theme.Alpha(p.Accent, 200) : (p.IsDark ? Theme.Mix(p.Line, p.Accent2, .15f) : p.Line)))
            g.DrawPath(border, trackPath);
        var knob = track.Height - 6;
        var kx = track.X + 3 + (track.Width - knob - 6) * _pos;
        using (var knobBrush = new SolidBrush(_pos > .5f ? (p.IsDark ? p.Bg : Color.White) : (Enabled ? p.Muted : p.Line)))
            g.FillEllipse(knobBrush, kx, track.Y + 3, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Alpha(p.Accent2, 200), 1.5f);
            using var fp = Theme.Rounded(RectangleF.Inflate(track, 2.5f, 2.5f), track.Height / 2 + 2);
            g.DrawPath(focus, fp);
        }

        var x = (int)track.Right + LogicalToDeviceUnits(14);
        var textArea = new Rectangle(x, 2, Width - x, Height);
        var title = TextRenderer.MeasureText(g, Text, Font, new Size(textArea.Width, 0), TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textArea.X, textArea.Y, textArea.Width, title.Height), Enabled ? p.Ink : p.Muted, TextFormatFlags.WordBreak);
        if (_description is not null)
            TextRenderer.DrawText(g, _description, Theme.Ui(8.75f), new Rectangle(textArea.X, textArea.Y + title.Height + 2, textArea.Width, Height), p.Muted, TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Umschalter mit mehreren Segmenten, z. B. Dunkel / Hell / Wie Windows.</summary>
internal sealed class Segmented : Control
{
    private readonly string[] _options;
    private int _selected, _hover = -1;
    public event EventHandler? SelectedChanged;

    public Segmented(string[] options, int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _options = options;
        Font = Theme.Ui(9f, FontStyle.Bold);
        Size = new Size(width, 34);
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 2, 0, 8);
    }

    public int Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    private int At(int x) => Math.Clamp(x * _options.Length / Math.Max(1, Width), 0, _options.Length - 1);
    protected override void OnMouseMove(MouseEventArgs e) { var h = At(e.X); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { Focus(); Selected = At(e.X); base.OnMouseClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) Selected = Math.Max(0, _selected - 1);
        if (e.KeyCode == Keys.Right) Selected = Math.Min(_options.Length - 1, _selected + 1);
        base.OnKeyDown(e);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        using var track = Theme.Rounded(r, r.Height / 2);
        using (var fill = new SolidBrush(p.Surface2)) g.FillPath(fill, track);
        using (var border = new Pen(p.IsDark ? Theme.Mix(p.Line, p.Accent2, .15f) : p.Line)) g.DrawPath(border, track);
        var w = r.Width / _options.Length;
        for (var i = 0; i < _options.Length; i++)
        {
            var seg = new RectangleF(r.X + w * i + 3, r.Y + 3, w - 6, r.Height - 6);
            Color text;
            if (i == _selected)
            {
                using var path = Theme.Rounded(seg, seg.Height / 2);
                using var brush = new LinearGradientBrush(seg, p.Accent, p.Accent2, 0f);
                g.FillPath(brush, path);
                text = p.AccentInk;
            }
            else
            {
                if (i == _hover)
                {
                    using var path = Theme.Rounded(seg, seg.Height / 2);
                    using var brush = new SolidBrush(Theme.Alpha(p.Accent, 30));
                    g.FillPath(brush, path);
                }
                text = p.Muted;
            }
            TextRenderer.DrawText(g, _options[i], Font, Rectangle.Round(seg), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Alpha(p.Accent2, 200), 1.5f);
            g.DrawPath(focus, track);
        }
    }
}

/// <summary>Eingabefeld mit runden Ecken; leuchtet beim Fokus in Cyan.</summary>
internal sealed class BentoTextBox : Panel
{
    public TextBox Inner { get; } = new() { BorderStyle = BorderStyle.None };

    public BentoTextBox(int width, Font? font = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        var p = Theme.P;
        Inner.Font = font ?? Theme.Ui(10f);
        Inner.BackColor = p.Surface2;
        Inner.ForeColor = p.Ink;
        Padding = new Padding(12, 9, 12, 7);
        Size = new Size(width, Inner.PreferredHeight + 16);
        Margin = new Padding(0, 0, 0, 12);
        Inner.Dock = DockStyle.Fill;
        Controls.Add(Inner);
        Inner.GotFocus += (_, _) => Invalidate();
        Inner.LostFocus += (_, _) => Invalidate();
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Inner.Text; set => Inner.Text = value ?? ""; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(12));
        using (var fill = new SolidBrush(p.Surface2)) g.FillPath(fill, path);
        using var border = new Pen(Inner.Focused ? p.Accent2 : (p.IsDark ? Theme.Mix(p.Line, p.Accent2, .15f) : p.Line), Inner.Focused ? 1.6f : 1f);
        g.DrawPath(border, path);
    }

    protected override void OnClick(EventArgs e) { Inner.Focus(); base.OnClick(e); }
}

/// <summary>Kapsel mit Punkt und Text, z. B. „● VERBUNDEN“. Der Punkt pulsiert, solange <see cref="Live"/> gesetzt ist.</summary>
internal sealed class Pill : Control
{
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 60 };
    private float _phase;
    private Color _color;
    public bool Live { get; set; }

    public Pill(string text, Color color)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.P.IsDark ? Theme.Mono(7.5f, FontStyle.Bold) : Theme.Ui(8f, FontStyle.Bold);
        Margin = new Padding(0, 0, 0, 8);
        _color = color;
        Set(text, color);
        _pulse.Tick += (_, _) => { _phase += .12f; Invalidate(); };
    }

    public void Set(string text, Color color, bool live = false)
    {
        Text = Theme.P.IsDark ? text.ToUpperInvariant() : text;
        _color = color;
        Live = live;
        var size = TextRenderer.MeasureText(Text, Font);
        Size = new Size(size.Width + 34, Math.Max(24, size.Height + 10));
        _pulse.Enabled = live && Theme.Animations && Visible;
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _pulse.Enabled = Live && Theme.Animations && Visible; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, r.Height / 2);
        using (var fill = new SolidBrush(Theme.Alpha(_color, Theme.P.IsDark ? 30 : 26))) g.FillPath(fill, path);
        using (var border = new Pen(Theme.Alpha(_color, 90))) g.DrawPath(border, path);
        var d = 7f;
        var cx = 12f;
        var cy = r.Height / 2;
        if (Live)
        {
            var t = _phase % 1.6f / 1.6f;
            var ring = d + 10 * t;
            using var halo = new SolidBrush(Theme.Alpha(_color, (int)(120 * (1 - t))));
            g.FillEllipse(halo, cx - ring / 2, cy - ring / 2, ring, ring);
        }
        using (var dot = new SolidBrush(_color)) g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(22, 0, Width - 26, Height), _color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulse.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Fortschrittsbalken wie in der Weboberfläche: runde Spur, Verlauf violett → cyan.</summary>
internal sealed class Meter : Control
{
    private int _value, _maximum = 1;

    public Meter(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(width, 8);
        Margin = new Padding(0, 4, 0, 8);
    }

    public void Set(int value, int maximum) { _value = value; _maximum = Math.Max(1, maximum); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var track = Theme.Rounded(r, r.Height / 2))
        using (var fill = new SolidBrush(p.IsDark ? Color.FromArgb(30, 148, 163, 184) : p.Surface2))
            g.FillPath(fill, track);
        var w = r.Width * Math.Clamp(_value / (float)_maximum, 0, 1);
        if (w < r.Height) return;
        var bar = new RectangleF(0, 0, w, r.Height);
        using var barPath = Theme.Rounded(bar, r.Height / 2);
        using var brush = new LinearGradientBrush(new RectangleF(0, 0, r.Width, r.Height), p.Accent, p.Accent2, 0f);
        g.FillPath(brush, barPath);
    }
}

/// <summary>Kopfzeile aller Fenster: Logo, Wortmarke und rechts optional eine Kapsel.</summary>
internal sealed class Header : TableLayoutPanel
{
    public Header(string sub, Control? right = null, int width = 440)
    {
        BackColor = Color.Transparent;
        ColumnCount = 3;
        RowCount = 1;
        AutoSize = true;
        MinimumSize = new Size(width, 0);
        Margin = new Padding(0, 0, 0, 14);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(new LogoMark(42), 0, 0);
        Controls.Add(new Wordmark(sub) { Anchor = AnchorStyles.Left }, 1, 0);
        if (right is not null)
        {
            right.Anchor = AnchorStyles.Right;
            Controls.Add(right, 2, 0);
        }
    }
}

/// <summary>Spalte, in der Karten und Zeilen untereinander stehen.</summary>
internal sealed class Stack : FlowLayoutPanel
{
    public Stack(int width = 0)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Margin = new Padding(0);
        Padding = new Padding(0);
        if (width > 0) MinimumSize = new Size(width, 0);
    }
}

/// <summary>Knöpfe nebeneinander; <paramref name="rightToLeft"/> für Dialogknöpfe unten rechts.</summary>
internal sealed class Row : FlowLayoutPanel
{
    public Row(bool rightToLeft = false, int width = 0)
    {
        FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Margin = new Padding(0);
        if (width > 0) MinimumSize = new Size(width, 0);
    }
}
