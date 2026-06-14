using System.Drawing;
using System.Windows.Forms;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// Settings dialog for ClipBridge. Edits a <see cref="BridgeSettings"/>, persists it, and drives
/// connect/disconnect on the shared <see cref="BridgeManager"/> while showing live status.
/// </summary>
public sealed class BridgeSettingsForm : Form
{
    private readonly BridgeSettings _settings;
    private readonly BridgeManager _manager;

    private readonly RadioButton _rbServer = new() { Text = "LOCAL  (Server – wait for connection)", AutoSize = true };
    private readonly RadioButton _rbClient = new() { Text = "REMOTE (Client – connect to server)", AutoSize = true };
    private readonly TextBox _txtHost = new();
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtConfirm = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _chkShow = new() { Text = "Show", AutoSize = true };
    private readonly CheckBox _chkAutoConnect = new() { Text = "Connect automatically on startup", AutoSize = true };
    private readonly Label _lblStatus = new() { AutoSize = false, AutoEllipsis = true };
    private readonly Button _btnConnect = new() { Text = "Save && Connect" };
    private readonly Button _btnDisconnect = new() { Text = "Disconnect" };
    private readonly Button _btnClose = new() { Text = "Close" };

    public BridgeSettingsForm(BridgeSettings settings, BridgeManager manager)
    {
        _settings = settings;
        _manager = manager;

        BuildLayout();
        LoadFromSettings();

        _rbClient.CheckedChanged += (_, _) => _txtHost.Enabled = _rbClient.Checked;
        _chkShow.CheckedChanged += (_, _) =>
        {
            _txtPassword.UseSystemPasswordChar = !_chkShow.Checked;
            _txtConfirm.UseSystemPasswordChar = !_chkShow.Checked;
        };
        _btnConnect.Click += OnConnectClick;
        _btnDisconnect.Click += OnDisconnectClick;
        _btnClose.Click += (_, _) => Close();

        _manager.StatusChanged += OnManagerStatus;
        SetStatus(new BridgeStatus { State = _manager.State, Message = _manager.State.ToString() });
    }

    private void BuildLayout()
    {
        Text = "🔗 ClipBridge Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 320);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        int left = 18, fieldLeft = 110, fieldWidth = 300, y = 16;

        _rbServer.Location = new Point(left, y); y += 26;
        _rbClient.Location = new Point(left, y); y += 34;

        var lblHost = new Label { Text = "Server IP:", Location = new Point(left, y + 3), AutoSize = true };
        _txtHost.Location = new Point(fieldLeft, y);
        _txtHost.Width = fieldWidth;
        y += 32;

        var lblPort = new Label { Text = "Port:", Location = new Point(left, y + 3), AutoSize = true };
        _numPort.Location = new Point(fieldLeft, y);
        _numPort.Width = 90;
        y += 36;

        var lblPassword = new Label { Text = "Password:", Location = new Point(left, y + 3), AutoSize = true };
        _txtPassword.Location = new Point(fieldLeft, y);
        _txtPassword.Width = fieldWidth - 60;
        _chkShow.Location = new Point(fieldLeft + fieldWidth - 50, y + 2);
        y += 32;

        var lblConfirm = new Label { Text = "Confirm:", Location = new Point(left, y + 3), AutoSize = true };
        _txtConfirm.Location = new Point(fieldLeft, y);
        _txtConfirm.Width = fieldWidth - 60;
        y += 36;

        _chkAutoConnect.Location = new Point(left, y); y += 34;

        _lblStatus.Location = new Point(left, y);
        _lblStatus.Size = new Size(fieldLeft + fieldWidth - left, 22);
        y += 30;

        _btnConnect.Location = new Point(left, y);
        _btnConnect.Width = 130;
        _btnDisconnect.Location = new Point(left + 140, y);
        _btnDisconnect.Width = 110;
        _btnClose.Location = new Point(left + 320, y);
        _btnClose.Width = 100;

        Controls.AddRange(new Control[]
        {
            _rbServer, _rbClient, lblHost, _txtHost, lblPort, _numPort,
            lblPassword, _txtPassword, _chkShow, lblConfirm, _txtConfirm,
            _chkAutoConnect, _lblStatus, _btnConnect, _btnDisconnect, _btnClose
        });

        AcceptButton = _btnConnect;
        CancelButton = _btnClose;
    }

    private void LoadFromSettings()
    {
        _rbServer.Checked = _settings.Mode == BridgeMode.Server;
        _rbClient.Checked = _settings.Mode == BridgeMode.Client;
        _txtHost.Text = _settings.Host;
        _txtHost.Enabled = _settings.Mode == BridgeMode.Client;
        _numPort.Value = Math.Clamp(_settings.Port, 1, 65535);
        _txtPassword.Text = _settings.Password;
        _txtConfirm.Text = _settings.Password;
        _chkAutoConnect.Checked = _settings.AutoConnect;
    }

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        if (_txtPassword.Text.Length == 0)
        {
            Warn("Please enter a password (shared secret).");
            return;
        }
        if (_txtPassword.Text != _txtConfirm.Text)
        {
            Warn("The passwords do not match.");
            return;
        }
        if (_rbClient.Checked && string.IsNullOrWhiteSpace(_txtHost.Text))
        {
            Warn("Please enter the server IP / hostname.");
            return;
        }

        _settings.Mode = _rbClient.Checked ? BridgeMode.Client : BridgeMode.Server;
        _settings.Host = _txtHost.Text.Trim();
        _settings.Port = (int)_numPort.Value;
        _settings.Password = _txtPassword.Text;
        _settings.AutoConnect = _chkAutoConnect.Checked;

        try { _settings.Save(); }
        catch (Exception ex) { Warn($"Could not save settings: {ex.Message}"); return; }

        _btnConnect.Enabled = false;
        try { await _manager.StartAsync(_settings); }
        catch (Exception ex) { Warn($"Could not start bridge: {ex.Message}"); }
        finally { _btnConnect.Enabled = true; }
    }

    private async void OnDisconnectClick(object? sender, EventArgs e)
    {
        _btnDisconnect.Enabled = false;
        try { await _manager.StopAsync(); }
        catch (Exception ex) { Warn($"Could not stop bridge: {ex.Message}"); }
        finally { _btnDisconnect.Enabled = true; }
    }

    private void OnManagerStatus(BridgeStatus status)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => SetStatus(status));
            else SetStatus(status);
        }
        catch (ObjectDisposedException) { /* closing */ }
    }

    private void SetStatus(BridgeStatus status)
    {
        var (symbol, color) = status.State switch
        {
            BridgeState.Connected => ("🟢", Color.Green),
            BridgeState.Listening => ("🟡", Color.DarkOrange),
            BridgeState.Connecting => ("🟡", Color.DarkOrange),
            BridgeState.Error => ("🔴", Color.Firebrick),
            BridgeState.Disconnected => ("🔴", Color.Firebrick),
            _ => ("⚪", Color.Gray)
        };
        _lblStatus.ForeColor = color;
        _lblStatus.Text = $"Status: {symbol} {status.Message}";
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _manager.StatusChanged -= OnManagerStatus;
        base.OnFormClosed(e);
    }
}
