[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $UpdaterPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$updater = (Resolve-Path -LiteralPath $UpdaterPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-updater-test-" + [Guid]::NewGuid().ToString('N'))
$previousHome = $env:TOOLDOCK_HOME
$previousNoConsole = $env:TOOLDOCK_NO_CONSOLE

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $config = [ordered]@{ catalogUrl = 'https://127.0.0.1:1/tools.json' } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'config.json'),
        $config,
        [Text.UTF8Encoding]::new($false))

    $env:TOOLDOCK_HOME = $testRoot
    $env:TOOLDOCK_NO_CONSOLE = '1'
    $process = Start-Process -FilePath $updater -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($exitCode -ne 1) {
        throw "Updater returned $exitCode instead of the handled failure exit code 1."
    }

    $log = Get-Content -LiteralPath (Join-Path $testRoot 'logs\updater.log') -Raw
    if ($log -notmatch 'update failed') {
        throw 'Updater did not log the expected handled failure.'
    }
    if ($log -match 'Object synchronization method') {
        throw 'Updater released its mutex from a different thread.'
    }

    'updater async mutex cleanup: OK'
}
finally {
    $env:TOOLDOCK_HOME = $previousHome
    $env:TOOLDOCK_NO_CONSOLE = $previousNoConsole
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
