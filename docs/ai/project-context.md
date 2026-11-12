# ToolDock project context and implementation contract

This document preserves decisions for AI-assisted development. User-facing instructions and inline examples belong in [README.md](../../README.md).

Current implementation: a .NET 10 Windows x64 CLI in `src/ToolDock`, with executable integration tests in `tests/ToolDock.Tests`. The requirements below remain the behavior contract; verification coverage and environment limitations are recorded at the end of this document.

## Scope and architecture

ToolDock is a user-level daemon runner for modern Windows on AMD64/x64. One persistent `ToolDock.exe` process manages one daemon instance and processes remaining in its Windows Job Object. Normal operation must not require elevation.

ToolDock and the installation manager are independent projects. Their shared interface is an executable name and arguments. ToolDock does not read installation manifests or manage installed versions, installation directories, `current` links, builds, updates, or cleanup of installed copies.

External scripts select daemons and arrange startup, including startup at sign-in. A brief console window from the external PowerShell launcher is acceptable. Scheduling, CI/CD, and recovery of the runner itself remain external operations. Packaging will be decided during implementation; old Windows and other architectures are outside the target scope.

## Identity and files

The configuration root is `~/.config/ToolDock`, where `~` is the current Windows user's profile. The ToolDock binary can live elsewhere.

| Path beneath the root | Purpose |
| --- | --- |
| `tools/*.json` | User-written configurations; no recursive discovery |
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
| `run <daemon>` | Become the persistent runner in the invoking environment |
| `stop <daemon>` | Signal the runner and wait for shutdown |
| `status <daemon> [--json]` | Report runner activity and its last recorded result |
| `--help` | Print CLI help |
| `--version` | Print the executable version |

`start` creates another instance of the same executable in `run` mode. Direct and background runs share lifecycle logic, locking, state, logs, and stop handling. Direct `run` also logs to the console; Ctrl+C requests ordinary shutdown. Direct `run` need not detach from the caller's console or external Job.

A new runner acknowledges startup only after acquiring the lock, publishing unfinished state, preparing stop handling, creating its Job, and successfully creating and starting the first daemon process. This confirms process creation, not application readiness. Errors before acknowledgement go back to the short command.

Repeated `start` reports `already running`; new successful startup reports `started`. Stopping an absent runner succeeds with `already stopped`; successful shutdown reports `stopped`. `stop` and `status` do not read or validate the daemon configuration, including after it has been deleted.

Short commands write results to stdout and errors to stderr. CLI exit codes: `0` for success, `1` for operation/configuration/state errors or timeout, `2` for invalid syntax. A successfully read `status` returns `0` even for a reported `failed` state.

The runner records `0` for successful child completion or successful manual stop. It records `1` for its own errors or a nonzero child exit for which no retry is scheduled. Exact child exit codes belong in the log. Process-creation errors are fatal; restart policies apply to exits of a successfully created child.

## Background lifetime

The detached runner and its managed group must survive the short command exiting and its console closing in supported environments. `start` waits for acknowledgement and exits; the persistent runner has another PID.

Create the background runner without a console, inherited standard streams, or unrelated inherited handles. Create the daemon without a console and with explicitly redirected streams. `CREATE_NO_WINDOW` provides console-free creation for console executables.

`FreeConsole` changes console attachment only. A process already assigned to a Job cannot simply detach from it. Resolve external Job separation when creating the background runner using applicable breakaway rules. A dedicated daemon Job does not cancel external Job restrictions. Fail startup if required detachment or group management cannot be established.

A separate process still has a parent relationship. Verify actual launch environments, including the user's SSH mechanism and sign-in launcher, against console closure, external Jobs, and other descendant-termination mechanisms.

## Configuration and launch snapshot

The user guide defines fields and examples. Required: `executable`, an `.exe` file name. Defaults: `arguments: []`, `environment: {}`, the configuration directory as `workingDirectory`, `restart: "on-failure"`, and `restartDelaySeconds: 5`.

Arguments are an array of strings, passed without a shell or ToolDock substitutions. Serialize them using appropriate Windows command-line quoting; users should only need JSON escaping. The program interprets its own argument values and paths.

The working directory must exist. Resolve relative directories from the JSON file's directory. Environment overrides apply to the child; other variables are inherited. Executable selection uses the runner's original `PATH`, not a child environment override.

On each runner launch:

1. Acquire the lock, prepare stop signaling, and publish the PID and unfinished result; then validate configuration.
2. Search the runner's `PATH` directories in order. Do not implicitly add the current or configuration directory.
3. Resolve the final executable path, including links in directory components. `GetFinalPathNameByHandleW` is suitable for this.
4. Capture the executable path, arguments, working directory, environment, and restart policy.
5. Create the Job and start the daemon. Acknowledge the first successful process launch to `start`.
6. Read stdout and stderr while waiting for child exit or a stop request.
7. If needed, clean up the old attempt, wait the configured delay, and retry from the same snapshot.

Retries reuse the selected path; a new runner reads JSON and resolves `PATH` again. Switching an installation link does not redirect an existing runner. The snapshot does not copy or freeze executable contents, the working directory, or external files. Keeping the selected program files available is an external responsibility.

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

Policy and retry delay are configurable per daemon. Keep remaining settings together in code; no CLI timeout override or global settings file is required.

A command timeout is an error. A failed or cancelled new `start` must clean up only its own runner, leaving an existing runner from another invocation untouched. Resolve ownership, acknowledgement, and cancellation races during implementation within this contract.

## Acceptance checks

- New `start` returns after the first daemon process starts; runner and group survive command exit and console closure in supported environments.
- Different IDs run one EXE independently. Repeating an ID, including a case variant or an ID held by direct `run`, creates no duplicate daemon.
- A bad configuration affects only its own instance.
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

Build with `dotnet build ToolDock.slnx -c Release`. Run the integration executable with `dotnet run --project tests/ToolDock.Tests -c Release`. Tests use isolated directories under `artifacts/tests/` and do not modify the user's ToolDock configuration.

The suite exercises argument quoting, environment and working-directory handling, both output streams, exit policies, case-insensitive duplicate protection, stop during retry, snapshot retention after configuration edits, state failures, file rotation and retention, startup acknowledgement, concurrent starts, cancelled startup ownership, and Job cleanup on retry and runner death.

The current execution environment places even the interactive shell in an external Job that forbids detachment. Tests report this explicitly and inject a contained native launcher only into test calls to exercise the remaining background protocol. The public CLI always requires detachment; it has no override to bypass this requirement.

Detached lifetime and explicit breakaway remain unverified in this environment. The suite includes a breakaway/output-drain scenario for an environment permitting detachment and separately checks cancellation of output reads while a writer remains open. Cross-session control, actual console closure/Ctrl+C, and the user's SSH launch mechanism still require validation in their target environments.

## API references

- [Process creation flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)
- [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
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
