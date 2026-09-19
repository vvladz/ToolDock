# ToolDock

ToolDock installs Windows CLI tools from GitHub Releases, keeps them up to date, and can run background applications in the current user session. Managed applications require no ToolDock-specific integration.

ToolDock works without administrator privileges, inbound connections, SSH, or Windows Services. Two invisible Task Scheduler tasks run:

- `starter.exe`, which manages processes and their complete child-process trees;
- `updater.exe`, which periodically reads the tool catalog and installs new releases.

## Requirements

- Windows 10/11 x64;
- public GitHub Releases for ToolDock and its managed tools;
- PowerShell 5.1 or newer;
- an interactive sign-in for the current user.

Releases are self-contained, so a separate .NET installation is not required.

## Installation

Replace `OWNER/ToolDock` with the repository in which ToolDock is published:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OWNER/ToolDock/main/install.ps1'))) -Repository 'OWNER/ToolDock'
```

To inspect the remote script before running it, download it first:

```powershell
Invoke-WebRequest 'https://raw.githubusercontent.com/OWNER/ToolDock/main/install.ps1' -OutFile install.ps1
.\install.ps1 -Repository 'OWNER/ToolDock'
```

By default, ToolDock loads the catalog from `https://raw.githubusercontent.com/OWNER/ToolDock/main/tools.json`. To use another URL, specify it explicitly:

```powershell
.\install.ps1 -Repository 'OWNER/ToolDock' -CatalogUrl 'https://example.org/tools.json'
```

The installer:

- places files in `%LOCALAPPDATA%\ToolDock`;
- adds `%LOCALAPPDATA%\ToolDock\bin` to the user `PATH`;
- creates the `ToolDock Starter` and `ToolDock Updater` scheduled tasks;
- starts the supervisor and performs the first update check.

Before extracting a release, the installer verifies it against the published `ToolDock-win-x64.zip.sha256` file.

To update ToolDock itself, run the installer again. Use `-Version v1.2.3` to install a specific release.

## Tool catalog

Edit `tools.json` on the default branch:

```json
{
  "tools": {
    "farshell": {
      "repo": "owner/farshell",
      "asset": "farshell-win-x64.zip",
      "enabled": true,
      "autostart": true,
      "restart": true
    }
  }
}
```

The release ZIP must contain `<tool>.exe` at its root. If the executable is located elsewhere, specify its relative path:

```json
"executable": "app/devproxy.exe"
```

Catalog fields:

| Field | Meaning |
|---|---|
| `repo` | GitHub repository in `owner/name` form |
| `asset` | Exact ZIP asset name in the latest release |
| `enabled` | Whether the tool may be updated and started |
| `autostart` | Whether to start the tool after sign-in or its first installation |
| `restart` | Whether to restart the process after an update |
| `executable` | Optional path to the executable inside the ZIP |

New versions are installed alongside existing versions:

```text
%LOCALAPPDATA%\ToolDock\tools\farshell\
├── v1.0.0\
├── v1.1.0\
└── current\        directory junction → v1.1.0
```

The updater creates `%LOCALAPPDATA%\ToolDock\bin\farshell.cmd`. After opening a new terminal, the tool can be launched as a regular command:

```powershell
farshell --help
```

## Process management

```powershell
starter status farshell
starter start farshell
starter restart farshell
starter stop farshell
```

Each application is created in a suspended state, assigned to its own Windows Job Object, and only then allowed to run. As a result, `stop` and `restart` terminate the application's complete process tree rather than only its root PID.

Run an update check manually with:

```powershell
updater
```

Manual invocations write output to the current terminal. Task Scheduler runs do not create console windows.

## Diagnostics

Logs are stored in `%LOCALAPPDATA%\ToolDock\logs`:

- `starter.log` contains supervisor and Named Pipe activity;
- `updater.log` contains catalog, GitHub, and installation activity;
- `<tool>.log` contains the tool's stdout, stderr, and process lifecycle events.

Logs rotate at 10 MB, with five archives retained. If `starter` is unavailable during an update, the installation still succeeds and a warning is written to `updater.log`.

## Build and release

Local builds require the .NET 10 SDK:

```powershell
dotnet build ToolDock.sln -c Release
```

The `.github/workflows/build-release.yml` workflow builds the solution on Windows. A `v*` tag also publishes `ToolDock-win-x64.zip` and its SHA-256 checksum to a GitHub Release.

CI exercises a real `autostart → stdout/stderr → stop` cycle and verifies that closing the Job Object terminates both the root and child processes.

See [ToolDock.md](ToolDock.md) for the detailed architecture, constraints, and non-goals.
