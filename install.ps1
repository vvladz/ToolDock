[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository,

    [ValidateNotNullOrEmpty()]
    [string] $CatalogUrl,

    [string] $Version,

    [ValidateRange(1, 1440)]
    [int] $UpdateIntervalMinutes = 5,

    [ValidateNotNullOrEmpty()]
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'ToolDock')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'ToolDock can only be installed on Windows.'
}

if ([string]::IsNullOrWhiteSpace($CatalogUrl)) {
    $CatalogUrl = "https://raw.githubusercontent.com/$Repository/main/tools.json"
}

$installPath = [IO.Path]::GetFullPath($InstallRoot)
$binPath = Join-Path $installPath 'bin'
$assetName = 'ToolDock-win-x64.zip'
$checksumAssetName = "$assetName.sha256"
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'ToolDock-Installer'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$releaseUri = if ([string]::IsNullOrWhiteSpace($Version)) {
    "https://api.github.com/repos/$Repository/releases/latest"
} else {
    "https://api.github.com/repos/$Repository/releases/tags/$([Uri]::EscapeDataString($Version))"
}

Write-Host "Resolving ToolDock release from $Repository..."
$release = Invoke-RestMethod -Uri $releaseUri -Headers $headers
$matchingAssets = @($release.assets) | Where-Object { $_.name -ceq $assetName }
if ($matchingAssets.Count -ne 1) {
    throw "Release '$($release.tag_name)' does not contain $assetName."
}
$asset = $matchingAssets[0]
$matchingChecksums = @($release.assets) | Where-Object { $_.name -ceq $checksumAssetName }
if ($matchingChecksums.Count -ne 1) {
    throw "Release '$($release.tag_name)' does not contain $checksumAssetName."
}
$checksumAsset = $matchingChecksums[0]

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-install-" + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryRoot $assetName
$checksumPath = Join-Path $temporaryRoot $checksumAssetName
$packagePath = Join-Path $temporaryRoot 'package'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath
    Invoke-WebRequest -Uri $checksumAsset.browser_download_url -Headers $headers -OutFile $checksumPath
    $expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
    if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Release checksum file $checksumAssetName is invalid."
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 verification failed for $assetName."
    }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $packagePath

    $packageBin = Join-Path $packagePath 'bin'
    $starterSource = Join-Path $packageBin 'starter.exe'
    $updaterSource = Join-Path $packageBin 'updater.exe'
    if (-not (Test-Path -LiteralPath $starterSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $updaterSource -PathType Leaf)) {
        throw 'Release package is invalid: bin\starter.exe or bin\updater.exe is missing.'
    }

    $starterTaskName = 'ToolDock Starter'
    $updaterTaskName = 'ToolDock Updater'
    Get-ScheduledTask -TaskName $starterTaskName, $updaterTaskName -ErrorAction SilentlyContinue |
        Stop-ScheduledTask -ErrorAction SilentlyContinue

    foreach ($processName in @('starter', 'updater')) {
        $existingExecutable = Join-Path $binPath "$processName.exe"
        Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
            try { [string]::Equals($_.Path, $existingExecutable, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        } | Stop-Process -Force
    }

    New-Item -ItemType Directory -Path $binPath -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'tools') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'state') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'logs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'temp') -Force | Out-Null
    Copy-Item -Path (Join-Path $packageBin '*') -Destination $binPath -Force

    $config = [ordered]@{ catalogUrl = $CatalogUrl } | ConvertTo-Json
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $installPath 'config.json'), $config + [Environment]::NewLine, $utf8NoBom)

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $pathEntries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $hasBinPath = $pathEntries | Where-Object {
        [string]::Equals($_.TrimEnd('\'), $binPath.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $hasBinPath) {
        $newUserPath = (@($pathEntries) + $binPath) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }
    if (-not (($env:Path -split ';') -contains $binPath)) {
        $env:Path = "$env:Path;$binPath"
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited

    $starterAction = New-ScheduledTaskAction -Execute (Join-Path $binPath 'starter.exe')
    $starterTrigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $starterSettings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask `
        -TaskName $starterTaskName `
        -Action $starterAction `
        -Trigger $starterTrigger `
        -Principal $principal `
        -Settings $starterSettings `
        -Description 'Starts ToolDock process supervision in the current user session.' `
        -Force | Out-Null

    $updaterAction = New-ScheduledTaskAction -Execute (Join-Path $binPath 'updater.exe')
    $updaterTriggers = @(
        New-ScheduledTaskTrigger -AtLogOn -User $identity
        New-ScheduledTaskTrigger `
            -Once `
            -At ((Get-Date).AddMinutes(1)) `
            -RepetitionInterval (New-TimeSpan -Minutes $UpdateIntervalMinutes)
    )
    $updaterSettings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask `
        -TaskName $updaterTaskName `
        -Action $updaterAction `
        -Trigger $updaterTriggers `
        -Principal $principal `
        -Settings $updaterSettings `
        -Description 'Checks GitHub Releases and installs ToolDock-managed tools.' `
        -Force | Out-Null

    Start-ScheduledTask -TaskName $starterTaskName
    $firstUpdate = Start-Process -FilePath (Join-Path $binPath 'updater.exe') -Wait -PassThru
    if ($firstUpdate.ExitCode -ne 0) {
        Write-Warning "Initial update returned exit code $($firstUpdate.ExitCode). See $installPath\logs\updater.log."
    }

    Write-Host "ToolDock $($release.tag_name) installed in $installPath."
    Write-Host 'Open a new terminal to use managed tool commands from PATH.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
