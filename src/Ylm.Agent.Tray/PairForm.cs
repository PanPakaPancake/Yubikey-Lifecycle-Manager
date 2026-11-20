namespace Ylm.Agent.Tray;

/// <summary>"Agent koppeln": Server-Adresse plus der Code aus der Weboberfläche.</summary>
internal sealed class PairForm : BentoForm
{
    private const int W = 400;
    private readonly BentoTextBox _url = new(W - 36);
    private readonly BentoTextBox _code = new(220, Theme.Mono(14f, FontStyle.Bold));
    private readonly Label _result = Txt.Body("", W - 36);
    private readonly BentoButton _ok = new("Koppeln", ButtonKind.Primary) { Margin = new Padding(8, 2, 0, 0) };

    public string ServerUrl => _url.Text.Trim();
    public string Code => _code.Text.Trim();
    public event Func<string, string, Task<(bool Ok, string Message)>>? PairRequested;

    public PairForm(string? serverUrl)
    {
        Text = "Agent koppeln – YubiKey LC Manager";
        StartPosition = FormStartPosition.CenterScreen;
        var p = Theme.P;
        _url.Text = serverUrl ?? "https://";
        _code.Inner.CharacterCasing = CharacterCasing.Upper;

        var stack = new Stack(W);
        stack.Controls.Add(new Header("Agent koppeln", width: W));
        var card = new Card(p.Green);
        var c = new Stack(W - 36);
        c.Controls.Add(new Eyebrow("Kopplung", p.Green, dot: true));
        c.Controls.Add(Txt.Body("Öffnen Sie in der Weboberfläche „Mein Agent“ → „Kopplungscode erzeugen“ und geben Sie hier die Adresse und den Code ein.", W - 36, p.Muted));
        c.Controls.Add(new Eyebrow("Server-Adresse") { Margin = new Padding(0, 8, 0, 6) });
        c.Controls.Add(_url);
        c.Controls.Add(new Eyebrow("Kopplungscode") { Margin = new Padding(0, 2, 0, 6) });
        c.Controls.Add(_code);
        c.Controls.Add(_result);
        card.Controls.Add(c);
        stack.Controls.Add(card);

        var buttons = new Row(rightToLeft: true, width: W);
        var cancel = new BentoButton("Abbrechen") { Margin = new Padding(0, 2, 0, 0) };
        cancel.Click += (_, _) => Close(); // nicht modal: DialogResult allein schließt nicht
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        AcceptButton = _ok;
        CancelButton = cancel;

        _ok.Click += async (_, _) =>
        {
            if (PairRequested is null) return;
            _ok.Enabled = false;
            _result.ForeColor = Theme.P.Muted;
            _result.Text = "Wird gekoppelt …";
            var (ok, message) = await PairRequested(ServerUrl, Code);
            _result.ForeColor = ok ? Theme.P.Ok : Theme.P.Error;
            _result.Text = message;
            _ok.Enabled = true;
            if (ok)
            {
                await Task.Delay(1200);
                Close();
            }
        };
    }

    /// <summary>Nur für Vorschaubilder.</summary>
    internal void PreviewValues(string url, string code, string? result, bool ok)
    {
        _url.Text = url;
        _code.Text = code;
        if (result is null) return;
        _result.Text = result;
        _result.ForeColor = ok ? Theme.P.Ok : Theme.P.Error;
    }
}
