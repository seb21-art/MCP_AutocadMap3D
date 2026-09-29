<#
.SYNOPSIS
    Désenregistre le serveur MCP de Claude Desktop et Claude Code, puis retire le serveur et le plug-in.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    throw "AutoCAD est ouvert : fermez-le avant de désinstaller."
}
if (Test-ClaudeDesktopRunning) {
    throw ("Claude Desktop est ouvert : quittez-le complètement (zone de notification > Quitter) avant de désinstaller, " +
        "sinon il réécrirait sa configuration et garderait le serveur verrouillé.")
}

$serverDir = Get-ServerDirectory
$exe = Join-Path $serverDir 'McpMap3D.Server.exe'
if (Test-Path $exe) { & $exe unregister-desktop }

$claude = Get-ClaudeCli
if ($claude) {
    if (Test-ClaudeCodeServer $claude) { & $claude mcp remove $McpServerName --scope user }
}

$installRoot = Get-InstallRoot
if (Test-Path $installRoot) {
    try {
        Remove-Item $installRoot -Recurse -Force
        Write-Host "Serveur et journaux supprimés : $installRoot" -ForegroundColor Green
    } catch {
        Write-Warning "Serveur encore utilisé (session Claude Code en cours ?) : fermez-la puis supprimez $installRoot."
    }
}

$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\MCPMap3D.bundle'
if (Test-Path $bundle) {
    Remove-Item $bundle -Recurse -Force
    Write-Host "Plug-in supprimé : $bundle" -ForegroundColor Green
}
