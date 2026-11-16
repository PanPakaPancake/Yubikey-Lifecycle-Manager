namespace Ylm.Agent.Tray;

/// <summary>"Agent koppeln": server URL plus the code from the WebGUI.</summary>
internal sealed class PairForm : Form
{
    private readonly TextBox _url = new() { Width = 320 };
    private readonly TextBox _code = new() { Width = 160, CharacterCasing = CharacterCasing.Upper };
    private readonly Label _result = new() { AutoSize = false, Width = 340, Height = 40, ForeColor = Color.Firebrick };
    private readonly Button _ok = new() { Text = "Koppeln", Width = 100 };

    public string ServerUrl => _url.Text.Trim();
    public string Code => _code.Text.Trim();
    public event Func<string, string, Task<(bool Ok, string Message)>>? PairRequested;

    public PairForm(string? serverUrl)
    {
        Text = "Agent koppeln – Yubikey Lifecycle Manager";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        _url.Text = serverUrl ?? "https://";

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Öffnen Sie in der Weboberfläche „Agent koppeln“ und geben Sie hier die Adresse und den angezeigten Code ein.", AutoSize = false, Width = 340, Height = 36 });
        layout.Controls.Add(new Label { Text = "Server-Adresse", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        layout.Controls.Add(_url);
        layout.Controls.Add(new Label { Text = "Kopplungscode", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        layout.Controls.Add(_code);
        layout.Controls.Add(_result);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "Abbrechen", Width = 100, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;

        _ok.Click += async (_, _) =>
        {
            if (PairRequested is null) return;
            _ok.Enabled = false;
            _result.ForeColor = Color.DimGray;
            _result.Text = "Wird gekoppelt …";
            var (ok, message) = await PairRequested(ServerUrl, Code);
            _result.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            _result.Text = message;
            _ok.Enabled = true;
            if (ok)
            {
                await Task.Delay(1200);
                DialogResult = DialogResult.OK;
                Close();
            }
        };
    }
}
