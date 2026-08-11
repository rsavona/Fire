<#
.SYNOPSIS
    Compiles and publishes the Fusion.Console host plus the plugins required by the
    TuraHost blueprint (fbp_TuraHost.json), producing a self-contained deployment folder
    at Publish\TuraHost\app — the same layout FusionLab's /publish page produces.

.PARAMETER RuntimeId
    Target runtime: win-x64 (default) or linux-x64.

.PARAMETER SelfContained
    Publish self-contained (bundles the .NET runtime). Default: $true.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    .\publish-turahost.ps1
    .\publish-turahost.ps1 -RuntimeId linux-x64 -SelfContained:$false
#>
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [bool]$SelfContained = $true,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$Customer = 'TuraHost'
$BlueprintPath = Join-Path $RepoRoot 'FusionLab\ProjectConfigurations\fbp_TuraHost.json'
$CustomerFolder = Join-Path $RepoRoot "Publish\$Customer"
$AppFolder = Join-Path $CustomerFolder 'app'
$ConsoleProj = Join-Path $RepoRoot 'DeviceSpace.Console\Fusion.Console.csproj'

# Plugins required by fbp_TuraHost.json's ElementList/ReactionList (see PublishService.cs
# for the general-purpose blueprint->plugin resolver; this script hardcodes TuraHost's set).
$PluginProjects = @(
    'Fusion.Element.Database.Suite',
    'Fusion.Element.HostComm',
    'Fusion.Element.Support.CLI',
    'Fusion.Reaction.Tura.HostComm'
)

if (-not (Test-Path $BlueprintPath)) {
    throw "Blueprint not found: $BlueprintPath"
}
if (-not (Test-Path $ConsoleProj)) {
    throw "Fusion.Console project not found: $ConsoleProj"
}

Write-Host "Customer: $Customer" -ForegroundColor Cyan
Write-Host "Output folder: $CustomerFolder" -ForegroundColor Cyan

if (Test-Path $AppFolder) {
    Write-Host 'Removing previous app folder...'
    Remove-Item $AppFolder -Recurse -Force
}
New-Item -ItemType Directory -Path $AppFolder -Force | Out-Null

# Every csproj in this repo sets OutputPath=..\Output, which FusionLab (if running) has locked.
# Redirect to an isolated temp bin so this script never touches the shared Output folder.
$BuildBin = Join-Path ([System.IO.Path]::GetTempPath()) "FusionPublish\bin-$([guid]::NewGuid().ToString('N'))"
$BuildBin = $BuildBin.Replace('\', '/')
$IsolatedBin = "-p:OutputPath=$BuildBin/"

function Invoke-DotnetPublish {
    param([string[]]$Arguments)
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE): $($Arguments -join ' ')"
    }
}

function Copy-MissingFiles {
    param([string]$Source, [string]$Target)
    $copied = 0
    Get-ChildItem -Path $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Source.Length).TrimStart('\', '/')
        $destination = Join-Path $Target $relative
        if (-not (Test-Path $destination)) {
            $destDir = Split-Path $destination -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item $_.FullName $destination
            $copied++
        }
    }
    return $copied
}

try {
    # 1. Publish the Fusion.Console plugin host (folder publish — never single-file; single-file
    #    breaks Windows service starts and Fusion.Element.*/Fusion.Reaction.* DLL scanning).
    Write-Host "`nPublishing host: Fusion.Console ($RuntimeId, self-contained=$SelfContained)..." -ForegroundColor Green
    Invoke-DotnetPublish @(
        'publish', $ConsoleProj,
        '-c', $Configuration,
        '-r', $RuntimeId,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '-o', $AppFolder,
        $IsolatedBin,
        '--nologo'
    )

    # 2. Publish each plugin project (framework-dependent, no RID) into a temp staging folder,
    #    then copy only files the host publish did not already produce.
    $includedPlugins = @()
    foreach ($plugin in $PluginProjects) {
        $pluginProj = Join-Path $RepoRoot "$plugin\$plugin.csproj"
        if (-not (Test-Path $pluginProj)) {
            throw "Plugin project not found: $pluginProj"
        }

        $staging = Join-Path ([System.IO.Path]::GetTempPath()) "FusionPublish\$plugin-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        try {
            Write-Host "`nPublishing plugin: $plugin..." -ForegroundColor Green
            Invoke-DotnetPublish @(
                'publish', $pluginProj,
                '-c', $Configuration,
                '-o', $staging,
                $IsolatedBin,
                '--nologo'
            )
            $copied = Copy-MissingFiles -Source $staging -Target $AppFolder
            Write-Host "  copied $copied new file(s) into app folder."
            $includedPlugins += $plugin
        }
        finally {
            Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # 3. Blueprint json goes next to the exe (ConfigurationLoader resolves --config relative to cwd).
    $blueprintFileName = Split-Path $BlueprintPath -Leaf
    Copy-Item $BlueprintPath (Join-Path $AppFolder $blueprintFileName) -Force
    Write-Host "`nCopied blueprint $blueprintFileName into app folder."

    # 4. Install script next to (not inside) app, so the whole customer folder is portable.
    if ($RuntimeId -eq 'win-x64') {
        $ps1Path = Join-Path $CustomerFolder 'install-service.ps1'
        @"
# Installs $Customer as a Windows service. Run as Administrator from the deployment folder.
`$exe = Join-Path `$PSScriptRoot 'app\Fusion.Console.exe'
sc.exe stop $Customer 2>`$null
sc.exe delete $Customer 2>`$null
sc.exe create $Customer binPath= "`"`$exe`" --config $blueprintFileName --service-name $Customer" start= auto
sc.exe start $Customer
"@ | Set-Content -Path $ps1Path -Encoding UTF8
        Write-Host "Wrote install-service.ps1."
    }
    else {
        $unitPath = Join-Path $CustomerFolder "fusion-$($Customer.ToLowerInvariant()).service"
        @"
[Unit]
Description=Fusion service for $Customer
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/fusion/$($Customer.ToLowerInvariant())/app
ExecStart=/opt/fusion/$($Customer.ToLowerInvariant())/app/Fusion.Console --config $blueprintFileName
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
"@ | Set-Content -Path $unitPath -Encoding UTF8
        Write-Host "Wrote fusion-$($Customer.ToLowerInvariant()).service."
    }

    Write-Host "`nPublish complete: $CustomerFolder" -ForegroundColor Cyan
    Write-Host "Included plugins: $($includedPlugins -join ', ')"
}
finally {
    Remove-Item (Split-Path $BuildBin -Parent) -Recurse -Force -ErrorAction SilentlyContinue
}
