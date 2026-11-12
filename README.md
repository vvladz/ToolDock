# ToolDock

ToolDock runs background programs under your Windows user account. Give each program a JSON configuration, then use `start`, `stop`, and `status` to manage it. ToolDock can restart a program when it exits and collect its output in rotating log files.

Target platform: modern Windows on AMD64/x64, without administrator privileges.

## Build and run

Install the .NET 10 SDK on Windows, then build from the repository root:

```powershell
dotnet build ToolDock.slnx -c Release
```

The CLI is generated in `src/ToolDock/bin/Release/net10.0-windows/win-x64/`. Keep that directory's files together. Add it to your `PATH`, or invoke the executable by its full path:

```powershell
& './src/ToolDock/bin/Release/net10.0-windows/win-x64/ToolDock.exe' --help
```

This build requires the .NET 10 runtime. Configuration is stored in your user profile independently of the executable directory.

## Configure a program

Place configuration files directly in `$HOME/.config/ToolDock/tools/`, where `$HOME` is your Windows user profile. Each file defines a named daemon: a program that ToolDock manages in the background.

The file name without `.json` is the daemon name. Names are case-insensitive: `work-tunnel` and `WORK-TUNNEL` refer to the same daemon. Different names can run the same executable with different arguments.

For example, create `work-tunnel.json`:

```json
{
  "executable": "tunnel.exe",
  "arguments": ["--config", "work.json"],
  "workingDirectory": "./data/work",
  "environment": {},
  "restart": "on-failure",
  "restartDelaySeconds": 5
}
```

`tunnel.exe` and its arguments are placeholders. Replace them with your program's executable name and arguments. The executable must be available on the `PATH` used to launch ToolDock. ToolDock does not install or update programs.

| Field | Description | Default |
| --- | --- | --- |
| `executable` | An `.exe` file name to find on ToolDock's `PATH` | Required |
| `arguments` | An array of argument strings | `[]` |
| `workingDirectory` | An existing working directory for the program | The configuration file's directory |
| `environment` | Environment variables to add or override for the program | `{}`; other variables are inherited |
| `restart` | `never`, `on-failure`, or `always` | `on-failure` |
| `restartDelaySeconds` | A positive delay between attempts, in seconds | `5` |

Relative working directories are resolved from the configuration file's directory. In this example, `./data/work` means `$HOME/.config/ToolDock/tools/data/work`. Create that directory before starting the daemon. The program interprets `work.json` relative to its own working directory.

Write each argument as a separate JSON string, including arguments that contain spaces. ToolDock handles command-line quoting; it does not run a shell or expand variables in arguments. For example:

```json
{
  "executable": "worker.exe",
  "arguments": ["--input", "C:\\Work Files\\input.json", "--label", "nightly run"]
}
```

An `environment` override applies to the child program. It does not change the `PATH` ToolDock uses to select the executable.

## Start and stop

Run these commands from PowerShell with `ToolDock.exe` available on your `PATH`:

```powershell
ToolDock.exe start work-tunnel
ToolDock.exe status work-tunnel
ToolDock.exe stop work-tunnel
```

`start` launches a background runner and returns after the first program process has been created and started. It confirms process creation, not application readiness. The runner continues after the command exits and its console closes in supported launch environments.

Starting a daemon that already has a runner succeeds with `already running`. Stopping a daemon that is already stopped succeeds with `already stopped`.

`stop` cancels automatic restarts and forcibly terminates the managed process group. Programs should not rely on receiving a graceful shutdown notification. Child processes that explicitly leave the group using Windows breakaway are responsible for their own lifetime.

To keep ToolDock attached to your terminal and see output as it arrives:

```powershell
ToolDock.exe run work-tunnel
```

Press Ctrl+C to stop it. You can also stop this runner with `ToolDock.exe stop work-tunnel` from another terminal, including another Windows session under the same user account.

## Restart behavior

| Policy | After the program exits |
| --- | --- |
| `never` | Stop the runner |
| `on-failure` | Restart after a nonzero exit code; stop after exit code `0` |
| `always` | Restart regardless of the exit code |

There is no retry limit. The configured delay applies between attempts. A manual stop cancels retries, including a retry that is waiting for its delay to expire.

Changes to a configuration file take effect on the next runner launch. Stop and start the daemon to apply them:

```powershell
ToolDock.exe stop work-tunnel
if ($LASTEXITCODE -eq 0) {
    ToolDock.exe start work-tunnel
}
```

Automatic restarts keep using the executable path and settings selected at the beginning of the runner's lifetime. Switching a link to a different installed copy does not change that selection. Keep the selected program files available while its runner is active.

## Check status

`status` describes the ToolDock runner. A runner waiting to restart its program is still `running`.

| Status | Meaning |
| --- | --- |
| `running` | A runner is active |
| `stopped` | No runner is active; the recorded result is successful or unknown |
| `failed` | No runner is active; a nonzero runner result was recorded |

Use JSON output in scripts:

```powershell
$output = ToolDock.exe status work-tunnel --json
if ($LASTEXITCODE -eq 0) {
    $status = $output | ConvertFrom-Json
    $status.status
}
```

Example response:

```json
{
  "daemon": "work-tunnel",
  "status": "running",
  "pid": 1234,
  "startedAtUtc": "2026-11-12T15:20:00Z",
  "finishedAtUtc": null,
  "exitCode": null
}
```

The PID and timestamps describe the last recorded runner launch. Missing information is `null`; timestamps use UTC. `exitCode` is the runner's result, not the child program's exit code. Child exit codes are written to the log. A forced runner termination can leave its result unknown.

`stop` and `status` remain usable after the daemon's configuration has been changed or deleted.

## Read logs

Each daemon has a combined log under `$HOME/.config/ToolDock/logs/<daemon>/`. Files contain the program's stdout, stderr, and runner events:

```text
2026-11-12T15:20:00.000Z [runner] starting
2026-11-12T15:20:00.125Z [stdout] tunnel connected
2026-11-12T15:21:03.410Z [stderr] connection lost
2026-11-12T15:21:03.420Z [runner] daemon exited code=1; retry in 5s
```

Logs use UTF-8 and UTC timestamps. They rotate daily and at approximately 20 MiB, retaining up to 10 files per daemon. `run` also displays the log in your terminal.

## Commands and exit codes

```text
ToolDock.exe start <daemon>
ToolDock.exe run <daemon>
ToolDock.exe stop <daemon>
ToolDock.exe status <daemon> [--json]
ToolDock.exe --help
ToolDock.exe --version
```

Short commands write results to stdout and errors to stderr.

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded |
| `1` | An operation, configuration, or state error, or a timeout |
| `2` | Invalid command-line syntax |

A successful `status` command returns `0` even when the reported status is `failed`. Read its `exitCode` field for the recorded runner result.

`start` waits up to 30 seconds and `stop` up to 10 seconds. These limits and log rotation settings are currently fixed. Restart policy and delay are configurable per daemon.

## Start at sign-in

Use an external PowerShell script or your preferred sign-in mechanism to select and start daemons:

```powershell
ToolDock.exe start work-tunnel
ToolDock.exe start personal-tunnel
```

Each daemon needs its own configuration file. Re-running the script does not create duplicate instances. ToolDock handles child program restarts; restarting a failed ToolDock runner is the responsibility of the external launcher.

## Troubleshooting

- **Executable not found:** check the `PATH` in the environment that launches ToolDock. The configuration directory is not implicitly searched for executables.
- **Working directory not found:** create it and check that a relative path is resolved from the configuration file's directory.
- **Configuration changes have no effect:** stop and start the daemon to load the new settings.
- **State access error:** check access to `$HOME/.config/ToolDock/state/`. The command reports an error rather than treating unreadable state as a stopped daemon.
- **Background launch fails under a host or SSH session:** that environment may prevent the runner from detaching from its process group. ToolDock reports the launch failure; launch support must be verified for that environment.

ToolDock stores configuration, logs, and runtime state separately:

```text
~/.config/ToolDock/
  tools/<daemon>.json
  logs/<daemon>/log-*.log
  state/<daemon>.lock
  state/<daemon>.json
```

State files can remain after a daemon stops. Use `status` to check activity; a saved PID or an existing lock file alone does not indicate a running daemon.
