using DeadworksManaged.Api;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeadlockAverageSpeed;

public sealed class AverageSpeedPlugin : DeadworksPluginBase
{
    public override string Name => "Average Speed";

    private const int SampleIntervalMilliseconds = 16;
    private const int OverlayIntervalMilliseconds = 33;
    private const int OverlayCommandPollMilliseconds = 100;
    private const int WatchIntervalMilliseconds = 500;
    private const int CreepCleanupIntervalMilliseconds = 1000;
    private const int OverlayUdpPort = 47777;
    private const int OverlayCommandUdpPort = 47778;
    private const string OverlaySettingsFolderName = "DeadlockAverageSpeedOverlay";
    private const string OverlaySettingsFileName = "settings.json";
    private const double RollingMaxSeconds = 5.0;
    private const double MovementStartThreshold = 0.5;
    private const double HudIntervalSeconds = 1.0;

    private readonly Dictionary<int, SpeedStats> _statsByController = new();
    private readonly HashSet<int> _watchingControllers = new();
    private readonly HashSet<int> _hudControllers = new();
    private TrackedKey _dashKey = TrackedKey.FromName("Shift");
    private TrackedKey _jumpKey = TrackedKey.FromName("Space");
    private UdpClient? _overlayUdp;
    private UdpClient? _overlayCommandUdp;
    private IHandle? _sampleTimer;
    private IHandle? _overlayTimer;
    private IHandle? _overlayCommandTimer;
    private IHandle? _displayTimer;
    private IHandle? _creepCleanupTimer;

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine(isReload ? "[Average Speed] Reloaded" : "[Average Speed] Loaded");
        LoadActionButtonSettings();

        _sampleTimer?.Cancel();
        _overlayTimer?.Cancel();
        _overlayCommandTimer?.Cancel();
        _displayTimer?.Cancel();
        _creepCleanupTimer?.Cancel();
        _overlayUdp?.Dispose();
        _overlayCommandUdp?.Dispose();

        _overlayUdp = new UdpClient();
        _overlayCommandUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, OverlayCommandUdpPort));
        _sampleTimer = Timer.Every(SampleIntervalMilliseconds.Milliseconds(), SampleAllPlayers);
        _overlayTimer = Timer.Every(OverlayIntervalMilliseconds.Milliseconds(), BroadcastOverlayStats);
        _overlayCommandTimer = Timer.Every(OverlayCommandPollMilliseconds.Milliseconds(), PollOverlayCommands);
        _displayTimer = Timer.Every(WatchIntervalMilliseconds.Milliseconds(), PushWatchedStats);
        _creepCleanupTimer = Timer.Every(CreepCleanupIntervalMilliseconds.Milliseconds(), DisableCreeps);
    }

    public override void OnStartupServer()
    {
        Server.ExecuteCommand("sv_cheats 1");
        Server.ExecuteCommand("citadel_hero_testing_enabled true");
        Server.ExecuteCommand("citadel_hero_testing_infinite_money true");
        Server.ExecuteCommand("citadel_allow_purchasing_anywhere true");
        DisableCreeps();
        Console.WriteLine("[Average Speed] Enabled sv_cheats, hero testing, infinite souls, anywhere buying, and creep cleanup.");
    }

    public override void OnClientFullConnect(ClientFullConnectEvent args)
    {
        Timer.Once(1500.Milliseconds(), () =>
        {
            Server.ClientCommand(args.Slot, "changeteam 0");
            Server.ClientCommand(args.Slot, "citadel_hero_testing_enabled true");
        });
    }

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        if (args.Controller == null || args.Usercmds == null)
            return;

        var stats = GetOrCreateStats(args.Controller);

        foreach (var cmd in args.Usercmds)
            TrackActionAngle(args.Controller, stats, cmd);
    }

    public override void OnUnload()
    {
        _sampleTimer?.Cancel();
        _overlayTimer?.Cancel();
        _overlayCommandTimer?.Cancel();
        _displayTimer?.Cancel();
        _creepCleanupTimer?.Cancel();
        _overlayUdp?.Dispose();
        _overlayCommandUdp?.Dispose();
        _overlayUdp = null;
        _overlayCommandUdp = null;
        _statsByController.Clear();
        _watchingControllers.Clear();
        _hudControllers.Clear();
        Console.WriteLine("[Average Speed] Unloaded");
    }

    [Command("speed", Description = "Show current, average, and max movement speed")]
    public void CmdSpeed(CCitadelPlayerController caller)
    {
        var stats = GetOrCreateStats(caller);
        SendStatsToPlayer(caller, stats, useHud: false);
    }

    [Command("speedreset", Description = "Reset your average speed counters")]
    public void CmdSpeedReset(CCitadelPlayerController caller)
    {
        GetOrCreateStats(caller).Reset();
        caller.PrintToConsole("[Average Speed] Reset.");
    }

    [Command("speedwatch", Description = "Toggle periodic console speed output")]
    public void CmdSpeedWatch(CCitadelPlayerController caller, string mode = "toggle")
    {
        SetToggle(_watchingControllers, caller, mode, "console watch");
    }

    [Command("speedhud", Description = "Toggle periodic HUD speed popups")]
    public void CmdSpeedHud(CCitadelPlayerController caller, string mode = "toggle")
    {
        SetToggle(_hudControllers, caller, mode, "HUD watch");
    }

    [Command("speedall", Description = "Print average speed for every connected player")]
    public void CmdSpeedAll(CCitadelPlayerController caller)
    {
        caller.PrintToConsole("[Average Speed] Connected player stats:");

        foreach (var controller in Players.GetAll())
        {
            var stats = GetOrCreateStats(controller);
            caller.PrintToConsole($"{controller.PlayerName}: {stats.Format()}");
        }
    }

    [Command("speedoverlay", Description = "Show overlay connection info")]
    public void CmdSpeedOverlay(CCitadelPlayerController caller)
    {
        caller.PrintToConsole($"[Average Speed] Overlay stream: UDP 127.0.0.1:{OverlayUdpPort}. Start DeadlockAverageSpeedOverlay.exe.");
    }

    [Command("speedkeys", Description = "Set action angle keys. Example: speedkeys Shift Space")]
    public void CmdSpeedKeys(CCitadelPlayerController caller, string dashKey = "Shift", string jumpKey = "Space")
    {
        ConfigureActionKeys(dashKey, jumpKey);
        caller.PrintToConsole($"[Average Speed] Action angle keys: dash={_dashKey.Name}, jump={_jumpKey.Name}.");
    }

    private void SampleAllPlayers()
    {
        var seen = new HashSet<int>();

        foreach (var controller in Players.GetAll())
        {
            var key = ControllerKey(controller);
            seen.Add(key);

            var pawn = controller.GetHeroPawn();
            var stats = GetOrCreateStats(controller);

            if (pawn == null || pawn.Health <= 0)
            {
                stats.MarkPaused();
                continue;
            }

            stats.Sample(pawn.AbsVelocity);
        }

        RemoveDisconnectedPlayers(seen);
    }

    private void PushWatchedStats()
    {
        foreach (var controller in Players.GetAll())
        {
            var key = ControllerKey(controller);
            var stats = GetOrCreateStats(controller);

            if (_watchingControllers.Contains(key))
                SendStatsToPlayer(controller, stats, useHud: false);
        }

        if (_hudControllers.Count == 0)
            return;

        var now = Stopwatch.GetTimestamp();
        foreach (var controller in Players.GetAll())
        {
            var key = ControllerKey(controller);
            if (!_hudControllers.Contains(key))
                continue;

            var stats = GetOrCreateStats(controller);
            if (stats.ShouldSendHud(now, HudIntervalSeconds))
                SendStatsToPlayer(controller, stats, useHud: true);
        }
    }

    private void DisableCreeps()
    {
        Server.ExecuteCommand("trooper_kill_all");
        Server.ExecuteCommand("trooper_kill_non_bosses");
    }

    private void BroadcastOverlayStats()
    {
        if (_overlayUdp == null)
            return;

        foreach (var controller in Players.GetAll())
        {
            var stats = GetOrCreateStats(controller);
            var snapshot = stats.CreateSnapshot(controller.EntityIndex, controller.PlayerName);
            var json = JsonSerializer.Serialize(snapshot);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            try
            {
                _overlayUdp.Send(bytes, bytes.Length, "127.0.0.1", OverlayUdpPort);
            }
            catch (SocketException)
            {
                // The overlay is optional; keep the plugin running if localhost UDP is unavailable.
            }
        }
    }

    private void PollOverlayCommands()
    {
        if (_overlayCommandUdp == null)
            return;

        while (_overlayCommandUdp.Available > 0)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
                var bytes = _overlayCommandUdp.Receive(ref endpoint);
                var json = Encoding.UTF8.GetString(bytes);
                var command = JsonSerializer.Deserialize<OverlayCommand>(json);
                switch (command?.Command)
                {
                    case "reset":
                        ResetStatsForSlot(command.Slot);
                        break;
                    case "reset_max":
                        ResetMaxStatsForSlot(command.Slot);
                        break;
                    case "configure_keys":
                        ConfigureActionKeys(command.DashKey, command.JumpKey);
                        break;
                }
            }
            catch (SocketException)
            {
                return;
            }
            catch (JsonException)
            {
                // Ignore malformed overlay messages.
            }
        }
    }

    private void ResetStatsForSlot(int slot)
    {
        if (_statsByController.TryGetValue(slot, out var stats))
        {
            stats.Reset();
            return;
        }

        foreach (var controller in Players.GetAll())
        {
            if (controller.EntityIndex != slot)
                continue;

            GetOrCreateStats(controller).Reset();
            return;
        }
    }

    private void ResetMaxStatsForSlot(int slot)
    {
        if (_statsByController.TryGetValue(slot, out var stats))
        {
            stats.ResetMax();
            return;
        }

        foreach (var controller in Players.GetAll())
        {
            if (controller.EntityIndex != slot)
                continue;

            GetOrCreateStats(controller).ResetMax();
            return;
        }
    }

    private void TrackActionAngle(CCitadelPlayerController controller, SpeedStats stats, CCitadelUserCmdPB cmd)
    {
        var yaw = TryGetCameraYaw(cmd) ?? TryGetPawnYaw(controller);
        if (yaw.HasValue)
            stats.LastCameraYawDegrees = yaw.Value;

        var dashPressed = IsNewKeyPress(_dashKey.VirtualKey, ref stats.PreviousDashKeyDown);
        var jumpPressed = IsNewKeyPress(_jumpKey.VirtualKey, ref stats.PreviousJumpKeyDown);
        if (!dashPressed && !jumpPressed)
            return;

        if (stats.LastCameraYawDegrees.HasValue)
            stats.SetLastActionAngle(stats.LastCameraYawDegrees.Value);
    }

    private void LoadActionButtonSettings()
    {
        try
        {
            var settingsPath = GetOverlaySettingsPath();
            if (!File.Exists(settingsPath))
                return;

            var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(settingsPath));
            ConfigureActionKeys(settings?.DashKey, settings?.JumpKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"[Average Speed] Could not load action button settings: {ex.Message}");
        }
    }

    private void ConfigureActionKeys(string? dashKey, string? jumpKey)
    {
        if (TrackedKey.TryFromName(dashKey, out var parsedDashKey))
            _dashKey = parsedDashKey;

        if (TrackedKey.TryFromName(jumpKey, out var parsedJumpKey))
            _jumpKey = parsedJumpKey;
    }

    private static string GetOverlaySettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, OverlaySettingsFolderName, OverlaySettingsFileName);
    }

    private static bool IsNewKeyPress(int virtualKey, ref bool wasDown)
    {
        var state = GetAsyncKeyState(virtualKey);
        var isDown = (state & 0x8000) != 0;
        var wasPressedSinceLastCall = (state & 0x0001) != 0;
        var isNewPress = (isDown && !wasDown) || (wasPressedSinceLastCall && !wasDown);

        wasDown = isDown;
        return isNewPress;
    }

    private static double? TryGetCameraYaw(CCitadelUserCmdPB cmd)
    {
        if (cmd.AngCameraAngles != null && cmd.AngCameraAngles.HasY)
            return NormalizeToRightAngle(cmd.AngCameraAngles.Y);

        if (cmd.Base?.Viewangles != null && cmd.Base.Viewangles.HasY)
            return NormalizeToRightAngle(cmd.Base.Viewangles.Y);

        return null;
    }

    private static double? TryGetPawnYaw(CCitadelPlayerController controller)
    {
        var pawn = controller.GetHeroPawn();
        if (pawn == null)
            return null;

        return NormalizeToRightAngle(pawn.CameraAngles.Y);
    }

    private static double NormalizeToRightAngle(double angle)
    {
        var normalized = angle % 360.0;
        if (normalized < 0)
            normalized += 360.0;

        var folded = normalized % 180.0;
        return folded > 90.0 ? 180.0 - folded : folded;
    }

    private void SendStatsToPlayer(CCitadelPlayerController controller, SpeedStats stats, bool useHud)
    {
        var text = stats.Format();

        if (!useHud)
        {
            controller.PrintToConsole($"[Average Speed] {text}");
            return;
        }

        var msg = new CCitadelUserMsg_HudGameAnnouncement
        {
            TitleLocstring = "AVERAGE SPEED",
            DescriptionLocstring = text
        };

        NetMessages.Send(msg, RecipientFilter.Single(controller.EntityIndex - 1));
    }

    private void SetToggle(HashSet<int> set, CCitadelPlayerController caller, string mode, string label)
    {
        var key = ControllerKey(caller);
        var enabled = mode.ToLowerInvariant() switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            "toggle" => !set.Contains(key),
            _ => throw new CommandException("Use on, off, or toggle.")
        };

        if (enabled)
            set.Add(key);
        else
            set.Remove(key);

        caller.PrintToConsole($"[Average Speed] {label}: {(enabled ? "on" : "off")}.");
    }

    private SpeedStats GetOrCreateStats(CCitadelPlayerController controller)
    {
        var key = ControllerKey(controller);
        if (!_statsByController.TryGetValue(key, out var stats))
        {
            stats = new SpeedStats();
            _statsByController[key] = stats;
        }

        return stats;
    }

    private void RemoveDisconnectedPlayers(HashSet<int> seen)
    {
        foreach (var key in _statsByController.Keys.ToArray())
        {
            if (seen.Contains(key))
                continue;

            _statsByController.Remove(key);
            _watchingControllers.Remove(key);
            _hudControllers.Remove(key);
        }
    }

    private static int ControllerKey(CCitadelPlayerController controller) => controller.EntityIndex;

    private sealed class SpeedStats
    {
        private long _lastTimestamp;
        private long _lastHudTimestamp;
        private double _weightedHorizontalSum;
        private double _weightedVerticalSum;
        private double _weightedTotalSum;
        private double _seconds;
        private bool _waitingForFirstMovement = true;
        private readonly Queue<SpeedSample> _recentSamples = new();

        public double CurrentHorizontal { get; private set; }
        public double CurrentVertical { get; private set; }
        public double CurrentTotal { get; private set; }
        public double MaxHorizontal { get; private set; }
        public double MaxVertical { get; private set; }
        public double MaxTotal { get; private set; }
        public double Max5Horizontal { get; private set; }
        public double Max5Vertical { get; private set; }
        public double Max5Total { get; private set; }
        public double? LastActionAngleDegrees { get; private set; }
        public double? LastCameraYawDegrees { get; set; }
        public bool PreviousDashKeyDown;
        public bool PreviousJumpKeyDown;

        public void Reset()
        {
            _lastTimestamp = 0;
            _lastHudTimestamp = 0;
            PreviousDashKeyDown = false;
            PreviousJumpKeyDown = false;
            LastCameraYawDegrees = null;
            _weightedHorizontalSum = 0;
            _weightedVerticalSum = 0;
            _weightedTotalSum = 0;
            _seconds = 0;
            _waitingForFirstMovement = true;
            _recentSamples.Clear();
            CurrentHorizontal = 0;
            CurrentVertical = 0;
            CurrentTotal = 0;
            MaxHorizontal = 0;
            MaxVertical = 0;
            MaxTotal = 0;
            Max5Horizontal = 0;
            Max5Vertical = 0;
            Max5Total = 0;
            LastActionAngleDegrees = null;
        }

        public void ResetMax()
        {
            _recentSamples.Clear();
            MaxHorizontal = 0;
            MaxVertical = 0;
            MaxTotal = 0;
            Max5Horizontal = 0;
            Max5Vertical = 0;
            Max5Total = 0;
        }

        public void SetLastActionAngle(double angle)
        {
            LastActionAngleDegrees = angle;
        }

        public void MarkPaused()
        {
            _lastTimestamp = 0;
            PreviousDashKeyDown = false;
            PreviousJumpKeyDown = false;
            _recentSamples.Clear();
            CurrentHorizontal = 0;
            CurrentVertical = 0;
            CurrentTotal = 0;
            Max5Horizontal = 0;
            Max5Vertical = 0;
            Max5Total = 0;
        }

        public void Sample(Vector3 velocity)
        {
            var now = Stopwatch.GetTimestamp();
            var horizontal = Math.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
            var vertical = Math.Abs(velocity.Z);
            var total = velocity.Length();

            CurrentHorizontal = horizontal;
            CurrentVertical = vertical;
            CurrentTotal = total;

            if (_waitingForFirstMovement)
            {
                if (total < MovementStartThreshold)
                {
                    _lastTimestamp = 0;
                    return;
                }

                _waitingForFirstMovement = false;
                _lastTimestamp = now;
            }

            MaxHorizontal = Math.Max(MaxHorizontal, horizontal);
            MaxVertical = Math.Max(MaxVertical, vertical);
            MaxTotal = Math.Max(MaxTotal, total);
            AddRecentSample(now, horizontal, vertical, total);

            if (_lastTimestamp != 0)
            {
                var dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                if (dt > 0 && dt < 1.0)
                {
                    _weightedHorizontalSum += horizontal * dt;
                    _weightedVerticalSum += vertical * dt;
                    _weightedTotalSum += total * dt;
                    _seconds += dt;
                }
            }

            _lastTimestamp = now;
        }

        public bool ShouldSendHud(long now, double intervalSeconds)
        {
            if (_lastHudTimestamp == 0)
            {
                _lastHudTimestamp = now;
                return true;
            }

            var elapsed = (now - _lastHudTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed < intervalSeconds)
                return false;

            _lastHudTimestamp = now;
            return true;
        }

        public string Format()
        {
            var averageHorizontal = _seconds > 0 ? _weightedHorizontalSum / _seconds : 0;
            var averageVertical = _seconds > 0 ? _weightedVerticalSum / _seconds : 0;
            var averageTotal = _seconds > 0 ? _weightedTotalSum / _seconds : 0;

            return $"cur_xy={CurrentHorizontal:0.0} avg_xy={averageHorizontal:0.0} max_xy={MaxHorizontal:0.0} max5_xy={Max5Horizontal:0.0} | cur_z={CurrentVertical:0.0} avg_z={averageVertical:0.0} max_z={MaxVertical:0.0} max5_z={Max5Vertical:0.0} | cur_3d={CurrentTotal:0.0} avg_3d={averageTotal:0.0} max_3d={MaxTotal:0.0} max5_3d={Max5Total:0.0} | t={_seconds:0.0}s";
        }

        public SpeedOverlaySnapshot CreateSnapshot(int slot, string playerName)
        {
            var averageHorizontal = _seconds > 0 ? _weightedHorizontalSum / _seconds : 0;
            var averageVertical = _seconds > 0 ? _weightedVerticalSum / _seconds : 0;
            var averageTotal = _seconds > 0 ? _weightedTotalSum / _seconds : 0;

            return new SpeedOverlaySnapshot(
                2,
                slot,
                playerName,
                CurrentHorizontal,
                CurrentVertical,
                averageHorizontal,
                averageVertical,
                MaxHorizontal,
                MaxVertical,
                Max5Horizontal,
                Max5Vertical,
                CurrentTotal,
                averageTotal,
                MaxTotal,
                Max5Total,
                _seconds,
                LastActionAngleDegrees);
        }

        private void AddRecentSample(long now, double horizontal, double vertical, double total)
        {
            _recentSamples.Enqueue(new SpeedSample(now, horizontal, vertical, total));

            var oldestAllowed = now - (long)(RollingMaxSeconds * Stopwatch.Frequency);
            while (_recentSamples.Count > 0 && _recentSamples.Peek().Timestamp < oldestAllowed)
                _recentSamples.Dequeue();

            Max5Horizontal = 0;
            Max5Vertical = 0;
            Max5Total = 0;

            foreach (var sample in _recentSamples)
            {
                Max5Horizontal = Math.Max(Max5Horizontal, sample.Horizontal);
                Max5Vertical = Math.Max(Max5Vertical, sample.Vertical);
                Max5Total = Math.Max(Max5Total, sample.Total);
            }
        }

        private sealed record SpeedSample(long Timestamp, double Horizontal, double Vertical, double Total);
    }

    private sealed record SpeedOverlaySnapshot(
        int Version,
        int Slot,
        string Player,
        double CurrentHorizontal,
        double CurrentVertical,
        double AverageHorizontal,
        double AverageVertical,
        double MaxHorizontal,
        double MaxVertical,
        double Max5Horizontal,
        double Max5Vertical,
        double CurrentTotal,
        double AverageTotal,
        double MaxTotal,
        double Max5Total,
        double Seconds,
        double? LastActionAngleDegrees);

    private sealed class OverlaySettings
    {
        public string DashKey { get; set; } = "Shift";
        public string JumpKey { get; set; } = "Space";
    }

    private sealed class OverlayCommand
    {
        public string Command { get; set; } = "";
        public int Slot { get; set; }
        public string? DashKey { get; set; }
        public string? JumpKey { get; set; }
    }

    private readonly record struct TrackedKey(string Name, int VirtualKey)
    {
        private static readonly Dictionary<string, int> KeyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Shift"] = 0x10,
            ["LeftShift"] = 0xA0,
            ["RightShift"] = 0xA1,
            ["Space"] = 0x20,
            ["Ctrl"] = 0x11,
            ["LeftCtrl"] = 0xA2,
            ["RightCtrl"] = 0xA3,
            ["Alt"] = 0x12,
            ["LeftAlt"] = 0xA4,
            ["RightAlt"] = 0xA5,
            ["Mouse4"] = 0x05,
            ["Mouse5"] = 0x06,
            ["C"] = 0x43,
            ["F"] = 0x46,
            ["Q"] = 0x51,
            ["E"] = 0x45
        };

        public static TrackedKey FromName(string name)
        {
            return TryFromName(name, out var key) ? key : new TrackedKey("Shift", 0x10);
        }

        public static bool TryFromName(string? name, out TrackedKey key)
        {
            if (!string.IsNullOrWhiteSpace(name) && KeyMap.TryGetValue(name, out var virtualKey))
            {
                key = new TrackedKey(name, virtualKey);
                return true;
            }

            key = default;
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
