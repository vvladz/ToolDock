# ToolDock architecture

ToolDock is a user-scoped Windows package updater and process supervisor. It installs release packages, exposes their CLI commands, and runs selected background applications in the current interactive user session.

## Invariants

- Managed packages remain ordinary standalone ZIP archives and executables.
- GitHub polling and installation stay outside managed tools.
- Only the starter owns managed processes and their Job Objects.
- Only one update operation may run in the user session at a time.
- ToolDock requires no administrator privileges, Windows Service, SSH, or inbound connection.
- Scheduled components never create console windows.
- CLI output and persistent diagnostic logging remain separate concerns.

## Executables

```text
ToolDock.Starter.exe   WinExe   long-running process supervisor
ToolDock.Updater.exe   WinExe   scheduled one-shot update host
ToolDock.Client.exe    Exe      interactive control host
tdctl.cmd                       short client shim
```

`ToolDock.Starter.exe` and `ToolDock.Updater.exe` are Windows-subsystem applications. Task Scheduler can launch them without creating or hiding a console window. They always write diagnostics to files.

`ToolDock.Client.exe` is a console-subsystem application. It writes results to stdout, errors to stderr, and never needs to attach to a parent console manually.

The three executables are framework-dependent single-file publications for `win-x64`. ToolDock assemblies and logging dependencies are bundled into each executable, while the shared .NET 10 runtime remains an installation prerequisite.

## Component boundaries

```text
                            GitHub Releases
                                  │
                                  ▼
                    ┌─────────────────────────┐
Task Scheduler ────►│ ToolDock.Updater.exe    │
                    │                         │
tdctl update ──────►│ UpdateCoordinator       │
                    │ UpdateEngine            │
                    └────────────┬────────────┘
                                 │ install package
                                 │ switch junction
                                 │ save state
                                 ▼
                           Package storage
                                 │
                                 │ package-updated
                                 ▼
tdctl start/status ──pipe──► ToolDock.Starter.exe
                                 │
                     ┌───────────┼───────────┐
                     ▼           ▼           ▼
                  Job[a]      Job[b]      Job[c]
```

### Starter

The starter is stateful and exclusive. It:

- loads daemon definitions from the cached catalog;
- starts configured autostart daemons;
- owns the process and Job Object handles;
- starts, stops, restarts, and reports daemon status;
- captures stdout and stderr;
- accepts local commands through `ToolDock.Starter.v1`;
- reconciles running daemons after a package update.

The starter does not download, install, or version packages. Its singleton mutex is:

```text
Local\ToolDock.Starter
```

### Update coordinator and engine

`ToolDock.Updating` contains the shared update implementation. Both update hosts call the same synchronous coordinator entry point:

```text
UpdateCoordinator.Run(cancellationToken)
    acquire Local\ToolDock.Update
    run asynchronous UpdateEngine work
    release mutex on the acquiring thread
```

The synchronous boundary is intentional because a Windows mutex must be released by the thread that acquired it. The coordinator returns one of:

```text
Completed
CompletedWithErrors
AlreadyRunning
Cancelled
Failed
```

The engine:

1. downloads and validates the catalog;
2. resolves the latest GitHub release for every enabled package;
3. downloads and extracts a changed package into a staging directory;
4. validates every declared executable;
5. moves the staged version into its final directory;
6. atomically switches the `current` junction;
7. creates command shims and saves installed state;
8. asks the starter to reconcile the updated package.

The scheduled updater and interactive client differ only at the host boundary:

| Host | Subsystem | Cancellation | Log providers |
|---|---|---|---|
| `ToolDock.Updater.exe` | WinExe | process lifetime | rotating `ToolDock.Updater.log` |
| `tdctl update` | Exe | Ctrl+C | rotating `ToolDock.Updater.log` and terminal |

### Client

The client has three kinds of operation:

- process control goes through the starter Named Pipe;
- `update` directly runs the shared update coordinator;
- `logs` reads rotating files with read/write/delete sharing and optionally follows them.

The client does not own managed processes and does not have a `client.log`.

## Catalog model

The top-level `tools` object maps package names to installation definitions:

```json
{
  "tools": {
    "package-name": {
      "repo": "owner/repository",
      "asset": "package-win-x64.zip",
      "enabled": true,
      "commands": {
        "command-name": "relative/path/command.exe"
      },
      "daemons": {
        "daemon-name": {
          "executable": "relative/path/daemon.exe",
          "arguments": ["serve"],
          "autostart": true,
          "restartOnUpdate": true
        }
      }
    }
  }
}
```

Package names, command names, and daemon names are case-insensitively unique within their respective namespaces. Command and daemon executable paths must remain under the extracted package directory.

## Installation layout

```text
%LOCALAPPDATA%\ToolDock\
├── bin\
│   ├── ToolDock.Starter.exe
│   ├── ToolDock.Updater.exe
│   ├── ToolDock.Client.exe
│   ├── tdctl.cmd
│   └── <managed-command>.cmd
├── tools\
│   └── <package>\
│       ├── <version>\
│       └── current\ -> <version>
├── state\
│   ├── catalog.json
│   └── installed.json
├── logs\
│   ├── ToolDock.Starter.log
│   ├── ToolDock.Updater.log
│   └── <daemon>.log
├── temp\
└── config.json
```

Installed state records the package version directory, not a single executable. That permits one release package to provide multiple commands and daemons. State also records generated command names so obsolete shims can be removed safely.

## Update and restart semantics

An updated command begins using the new `current` junction on its next invocation. Existing command processes are not managed by ToolDock.

For daemons, the engine sends:

```text
package-updated <package> installed|updated
```

The starter then applies these rules:

- first installation: start entries with `autostart: true`;
- package update: restart running entries with `restartOnUpdate: true`;
- stopped entries remain stopped;
- command-only packages require no process action.

The updater never stops or starts managed processes directly. If the starter cannot reconcile an installed package, installation remains complete, the problem is logged, and the update result reports an error.

## Process containment

Each daemon is created suspended, assigned to a dedicated Windows Job Object, and then resumed. The Job Object uses kill-on-close semantics. `stop` and `restart` therefore terminate the complete process tree rather than only the root process.

Daemon arguments are encoded using Windows command-line quoting rules. The executable path is also passed separately as the `CreateProcess` application name.

The starter captures stdout and stderr through inherited anonymous pipes. It drains them after Job Object termination before recording the final exit event.

## IPC and security

The starter listens on:

```text
ToolDock.Starter.v1
```

The Named Pipe ACL grants access only to the current Windows user and its effective owner SID. The protocol is line-oriented and intentionally local.

Supported public requests are:

```text
list
start <daemon>
stop <daemon>
restart <daemon>
status <daemon>
```

`package-updated` is an internal update-engine request.

## Logging

Application diagnostics use `Microsoft.Extensions.Logging` with a ToolDock rotating-file provider. Structured message templates are retained at call sites without adding a third-party logging dependency.

Managed daemon stdout/stderr is not application logging. It remains a raw transcript with `SYS`, `OUT`, and `ERR` markers so arbitrary child output is not interpreted as ToolDock diagnostic events.

Log files rotate at 10 MB with five archives. Writers allow read and delete sharing so `tdctl logs --follow` can coexist with rotation.

## Task Scheduler

The installer creates two limited, interactive-user tasks:

- `ToolDock Starter`: at logon, no execution time limit;
- `ToolDock Updater`: at logon and then at the configured repetition interval.

The updater is a daemon in the deployment sense: it is a windowless background executable. It remains one-shot rather than resident; Task Scheduler owns its schedule.

## Build and release

The Windows workflow:

1. restores and builds the solution;
2. verifies Windows PowerShell installer compatibility;
3. publishes all three executables as framework-dependent single files and rejects managed sidecars;
4. tests updater mutex ownership and collision behavior;
5. tests client output and interactive updating;
6. runs an actual autostart, argument, output-capture, package-restart, and process-tree smoke cycle;
7. creates the release ZIP and SHA-256 file.

Pull requests and manual workflow runs stop after validation and artifact upload. A successful push to `master` creates or repairs the release for that workflow run. The version format is:

```text
v0.2.<github-run-number>
```

The run number makes a workflow retry idempotent. Master runs are serialized so an older commit cannot finish after a newer commit and become the latest release.
