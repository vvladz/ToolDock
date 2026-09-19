[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StarterPath,

    [Parameter(Mandatory = $true)]
    [string] $ToolDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$starter = [IO.Path]::GetFullPath($StarterPath)
$toolSource = [IO.Path]::GetFullPath($ToolDirectory)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-smoke-" + [Guid]::NewGuid().ToString('N'))
$versionRoot = Join-Path $testRoot 'tools\smoke\v1'
$stateRoot = Join-Path $testRoot 'state'
$logPath = Join-Path $testRoot 'logs\smoke.log'
$previousHome = $env:TOOLDOCK_HOME
$previousNoConsole = $env:TOOLDOCK_NO_CONSOLE
$starterProcess = $null

function Write-Utf8Json([string] $Path, [object] $Value) {
    $json = $Value | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Invoke-Starter([string] $Command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        'tool-starter',
        [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $encoding = [Text.UTF8Encoding]::new($false)
        $writer = [IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
        $reader = [IO.StreamReader]::new($pipe, $encoding, $false, 1024, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine($Command)
            return $reader.ReadLine()
        }
        finally {
            $writer.Dispose()
            $reader.Dispose()
        }
    }
    finally {
        $pipe.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $toolSource '*') -Destination $versionRoot -Recurse

    Write-Utf8Json (Join-Path $stateRoot 'catalog.json') ([ordered]@{
        tools = [ordered]@{
            smoke = [ordered]@{
                repo = 'example/smoke'
                asset = 'smoke.zip'
                enabled = $true
                autostart = $true
                restart = $true
                executable = 'smoke-tool.exe'
            }
        }
    })
    Write-Utf8Json (Join-Path $stateRoot 'installed.json') ([ordered]@{
        tools = [ordered]@{
            smoke = [ordered]@{
                version = 'v1'
                path = 'tools\smoke\v1\smoke-tool.exe'
            }
        }
    })

    $env:TOOLDOCK_HOME = $testRoot
    $env:TOOLDOCK_NO_CONSOLE = '1'
    $starterProcess = Start-Process -FilePath $starter -PassThru
    $env:TOOLDOCK_NO_CONSOLE = $previousNoConsole

    $status = $null
    $lastConnectError = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $status = Invoke-Starter 'status smoke'
            if ($status -like 'OK running pid=*') { break }
        }
        catch {
            $lastConnectError = $_.Exception.Message
            Start-Sleep -Milliseconds 200
        }
    }
    if ($status -notlike 'OK running pid=*') {
        throw "Autostart failed: $status; pipe error: $lastConnectError"
    }

    $rootPid = [int]($status -replace '^OK running pid=', '')
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $childPid = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            $childLine = Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'OUT child pid=(\d+)' } | Select-Object -Last 1
            if ($childLine -match 'OUT child pid=(\d+)') {
                $childPid = [int]$Matches[1]
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $childPid) {
        throw 'Managed stdout was not captured.'
    }
    if (-not (Select-String -LiteralPath $logPath -SimpleMatch 'ERR stderr ready' -Quiet)) {
        throw 'Managed stderr was not captured.'
    }

    $stop = Invoke-Starter 'stop smoke'
    if ($stop -ne 'OK stopped') {
        throw "Stop failed: $stop"
    }
    if (Get-Process -Id $rootPid, $childPid -ErrorAction SilentlyContinue) {
        throw 'Closing the Job Object did not terminate the complete process tree.'
    }
    if ((Invoke-Starter 'status smoke') -ne 'OK stopped') {
        throw 'Stopped status was not reported.'
    }

    'starter smoke test: OK'
}
catch {
    $logFiles = Get-ChildItem -LiteralPath (Join-Path $testRoot 'logs') -File -ErrorAction SilentlyContinue
    foreach ($logFile in $logFiles) {
        Write-Warning "--- $($logFile.Name) ---"
        Get-Content -LiteralPath $logFile.FullName | Write-Warning
    }
    throw
}
finally {
    if ($null -ne $starterProcess -and -not $starterProcess.HasExited) {
        Stop-Process -Id $starterProcess.Id -Force
        $starterProcess.WaitForExit(5000) | Out-Null
    }
    $env:TOOLDOCK_HOME = $previousHome
    $env:TOOLDOCK_NO_CONSOLE = $previousNoConsole
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
