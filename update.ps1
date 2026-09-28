<#
.SYNOPSIS
Updates the ToolDock installation containing this script.
.DESCRIPTION
Runs a temporary copy of the verified installer saved beside this script.
Installation settings default to values stored in config.json.
#>
[CmdletBinding()]
param(
    [string] $Repository,
    [string] $CatalogUrl,
    [string] $Version,
    [ValidateRange(1, 1440)]
    [int] $UpdateIntervalMinutes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = Join-Path $PSScriptRoot 'install.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    $installer = Join-Path $PSScriptRoot 'install.previous.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "ToolDock installer is missing from $PSScriptRoot."
    }
}

$temporaryInstaller = Join-Path ([IO.Path]::GetTempPath()) ('ToolDock-installer-' + [Guid]::NewGuid().ToString('N') + '.ps1')
try {
    Copy-Item -LiteralPath $installer -Destination $temporaryInstaller
    & $temporaryInstaller -InstallRoot $PSScriptRoot @PSBoundParameters
}
finally {
    if (Test-Path -LiteralPath $temporaryInstaller) {
        Remove-Item -LiteralPath $temporaryInstaller -Force
    }
}
