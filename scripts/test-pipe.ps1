<#
.SYNOPSIS
    Envoie une requête au plug-in par le named pipe et affiche la réponse.
.EXAMPLE
    .\test-pipe.ps1
    .\test-pipe.ps1 -Method ping -TimeoutMs 3000
#>
param(
    [string]$Method = 'ping',
    [string]$ParamsJson = '{}',
    [int]$TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'

# Même règle de nommage que PipeProtocol.PipeName.
$sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$pipeName = "McpMap3D.v1.s$sessionId"

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try {
    try {
        $client.Connect(2000)
    } catch [System.TimeoutException] {
        Write-Host "Impossible de joindre \\.\pipe\$pipeName : AutoCAD Map 3D est-il lancé avec le plug-in chargé ?" -ForegroundColor Red
        exit 1
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $writer = New-Object System.IO.StreamWriter($client, $utf8)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($client, $utf8)

    $request = [ordered]@{
        id        = [guid]::NewGuid().ToString('N').Substring(0, 8)
        method    = $Method
        params    = ($ParamsJson | ConvertFrom-Json)
        timeoutMs = $TimeoutMs
    } | ConvertTo-Json -Compress -Depth 10

    Write-Host "-> $request" -ForegroundColor DarkGray
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $writer.WriteLine($request)
    $response = $reader.ReadLine()
    Write-Host "<- ($($clock.ElapsedMilliseconds) ms)" -ForegroundColor DarkGray
    $response | ConvertFrom-Json | ConvertTo-Json -Depth 10
} finally {
    $client.Dispose()
}
