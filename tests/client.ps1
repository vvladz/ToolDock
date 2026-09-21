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
    $logs = New-Item -ItemType Directory -Path (Join-Path $testRoot 'logs') -Force
    [IO.File]::WriteAllLines(
        (Join-Path $logs.FullName 'updater.log'),
        @('first', 'second', 'third'),
        [Text.UTF8Encoding]::new($false))
    $env:TOOLDOCK_HOME = $testRoot

    $output = @(& $client logs updater --lines 2)
    if ($LASTEXITCODE -ne 0 -or ($output -join ',') -ne 'second,third') {
        throw "Unexpected log output: $($output -join ',')"
    }

    $help = @(& $client --help)
    if ($LASTEXITCODE -ne 0 -or -not ($help -match 'tdctl update')) {
        throw 'Client help did not contain the expected commands.'
    }

    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'config.json'),
        '{"catalogUrl":"https://127.0.0.1:1/tools.json"}',
        [Text.UTF8Encoding]::new($false))
    $updateOutput = @(& $client update 2>&1)
    if ($LASTEXITCODE -ne 1 -or -not ($updateOutput -match 'Fetching catalog')) {
        throw "Interactive update did not report the expected failure: $($updateOutput -join ',')"
    }
    $updateLog = Get-Content -LiteralPath (Join-Path $logs.FullName 'updater.log') -Raw
    if ($updateLog -notmatch 'Update failed') {
        throw 'Interactive update did not append its failure to updater.log.'
    }

    'client logs, help, and update: OK'
}
finally {
    $env:TOOLDOCK_HOME = $previousHome
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
