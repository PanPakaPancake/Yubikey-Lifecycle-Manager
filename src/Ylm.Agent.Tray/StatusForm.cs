using System.Drawing.Drawing2D;
using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>Kleines Statusfenster unten rechts: Verbindung, erkannter Key, laufender Vorgang mit Anweisung.</summary>
internal sealed class StatusForm : BentoForm
{
    private const int W = 400;
    private readonly Pill _connection = new("Verbinde", Theme.P.Warn);
    private readonly Label _instance = Txt.Muted("", W - 36);
    private readonly Label _model = Txt.Body("", W - 36, null, Theme.Ui(11f, FontStyle.Bold));
    private readonly Label _serial = Txt.Value("", W - 36);
    private readonly Row _badges = new();
    private readonly Instruction _instruction = new(W);
    private readonly Card _operationCard = new(Theme.P.Violet);
    private readonly Label _operation = Txt.Body("", W - 36);
    private readonly Meter _progress = new(W - 36);
    private readonly Label _lastError = Txt.Body("", W, Theme.P.Error);

    public StatusForm()
    {
        Text = "YubiKey LC Manager – Agent";
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        var p = Theme.P;

        var stack = new Stack(W);
        stack.Controls.Add(new Header("Agent", _connection, W));
        stack.Controls.Add(_instance);

        var key = new Card(p.Green);
        var k = new Stack(W - 36);
        k.Controls.Add(new Eyebrow("Erkannter Key", p.Green, dot: true));
        k.Controls.Add(_model);
        k.Controls.Add(_serial);
        k.Controls.Add(_badges);
        key.Controls.Add(k);
        stack.Controls.Add(key);

        stack.Controls.Add(_instruction);
        var o = new Stack(W - 36);
        o.Controls.Add(new Eyebrow("Vorgang", p.Violet, dot: true));
        o.Controls.Add(_operation);
        o.Controls.Add(_progress);
        _operationCard.Controls.Add(o);
        stack.Controls.Add(_operationCard);
        stack.Controls.Add(_lastError);
        Controls.Add(stack);

        Load += (_, _) => PlaceBottomRight();
        SizeChanged += (_, _) => { if (Visible) PlaceBottomRight(); };
    }

    private void PlaceBottomRight()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    /// <summary>Schließen blendet nur aus; das Fenster wird für jede Statusänderung wiederverwendet.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void Show(TrayStatus s, bool pipeConnected)
    {
        var p = Theme.P;
        SuspendLayout();
        var (text, color, live) = TrayContext.Describe(s.Connection, pipeConnected);
        _connection.Set(text, color, live);
        _instance.Text = s.ServerUrl is null ? "Nicht gekoppelt" : $"{s.InstanceName} · {s.ServerUrl}{(s.PairedUser is null ? "" : $" · {s.PairedUser}")}";

        foreach (var old in _badges.Controls.Cast<Control>().ToList()) old.Dispose();
        if (s.Key is null)
        {
            _model.Text = "Kein YubiKey eingesteckt";
            _model.ForeColor = p.Muted;
            _serial.Text = "Key einstecken, der Agent erkennt ihn sofort.";
        }
        else
        {
            _model.Text = s.Key.Model ?? "YubiKey";
            _model.ForeColor = p.Ink;
            _serial.Text = $"SN {s.Key.SerialNumber}  ·  FW {s.Key.FirmwareVersion}";
            _badges.Controls.Add(new Pill(s.Key.SupportsForcePinChange ? "PIN-Wechsel erzwingbar" : "PIN-Wechsel nicht erzwingbar (FW < 5.7)",
                s.Key.SupportsForcePinChange ? p.Ok : p.Warn) { Margin = new Padding(0, 4, 6, 0) });
            if (s.Key.PinIsSet) _badges.Controls.Add(new Pill("PIN gesetzt", p.Cyan) { Margin = new Padding(0, 4, 6, 0) });
        }

        _instruction.Prompt = s.Operation?.Prompt;
        if (s.Operation is { } op)
        {
            _operationCard.Visible = true;
            _operation.Text = op.Title + (op.Step is { } n && op.Total is { } t ? $"  ·  Schritt {n} von {t}" : "");
            _progress.Visible = op.Step is not null && op.Total is > 0;
            if (_progress.Visible) _progress.Set(op.Step!.Value, op.Total!.Value);
            _lastError.Visible = false;
        }
        else
        {
            _operationCard.Visible = false;
            _lastError.Visible = s.LastError is not null;
            _lastError.Text = s.LastError is null ? "" : "Letzter Fehler: " + s.LastError;
        }
        ResumeLayout(true);
    }

    /// <summary>Was der Admin am Key tun soll: große, pulsierende Fläche mit Pfeil oder Punkt.</summary>
    private sealed class Instruction : Control
    {
        private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 50 };
        private float _phase;
        private string? _prompt;

        public Instruction(int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(width, 76);
            Margin = new Padding(0, 0, 0, 12);
            Visible = false;
            _pulse.Tick += (_, _) => { _phase += .09f; Invalidate(); };
        }

        public string? Prompt
        {
            get => _prompt;
            set
            {
                _prompt = value;
                Visible = value is not null;
                _pulse.Enabled = Visible && Theme.Animations;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.P;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var wave = .5f + .5f * MathF.Sin(_phase);
            var r = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
            using var path = Theme.Rounded(r, LogicalToDeviceUnits(18));
            using (var glow = new Pen(Theme.Alpha(p.Accent2, (int)(60 + 120 * wave)), 3f)) g.DrawPath(glow, path);
            using (var fill = new LinearGradientBrush(r, Theme.Mix(p.Accent, p.Accent2, .15f * wave), Theme.Mix(p.Accent2, p.Accent, .25f * wave), 0f)) g.FillPath(fill, path);
            var prompt = _prompt ?? "";
            var lower = prompt.ToLowerInvariant();
            var symbol = lower.Contains("abziehen") ? "↑" : lower.Contains("einstecken") || lower.Contains("anstecken") ? "↓" : lower.Contains("berühren") ? "◉" : "▶";
            var symbolBox = new RectangleF(r.X + 14, r.Y + (r.Height - 44) / 2 + (symbol is "↑" ? -4 * wave : symbol is "↓" ? 4 * wave : 0), 44, 44);
            using (var circle = new SolidBrush(Theme.Alpha(p.AccentInk, 40))) g.FillEllipse(circle, symbolBox);
            TextRenderer.DrawText(g, symbol, Theme.Ui(16f, FontStyle.Bold), Rectangle.Round(symbolBox), p.AccentInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var textBox = new Rectangle((int)symbolBox.Right + 12, (int)r.Y, (int)(r.Right - symbolBox.Right - 24), (int)r.Height);
            TextRenderer.DrawText(g, prompt, Theme.Display(13.5f), textBox, p.AccentInk, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pulse.Dispose();
            base.Dispose(disposing);
        }
    }
}
