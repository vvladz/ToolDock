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

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $config = [ordered]@{ catalogUrl = 'https://127.0.0.1:1/tools.json' } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'config.json'),
        $config,
        [Text.UTF8Encoding]::new($false))

    $env:TOOLDOCK_HOME = $testRoot
    $process = Start-Process -FilePath $updater -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($exitCode -ne 1) {
        throw "Updater returned $exitCode instead of the handled failure exit code 1."
    }

    $log = Get-Content -LiteralPath (Join-Path $testRoot 'logs\ToolDock.Updater.log') -Raw
    if ($log -notmatch 'update failed') {
        throw 'Updater did not log the expected handled failure.'
    }
    if ($log -match 'Object synchronization method') {
        throw 'Updater released its mutex from a different thread.'
    }

    $mutex = [Threading.Mutex]::new($false, 'Local\ToolDock.Update')
    try {
        if (-not $mutex.WaitOne(0)) {
            throw 'Test could not acquire the update mutex.'
        }
        $blocked = Start-Process -FilePath $updater -Wait -PassThru
        if ($blocked.ExitCode -ne 0) {
            throw "Updater returned $($blocked.ExitCode) while another update owned the mutex."
        }
    }
    finally {
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
    }

    'updater mutex coordination: OK'
}
finally {
    $env:TOOLDOCK_HOME = $previousHome
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
