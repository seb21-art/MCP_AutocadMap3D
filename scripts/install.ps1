<#
.SYNOPSIS
    Compile et installe le plug-in AutoCAD et/ou le serveur MCP, sans droits administrateur.
.DESCRIPTION
    Plug-in : bundle copié dans %APPDATA%\Autodesk\ApplicationPlugins\MCPMap3D.bundle (AutoCAD doit être fermé),
    avec une DLL par version d'AutoCAD installée (2026 : .NET 8, 2027 : .NET 10). -AcadVersion en force une seule.
    Serveur : exécutable copié dans %LOCALAPPDATA%\McpMap3D\server. S'il est en cours d'utilisation par Claude,
    l'ancien exécutable est renommé : les sessions ouvertes le gardent, les nouvelles prennent le nouveau.
    L'enregistrement dans Claude Desktop et Claude Code se fait à part, avec register.ps1.
#>
param(
    [ValidateSet('All', 'Plugin', 'Server')]
    [string]$Component = 'All',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('2026', '2027')]
    [string[]]$AcadVersion
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-DotNetSdk

if ($Component -in 'All', 'Plugin') {
    if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
        throw "AutoCAD est ouvert : fermez-le avant d'installer le plug-in (sa DLL serait verrouillée)."
    }

    if (-not $AcadVersion) { $AcadVersion = @(Get-InstalledAcadVersions) }
    if (-not $AcadVersion) {
        throw "Aucun AutoCAD 2026 ou 2027 trouvé dans $env:ProgramFiles\Autodesk."
    }

    # Compilation de toutes les versions avant de toucher au bundle installé.
    $outputs = [ordered]@{}
    foreach ($version in $AcadVersion) {
        $sdk = Get-DotNetSdk $AcadVersions[$version]
        $output = Join-Path $root "artifacts\plugin\$version\$Configuration"
        & $sdk build (Join-Path $root 'src\McpMap3D.Plugin\McpMap3D.Plugin.csproj') -c $Configuration -o $output --nologo `
            -p:AcadVersion=$version
        if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation du plug-in pour AutoCAD $version." }
        $outputs[$version] = $output
    }

    $bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\MCPMap3D.bundle'
    $contents = Join-Path $bundle 'Contents'
    if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
    Copy-Item (Join-Path $root 'bundle\MCPMap3D.bundle\PackageContents.xml') (New-Item -ItemType Directory -Force $bundle) -Force
    foreach ($version in $outputs.Keys) {
        $target = New-Item -ItemType Directory -Force (Join-Path $contents $version)
        Copy-Item (Join-Path $outputs[$version] 'McpMap3D.Plugin.dll'), (Join-Path $outputs[$version] 'McpMap3D.Plugin.pdb') $target -Force
    }
    Write-Host "Plug-in installé dans $bundle pour AutoCAD $($outputs.Keys -join ', ')" -ForegroundColor Green
}

if ($Component -in 'All', 'Server') {
    $output = Join-Path $root 'artifacts\server'
    & $dotnet publish (Join-Path $root 'src\McpMap3D.Server\McpMap3D.Server.csproj') -c $Configuration -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication du serveur MCP." }

    $serverDir = Get-ServerDirectory
    $exe = Join-Path $serverDir 'McpMap3D.Server.exe'
    New-Item -ItemType Directory -Force $serverDir | Out-Null

    # Un exécutable en cours d'exécution ne peut pas être remplacé, mais il peut être renommé.
    if (Test-Path $exe) { Rename-Item $exe "McpMap3D.Server.exe.$(Get-Date -Format yyyyMMdd-HHmmss).old" }
    Copy-Item (Join-Path $output 'McpMap3D.Server.exe') $exe
    Get-ChildItem $serverDir -Filter '*.old' | ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    Write-Host "Serveur MCP installé : $exe" -ForegroundColor Green
}
