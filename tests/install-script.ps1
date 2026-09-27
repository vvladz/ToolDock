[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sentinel = 'STOP_AFTER_RELEASE_ASSET_SELECTION'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-installer-test-" + [Guid]::NewGuid().ToString('N'))

function Invoke-RestMethod {
    return [pscustomobject]@{
        tag_name = 'v-test'
        assets = @(
            [pscustomobject]@{
                name = 'ToolDock-win-x64.zip'
                browser_download_url = 'https://example.org/ToolDock-win-x64.zip'
            }
            [pscustomobject]@{
                name = 'ToolDock-win-x64.zip.sha256'
                browser_download_url = 'https://example.org/ToolDock-win-x64.zip.sha256'
            }
        )
    }
}

function Invoke-WebRequest {
    throw $sentinel
}

try {
    $help = Get-Help $installer -Full | Out-String
    foreach ($expected in @('Installs or updates ToolDock', 'Repository', 'CatalogUrl', 'Version', 'UpdateIntervalMinutes', 'InstallRoot')) {
        if (-not $help.Contains($expected)) { throw "Installer help is missing: $expected" }
    }
    try {
        & $installer `
            -Repository 'example/ToolDock' `
            -CatalogUrl 'https://example.org/tools.json' `
            -InstallRoot $installRoot
        throw 'Installer unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) {
            throw
        }
    }

    # Exercise the actual staging/switch functions without changing scheduled tasks or user PATH.
    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Installer has PowerShell syntax errors.' }
    $definitions = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -in @('Remove-InstallDirectory', 'Stage-ToolDockBin', 'Switch-ToolDockBin')
    }, $false)
    foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    $releaseBin = Join-Path $installRoot 'release'
    $liveBin = Join-Path $installRoot 'bin'
    New-Item -ItemType Directory -Path $releaseBin, $liveBin -Force | Out-Null
    $coreFiles = @('ToolDock.Starter.exe', 'ToolDock.Updater.exe', 'ToolDock.Client.exe', 'tdctl.cmd')
    foreach ($name in $coreFiles) {
        [IO.File]::WriteAllText((Join-Path $releaseBin $name), 'new')
        [IO.File]::WriteAllText((Join-Path $liveBin $name), 'old')
    }
    [IO.File]::WriteAllText((Join-Path $liveBin 'managed.cmd'), 'managed command')
    Stage-ToolDockBin $releaseBin $installRoot
    foreach ($name in $coreFiles) {
        if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'old') { throw 'Staging modified live bin.' }
    }
    Switch-ToolDockBin $installRoot
    foreach ($name in $coreFiles) {
        if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'new') { throw 'Switched bin contains mixed versions.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $liveBin 'managed.cmd')) -ne 'managed command') { throw 'Managed shim was lost.' }

    $held = [IO.File]::Open((Join-Path $releaseBin 'ToolDock.Client.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $failedCopy = $false
        try { Stage-ToolDockBin $releaseBin $installRoot } catch { $failedCopy = $true }
        if (-not $failedCopy) { throw 'Test did not inject the expected copy failure.' }
        foreach ($name in $coreFiles) {
            if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'new') { throw 'Failed copy modified live bin.' }
        }
    }
    finally { $held.Dispose() }

    # Simulate interruption after moving bin aside. Rerunning must finish forward installation.
    Stage-ToolDockBin $releaseBin $installRoot
    Remove-InstallDirectory (Join-Path $installRoot 'bin.previous') $installRoot
    Move-Item -LiteralPath $liveBin -Destination (Join-Path $installRoot 'bin.previous')
    Stage-ToolDockBin $releaseBin $installRoot
    Switch-ToolDockBin $installRoot
    if (-not (Test-Path -LiteralPath (Join-Path $liveBin 'managed.cmd'))) { throw 'Interrupted install lost managed shim.' }
    Stage-ToolDockBin $releaseBin $installRoot
    Remove-Item -LiteralPath (Join-Path $installRoot 'bin.next\ToolDock.Updater.exe')
    $rejected = $false
    try { Switch-ToolDockBin $installRoot } catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $liveBin 'ToolDock.Updater.exe')) -ne 'new') {
        throw 'Incomplete staging replaced live bin.'
    }
    "PowerShell $($PSVersionTable.PSVersion) installer help, staging, and recovery: OK"
}
finally {
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}
