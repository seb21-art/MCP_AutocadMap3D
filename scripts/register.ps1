<#
.SYNOPSIS
    Enregistre le serveur MCP installé dans Claude Desktop et dans Claude Code (portée utilisateur).
.DESCRIPTION
    Claude Desktop : entrée « map3d » ajoutée à claude_desktop_config.json (copie .bak créée avant modification).
    Claude Desktop garde ce fichier en mémoire et le réécrit en entier : il doit être complètement quitté
    (icône de la zone de notification > Quitter) pendant l'enregistrement, puis relancé.
    Claude Code : « claude mcp add --scope user map3d », possible à tout moment.
#>
param(
    [switch]$DesktopOnly,
    [switch]$CodeOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$exe = Join-Path (Get-ServerDirectory) 'McpMap3D.Server.exe'
if (-not (Test-Path $exe)) { throw "Serveur non installé ($exe) : lancez d'abord install.ps1." }

if (-not $DesktopOnly) {
    $claude = Get-ClaudeCli
    if (-not $claude) {
        Write-Warning "CLI Claude Code introuvable : enregistrement dans Claude Code ignoré."
    } else {
        if (Test-ClaudeCodeServer $claude) { & $claude mcp remove $McpServerName --scope user }
        & $claude mcp add --scope user $McpServerName -- $exe
        if ($LASTEXITCODE -ne 0) { throw "Échec de l'enregistrement dans Claude Code." }
    }
}

if (-not $CodeOnly) {
    if (Test-ClaudeDesktopRunning) {
        Write-Warning ("Claude Desktop est ouvert : il écraserait la modification de sa configuration. " +
            "Quittez-le complètement (icône de la zone de notification > Quitter), puis relancez dans un terminal :`n" +
            "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -DesktopOnly")
        exit 1
    }

    & $exe register-desktop
    if ($LASTEXITCODE -ne 0) { throw "Échec de l'enregistrement dans Claude Desktop." }
    Write-Host "Relancez Claude Desktop pour charger le serveur « $McpServerName »." -ForegroundColor Green
}
