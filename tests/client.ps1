[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ClientPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$client = (Resolve-Path -LiteralPath $ClientPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-client-test-" + [Guid]::NewGuid().ToString('N'))
$previousHome = $env:TOOLDOCK_HOME

try {
    $env:TOOLDOCK_HOME = $testRoot
    foreach ($arguments in @(@('--help'), @('-h'), @('help'))) {
        $help = @(& $client @arguments)
        if ($LASTEXITCODE -ne 0 -or -not ($help -match 'tdctl exec') -or -not ($help -match 'tdctl commands')) {
            throw 'Client overview did not contain command execution and discovery.'
        }
    }
    foreach ($topic in @('commands', 'list', 'start', 'stop', 'restart', 'status', 'exec', 'update', 'variable', 'secret', 'logs')) {
        $help = @(& $client help $topic)
        if ($LASTEXITCODE -ne 0 -or -not ($help -match 'Usage:')) { throw "Missing help topic: $topic" }
        $alias = @(& $client $topic --help)
        if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -ne ($alias -join "`n")) { throw "Help alias mismatch: $topic" }
    }
    $empty = @(& $client commands)
    if ($LASTEXITCODE -ne 0 -or $empty.Count -ne 0 -or (Test-Path -LiteralPath $testRoot)) {
        throw 'Help and empty command discovery must work without creating installation state.'
    }
    foreach ($arguments in @(@('help', 'unknown'), @('exec', 'missing'), @('logs'), @('logs', 'daemon', '--lines', '-1'))) {
        $invalid = @(& $client @arguments 2>&1)
        if ($LASTEXITCODE -ne 2 -or -not ($invalid -match 'Usage:')) { throw "Expected usage error: $arguments" }
    }

    $logs = New-Item -ItemType Directory -Path (Join-Path $testRoot 'logs') -Force
    [IO.File]::WriteAllLines(
        (Join-Path $logs.FullName 'ToolDock.Updater.log'),
        @('first', 'second', 'third'),
        [Text.UTF8Encoding]::new($false))

    $output = @(& $client logs ToolDock.Updater --lines 2)
    if ($LASTEXITCODE -ne 0 -or ($output -join ',') -ne 'second,third') {
        throw "Unexpected log output: $($output -join ',')"
    }

    $state = New-Item -ItemType Directory -Path (Join-Path $testRoot 'state') -Force
    $catalog = @{
        tools = @{
            app = @{ repo = 'example/app'; asset = 'app.zip'; commands = @{ Zulu = @{ executable = 'app.exe' }; alpha = @{ executable = 'app.exe' } } }
            disabled = @{ repo = 'example/disabled'; asset = 'app.zip'; enabled = $false; commands = @{ hidden = @{ executable = 'app.exe' } } }
            absent = @{ repo = 'example/absent'; asset = 'app.zip'; commands = @{ missing = @{ executable = 'app.exe' } } }
        }
    }
    $installed = @{ activeCatalog = $catalog; tools = @{ APP = @{ version = 'v1' }; disabled = @{ version = 'v1' } } }
    $installed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $state.FullName 'installed.json') -Encoding utf8
    # A stale derived cache must not override committed state.
    '{"tools":{}}' | Set-Content -LiteralPath (Join-Path $state.FullName 'catalog.json') -Encoding utf8
    $commands = @(& $client commands)
    if ($LASTEXITCODE -ne 0 -or ($commands -join ',') -ne 'alpha,Zulu') { throw 'Command discovery did not use the active installed snapshot.' }
    '{"activeCatalog":{"tools":{}},"tools":{}}' | Set-Content -LiteralPath (Join-Path $state.FullName 'installed.json') -Encoding utf8

    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'config.json'),
        '{"catalogUrl":"https://127.0.0.1:1/tools.json"}',
        [Text.UTF8Encoding]::new($false))
    $updateOutput = @(& $client update 2>&1)
    if ($LASTEXITCODE -ne 1 -or -not ($updateOutput -match 'Fetching catalog')) {
        throw "Interactive update did not report the expected failure: $($updateOutput -join ',')"
    }
    $updateLog = Get-Content -LiteralPath (Join-Path $logs.FullName 'ToolDock.Updater.log') -Raw
    if ($updateLog -notmatch 'Update failed') {
        throw 'Interactive update did not append its failure to ToolDock.Updater.log.'
    }

    'client help, command discovery, usage errors, logs, and update: OK'
    # GitHub Actions propagates the last native process exit code after the script returns.
    $global:LASTEXITCODE = 0
}
finally {
    $env:TOOLDOCK_HOME = $previousHome
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
