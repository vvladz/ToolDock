[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sentinel = 'STOP_AFTER_RELEASE_ASSET_SELECTION'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$updateLauncher = Join-Path (Split-Path -Parent $installer) 'update.ps1'
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-installer-test-" + [Guid]::NewGuid().ToString('N'))
$expectedDefaultRoot = [IO.Path]::GetFullPath((Join-Path $HOME '.tooldock'))
$verifyDefaultRoot = $false
$verifySavedSettings = $false
$expectedReleaseUri = 'https://api.github.com/repos/example/ToolDock/releases/latest'

function Invoke-RestMethod {
    param([string] $Uri)
    if ($verifySavedSettings -and $Uri -cne $expectedReleaseUri) {
        throw "Saved repository was not used: $Uri"
    }
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
    if ($verifyDefaultRoot -and -not [string]::Equals($installPath, $expectedDefaultRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer chose $installPath instead of $expectedDefaultRoot."
    }
    if ($verifySavedSettings -and
        (-not [string]::Equals($installPath, $installRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $CatalogUrl -cne 'https://example.org/saved.json' -or $UpdateIntervalMinutes -ne 17)) {
        throw 'Installed update.ps1 did not use its root and saved settings.'
    }
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

    $verifyDefaultRoot = $true
    try {
        & $installer -Repository 'example/ToolDock' -CatalogUrl 'https://example.org/tools.json'
        throw 'Installer unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) {
            throw
        }
    }
    $verifyDefaultRoot = $false

    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installRoot 'config.json'),
        '{"catalogUrl":"https://example.org/saved.json","repository":"example/ToolDock","updateIntervalMinutes":17}')
    $updateScript = Join-Path $installRoot 'update.ps1'
    Copy-Item -LiteralPath $installer -Destination (Join-Path $installRoot 'install.ps1')
    Copy-Item -LiteralPath $updateLauncher -Destination $updateScript
    $verifySavedSettings = $true
    try {
        & $updateScript
        throw 'Installed update.ps1 unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }
    $verifySavedSettings = $false
    Move-Item -LiteralPath (Join-Path $installRoot 'install.ps1') -Destination (Join-Path $installRoot 'install.previous.ps1')
    $expectedReleaseUri = 'https://api.github.com/repos/example/ToolDock/releases/tags/v-selected'
    $verifySavedSettings = $true
    try {
        & $updateScript -Version v-selected
        throw 'Launcher unexpectedly continued past the download boundary during recovery.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }
    $verifySavedSettings = $false
    $expectedReleaseUri = 'https://api.github.com/repos/example/ToolDock/releases/latest'

    # Exercise the actual staging/switch functions without changing scheduled tasks or user PATH.
    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Installer has PowerShell syntax errors.' }
    $definitions = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -in @('Remove-InstallDirectory', 'Put-PathEntryFirst', 'Stage-ToolDockBin', 'Switch-ToolDockBin', 'Publish-InstallerFile', 'Publish-InstallerScripts')
    }, $false)
    foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    $orderedPath = Put-PathEntryFirst 'C:\Previous\bin;C:\New\bin;C:\Other;C:\NEW\bin\' 'C:\New\bin'
    if ($orderedPath -cne 'C:\New\bin;C:\Previous\bin;C:\Other' -or
        (Put-PathEntryFirst $orderedPath 'C:\New\bin') -cne $orderedPath) {
        throw 'Installer did not prioritize the selected bin directory without duplicates.'
    }
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
    $scriptsSource = Join-Path $installRoot 'release-scripts'
    New-Item -ItemType Directory -Path $scriptsSource | Out-Null
    [IO.File]::WriteAllText((Join-Path $scriptsSource 'install.ps1'), '# updated installer')
    [IO.File]::WriteAllText((Join-Path $scriptsSource 'update.ps1'), '# new launcher')
    Publish-InstallerScripts $scriptsSource $installRoot
    if ([IO.File]::ReadAllText((Join-Path $installRoot 'install.ps1')) -ne '# updated installer' -or
        [IO.File]::ReadAllText($updateScript) -ne [IO.File]::ReadAllText($updateLauncher) -or
        (Test-Path -LiteralPath (Join-Path $installRoot 'install.previous.ps1'))) {
        throw 'Installer was not updated or the existing launcher was changed.'
    }
    [IO.File]::WriteAllText((Join-Path $scriptsSource 'install.ps1'), '# next installer')
    Publish-InstallerScripts $scriptsSource $installRoot
    if ([IO.File]::ReadAllText((Join-Path $installRoot 'install.ps1')) -ne '# next installer' -or
        (Test-Path -LiteralPath (Join-Path $installRoot 'install.previous.ps1'))) {
        throw 'Existing installer was not refreshed cleanly.'
    }
    $freshScriptsRoot = Join-Path $installRoot 'fresh-scripts'
    New-Item -ItemType Directory -Path $freshScriptsRoot | Out-Null
    Publish-InstallerScripts $scriptsSource $freshScriptsRoot
    if (-not (Test-Path -LiteralPath (Join-Path $freshScriptsRoot 'install.ps1') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $freshScriptsRoot 'update.ps1') -PathType Leaf)) {
        throw 'First installation did not publish both scripts.'
    }
    "PowerShell $($PSVersionTable.PSVersion) installer help, staging, recovery, and update launcher: OK"
}
finally {
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}
