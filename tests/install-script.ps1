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

    'Windows PowerShell installer compatibility: OK'
}
finally {
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}
