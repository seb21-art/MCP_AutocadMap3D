<#
.SYNOPSIS
    Dialogue avec le serveur MCP sur stdio, comme le ferait Claude : initialize, tools/list puis appel d'un outil.
.EXAMPLE
    .\test-mcp.ps1
    .\test-mcp.ps1 -Tool ping -Server "$env:LOCALAPPDATA\McpMap3D\server\McpMap3D.Server.exe"
#>
param(
    [string]$Server = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\server\McpMap3D.Server.exe'),
    [string]$Tool = 'ping',
    [string]$ArgumentsJson = '{}',
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Server)) { throw "Serveur introuvable : $Server" }

$startInfo = New-Object System.Diagnostics.ProcessStartInfo($Server)
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$process = [System.Diagnostics.Process]::Start($startInfo)

function Send-Message([string]$json) {
    $process.StandardInput.WriteLine($json)
    $process.StandardInput.Flush()
}

function Receive-Response([int]$id) {
    while ($true) {
        $task = $process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($TimeoutSeconds * 1000)) { throw "Pas de réponse à la requête $id en $TimeoutSeconds s." }
        if ($null -eq $task.Result) { throw "Le serveur s'est arrêté avant de répondre à la requête $id." }
        $message = $task.Result | ConvertFrom-Json
        if ($message.id -eq $id) { return $message }
        Write-Host "(notification $($message.method))" -ForegroundColor DarkGray
    }
}

try {
    Send-Message '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-mcp","version":"1.0"}}}'
    $init = Receive-Response 1
    Write-Host "Serveur : $($init.result.serverInfo.name) $($init.result.serverInfo.version), protocole $($init.result.protocolVersion)" -ForegroundColor Cyan
    Send-Message '{"jsonrpc":"2.0","method":"notifications/initialized"}'

    Send-Message '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    $tools = (Receive-Response 2).result.tools
    Write-Host "Outils : $(($tools | ForEach-Object { $_.name }) -join ', ')" -ForegroundColor Cyan

    $call = '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"' + $Tool + '","arguments":' + $ArgumentsJson + '}}'
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Message $call
    $result = (Receive-Response 3).result
    $color = if ($result.isError) { 'Yellow' } else { 'Green' }
    Write-Host "$Tool -> isError=$([bool]$result.isError) en $($clock.ElapsedMilliseconds) ms" -ForegroundColor $color
    $result.content | ForEach-Object { $_.text }
} finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill() }
    $errors = $process.StandardError.ReadToEnd().Trim()
    if ($errors) { Write-Host "--- stderr du serveur ---`n$errors" -ForegroundColor DarkGray }
}
