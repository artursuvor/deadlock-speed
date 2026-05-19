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
    private readonly ValueRow _currentRow = new("CURRENT");
    private readonly ValueRow _averageRow = new("AVERAGE");
    private readonly ValueRow _maxRow = new("MAX");
    private readonly ValueRow _max5Row = new("MAX 5S");
    private readonly Label _timeLabel = new();
    private readonly System.Windows.Forms.Timer _staleTimer = new();

    private DateTime _lastPacketUtc = DateTime.MinValue;
    private SpeedSnapshot? _lastSnapshot;
    private int _activeSlot;
    private SpeedMode _mode = SpeedMode.Xy;
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

        _resetButton.Text = "Reset";
        _resetButton.FlatStyle = FlatStyle.Flat;
        _resetButton.ForeColor = Color.FromArgb(235, 238, 240);
        _resetButton.BackColor = Color.FromArgb(44, 50, 58);
        _resetButton.Location = new Point(218, 58);
        _resetButton.Size = new Size(64, 28);
        _resetButton.TabStop = false;
        _resetButton.FlatAppearance.BorderColor = Color.FromArgb(70, 190, 160);
        _resetButton.Click += (_, _) => SendReset();

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

        _timeLabel.Text = "sampled 0.0s";
        _timeLabel.Font = new Font("Segoe UI", 8.75f);
        _timeLabel.ForeColor = Color.FromArgb(145, 150, 158);
        _timeLabel.Location = new Point(18, 18);
        _timeLabel.Size = new Size(230, 18);

        Controls.Add(closeButton);
        foreach (var button in _modeButtons)
            Controls.Add(button);

        Controls.Add(_resetButton);
        Controls.Add(_timeLabel);

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

                BeginInvoke(() => ApplySnapshot(snapshot));
            }
        }, _cts.Token);
    }

    private void ApplySnapshot(SpeedSnapshot snapshot)
    {
        _lastPacketUtc = DateTime.UtcNow;
        _lastSnapshot = snapshot;
        _activeSlot = snapshot.Slot;

        UpdateDisplayedValues();
        _timeLabel.Text = $"sampled {snapshot.Seconds:0.0}s";
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

        _currentRow.ValueLabel.Text = values.Current.ToString("0.0");
        _averageRow.ValueLabel.Text = values.Average.ToString("0.0");
        _maxRow.ValueLabel.Text = values.Max.ToString("0.0");
        _max5Row.ValueLabel.Text = values.Max5.ToString("0.0");
    }

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
        var payload = JsonSerializer.Serialize(new OverlayCommand("reset", _activeSlot));
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var udp = new UdpClient();
        udp.Send(bytes, bytes.Length, "127.0.0.1", CommandPort);
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
                Font = new Font("Consolas", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 248, 250),
                Size = new Size(124, 26)
            };
        }

        public Label NameLabel { get; }
        public Label ValueLabel { get; }

        public void SetLocation(int x, int y)
        {
            NameLabel.Location = new Point(x, y + 6);
            ValueLabel.Location = new Point(x + 138, y);
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
    }

    private sealed record OverlayCommand(string Command, int Slot);

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
