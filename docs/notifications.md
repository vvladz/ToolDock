# Notifications

ToolDock can send events to an ordinary installed catalog command. No handler is bundled or enabled by default. The `tdlog` package below is an example: supply your own executable and replace the repository, asset, and path with its actual release layout.

## Set up a handler

1. Add the handler package to your hosted catalog. Merge this entry into the existing `tools` object:

   ```json
   {
     "tools": {
       "notifications": {
         "repo": "owner/tdlog",
         "asset": "tdlog-win-x64.zip",
         "commands": {
           "tdlog": {
             "executable": "tdlog.exe",
             "environment": {
               "NOTIFY_TARGET": { "variable": "notifications.target" },
               "NOTIFY_TOKEN": { "secret": "notifications.token" }
             }
           }
         }
       }
     }
   }
   ```

2. Install it and configure the values used by your handler. The environment names below are illustrative; omit entries the handler does not need.

   ```powershell
   tdctl update
   tdctl commands
   tdctl variable set notifications.target 'https://service.example/notifications'
   tdctl secret set notifications.token
   'Manual notification test' | tdctl exec tdlog -- tool-update-success
   $LASTEXITCODE
   ```

   The manual call displays handler output and returns its exit code. The PowerShell pipeline adds a newline to this test message; ToolDock's automatic delivery sends its original message text without adding a newline.

3. Enable delivery in `<InstallRoot>\config.json`, retaining your actual catalog URL:

   ```json
   {
     "catalogUrl": "https://example.org/tools.json",
     "notificationCommand": "tdlog",
     "notificationTimeoutSeconds": 10
   }
   ```

   Settings are read for each event. Remove `notificationCommand` to disable delivery; there is no need to restart Starter. These local settings are preserved when the installer is rerun.

## Handler contract

| Input / behavior | Contract |
|---|---|
| Executable | A command from an enabled, installed package in the active catalog; no `PATH` lookup or shell invocation |
| Arguments | Exactly one argument: `daemon-crash`, `tool-update-success`, or `tool-update-failure` |
| Standard input | Complete plain-text message, UTF-8 without BOM; read until EOF; whitespace and line breaks are preserved |
| Environment | Normal catalog command environment, including variable and secret references |
| Working directory | The handler's installed version directory; ordinary `tdctl exec` instead uses the caller's directory |
| Standard output/error | Drained and discarded during automatic delivery; use your own handler diagnostics if needed |
| Exit code | `0` means success; a nonzero exit is logged as a delivery warning |
| Timeout | 10 seconds by default; configurable from 1 to 60 seconds; a running timed-out handler is terminated with its process tree |

For a .NET handler, set `Console.InputEncoding = new UTF8Encoding(false)` and read `Console.In.ReadToEndAsync()` before processing the message. The event type is the first application argument. Message text is intended for people; its wording is not a structured schema.

Keep the handler short-lived. Different daemon exits and updates may invoke it concurrently. ToolDock sends notifications outside the supervisor lock, so a slow handler does not prevent daemon status and control operations. It can delay completion of the update that produced the event, up to the timeout.

## Events and delivery limits

- **`daemon-crash`**: an unexpected main-process exit, including exit code `0`. Contains daemon name, PID, exit code, and the latest nonempty stderr line (up to 2,048 characters), when available. Requested stops and restarts do not emit this event. It does not enable automatic restart.
- **`tool-update-success`**: first installation or a version change after state persistence and Starter reconciliation succeed. Contains package name and old/new version; the first install reports `none` as the old version. A source change with an unchanged tag does not emit version-change success.
- **`tool-update-failure`**: a package or run-level failure. Contains a concise error and at most 20 lines / 4 KiB of diagnostic context from the current attempt.

Consecutive identical failures are suppressed separately for each package and for the overall run. A successful check clears that failure's suppression; a changed error is eligible immediately. Disabled packages and unchanged successful checks emit no update events.

Delivery is best effort, with no durable delivery queue or handler retry. A failed handler does not change the update or daemon result and does not generate another notification. Failure suppression records the attempted event even when no handler is configured or delivery fails. Enabling or fixing a handler therefore does not replay an already suppressed failure. A process interruption can also lose an event.

For delivery warnings, inspect `tdctl logs ToolDock.Starter` for daemon events and `tdctl logs ToolDock.Updater` for update events. Handler secret references are resolved normally; avoid including credentials in the text your applications write to stderr, which can appear in a crash message.
