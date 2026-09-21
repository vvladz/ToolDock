# ToolDock

ToolDock is a small Windows utility deployment and process supervision system.

Its purpose is deliberately narrow:

- install CLI tools and session-scoped services from GitHub Releases;
- discover new tools from a central catalog;
- update installed tools automatically;
- start and supervise selected tools in the current Entra user session;
- restart complete process trees safely;
- capture and rotate logs;
- keep all deployment logic outside the tools themselves.

The tools managed by ToolDock must remain ordinary standalone executables. They do not need to know anything about ToolDock, GitHub polling, updating, process supervision, logging, or Task Scheduler.

---

# Goals

ToolDock should be:

- simple;
- user-scoped;
- usable without SSH;
- usable without inbound network connectivity;
- usable without Windows Services;
- suitable for tools running in an interactive Microsoft Entra user session;
- based on GitHub Releases over HTTPS;
- capable of managing both plain CLI tools and long-running session processes;
- safe when restarting applications with child processes;
- invisible when started by Task Scheduler;
- usable interactively from an existing terminal when started manually.

The system consists of two executables:

```text
starter.exe
updater.exe
```

Both are launched independently by Windows Task Scheduler.

---

# High-level architecture

```text
                         GitHub
                           │
                           │ HTTPS polling
                           ▼
                    ┌─────────────┐
                    │ updater.exe │
                    └──────┬──────┘
                           │
                           │ install/update
                           ▼
                      Tool storage
                           │
                           │ Named Pipe
                           ▼
Task Scheduler       ┌─────────────┐
      │               │ starter.exe │
      └──────────────►└──────┬──────┘
                             │
               ┌─────────────┼─────────────┐
               ▼             ▼             ▼
          Job[farshell]  Job[tool-a]   Job[tool-b]
               │             │             │
          process tree   process tree   process tree
```

Responsibilities are intentionally separated:

```text
Task Scheduler
    decides when ToolDock components run

updater.exe
    decides what software should be installed

starter.exe
    decides what processes should be running

managed tools
    only implement their own functionality
```

---

# Components

## starter.exe

`starter.exe` is a persistent process supervisor.

It is started by Task Scheduler when the Entra user logs in.

It is responsible for:

- starting configured session applications;
- stopping them;
- restarting them;
- supervising their process trees;
- exposing a local Named Pipe API;
- capturing stdout/stderr;
- writing tool logs;
- rotating logs.

It does not:

- poll GitHub;
- download releases;
- install binaries;
- determine the latest version.

---

## updater.exe

`updater.exe` is a short-lived one-shot process.

It is started periodically by its own Task Scheduler task.

It performs:

```text
start
  ↓
fetch catalog
  ↓
check latest releases
  ↓
compare local state
  ↓
download changed tools
  ↓
install
  ↓
request restart/start from starter
  ↓
exit
```

It does not stay resident between update checks.

It does not manage process trees directly.

---

# Executable subsystem and console behavior

Both ToolDock executables should be built as Windows-subsystem applications rather than console-subsystem applications.

Recommended project configuration:

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
</PropertyGroup>
```

This is intentional.

When Task Scheduler launches:

```text
starter.exe
updater.exe
```

Windows should not create a console window at all.

The desired behavior is:

```text
Task Scheduler
    ↓
WinExe
    ↓
no console window created
    ↓
file logging
```

Do not rely on hiding an already-created console window.

Avoid launcher workarounds such as:

```text
powershell.exe -WindowStyle Hidden
wscript.exe
cmd.exe /c start /b
```

They add an unnecessary process layer.

---

# Manual console attachment

Although `starter.exe` and `updater.exe` are `WinExe` applications, manual invocation from an existing PowerShell, Command Prompt, or Windows Terminal should reuse that terminal.

At startup, ToolDock should attempt:

```text
AttachConsole(ATTACH_PARENT_PROCESS)
```

Conceptual Win32 wrapper:

```csharp
internal static class ConsoleHost
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static bool TryAttachParent()
        => AttachConsole(ATTACH_PARENT_PROCESS);
}
```

Startup behavior:

```text
TryAttachParent()
    │
    ├── success
    │     ↓
    │   log to existing console
    │
    └── failure
          ↓
        log to rotating file
```

Therefore:

```powershell
.\updater.exe
```

from an existing shell should display updater output in that shell.

Likewise:

```powershell
.\starter.exe status farshell
```

should use the existing terminal.

No new console window should be created.

---

# Console detection

Do not use:

```csharp
Environment.UserInteractive
```

to determine whether ToolDock should log to a console.

A process launched by Task Scheduler may run in an interactive Entra user session while having no attached console.

The relevant distinction is:

```text
attached console exists
vs.
attached console does not exist
```

not:

```text
interactive user session
vs.
non-interactive session
```

---

# Task Scheduler

ToolDock uses two independent scheduled tasks.

## Starter task

Conceptually:

```text
Trigger:
    At log on

User:
    current Entra user

Program:
    starter.exe

Mode:
    Run only when user is logged on
```

`starter.exe` remains running for the duration of the user session.

It must not be installed as a Windows Service.

Because the executable is built as `WinExe`, Task Scheduler starts it without a console window.

The Task Scheduler `Hidden` option is not required for console suppression and should not be relied upon for this purpose.

---

## Updater task

Conceptually:

```text
Trigger:
    At log on
    +
    repeat periodically

User:
    current Entra user

Program:
    updater.exe

Mode:
    Run only when user is logged on
```

Example polling interval:

```text
5 minutes
```

Exact polling frequency should remain configuration rather than application logic.

Only one updater instance should run at a time.

The Task Scheduler task should therefore avoid overlapping executions.

Because `updater.exe` is also `WinExe`, scheduled update checks produce no visible console window.

---

# GitHub model

ToolDock uses outbound HTTPS only.

GitHub does not need connectivity to the laptop.

No:

- SSH;
- webhook receiver;
- self-hosted Actions runner;
- public endpoint;
- inbound firewall rule.

The laptop polls GitHub periodically.

---

# Central tool catalog

`updater.exe` reads one configured catalog URL from the local ToolDock configuration.

Example:

```text
https://example.org/tooldock/tools.json
```

The catalog answers:

> Which tools should exist on this machine?

It does not need to contain the current release version.

Example:

```json
{
  "tools": {
    "farshell": {
      "repo": "owner/farshell",
      "asset": "farshell-win-x64.zip",
      "enabled": true,
      "autostart": true,
      "restart": true
    },

    "devproxy": {
      "repo": "owner/devproxy",
      "asset": "devproxy-win-x64.zip",
      "enabled": true,
      "autostart": false,
      "restart": false
    }
  }
}
```

Recommended fields:

```text
repo
asset
enabled
autostart
restart
```

Possible future fields:

```text
arguments
workingDirectory
environment
channel
healthCheck
```

These are not required for the MVP.

---

# New tool discovery

A new tool becomes managed by ToolDock simply by adding it to `tools.json`.

Example:

```json
{
  "tools": {
    "new-tool": {
      "repo": "owner/new-tool",
      "asset": "new-tool-win-x64.zip",
      "enabled": true,
      "autostart": true,
      "restart": true
    }
  }
}
```

On the next updater cycle:

```text
catalog contains new-tool
        ↓
new-tool is absent locally
        ↓
query latest GitHub Release
        ↓
download asset
        ↓
install
        ↓
starter start new-tool
```

No changes to `new-tool` itself are necessary.

---

# Release discovery

For each configured tool, updater queries the latest GitHub Release for:

```text
owner/repository
```

The release determines:

```text
current version
downloadable asset
```

The catalog determines:

```text
which repository
which asset
how ToolDock treats the application
```

This separation is intentional.

```text
tools.json
    = desired software inventory

GitHub Releases
    = available versions
```

---

# Managed tool requirements

A managed tool should ideally be nothing more than:

```text
tool.exe
```

or a small standalone distribution:

```text
tool.exe
dependency.dll
config.json
...
```

A tool must not require ToolDock-specific code.

In particular, managed tools should not implement:

- self-update;
- GitHub polling;
- self-restart;
- Task Scheduler registration;
- ToolDock IPC;
- ToolDock logging APIs.

The deployment system must remain orthogonal to application code.

---

# Installation layout

Recommended user-scoped location:

```text
%LOCALAPPDATA%\ToolDock\
```

Example:

```text
%LOCALAPPDATA%\ToolDock\
│
├── bin\
│   ├── starter.exe
│   └── updater.exe
│
├── tools\
│   ├── farshell\
│   │   ├── 1.4.1\
│   │   │   └── farshell.exe
│   │   │
│   │   ├── 1.4.2\
│   │   │   └── farshell.exe
│   │   │
│   │   └── current\
│   │
│   └── devproxy\
│       └── ...
│
├── state\
│   └── installed.json
│
└── logs\
    ├── starter.log
    ├── updater.log
    ├── farshell.log
    └── devproxy.log
```

No administrator privileges should be required.

---

# Versioned installation

Do not overwrite a running executable in place.

Install releases into version-specific directories.

Example:

```text
tools\farshell\
├── 1.4.1\
│   └── farshell.exe
│
├── 1.4.2\
│   └── farshell.exe
│
└── current
```

Updater flow:

```text
download
    ↓
extract into new version directory
    ↓
validate
    ↓
switch current
    ↓
request restart
```

This avoids problems with locked Windows executables.

It also gives a natural basis for rollback later.

---

# Current version pointer

The exact implementation can remain simple.

Possible implementations:

1. small launcher/shim;
2. current-version file;
3. directory junction;
4. symbolic link where practical.

For the first implementation, a plain state file is sufficient.

Example:

```json
{
  "farshell": {
    "version": "1.4.2",
    "path": "tools/farshell/1.4.2/farshell.exe"
  }
}
```

`starter.exe` resolves the executable path from ToolDock state/configuration when starting the application.

This avoids relying on filesystem symlink privileges.

---

# Local updater state

Example:

```json
{
  "farshell": {
    "version": "1.4.2"
  },

  "devproxy": {
    "version": "2.1.0"
  }
}
```

Updater compares:

```text
installed version
vs.
latest GitHub Release
```

If equal:

```text
skip
```

If different:

```text
install latest
```

---

# Process supervision

Each managed long-running tool receives its own Windows Job Object.

Example:

```text
starter.exe
│
├── Job[farshell]
│   └── farshell.exe
│       ├── child.exe
│       └── helper.exe
│
└── Job[proxy]
    └── proxy.exe
        └── worker.exe
```

One Job Object per logical application is important.

It allows:

```text
restart farshell
```

without affecting:

```text
proxy
other-tool
...
```

---

# Job Object configuration

Each job uses:

```text
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
```

This means closing the Job Object kills all processes assigned to it.

Do not enable:

```text
JOB_OBJECT_LIMIT_BREAKAWAY_OK
JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK
```

unless a future use case explicitly requires it.

The intended invariant is:

> Every descendant process of a managed tool belongs to the same Job Object.

---

# Safe process startup

Managed applications must be created suspended.

Conceptual sequence:

```text
CreateProcess(
    CREATE_SUSPENDED |
    CREATE_NO_WINDOW
)

        ↓

AssignProcessToJobObject()

        ↓

ResumeThread()
```

`CREATE_SUSPENDED` prevents the application from spawning descendants before it has been assigned to the Job Object.

This avoids leaking child processes outside ToolDock supervision.

---

# Managed-tool console behavior

Managed tools must not display console windows.

For console-subsystem applications use:

```text
CREATE_NO_WINDOW
```

Do not use `DETACHED_PROCESS` as the default mechanism.

Applications built as GUI-subsystem binaries naturally do not create a console.

This is separate from the `WinExe` behavior of ToolDock's own `starter.exe` and `updater.exe`.

There are therefore two independent console rules:

```text
starter.exe / updater.exe
    WinExe + optional AttachConsole(parent)

managed tools
    CREATE_NO_WINDOW
```

---

# Restart semantics

Restarting a managed process means restarting the entire logical process tree.

Conceptually:

```text
restart farshell
        ↓
close/terminate old Job Object
        ↓
all descendants die
        ↓
create new Job
        ↓
create process suspended
        ↓
assign process to Job
        ↓
resume process
```

Do not implement restart using process enumeration.

Avoid:

```text
taskkill /T
WMI parent lookup
recursive PID enumeration
Process.Kill(entireProcessTree)
```

Job Objects are the source of truth for process ownership.

---

# Named Pipe control interface

`starter.exe` exposes:

```text
\\.\pipe\tool-starter
```

No SID is included in the name.

The protocol should deliberately remain simple.

Initial commands:

```text
start <tool>
stop <tool>
restart <tool>
status <tool>
```

Responses:

```text
OK
```

or:

```text
ERROR <message>
```

Examples:

```text
restart farshell
```

Response:

```text
OK
```

Possible status response:

```text
OK running pid=1234
```

No JSON protocol is required for the MVP.

Line-oriented UTF-8 text is sufficient.

---

# Updater → Starter interaction

Updater never kills processes directly.

After successfully installing a new version:

```text
updater
   ↓
Named Pipe
   ↓
restart farshell
   ↓
starter
```

The updater only asks for a lifecycle operation.

Starter owns all process management.

---

# Starter unavailable during update

Installation should not fail merely because `starter.exe` is unavailable.

Example:

```text
release downloaded
        ↓
version installed
        ↓
current version switched
        ↓
pipe unavailable
```

Expected behavior:

```text
installation = successful
restart = not performed
warning logged
```

The next normal starter launch will use the newly installed version.

No rollback is required solely because the restart request failed.

---

# Autostart

The catalog contains:

```json
"autostart": true
```

Starter should launch all enabled autostart tools when it starts.

Example:

```text
starter starts
    ↓
load catalog/local resolved configuration
    ↓
start farshell
    ↓
start proxy
```

Tools with:

```json
"autostart": false
```

remain installed but are not started automatically.

---

# Enabled flag

The catalog may contain:

```json
"enabled": false
```

Meaning:

```text
do not update
do not autostart
```

Disabling a catalog entry should not automatically delete local files.

Deletion is intentionally outside the MVP.

---

# Logging

Every ToolDock log uses the same rotation mechanism.

Logs include:

```text
starter.log
updater.log
<tool>.log
```

Example:

```text
logs\
├── starter.log
├── starter.log.1
├── starter.log.2
│
├── updater.log
├── updater.log.1
│
├── farshell.log
├── farshell.log.1
│
└── proxy.log
```

---

# ToolDock own logging

For `starter.exe` and `updater.exe`:

```text
manual launch from existing terminal
    ↓
AttachConsole(parent) succeeds
    ↓
log to console
```

For scheduled execution:

```text
Task Scheduler
    ↓
no parent console
    ↓
AttachConsole fails
    ↓
log to rotating file
```

Neither executable should create a console of its own.

---

# Managed process logging

Starter captures:

```text
stdout
stderr
```

from every managed process.

Both streams must be consumed concurrently and asynchronously.

Do not synchronously drain one stream before reading the other.

Otherwise a process can deadlock when one OS pipe buffer fills.

---

# stdin

Managed services receive no stdin by default.

Conceptually:

```text
stdin  → disabled / closed
stdout → starter
stderr → starter
```

If a future application genuinely requires stdin, it should become an explicit per-tool capability.

It is not part of the MVP.

---

# Tool log format

Both stdout and stderr can be stored in a single logical tool log.

Example:

```text
2026-09-19 18:42:01.123 OUT connected
2026-09-19 18:42:03.991 ERR reconnect failed
```

Starter may also write lifecycle messages into the same tool log:

```text
2026-09-19 18:40:00.102 SYS process started pid=1234
2026-09-19 19:03:52.774 SYS restart requested
2026-09-19 19:03:52.810 SYS process exited pid=1234 code=-1
2026-09-19 19:03:52.910 SYS process started pid=5678
```

Application output should otherwise remain unchanged.

---

# Starter log

`starter.log` records supervisor activity.

Examples:

```text
starter initialized
pipe server started
starting farshell
job created
process started
restart request received
job terminated
process exited
```

It should not duplicate every line from the application's own log.

---

# Updater log

`updater.log` records deployment activity.

Examples:

```text
catalog fetched
checking farshell
installed=1.4.1 latest=1.4.2
downloading release
download complete
install complete
restart requested
starter replied OK
```

---

# Log rotation

All logs use the same size-based rotation policy.

Initial defaults:

```text
max file size: 10 MB
archives:      5
```

Example:

```text
farshell.log
farshell.log.1
farshell.log.2
farshell.log.3
farshell.log.4
farshell.log.5
```

Rotation:

```text
delete .5
.4 → .5
.3 → .4
.2 → .3
.1 → .2
.log → .1
create new .log
```

Rotation must be synchronized per logical log file.

Multiple writers such as stdout/stderr must never attempt rotation concurrently.

---

# Logging implementation

Use one shared abstraction:

```text
RotatingFileWriter
```

Used by:

```text
starter own logging
updater own logging
managed stdout
managed stderr
managed lifecycle events
```

A higher-level abstraction may provide:

```text
ConsoleOrFileLogger
```

Its selection rule is:

```text
attached parent console available
    → console

otherwise
    → rotating file
```

---

# Proposed solution structure

```text
ToolDock.sln

src\
├── ToolDock.Starter\
│   ├── Program.cs
│   ├── ConsoleHost.cs
│   ├── Supervisor\
│   ├── Jobs\
│   ├── Processes\
│   ├── Pipes\
│   └── Logging\
│
├── ToolDock.Updater\
│   ├── Program.cs
│   ├── ConsoleHost.cs
│   ├── Catalog\
│   ├── GitHub\
│   ├── Installation\
│   ├── State\
│   ├── Pipes\
│   └── Logging\
│
└── ToolDock.Common\
    ├── Configuration\
    ├── Protocol\
    ├── State\
    └── Logging\
```

`ConsoleHost` may instead live in `ToolDock.Common`.

Possible output:

```text
starter.exe
updater.exe
ToolDock.Common.dll
```

Alternatively, both executables may be published self-contained/single-file later.

That is not required for the first implementation.

---

# Suggested core types

## Common

```text
ToolCatalog
ToolDefinition
InstalledState
InstalledTool
RotatingFileWriter
ConsoleOrFileLogger
ConsoleHost
StarterClient
```

## Starter

```text
ToolSupervisor
ToolInstance
JobObject
ProcessLauncher
PipeServer
```

## Updater

```text
CatalogClient
GitHubReleaseClient
ReleaseInstaller
UpdatePlanner
StarterClient
```

---

# Catalog model

Conceptual C# model:

```csharp
public sealed class ToolCatalog
{
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
}

public sealed class ToolDefinition
{
    public required string Repo { get; init; }
    public required string Asset { get; init; }

    public bool Enabled { get; init; } = true;
    public bool Autostart { get; init; }
    public bool Restart { get; init; }
}
```

Exact serialization details can evolve later.

---

# Installed state model

Conceptually:

```csharp
public sealed class InstalledState
{
    public Dictionary<string, InstalledTool> Tools { get; init; } = [];
}

public sealed class InstalledTool
{
    public required string Version { get; init; }
    public required string Path { get; init; }
}
```

Writes must be atomic.

Recommended pattern:

```text
write state.tmp
flush
replace state.json
```

Never leave a partially written state file after interruption.

---

# Update algorithm

Conceptually:

```text
fetch catalog

for each enabled tool:

    get latest GitHub release

    if tool not installed:
        download
        install
        update state

        if autostart:
            starter start <tool>

        continue

    if installed version == latest version:
        continue

    download
    install new version
    update state

    if restart:
        starter restart <tool>
```

A failed tool update should be logged and should not prevent unrelated tools from being checked.

---

# Download safety

Download into a temporary location first.

Example:

```text
temp\
    farshell-1.4.2.zip.part
```

Only treat the release as available after the full download succeeds.

Installation should similarly use a temporary extraction directory:

```text
tools\farshell\.install-1.4.2\
```

Then move/rename into:

```text
tools\farshell\1.4.2\
```

This prevents half-installed versions from becoming active.

---

# HTTP caching

The catalog may use standard HTTP caching such as:

```text
ETag
If-None-Match
```

A `304 Not Modified` response can end catalog processing quickly.

This is an optimization, not a requirement for the initial implementation.

Correctness must not depend on ETag support.

---

# GitHub authentication

Initial implementation should support public GitHub repositories without authentication.

Private repository support can be added separately.

Do not couple the updater architecture to SSH credentials.

If authentication is later required, it should remain an HTTP concern.

---

# Failure model

ToolDock should prefer simple, recoverable states.

## GitHub unavailable

```text
log error
exit updater
try again on next scheduled run
```

Installed tools continue running.

## Download failed

```text
do not modify installed state
do not restart tool
```

## Extraction failed

```text
leave existing version untouched
log failure
```

## Starter unavailable

```text
install succeeds
restart request fails
log warning
```

## Tool crashes

Starter logs exit.

Automatic crash restart policy is not required for MVP unless explicitly added later.

---

# Concurrency

Only one updater instance should modify ToolDock state at a time.

Prefer preventing overlap through Task Scheduler configuration.

Updater may additionally use a local mutex as a defensive measure.

Starter is the sole owner of managed process lifecycle.

Updater must never compete with starter for process ownership.

---

# Security boundary

ToolDock intentionally runs applications in the current Entra user's security context.

It does not elevate privileges.

It does not provide cross-user management.

It does not expose network control interfaces.

The Named Pipe is local only.

ToolDock should avoid executing arbitrary shell commands from `tools.json`.

The catalog should describe executable artifacts and lifecycle settings rather than becoming a remote scripting system.

---

# MVP

The first usable version should contain only the following.

## Starter MVP

- `WinExe` output type;
- attach to parent console when manually launched;
- no console creation under Task Scheduler;
- Task Scheduler startup;
- load tool configuration/state;
- autostart configured tools;
- one Job Object per tool;
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`;
- suspended process creation;
- `CREATE_NO_WINDOW`;
- stdout/stderr capture;
- rotating logs;
- Named Pipe server;
- `start`;
- `stop`;
- `restart`;
- `status`.

## Updater MVP

- `WinExe` output type;
- attach to parent console when manually launched;
- no console creation under Task Scheduler;
- Task Scheduler execution;
- fetch `tools.json`;
- query latest GitHub Release;
- select configured asset;
- download ZIP;
- extract versioned directory;
- update installed state atomically;
- call starter via Named Pipe;
- rotating updater log.

---

# Explicit non-goals

Do not implement these initially:

- Windows Service;
- SSH;
- self-hosted GitHub Actions runner;
- webhooks;
- package repository;
- NuGet-based deployment protocol;
- MSI;
- Chocolatey;
- WinGet repository;
- arbitrary remote scripts;
- web UI;
- GUI;
- distributed control plane;
- automatic uninstall;
- dependency solver;
- rollback orchestration;
- health checks;
- automatic crash recovery;
- remote log shipping;
- database;
- application-specific updater APIs.

They can be considered later only if a concrete requirement appears.

---

# Design principles

## Applications stay clean

The managed application knows nothing about ToolDock.

```text
application responsibility:
    application logic

ToolDock responsibility:
    deployment and process lifecycle
```

## Process ownership belongs to starter

Updater never kills processes.

## Version ownership belongs to updater

Starter never talks to GitHub.

## Scheduling belongs to Windows

Neither starter nor updater needs its own scheduling engine.

## ToolDock creates no console windows

Scheduled execution must remain invisible.

Manual execution may attach to an already-existing terminal.

## Job Objects define process trees

PID enumeration does not.

## Logs are files first

No external logging infrastructure is required.

## Failure should preserve the last working installation

An update failure must not destroy the currently installed version.

---

# Initial implementation order

1. Create solution and common directory layout.
2. Configure `starter.exe` and `updater.exe` as `WinExe`.
3. Implement `ConsoleHost.TryAttachParent()`.
4. Implement console-or-file logging selection.
5. Implement rotating logging.
6. Implement `starter.exe`.
7. Add Job Object wrapper.
8. Implement safe suspended process startup.
9. Add `CREATE_NO_WINDOW` for managed tools.
10. Capture stdout/stderr.
11. Implement Named Pipe server.
12. Implement `start/stop/restart/status`.
13. Implement local catalog/state loading.
14. Implement `updater.exe`.
15. Add GitHub catalog fetch.
16. Add latest Release lookup.
17. Add download and extraction.
18. Add atomic installed-state update.
19. Add updater → starter restart request.
20. Create Starter Task Scheduler definition.
21. Create Updater Task Scheduler definition.
22. Verify that scheduled runs create no console window.
23. Test complete deployment cycle.

---

# First end-to-end scenario

Given:

```text
tools.json:
    farshell → 1.0 release exists
```

Machine state:

```text
farshell not installed
```

Expected flow:

```text
Updater task starts
        ↓
no console window appears
        ↓
AttachConsole(parent) fails
        ↓
updater logs to updater.log
        ↓
fetch catalog
        ↓
query latest farshell release
        ↓
download farshell
        ↓
install 1.0
        ↓
write local state
        ↓
pipe: start farshell
        ↓
starter creates Job Object
        ↓
starter creates farshell suspended + CREATE_NO_WINDOW
        ↓
assign Job
        ↓
redirect stdout/stderr
        ↓
resume process
        ↓
farshell.log receives output
```

Then publish `1.1`.

Expected:

```text
Updater task starts
        ↓
detect 1.1
        ↓
install alongside 1.0
        ↓
switch installed state to 1.1
        ↓
pipe: restart farshell
        ↓
starter kills old Job
        ↓
entire old process tree disappears
        ↓
new Job created
        ↓
1.1 starts
```

Manual diagnostic invocation:

```powershell
.\updater.exe
```

Expected:

```text
updater attaches to existing PowerShell/Terminal console
        ↓
logs are visible in that terminal
        ↓
no second console window is created
```

---

# Project summary

ToolDock consists of two simple pieces:

```text
starter.exe
    process supervisor

updater.exe
    software deployment client
```

Connected through:

```text
Named Pipe
```

Driven by:

```text
Task Scheduler
```

Using:

```text
GitHub Releases
```

Supervising applications through:

```text
Windows Job Objects
```

And running invisibly when scheduled through:

```text
WinExe
```

while retaining useful manual diagnostics through:

```text
AttachConsole(ATTACH_PARENT_PROCESS)
```

The managed tools themselves remain completely independent.

That separation is the primary architectural constraint of the project.
