namespace ToolDock.Client;

internal static class ClientHelp
{
    public const string Overview = """
        ToolDock - manage installed commands and background daemons.

        Usage:
          tdctl --help                     Show this help
          tdctl help <command>             Show command details and examples
          tdctl commands                   List installed, enabled command names
          tdctl list                       List catalog daemon names (requires Starter)
          tdctl start|stop|restart|status <daemon>
          tdctl exec <command> -- [arguments...]
          tdctl update                     Check and update all enabled packages
          tdctl variable set <name> <value>
          tdctl variable get|remove <name>
          tdctl variable list|status
          tdctl secret set <name> [--stdin]
          tdctl secret remove <name>
          tdctl secret list|status
          tdctl logs <target> [--lines <count>] [--follow]

        Log targets: ToolDock.Starter, ToolDock.Updater, ToolDock.Client, or a daemon name.
        Commands also accept --help or -h (for example, tdctl logs --help).

        Examples:
          tdctl commands
          tdctl exec farshell -- --help
          tdctl status farshell
          tdctl logs ToolDock.Updater --lines 50

        TOOLDOCK_HOME overrides the installation root; otherwise an installed client
        uses config.json beside its bin directory, then ~/.tooldock.
        Exit codes: 0 success, 1 operation failed, 2 invalid usage, 130 cancelled.
        exec returns the child process exit code.
        """;

    public static string? For(string topic) => topic switch
    {
        "commands" => """
            Usage: tdctl commands
            List installed, enabled catalog commands, one name per line, sorted by name.
            Reads local state without contacting Starter or checking for updates.
            An empty installation prints nothing. Use tdctl update to refresh the catalog.

            Example: tdctl exec farshell -- --help
            """,
        "list" => """
            Usage: tdctl list
            Ask Starter for daemon names from the active catalog, including disabled packages.
            The response starts with OK; use tdctl status <daemon> for process status.
            Use tdctl commands to discover interactive commands.
            """,
        "start" or "stop" or "restart" or "status" => """
            Usage: tdctl start|stop|restart|status <daemon>
            Control one daemon by its catalog name. Starter must be running.
            start leaves an already running daemon alone; restart stops and starts it.
            stop terminates the daemon's process tree. status reports running or stopped.
            An unexpected exit does not automatically restart the daemon.

            Examples:
              tdctl list
              tdctl status farshell
              tdctl restart farshell
            """,
        "exec" => """
            Usage: tdctl exec <command> -- [arguments...]
            Run an installed catalog command with its configured environment.
            The -- separator is required, even when there are no child arguments.
            Child arguments, stdin/stdout/stderr, and the child exit code are preserved.
            The child uses the current working directory. Starter is not required.
            Missing referenced variables or secrets prevent the launch.

            Examples:
              tdctl commands
              tdctl exec farshell -- --help
              tdctl exec farshell --
            A generated command shim also supports: farshell --help
            """,
        "update" => """
            Usage: tdctl update
            Fetch the configured HTTPS catalog and check all enabled packages.
            Shares the update lock and log with the scheduled updater.
            Starts/restarts eligible daemons through Starter after package activation.
            Run again to repair an interrupted activation or retry pending reconciliation.
            This updates managed packages; rerun install.ps1 to update ToolDock itself.

            Exit codes: 0 completed, 1 failure or update already running, 130 Ctrl+C.
            Diagnostics: tdctl logs ToolDock.Updater --lines 100
            """,
        "variable" => """
            Usage:
              tdctl variable set <name> <value>
              tdctl variable get|remove <name>
              tdctl variable list|status
            Manage plaintext user variables. Catalog variables override user values.
            list prints names; status prints set, missing, or unused for each user-store
            name/reference. Catalog-supplied references do not require a user value.
            Changes apply on the next launch; restart a daemon to apply them now.

            Examples:
              tdctl variable set service.url https://service.example
              tdctl variable get service.url
              tdctl variable status
            """,
        "secret" => """
            Usage:
              tdctl secret set <name> [--stdin]
              tdctl secret remove <name>
              tdctl secret list|status
            Store secrets protected by Windows DPAPI for the current user.
            set prompts with masked input; --stdin reads one line for automation.
            Secret values are never accepted as command-line arguments or printed.
            list prints names; status prints set, missing, or unused. There is no get.
            Changes apply on the next launch; restart a daemon to apply them now.

            Examples:
              tdctl secret set service.token
              tdctl secret status
              tdctl secret remove service.token
            """,
        "logs" => """
            Usage: tdctl logs <target> [--lines <count>] [--follow|-f]
            Targets: ToolDock.Starter, ToolDock.Updater, ToolDock.Client, or a daemon name.
            Prints the last 100 lines by default. --lines accepts zero or a positive integer.
            --follow waits for new output, including when the log does not exist yet.
            Press Ctrl+C to stop following (exit code 130).

            Examples:
              tdctl logs ToolDock.Updater --lines 50
              tdctl logs ToolDock.Client
              tdctl logs farshell --lines 0 --follow
            """,
        _ => null
    };
}
