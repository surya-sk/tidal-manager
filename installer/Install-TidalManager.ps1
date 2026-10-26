param(
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\TidalManager'),
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'TidalManager.App.exe'
$sourceIcon = Join-Path $PSScriptRoot 'Assets\AppIcon.ico'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "TidalManager.App.exe must be beside this installer. Extract the whole ZIP first."
}

$target = [System.IO.Path]::GetFullPath($InstallDirectory)
[System.IO.Directory]::CreateDirectory($target) | Out-Null
$targetExe = Join-Path $target 'TidalManager.App.exe'
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
if (Test-Path -LiteralPath $sourceIcon -PathType Leaf) {
    $assets = Join-Path $target 'Assets'
    [System.IO.Directory]::CreateDirectory($assets) | Out-Null
    Copy-Item -LiteralPath $sourceIcon -Destination (Join-Path $assets 'AppIcon.ico') -Force
}

if (-not $NoShortcut) {
    $startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
    [System.IO.Directory]::CreateDirectory($startMenu) | Out-Null
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startMenu 'Tidal Manager.lnk'))
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Save()
}

Write-Host "Installed Tidal Manager to $target"
Write-Host 'tiddl is a separate dependency. Open Settings after launch to check its path.'
