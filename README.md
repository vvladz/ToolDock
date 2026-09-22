# ToolDock

ToolDock installs Windows tool packages from GitHub Releases, keeps them up to date, and supervises background applications in the current user session. Managed applications require no ToolDock-specific integration.

ToolDock works without administrator privileges, inbound connections, SSH, or Windows Services. It installs three executables:

- `ToolDock.Starter.exe`: a long-running, windowless process supervisor;
- `ToolDock.Updater.exe`: a windowless, one-shot updater launched by Task Scheduler;
- `ToolDock.Client.exe`: the interactive console client exposed through the `tdctl` shim.

All three executables are published as framework-dependent single files. They require the .NET 10 x64 runtime but do not carry separate ToolDock or Microsoft.Extensions assemblies.

## Requirements

- Windows 10/11 x64;
- public GitHub Releases for ToolDock and its managed packages;
- [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0);
- PowerShell 5.1 or newer;
- an interactive sign-in for the current user.

## Installation

Choose the HTTPS URL of your package catalog, then install the latest ToolDock release:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/vvladz/ToolDock/master/install.ps1'))) `
    -Repository 'vvladz/ToolDock' `
    -CatalogUrl 'https://example.org/tools.json'
```

To inspect the script first:

```powershell
Invoke-WebRequest 'https://raw.githubusercontent.com/vvladz/ToolDock/master/install.ps1' -OutFile install.ps1
.\install.ps1 -Repository 'vvladz/ToolDock' -CatalogUrl 'https://example.org/tools.json'
```

`-Repository` uses GitHub's `owner/repository` form. ToolDock supplies the `https://api.github.com/repos/` base URI. `-CatalogUrl` is an independent, absolute HTTPS URL.

The installer:

- places ToolDock in `%LOCALAPPDATA%\ToolDock`;
- adds `%LOCALAPPDATA%\ToolDock\bin` to the user `PATH`;
- creates the `ToolDock Starter` and `ToolDock Updater` scheduled tasks;
- starts the supervisor and performs the first update;
- verifies the release archive against its published SHA-256 file.

Run the installer again to update ToolDock itself. Use `-Version v1.2.3` to select a specific release.

## Package catalog

A catalog entry describes one release package and its command and daemon entry points:

```json
{
  "tools": {
    "farshell": {
      "repo": "owner/farshell",
      "asset": "farshell-win-x64.zip",
      "enabled": true,
      "commands": {
        "farshell": {
          "executable": "farshell.exe",
          "environment": {
            "FARSHELL_SERVER": {
              "variable": "farshell.server"
            },
            "OPENAI_API_KEY": {
              "secret": "openai.api-key"
            }
          }
        }
      },
      "daemons": {
        "farshell": {
          "executable": "farshelld.exe",
          "arguments": [],
          "autostart": true,
          "restartOnUpdate": true,
          "environment": {
            "LOG_LEVEL": "info"
          }
        }
      }
    }
  }
}
```

One package may contain any combination of CLI commands and background daemons. The same executable may serve both roles; daemon arguments can select its background mode:

```json
"commands": {
  "example": {
    "executable": "example.exe"
  }
},
"daemons": {
  "example": {
    "executable": "example.exe",
    "arguments": ["serve"],
    "autostart": true,
    "restartOnUpdate": true
  }
}
```

Fields:

| Field | Meaning |
|---|---|
| `repo` | GitHub repository in `owner/name` form |
| `asset` | Exact ZIP asset name in the latest release |
| `enabled` | Whether ToolDock may update and start the package |
| `commands` | Globally unique command names mapped to command definitions |
| `daemons` | Globally unique supervised-daemon names and their launch settings |
| `executable` | Relative executable path inside the ZIP |
| `arguments` | Optional daemon arguments |
| `autostart` | Start the daemon after sign-in or its first installation |
| `restartOnUpdate` | Restart the daemon after its package changes, if it is currently running |
| `environment` | Optional process environment made from literals, variables, and secret references |

Versions are installed side by side and exposed through a `current` directory junction:

```text
%LOCALAPPDATA%\ToolDock\tools\farshell\
├── v1.0.0\
├── v1.1.0\
└── current\        directory junction → v1.1.0
```

Each command gets a shim in `%LOCALAPPDATA%\ToolDock\bin`. The shim delegates execution to `ToolDock.Client.exe`, which resolves the installed executable and its environment, preserves the calling terminal streams, and returns the child exit code. Daemons are started from the exact installed-version directory recorded in ToolDock state.

## Variables and secrets

Environment entries support three forms:

```json
{
  "environment": {
    "LOG_LEVEL": "info",
    "SERVICE_URL": {
      "variable": "service.url"
    },
    "ACCESS_TOKEN": {
      "secret": "service.token"
    }
  }
}
```

- literals live in the public catalog;
- variables are user-scoped named plaintext values in `%LOCALAPPDATA%\ToolDock\variables.json`;
- secrets are user-scoped named values protected with Windows DPAPI `CurrentUser` in `%LOCALAPPDATA%\ToolDock\secrets\secrets.dat`.

Variables and secrets are global within the current user's ToolDock installation, so multiple commands or daemons can reference the same name. They are not added to the global Windows environment; ToolDock injects them only into configured child processes.

Manage variables with:

```powershell
tdctl variable set service.url https://service.example
tdctl variable get service.url
tdctl variable list
tdctl variable status
tdctl variable remove service.url
```

Manage secrets with:

```powershell
tdctl secret set service.token
tdctl secret list
tdctl secret status
tdctl secret remove service.token
```

Secret input is hidden and is never accepted as a command-line value. `tdctl secret set <name> --stdin` is available for controlled automation. There is no normal secret `get` operation. A missing variable or secret prevents process creation. Changes take effect the next time a command starts or a daemon is started or restarted.

## Control client

```powershell
tdctl list
tdctl status farshell
tdctl start farshell
tdctl restart farshell
tdctl stop farshell
tdctl update
tdctl variable status
tdctl secret status
```

Process commands are sent to `ToolDock.Starter.exe` over a current-user-only Named Pipe. `tdctl update` runs the same update coordinator as the scheduled updater. A cross-process named mutex prevents both update hosts from running concurrently.

After a package update, the update engine notifies the starter. The starter alone owns process and Job Object handles, and restarts the running daemons from that package that have `restartOnUpdate` enabled. A stopped daemon remains stopped; an autostart daemon is started after its first installation.

## Logs

Logs are stored in `%LOCALAPPDATA%\ToolDock\logs`:

- `ToolDock.Starter.log`: supervisor and Named Pipe diagnostics;
- `ToolDock.Updater.log`: scheduled and interactive update diagnostics;
- `ToolDock.Client.log`: managed-command execution and environment diagnostics;
- `<daemon>.log`: captured stdout, stderr, and lifecycle events.

View them through the client:

```powershell
tdctl logs ToolDock.Updater
tdctl logs farshell --lines 200
tdctl logs farshell --follow
```

Daemon diagnostics use `Microsoft.Extensions.Logging`. Scheduled updates write to `ToolDock.Updater.log`; `tdctl update` writes the same operation to `ToolDock.Updater.log` and the terminal. Process launches log every catalog-configured environment entry. Literals use `ENV_NAME: value`, variables use `ENV_NAME: variable.name -> value`, and secrets use `ENV_NAME: secret.name -> *******`. Inherited environment entries are not logged.

Logs rotate at 10 MB with five archives retained.

## Build and release

Local builds require the .NET 10 SDK:

```powershell
dotnet build ToolDock.sln -c Release
```

Pull requests build, package, verify the single-file layout, and run the Windows integration tests. Every successful push to `master` creates a GitHub Release containing `ToolDock-win-x64.zip` and its SHA-256 file. Release patch versions use the stable GitHub Actions run number.

See [ToolDock.md](ToolDock.md) for the detailed architecture and invariants.
