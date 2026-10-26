$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'src\TidalManager.App\TidalManager.App.csproj'
$publish = Join-Path $repo 'dist\publish'
$package = Join-Path $repo 'dist\package'
$zip = Join-Path $repo 'dist\TidalManager-win-x64.zip'

& dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

[System.IO.Directory]::CreateDirectory($package) | Out-Null
[System.IO.Directory]::CreateDirectory((Join-Path $package 'Assets')) | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'TidalManager.App.exe') -Destination (Join-Path $package 'TidalManager.App.exe') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src\TidalManager.App\Assets\AppIcon.ico') -Destination (Join-Path $package 'Assets\AppIcon.ico') -Force
Copy-Item -LiteralPath (Join-Path $repo 'installer\Install-TidalManager.ps1') -Destination (Join-Path $package 'Install-TidalManager.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repo 'installer\INSTALL.txt') -Destination (Join-Path $package 'INSTALL.txt') -Force
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -Force
Write-Host "Package ready: $zip"
