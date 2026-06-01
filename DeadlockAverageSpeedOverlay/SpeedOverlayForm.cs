using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeadlockAverageSpeedOverlay;

public sealed class SpeedOverlayForm : Form
{
    private const int ListenPort = 47777;
    private const int CommandPort = 47778;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Button[] _modeButtons = new Button[3];
    private readonly Button _resetButton = new();
    private readonly Button _maxResetButton = new();
    private readonly Button _settingsButton = new();
    private readonly Button _stopButton = new();
    private readonly ValueRow _currentRow = new("CURRENT");
    private readonly ValueRow _averageRow = new("AVERAGE");
    private readonly ValueRow _maxRow = new("MAX");
    private readonly ValueRow _max5Row = new("MAX 5S");
    private readonly Label _timeLabel = new();
    private readonly System.Windows.Forms.Timer _staleTimer = new();
    private readonly ToolTip _toolTip = new();

    private DateTime _lastPacketUtc = DateTime.MinValue;
    private SpeedSnapshot? _lastSnapshot;
    private int _activeSlot;
    private SpeedMode _mode = SpeedMode.Xy;
    private OverlaySettings _settings = OverlaySettings.Load();
    private Point _dragOffset;
    private bool _dragging;

    public SpeedOverlayForm()
    {
        Text = "Deadlock Speed";
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 20, 24);
        ForeColor = Color.White;
        Opacity = 0.94;
        Width = 300;
        Height = 226;
        ShowInTaskbar = true;
        DoubleBuffered = true;

        BuildUi();
        PositionRight();
        StartListener();
        SendKeySettings();

        _staleTimer.Interval = 500;
        _staleTimer.Tick += (_, _) => UpdateStaleState();
        _staleTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        KeepTopMost();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _staleTimer.Stop();
        _toolTip.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var borderPen = new Pen(Color.FromArgb(70, 190, 160), 1);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        using var accent = new SolidBrush(Color.FromArgb(70, 190, 160));
        e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
    }

    private void BuildUi()
    {
        var closeButton = new Button
        {
            Text = "x",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(210, 215, 220),
            BackColor = Color.FromArgb(18, 20, 24),
            Location = new Point(263, 10),
            Size = new Size(24, 24),
            TabStop = false
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Close();

        _settingsButton.Text = "\u2699";
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.Font = new Font("Segoe UI Symbol", 10f);
        _settingsButton.ForeColor = Color.FromArgb(210, 215, 220);
        _settingsButton.BackColor = Color.FromArgb(18, 20, 24);
        _settingsButton.Location = new Point(234, 10);
        _settingsButton.Size = new Size(24, 24);
        _settingsButton.TabStop = false;
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 39, 46);
        _settingsButton.Click += (_, _) => ShowSettingsDialog();
        _toolTip.SetToolTip(_settingsButton, "Settings");

        _resetButton.Text = "Reset";
        _resetButton.FlatStyle = FlatStyle.Flat;
        _resetButton.ForeColor = Color.FromArgb(235, 238, 240);
        _resetButton.BackColor = Color.FromArgb(44, 50, 58);
        _resetButton.Location = new Point(224, 58);
        _resetButton.Size = new Size(58, 28);
        _resetButton.TabStop = false;
        _resetButton.FlatAppearance.BorderColor = Color.FromArgb(70, 190, 160);
        _resetButton.Click += (_, _) => SendReset();

        _stopButton.Text = "Stop";
        _stopButton.FlatStyle = FlatStyle.Flat;
        _stopButton.ForeColor = Color.FromArgb(255, 235, 235);
        _stopButton.BackColor = Color.FromArgb(74, 42, 48);
        _stopButton.Location = new Point(178, 58);
        _stopButton.Size = new Size(42, 28);
        _stopButton.TabStop = false;
        _stopButton.FlatAppearance.BorderColor = Color.FromArgb(190, 90, 96);
        _stopButton.Click += (_, _) =>
        {
            StopSessionProcesses(includeGame: true);
            Close();
        };
        _toolTip.SetToolTip(_stopButton, "Close Deadworks, Deadlock, and other speed overlays");

        _maxResetButton.Text = "";
        _maxResetButton.FlatStyle = FlatStyle.Flat;
        _maxResetButton.ForeColor = Color.FromArgb(225, 230, 234);
        _maxResetButton.BackColor = Color.FromArgb(18, 20, 24);
        _maxResetButton.Location = new Point(18, 159);
        _maxResetButton.Size = new Size(24, 24);
        _maxResetButton.TabStop = false;
        _maxResetButton.Padding = Padding.Empty;
        _maxResetButton.TextAlign = ContentAlignment.MiddleCenter;
        _maxResetButton.FlatAppearance.BorderSize = 0;
        _maxResetButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 39, 46);
        _maxResetButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 190, 160);
        _maxResetButton.Click += (_, _) => SendResetMax();
        _maxResetButton.Paint += DrawRefreshIcon;
        _toolTip.SetToolTip(_maxResetButton, "Reset max speed");

        CreateModeButton(0, "XY", SpeedMode.Xy, new Point(18, 58));
        CreateModeButton(1, "Z", SpeedMode.Z, new Point(72, 58));
        CreateModeButton(2, "3D", SpeedMode.Total, new Point(126, 58));
        RefreshModeButtons();

        var rows = new[] { _currentRow, _averageRow, _maxRow, _max5Row };
        var y = 102;
        foreach (var row in rows)
        {
            row.SetLocation(18, y);
            Controls.Add(row.NameLabel);
            Controls.Add(row.ValueLabel);
            y += 28;
        }

        _maxRow.NameLabel.Location = new Point(46, _maxRow.NameLabel.Top);
        _maxRow.NameLabel.Size = new Size(70, 24);

        _timeLabel.Text = "sampled 0.0s";
        _timeLabel.Font = new Font("Segoe UI", 8.75f);
        _timeLabel.ForeColor = Color.FromArgb(145, 150, 158);
        _timeLabel.Location = new Point(18, 18);
        _timeLabel.Size = new Size(200, 18);

        Controls.Add(closeButton);
        Controls.Add(_settingsButton);
        foreach (var button in _modeButtons)
            Controls.Add(button);

        Controls.Add(_resetButton);
        Controls.Add(_maxResetButton);
        Controls.Add(_stopButton);
        Controls.Add(_timeLabel);
        _maxResetButton.BringToFront();

        MouseDown += BeginDrag;
        MouseMove += ContinueDrag;
        MouseUp += EndDrag;

        foreach (Control control in Controls)
        {
            if (control is Button)
                continue;

            control.MouseDown += BeginDrag;
            control.MouseMove += ContinueDrag;
            control.MouseUp += EndDrag;
        }
    }

    private void PositionRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 24, area.Top + 160);
    }

    private void StartListener()
    {
        _ = Task.Run(async () =>
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, ListenPort));

            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(result.Buffer);
                var snapshot = JsonSerializer.Deserialize<SpeedSnapshot>(json);
                if (snapshot == null)
                    continue;

                try
                {
                    BeginInvoke(() => ApplySnapshot(snapshot));
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    private void ApplySnapshot(SpeedSnapshot snapshot)
    {
        _lastPacketUtc = DateTime.UtcNow;
        _lastSnapshot = snapshot;
        _activeSlot = snapshot.Slot;

        UpdateDisplayedValues();
        _timeLabel.Text = $"sampled {FormatSpeed(snapshot.Seconds)}s";
    }

    private void CreateModeButton(int index, string text, SpeedMode mode, Point location)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Location = location,
            Size = new Size(50, 28),
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) =>
        {
            _mode = mode;
            RefreshModeButtons();
            UpdateDisplayedValues();
        };

        _modeButtons[index] = button;
    }

    private void RefreshModeButtons()
    {
        for (var i = 0; i < _modeButtons.Length; i++)
        {
            var button = _modeButtons[i];
            var active = (i == 0 && _mode == SpeedMode.Xy)
                || (i == 1 && _mode == SpeedMode.Z)
                || (i == 2 && _mode == SpeedMode.Total);

            button.BackColor = active ? Color.FromArgb(70, 190, 160) : Color.FromArgb(34, 39, 46);
            button.ForeColor = active ? Color.FromArgb(10, 16, 18) : Color.FromArgb(225, 230, 234);
            button.FlatAppearance.BorderColor = active ? Color.FromArgb(70, 190, 160) : Color.FromArgb(72, 80, 90);
        }
    }

    private void UpdateDisplayedValues()
    {
        if (_lastSnapshot == null)
            return;

        var snapshot = _lastSnapshot;

        var values = _mode switch
        {
            SpeedMode.Z => (
                Current: snapshot.CurrentVertical,
                Average: snapshot.AverageVertical,
                Max: snapshot.MaxVertical,
                Max5: snapshot.Max5Vertical),
            SpeedMode.Total => (
                Current: snapshot.CurrentTotal,
                Average: snapshot.AverageTotal,
                Max: snapshot.MaxTotal,
                Max5: snapshot.Max5Total),
            _ => (
                Current: snapshot.CurrentHorizontal,
                Average: snapshot.AverageHorizontal,
                Max: snapshot.MaxHorizontal,
                Max5: snapshot.Max5Horizontal)
        };

        _currentRow.ValueLabel.Text = FormatSpeed(values.Current);
        if (snapshot.LastActionAngleDegrees.HasValue)
            _currentRow.ValueLabel.Text += $"({snapshot.LastActionAngleDegrees.Value:0}\u00B0)";

        _averageRow.ValueLabel.Text = FormatSpeed(values.Average);
        _maxRow.ValueLabel.Text = FormatSpeed(values.Max);
        _max5Row.ValueLabel.Text = FormatSpeed(values.Max5);
    }

    private static string FormatSpeed(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private void UpdateStaleState()
    {
        KeepTopMost();

        if (_lastPacketUtc == DateTime.MinValue)
            return;

        if ((DateTime.UtcNow - _lastPacketUtc).TotalSeconds <= 2.0)
            return;

        _timeLabel.Text = $"waiting on UDP {ListenPort}";
    }

    private void SendReset()
    {
        SendOverlayCommand("reset");
    }

    private void SendResetMax()
    {
        SendOverlayCommand("reset_max");
        ResetDisplayedMaxValues();
    }

    private void SendKeySettings()
    {
        SendOverlayCommand("configure_keys", _settings.DashKey, _settings.JumpKey);
    }

    private void SendOverlayCommand(string command, string? dashKey = null, string? jumpKey = null)
    {
        var payload = JsonSerializer.Serialize(new OverlayCommand(command, _activeSlot, dashKey, jumpKey));
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var udp = new UdpClient();
        udp.Send(bytes, bytes.Length, "127.0.0.1", CommandPort);
    }

    private void ShowSettingsDialog()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _settings = dialog.Settings;
        _settings.Save();
        SendKeySettings();
    }

    private void ResetDisplayedMaxValues()
    {
        if (_lastSnapshot == null)
            return;

        _lastSnapshot.MaxHorizontal = 0;
        _lastSnapshot.MaxVertical = 0;
        _lastSnapshot.MaxTotal = 0;
        _lastSnapshot.Max5Horizontal = 0;
        _lastSnapshot.Max5Vertical = 0;
        _lastSnapshot.Max5Total = 0;
        UpdateDisplayedValues();
    }

    private void DrawRefreshIcon(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var iconColor = Color.FromArgb(70, 190, 160);
        using var font = new Font("Segoe UI Symbol", 14f, FontStyle.Regular, GraphicsUnit.Point);
        TextRenderer.DrawText(
            e.Graphics,
            "\u27F3",
            font,
            new Rectangle(0, -1, _maxResetButton.Width, _maxResetButton.Height),
            iconColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void StopSessionProcesses(bool includeGame)
    {
        var currentProcessId = Environment.ProcessId;
        StopProcessesByName("DeadlockAverageSpeedOverlay", skipProcessId: currentProcessId);
        StopProcessesByName("deadworks");

        if (includeGame)
            StopProcessesByName("deadlock");
    }

    private static void StopProcessesByName(string processName, int? skipProcessId = null)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (skipProcessId == process.Id)
                    continue;

                StopProcess(process);
            }
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
            {
                if (process.WaitForExit(4000))
                    return;

                process.Refresh();
            }

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void KeepTopMost()
    {
        SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _dragging = true;
        _dragOffset = e.Location;
    }

    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var screenPoint = ((Control)sender!).PointToScreen(e.Location);
        Location = new Point(screenPoint.X - _dragOffset.X, screenPoint.Y - _dragOffset.Y);
    }

    private void EndDrag(object? sender, MouseEventArgs e)
    {
        _dragging = false;
    }

    private sealed class ValueRow
    {
        public ValueRow(string name)
        {
            NameLabel = new Label
            {
                Text = name,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(145, 150, 158),
                Size = new Size(120, 24)
            };

            ValueLabel = new Label
            {
                Text = "0.0",
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Consolas", 14.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 248, 250),
                Size = new Size(150, 26)
            };
        }

        public Label NameLabel { get; }
        public Label ValueLabel { get; }

        public void SetLocation(int x, int y)
        {
            NameLabel.Location = new Point(x, y + 6);
            ValueLabel.Location = new Point(x + 114, y);
        }
    }

    private sealed class SpeedSnapshot
    {
        public int Version { get; set; }
        public int Slot { get; set; }
        public string Player { get; set; } = "";
        public double CurrentHorizontal { get; set; }
        public double CurrentVertical { get; set; }
        public double AverageHorizontal { get; set; }
        public double AverageVertical { get; set; }
        public double MaxHorizontal { get; set; }
        public double MaxVertical { get; set; }
        public double Max5Horizontal { get; set; }
        public double Max5Vertical { get; set; }
        public double CurrentTotal { get; set; }
        public double AverageTotal { get; set; }
        public double MaxTotal { get; set; }
        public double Max5Total { get; set; }
        public double Seconds { get; set; }
        public double? LastActionAngleDegrees { get; set; }
    }

    private sealed record OverlayCommand(string Command, int Slot, string? DashKey = null, string? JumpKey = null);

    private sealed class OverlaySettings
    {
        private const string SettingsFolderName = "DeadlockAverageSpeedOverlay";
        private const string SettingsFileName = "settings.json";

        public string DashKey { get; set; } = "Shift";
        public string JumpKey { get; set; } = "Space";

        public static string SettingsPath
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, SettingsFolderName, SettingsFileName);
            }
        }

        public static OverlaySettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new OverlaySettings();

                return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsPath)) ?? new OverlaySettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new OverlaySettings();
            }
        }

        public void Save()
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class SettingsDialog : Form
    {
        private static readonly string[] KeyOptions =
        {
            "Shift",
            "LeftShift",
            "RightShift",
            "Space",
            "Ctrl",
            "LeftCtrl",
            "RightCtrl",
            "Alt",
            "LeftAlt",
            "RightAlt",
            "Mouse4",
            "Mouse5",
            "C",
            "F",
            "Q",
            "E"
        };

        private readonly ComboBox _dashCombo = new();
        private readonly ComboBox _jumpCombo = new();

        public SettingsDialog(OverlaySettings current)
        {
            Text = "Speed Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(260, 128);
            BackColor = Color.FromArgb(18, 20, 24);
            ForeColor = Color.FromArgb(235, 238, 240);
            Font = new Font("Segoe UI", 9f);

            AddLabel("Dash key", new Point(16, 18));
            AddLabel("Jump key", new Point(16, 54));
            ConfigureCombo(_dashCombo, current.DashKey, new Point(84, 14));
            ConfigureCombo(_jumpCombo, current.JumpKey, new Point(84, 50));

            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(84, 90),
                Size = new Size(70, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 190, 160),
                ForeColor = Color.FromArgb(10, 16, 18)
            };
            okButton.FlatAppearance.BorderSize = 0;

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(162, 90),
                Size = new Size(70, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 39, 46),
                ForeColor = Color.FromArgb(235, 238, 240)
            };
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(72, 80, 90);

            Controls.Add(_dashCombo);
            Controls.Add(_jumpCombo);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        public OverlaySettings Settings => new()
        {
            DashKey = _dashCombo.SelectedItem?.ToString() ?? "Shift",
            JumpKey = _jumpCombo.SelectedItem?.ToString() ?? "Space"
        };

        private void AddLabel(string text, Point location)
        {
            Controls.Add(new Label
            {
                Text = text,
                Location = location,
                Size = new Size(62, 22),
                ForeColor = Color.FromArgb(145, 150, 158),
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        private static void ConfigureCombo(ComboBox comboBox, string selectedValue, Point location)
        {
            comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox.Location = location;
            comboBox.Size = new Size(148, 24);
            comboBox.Items.AddRange(KeyOptions);
            comboBox.SelectedItem = KeyOptions.Contains(selectedValue) ? selectedValue : KeyOptions[0];
        }
    }

    private enum SpeedMode
    {
        Xy,
        Z,
        Total
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
