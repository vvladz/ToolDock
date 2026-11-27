# ToolDock project context and implementation contract

This document preserves decisions for AI-assisted development. User-facing instructions and inline examples belong in [README.md](../../README.md).

Current implementation: a .NET 11 Windows x64 CLI in `src/ToolDock`, with executable integration tests in `tests/ToolDock.Tests`. The requirements below remain the behavior contract; verification coverage and environment limitations are recorded at the end of this document.

## Scope and architecture

ToolDock is a user-level daemon runner for modern Windows on AMD64/x64. One persistent `ToolDock.exe` process manages one daemon instance and processes remaining in its Windows Job Object. Normal operation must not require elevation.

ToolDock and the installation manager are independent projects. Their shared interface is an executable name and arguments. ToolDock does not read installation manifests or manage installed versions, installation directories, `current` links, builds, updates, or cleanup of installed copies.

External deployment arranges startup, including startup at sign-in. `start --all` selects all configured daemons; callers may also select individual names. A brief console window from an external launcher is acceptable. Scheduling, installation, updates and recovery of the runner itself remain external operations. Old Windows and other architectures are outside the target scope.

## Identity and files

The configuration root is `~/.config/ToolDock`, where `~` is the current Windows user's profile. The ToolDock binary can live elsewhere.

| Path beneath the root | Purpose |
| --- | --- |
| `tools/*.json` | User-written configurations; no recursive discovery |
| `environment.json` | Optional global environment variables and secret references |
| `secrets.dat` | Current-user DPAPI-encrypted secret dictionary |
| `secrets.lock` | Exclusive writer lock for secret changes |
| `logs/<daemon>/log-*.log` | One daemon's rotating combined log |
| `state/<daemon>.lock` | The runner's held OS file lock |
| `state/<daemon>.json` | The last recorded runner launch and result |

A daemon ID is its configuration file name without `.json`, compared case-insensitively. There is no separate `id` field. Locking, state, logs, and stop signaling must use consistent identity regardless of command letter case.

Allow one active runner per user and ID. Different IDs may run the same executable. The lock prevents a second instance from launching. An occupied lock is sufficient for repeated `start` to succeed without a second daemon; no separate check of the existing runner's startup, retry, or shutdown phase is required.

Configuration edits do not change a running instance or trigger reload. A new runner launch is required.

## Commands

| Command | Behavior |
| --- | --- |
| `start <daemon>` | Create a detached runner, wait for its first process-start acknowledgement, and exit |
| `start --all` | Sequentially start all configured daemons, reporting per-daemon results and aggregate failure |
| `run <daemon>` | Become the persistent runner in the invoking environment |
| `stop <daemon>` | Signal the runner and wait for shutdown |
| `status <daemon> [--json]` | Report runner activity and its last recorded result |
| `list` | Print configured daemon names, one per line, sorted case-insensitively |
| `secret set <name> [--stdin]` | Create or replace a secret using hidden input or standard input |
| `secret list` | Print secret names only |
| `secret delete <name>` | Delete a secret; absence is successful |
| `--help` | Print CLI help |
| `--version` | Print the executable version |

`start` creates another instance of the same executable in `run` mode. Direct and background runs share lifecycle logic, locking, state, logs, and stop handling. Direct `run` also logs to the console; Ctrl+C requests ordinary shutdown. Direct `run` need not detach from the caller's console or external Job.

A new runner acknowledges startup only after acquiring the lock, publishing unfinished state, preparing stop handling, creating its Job, and successfully creating and starting the first daemon process. This confirms process creation, not application readiness. Errors before acknowledgement go back to the short command.

Repeated `start` reports `already running`; new successful startup reports `started`. Stopping an absent runner succeeds with `already stopped`; successful shutdown reports `stopped`. `stop` and `status` do not read or validate the daemon configuration, including after it has been deleted.

Short commands write results to stdout and errors to stderr. CLI exit codes: `0` for success, `1` for operation/configuration/state errors or timeout, `2` for invalid syntax. A successfully read `status` returns `0` even for a reported `failed` state.

The runner records `0` for successful child completion or successful manual stop. It records `1` for its own errors or a nonzero child exit for which no retry is scheduled. Exact child exit codes belong in the log. Process-creation errors are fatal; restart policies apply to exits of a successfully created child.

`list` enumerates only JSON files directly in the configuration `tools` directory. File extensions and daemon identity are case-insensitive; output preserves the file name's spelling without its extension. It neither parses configurations nor reads runtime state. A missing or empty directory yields no output and exit code `0`; other enumeration errors follow the normal operation-error path. The command accepts no additional arguments and does not create directories.

## Background lifetime

`start --all` enumerates the same top-level names as `list`, but a missing configuration directory is an error. An existing empty directory succeeds with an explicit message. Per-daemon configuration and startup failures do not stop the loop; any failure makes the final exit code `1`. Already running daemons succeed without restarting. Cancellation stops the loop, preserving earlier successful starts. Directory enumeration failures remain global errors.

The detached runner and its managed group must survive the short command exiting and its console closing in supported environments. `start` waits for acknowledgement and exits; the persistent runner has another PID.

Create the background runner without a console, inherited standard streams, or unrelated inherited handles. Create the daemon without a console and with explicitly redirected streams. `CREATE_NO_WINDOW` provides console-free creation for console executables.

`FreeConsole` changes console attachment only. A process already assigned to a Job cannot simply detach from it. Resolve external Job separation when creating the background runner using applicable breakaway rules. Request explicit breakaway when the caller is in a Job; retry without that flag only if the caller's immediate Job permits silent breakaway. A dedicated daemon Job does not cancel external Job restrictions. Fail startup if process creation, the background Job check, or group management fails.

Do not require `IsProcessInJob` to become false after breakaway: Windows can assign a new process to a system-managed Job independently of its launching shell. Before taking a daemon lock or creating a daemon, the background runner queries its own immediate Job. Accept no Job, or a Job whose only limit flags are `JOB_OBJECT_LIMIT_BREAKAWAY_OK`, `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`, or both. Reject a Job with no breakaway permission or any additional limits, including `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and return the error through the startup acknowledgement pipe.

`QueryInformationJobObject(NULL, ...)` describes only the immediate Job, not all ancestors. This check does not prove arbitrary nested Job hierarchies safe; hosts with other lifetime constraints still need target-environment verification. Do not add a broker or bypass a host that rejects breakaway.

A separate process still has a parent relationship. Verify actual launch environments, including the user's SSH mechanism and sign-in launcher, against console closure, external Jobs, and other descendant-termination mechanisms.

## Configuration and launch snapshot

The user guide defines fields and examples. Required: `executable`, an explicit `.exe` or `.cmd` file name on PATH. Defaults: `arguments: []`, `environment: {}`, the configuration directory as `workingDirectory`, `restart: "on-failure"`, and `restartDelaySeconds: 5`.

Arguments are an array of literal strings. EXEs use native Windows argument quoting. CMD launchers use the system-directory `cmd.exe` with `/d /e:on /v:off /s /c` and separate quoting, including protection against percent expansion. A launcher must wait for the application and return its exit code. The shell is created in the attempt's Job, with the same redirected streams and descendant cleanup as an EXE. Startup acknowledges shell creation, not the application's readiness. CMD paths containing percent signs or carets, argument line breaks, UNC working directories and command lines exceeding 8191 characters are rejected. EXE argument behavior is unchanged.

The working directory must exist. Resolve relative directories from the JSON file's directory. Environment overrides apply to the child; other variables are inherited. Executable selection uses the runner's original `PATH`, not a child environment override.

Merge the inherited Windows environment, optional global `environment.json`, and the daemon's `environment` section in that order, comparing variable names case-insensitively. Values are strings or objects containing exactly one `secret` string property. Resolve references only after merging, so overridden references do not require their secrets. Reject invalid names, NUL values, malformed JSON, invalid reference shapes, or missing required secrets without including values in diagnostics. A missing global file is allowed; corrupt files are errors.

`environment.json` and resolved secret values are part of the launch snapshot. Automatic retries keep them; a new runner reads them again. `list`, `stop`, and `status` never load these files.

## Secret storage and input

Store the complete secret dictionary encrypted in `secrets.dat` using Windows DPAPI with current-user scope and `CRYPTPROTECT_UI_FORBIDDEN`. Do not use machine-wide scope. Use the native Windows API already available to the Windows-only application; no additional package is required.

Secret names are case-insensitive, nonblank, at most 256 characters, and contain no control characters. Empty secret values are allowed; NUL characters are rejected. The CLI never accepts values as command-line arguments. Interactive entry is hidden, supports Backspace, and can be cancelled with Escape or Ctrl+C. Redirected input requires `--stdin`; read through EOF and remove one final LF or CRLF, preserving other whitespace.

Secret mutations hold an exclusive file handle on `secrets.lock` across the read/modify/write operation. A competing writer fails explicitly rather than overwriting another update. Write only encrypted bytes to a temporary file, then replace the target atomically. Readers allow file replacement and see a complete old or new snapshot. Do not replace an unreadable or corrupt store with an empty one. Clear temporary plaintext byte buffers after use; plaintext strings remain necessary for the child environment.

Secret listing exposes only names. Missing referenced secrets prevent startup; a corrupt store is only accessed when effective references or secret commands require it. Own diagnostics must not include secret values or malformed JSON fragments. Preserve the existing child-output logging behavior: the daemon can print its own environment, and this is not automatically redacted. DPAPI protects stored data, not against other processes under the same user account.

## Snapshot initialization

On each runner launch:

1. Acquire the lock, prepare stop signaling, and publish the PID and unfinished result; then validate configuration.
2. Search the runner's `PATH` directories in order. Do not implicitly add the current or configuration directory.
3. Resolve the final executable path, including links in directory components. `GetFinalPathNameByHandleW` is suitable for this.
4. Merge environment layers, resolve required secrets, and capture the executable path, arguments, working directory, environment, and restart policy.
5. Create the Job and start the daemon. Acknowledge the first successful process launch to `start`.
6. Read stdout and stderr while waiting for child exit or a stop request.
7. If needed, clean up the old attempt, wait the configured delay, and retry from the same snapshot.

Retries reuse the selected EXE or CMD path; a new runner reads JSON and resolves `PATH` again. A CMD launcher executes again on every retry and may select an updated program using its own version marker. ToolDock does not parse that marker or pin the launcher's target. Arguments, environment, working directory and restart settings remain snapshotted. Direct EXE launches retain the resolved physical EXE path. The snapshot does not copy or freeze file contents; keeping required files available is an external responsibility.

## Process groups and retries

Create a new Windows Job Object for each attempt, with the runner outside it. Assign the daemon to the Job at process creation through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, before its code runs. This avoids the orphan window between creating a suspended process and separately assigning its Job. No fallback for old Windows is required.

Set `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK`. Do not set `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`. The runner owns the controlling Job handle, which children must not inherit. Closing its last handle terminates remaining Job members.

Ordinary descendants stay in the Job. A program may explicitly create a child with `CREATE_BREAKAWAY_FROM_JOB`. Successfully detached descendants have an independent lifetime relative to this Job: ToolDock does not terminate them on stop, runner death, or retry. Their creator owns their lifecycle. Restrictions from other Jobs in the hierarchy still apply.

`never` ends the runner after child exit; `on-failure` retries nonzero exits and stops on `0`; `always` retries all child exits. Retries have a fixed positive delay and no attempt limit. Configuration, executable-resolution, process-creation, and Job-setup errors terminate the runner rather than entering a retry loop.

Before retrying, terminate and wait for all remaining processes in the previous Job. Breakaway descendants are excluded. Manual stop cancels retries, including a pending delay or startup, terminates the Job, and exits. Do not promise universal graceful Ctrl+C shutdown of arbitrary executables.

## Stop signaling and state

Use an OS-held lock on `<daemon>.lock` for the runner's lifetime. File existence is not proof of activity; process termination releases the OS lock. Keep the lock file separate from the replaceable state JSON.

Use a named stop event in `Global\` so the same user can control the runner across Windows sessions. Identity must account for the user SID and case-insensitive daemon ID, and address the intended runner instance. Exact internal naming and acknowledgement protocol are implementation details. Set appropriate access rights; a global name alone does not grant access.

`stop` signals the runner, which cancels retries, cleans up its Job, records its result, and exits. Wait for lock release with a bounded timeout. Never terminate a process solely by a PID from an old state file.

The state record describes the last runner launch, not each child attempt:

```json
{
  "pid": 1234,
  "startedAtUtc": "2026-11-12T15:20:00Z",
  "finishedAtUtc": null,
  "exitCode": null,
  "runId": "86fd7885-9d3f-41b2-8da4-3a44b3e5fe63"
}
```

Publish the PID and start time at initialization, clearing the previous result to `null`. On controlled exit, record finish time and runner exit code. Replace complete snapshots through a temporary file and replacement of the destination.

`runId` is an internal per-launch identifier used for the stop event and startup ownership. It is not included in the public `status --json` response. Short commands probe the exclusively locked byte by reading it; only a persistent runner acquires the lock.

State read/write failures are operation errors. Short commands exit with `1`; the runner cancels retries, cleans up its Job, and exits with `1`. Failure to persist a result does not become success. No additional crash-consistency mechanism is required for the window between taking the lock and publishing the new snapshot.

| Observation | Status | Reported `exitCode` |
| --- | --- | --- |
| Runner holds the lock | `running` | `null` |
| No runner; recorded completion code is `0` | `stopped` | `0` |
| No runner; recorded completion code is nonzero | `failed` | Recorded code |
| No runner; no launch record or no known result | `stopped` | `null` |

`failed` is a derived display result, not a separate lifecycle state machine. `running` includes retry delays. PID is diagnostic, not the source of liveness. Forced termination may prevent recording a result; do not invent an exit code or promise exact crash results without an external observer.

Access errors, corrupt state, and read failures make `status` fail rather than report `stopped`. JSON output contains `daemon`, `status`, `pid`, `startedAtUtc`, `finishedAtUtc`, and `exitCode` for every status. Times are UTC; missing values are `null`. PID and times refer to the last recorded launch. On error, emit diagnostics to stderr and no state JSON. Default text is a short line such as `work-tunnel: running (pid 1234)`.

## Logging

Use `Microsoft.Extensions.Logging` (`ILogger` / `ILogger<T>`) with Serilog: `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, and `Serilog.Sinks.Console`. A Generic Host is not required; if used, `Serilog.Extensions.Hosting` provides integration. Do not build a custom rolling logger.

Only the persistent runner writes its daemon's combined log. Configure file sink path `log-.log`; the sink adds date and sequence suffixes. Enable file output for every runner and console output only for direct `run`. Short commands do not open the daemon log. Do not add a second MEL console provider for the same events.

Format each line as UTC receipt time, origin, and original text. Store `Origin` separately as `runner`, `stdout`, or `stderr`. Child output uses `Information`; stderr origin does not automatically mean severity `Error`. Runner events use appropriate levels. Pass child text as a message value, not a template. Use UTF-8 and flush an unfinished line when its stream closes.

Read stdout and stderr independently and asynchronously, feeding one logger. Preserve order within each stream; between streams record receipt order without promising original global order. Use the sink's rotation and retention with `shared: false`. Dispose and flush the logger on controlled exit. No additional asynchronous logging queue or special machinery for high-volume output and log-write failures is required.

The daemon needs no interactive stdin. Breakaway does not close inherited stdout/stderr handles: a detached descendant can keep a pipe open after the main process and Job exit. After Job cleanup, drain output for a bounded time, then cancel reads and close pipes. EOF must not indefinitely delay stop or retry. Breakaway processes needing later output must provide their own streams.

## Operational defaults

| Setting | Value |
| --- | --- |
| Restart policy | `on-failure` |
| Restart delay | 5 seconds |
| Retry count | Unlimited |
| Startup acknowledgement timeout | 30 seconds |
| Stop timeout | 10 seconds |
| Output drain timeout after Job cleanup | 2 seconds |
| Log rotation | Daily and at approximately 20 MiB |
| Log retention | Up to 10 files per daemon |

Policy and retry delay are configurable per daemon. Keep operational timeouts and log settings together in code. The global `environment.json` file configures only the child environment.

A command timeout is an error. A failed or cancelled new `start` must clean up only its own runner, leaving an existing runner from another invocation untouched. Resolve ownership, acknowledgement, and cancellation races during implementation within this contract.

## Acceptance checks

- Global and per-daemon environment values override inherited values case-insensitively. The effective environment does not change executable lookup.
- DPAPI storage round-trips synthetic secrets, stores no plaintext values, preserves other entries on updates/deletes, rejects competing writers and corrupt data, and never prints values in own diagnostics.
- Missing effective secret references fail startup; overridden references do not access the secret store. Retries retain values across global-file and secret changes; a new runner receives updated values.
- Control commands remain usable with corrupt global environment or secret files.

- `list` returns sorted configured names, includes malformed JSON files, ignores nested files and runtime-only state, and succeeds without output when no configurations exist.

- New `start` returns after the first daemon process starts; runner and group survive command exit and console closure in supported environments.
- Different IDs run one EXE independently. Repeating an ID, including a case variant or an ID held by direct `run`, creates no duplicate daemon.
- A bad configuration affects only its own instance.
- CMD launchers forward literal arguments and exit codes, remain inside the managed Job, and can select a newly installed version on retry without reloading daemon settings.
- `start --all` continues after per-daemon errors, preserves already running instances and reports aggregate failure.
- Stop and runner death terminate remaining Job members. Ordinary descendants stay managed; explicit permitted breakaway descendants survive. Clean the previous Job before retrying.
- A stop during startup or retry delay prevents subsequent launches by that runner.
- Both output streams are serviced concurrently. Log lines retain their origin and do not interleave character by character.
- A breakaway descendant holding redirected streams cannot block stop or retry indefinitely.
- Configuration edits and installation-link changes do not alter an active snapshot. A new runner resolves both again.
- Normal operations and cross-session stop under the same user work without elevation.
- State read/write errors fail the operation; runner state errors cancel retries and clean up the Job.
- Status stays `running` during retries. Stale files and PIDs are not mistaken for activity or used as sole authority to terminate a process.
- Controlled exit records the runner result. Forced termination may leave an unknown result; no exit code is fabricated.
- Direct `run` logs to file and console; Ctrl+C follows normal stop handling.
- Verify file rotation and retention independently. Short commands never become additional log writers.
- Verify JSON shape and the distinction between CLI exit status and runner `exitCode`. Stop and status still work after configuration edits or removal.

## Build and verification

Build with `dotnet build ToolDock.slnx -c Release`. Run the integration executable directly with `& './tests/ToolDock.Tests/bin/Release/net11.0-windows/win-x64/ToolDock.Tests.exe'`. Do not use `dotnet run` for detached-lifetime tests: it can introduce a restrictive `KILL_ON_JOB_CLOSE` Job. Tests use isolated directories under `artifacts/tests/` and do not modify the user's ToolDock configuration.

The hosted Windows runner forbids background detachment. The release workflow passes `--restricted-job` to run the foreground scenarios and explicit rejection tests, and reports skipped detachment cases individually. Run the test executable without arguments in a permissive host for the complete suite.

The suite exercises argument quoting, environment and working-directory handling, both output streams, exit policies, case-insensitive duplicate protection, stop during retry, snapshot retention after configuration edits, state failures, file rotation and retention, startup acknowledgement, concurrent starts, cancelled startup ownership, and Job cleanup on retry and runner death.

Background tests use the production detached launcher. They require a host permitting breakaway and no longer substitute a contained launcher when detachment fails. The suite creates its own explicit-breakaway and silent-breakaway Jobs, closes them, and verifies that the runner and its daemon descendants remain alive and can be stopped. It also rejects both an immediate restrictive Job and a restrictive ancestor above a breakaway Job, before launching a daemon.

The suite also verifies that an explicit breakaway descendant survives stop without blocking output drain. The integration executable passed all 37 scenarios on .NET 11 when run directly. Fixture marker readers allow concurrent append operations, and one-time PID markers are published only after their contents are complete. Cross-session control, actual console closure/Ctrl+C, and the user's SSH launch mechanism still require validation in their target environments.

## API references

- [Windows DPAPI encryption](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
- [Windows DPAPI decryption](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata)

- [Process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)
- [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [Nested Jobs](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)
- [QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject)
- [Desktop Activity Moderator](https://learn.microsoft.com/en-us/windows/compatibility/desktop-activity-moderator)
- [Creating a process directly in a Job](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812)
- [Job limit flags](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
- [Kernel object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)
- [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex)
- [GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)
- [ProcessStartInfo.ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist)
- [Pipe handle inheritance](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-handle-inheritance)
- [Serilog logging integration](https://github.com/serilog/serilog-extensions-logging)
- [Serilog file sink](https://github.com/serilog/serilog-sinks-file)
- [Serilog console sink](https://github.com/serilog/serilog-sinks-console)
