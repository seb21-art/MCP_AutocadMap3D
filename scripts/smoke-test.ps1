<#
.SYNOPSIS
    Test de non-régression du pipe : ping répétés, erreurs de protocole, clients en parallèle.
    AutoCAD Map 3D doit être lancé, plug-in chargé, sans commande ni boîte de dialogue en cours.
#>
param([int]$Clients = 8, [int]$PingsPerClient = 5)

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$failures = 0

function Open-Connection {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $client.Connect(2000)
    $writer = New-Object System.IO.StreamWriter($client, $utf8)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true
    [pscustomobject]@{ Client = $client; Writer = $writer; Reader = (New-Object System.IO.StreamReader($client, $utf8)) }
}

function Send-Line($connection, [string]$line) {
    $connection.Writer.WriteLine($line)
    $connection.Reader.ReadLine() | ConvertFrom-Json
}

function Assert($condition, [string]$label) {
    if ($condition) { Write-Host "  OK    $label" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label" -ForegroundColor Red; $script:failures++ }
}

Write-Host "Requêtes sur une même connexion"
$connection = Open-Connection
try {
    foreach ($i in 1..3) {
        $r = Send-Line $connection "{`"id`":`"p$i`",`"method`":`"ping`",`"timeoutMs`":5000}"
        Assert ($r.ok -and $r.id -eq "p$i" -and $r.result.pong) "ping $i"
    }
    $r = Send-Line $connection 'pas du json'
    Assert (-not $r.ok -and $r.error.code -eq 'bad_request') 'JSON invalide -> bad_request'
    $r = Send-Line $connection '{"id":"m"}'
    Assert (-not $r.ok -and $r.error.code -eq 'bad_request' -and $r.id -eq 'm') 'method absente -> bad_request'
    $r = Send-Line $connection '{"id":"u","method":"inconnue"}'
    Assert (-not $r.ok -and $r.error.code -eq 'unknown_method') 'méthode inconnue -> unknown_method'
    $r = Send-Line $connection '{"id":"s","method":"status"}'
    Assert ($r.ok -and $r.result.server -like 'en écoute*') 'status'
    $r = Send-Line $connection '{"id":"last","method":"ping"}'
    Assert $r.ok 'ping après erreurs'
} finally {
    $connection.Client.Dispose()
}

Write-Host "$Clients clients en parallèle, $PingsPerClient ping(s) chacun"
$jobs = 1..$Clients | ForEach-Object {
    Start-Job -ArgumentList $pipeName, $_, $PingsPerClient -ScriptBlock {
        param($pipeName, $n, $count)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect(5000)
        $writer = New-Object System.IO.StreamWriter($client, $utf8)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($client, $utf8)
        $ok = 0
        foreach ($i in 1..$count) {
            $writer.WriteLine("{`"id`":`"c$n-$i`",`"method`":`"ping`"}")
            $response = $reader.ReadLine() | ConvertFrom-Json
            if ($response.ok -and $response.id -eq "c$n-$i") { $ok++ }
        }
        $client.Dispose()
        $ok
    }
}
$results = $jobs | Wait-Job -Timeout 60 | Receive-Job
$jobs | Remove-Job -Force
Assert (($results | Measure-Object -Sum).Sum -eq $Clients * $PingsPerClient) "$(($results | Measure-Object -Sum).Sum)/$($Clients * $PingsPerClient) réponses correctes"

if ($failures -gt 0) { Write-Host "$failures échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
