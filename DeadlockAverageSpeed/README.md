# Deadlock Average Speed

Deadworks server-side plugin for local/custom Deadlock practice servers. It samples each hero pawn's `AbsVelocity`, calculates time-weighted average speed, and reports current/average/max speed.

This does not read game memory from an external process, inject into normal matchmaking, or use `cl_showpos`. It is meant for local/custom servers only.

## One-command start

From the repository root:

```bat
start-speed-session.cmd
```

Or from this folder:

```bat
start-speed-session.cmd
```

That command builds and installs the plugin, restarts Deadworks, starts the overlay, and launches Deadlock with `+connect localhost:27067`.

Useful options:

```bat
start-speed-session.cmd -NoBuild
start-speed-session.cmd -NoGame
start-speed-session.cmd -ShowServer
```

To close Deadworks, the overlay, and Deadlock:

```bat
stop-speed-session.cmd
```

Use this if you want to leave Deadlock open and only stop the local tooling:

```bat
stop-speed-session.cmd -KeepGame
```

## Requirements

- Deadlock installed through Steam.
- Deadworks installed into the Deadlock folder.
- .NET 10 SDK.

Deadworks project setup docs: <https://docs.deadworks.net/getting-started/setup/>

## Build

From this folder:

```powershell
dotnet build .\DeadlockAverageSpeed.csproj -c Release
```

If Deadlock is not installed in the default Steam folder:

```powershell
dotnet build .\DeadlockAverageSpeed.csproj -c Release /p:DeadlockDir="D:\SteamLibrary\steamapps\common\Deadlock"
```

If you only have the Deadworks SDK unpacked somewhere else:

```powershell
dotnet build .\DeadlockAverageSpeed.csproj -c Release /p:DeadworksSdkDir="C:\path\to\deadworks"
```

To build and copy the plugin into Deadworks' plugin folder:

```powershell
dotnet build .\DeadlockAverageSpeed.csproj -c Release /p:DeployToDeadlock=true
```

Or run the installer script:

```powershell
.\install-plugin.ps1
```

The deploy target copies output to:

```text
Deadlock\game\bin\win64\managed\plugins
```

## Run

1. Start Deadworks from `Deadlock\game\bin\win64\deadworks.exe`.
2. Open Deadlock.
3. Connect to your local server:

```text
connect localhost:27067
```

## Commands

Use these in chat or console:

- `/speed` or `dw_speed`: print current stats once.
- `/speedreset` or `dw_speedreset`: reset your stats.
- `/speedwatch on|off|toggle`: print stats to your console every 0.5 seconds.
- `/speedhud on|off|toggle`: show stats as a HUD announcement about once per second.
- `/speedall`: print stats for all connected players to your console.
- `/speedkeys <dashKey> <jumpKey>`: change which physical keys capture the last camera angle. Defaults are `Shift` and `Space`.

Stats include:

- `cur_xy`, `avg_xy`, `max_xy`: horizontal speed, closest to normal movement speed.
- `cur_3d`, `avg_3d`, `max_3d`: full 3D speed including vertical movement.
- `t`: sampled alive time after the last reset.

## Notes

Deadworks documentation currently lists custom UI as in development, so the plugin uses console output and optional HUD announcements instead of a persistent custom HUD panel.

## Overlay Window

For a cleaner always-on-top box on the right side of the screen, run:

```powershell
dotnet run --project ..\DeadlockAverageSpeedOverlay\DeadlockAverageSpeedOverlay.csproj -c Release
```

Or start the built exe:

```powershell
..\DeadlockAverageSpeedOverlay\bin\Release\net10.0-windows\DeadlockAverageSpeedOverlay.exe
```

The plugin streams speed stats to the overlay over local UDP:

```text
127.0.0.1:47777
```

The overlay sends its Reset button command back to the plugin over:

```text
127.0.0.1:47778
```

You can check the port in-game with:

```text
/speedoverlay
```

The overlay mode selector changes which speed is shown:

- `XY`: horizontal ground speed.
- `Z`: absolute vertical speed.
- `3D`: total speed.

`CURRENT` shows the last dash/jump camera yaw in parentheses after the speed, for example `100.0(123°)`.
Use the gear button to change the dash/jump keys if your setup uses different keybinds.

`MAX` is the all-time max since the last reset. `MAX 5S` is the highest speed seen during the last 5 seconds.

The overlay's `Stop` button closes Deadworks, Deadlock, and any other speed overlay windows.
