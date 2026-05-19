# Deadlock Average Speed

Local/custom Deadlock practice tooling built around Deadworks.

It includes:

- a Deadworks server plugin that samples hero velocity from `AbsVelocity`;
- a small always-on-top Windows overlay;
- one-command session startup for Deadworks, overlay, and Deadlock;
- training server helpers such as cheats, infinite souls, anywhere buying, and trooper cleanup.

This is intended for local/custom servers only. It does not read process memory, inject into the game client, or target normal matchmaking.

## Quick Start

```bat
DeadlockAverageSpeed\start-speed-session.cmd
```

The script builds and installs the plugin, restarts Deadworks, starts the overlay, and launches Deadlock with:

```text
+connect localhost:27067
```

More details are in:

```text
DeadlockAverageSpeed\README.md
```

## Publish Private GitHub Repo

After installing GitHub CLI and logging in:

```bat
publish-private-github.cmd
```

By default it creates a private repository named `deadlock-average-speed`.
