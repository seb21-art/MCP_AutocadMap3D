<#
.SYNOPSIS
    Non-régression de l'outil de lot run_batch : lecture, création, arrêt à la première erreur, lot complet malgré
    une erreur, lots refusés, puis annulation.
.DESCRIPTION
    run_batch n'existe que dans le serveur MCP : le script le lance sur stdio, comme le ferait Claude, et garde une
    liaison directe avec le plug-in pour vérifier le dessin et annuler avec U.
    Ce script MODIFIE le dessin ouvert (objets créés autour de (0, 700), loin des données), puis annule chacune de
    ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
.EXAMPLE
    .\test-batch.ps1 -Server "$env:USERPROFILE\.mcpmap3d\server\McpMap3D.Server.exe"
#>
param(
    [string]$DrawingNameLike = '*test*',
    [string]$Server = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\server\McpMap3D.Server.exe')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Server)) { throw "Serveur introuvable : $Server" }
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Liaison directe avec le plug-in : contrôles et annulation.
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:writes = 0; $script:failures = 0

function Call([string]$method, [string]$paramsJson = '{}') {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Serveur MCP sur stdio.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo($Server)
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = $utf8
$process = [System.Diagnostics.Process]::Start($startInfo)
$null = $process.StandardError.ReadToEndAsync()
$mcpIn = New-Object System.IO.StreamWriter($process.StandardInput.BaseStream, $utf8); $mcpIn.AutoFlush = $true
$script:mcpId = 0

function Mcp([string]$method, $params) {
    $script:mcpId++
    $mcpIn.WriteLine((@{ jsonrpc = '2.0'; id = $script:mcpId; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress))
    while ($true) {
        $message = $process.StandardOutput.ReadLine() | ConvertFrom-Json
        if ($message.id -eq $script:mcpId) { return $message }
    }
}

# Renvoie { isError, summary } ; summary est le résumé JSON du lot, ou le texte de l'erreur s'il est refusé.
function Batch($calls, [bool]$stopOnError = $true) {
    $arguments = @{ calls = @($calls) }
    if (-not $stopOnError) { $arguments.stopOnError = $false }
    $r = (Mcp 'tools/call' @{ name = 'run_batch'; arguments = $arguments }).result
    $text = $r.content[0].text
    $summary = try { $text | ConvertFrom-Json } catch { $text }
    return [pscustomobject]@{ isError = [bool]$r.isError; summary = $summary }
}

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  OK    $label $detail" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label $detail" -ForegroundColor Red; $script:failures++ }
}

function Count-Entities { (Call 'get_drawing_info').modelSpaceEntityCount }

try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $layer = 'MCP_TEST_LOT'
    if ((Call 'list_layers').layers | Where-Object name -eq $layer) { throw "Le calque $layer existe déjà : supprimez-le avant le test." }
    "Départ : $($start.modelSpaceEntityCount) objets"

    $null = Mcp 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'test-batch'; version = '1.0' } }
    $mcpIn.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $tools = (Mcp 'tools/list' @{}).result.tools
    Check 'run_batch présent dans tools/list' ($tools.name -contains 'run_batch')

    Write-Host "Lecture"
    $b = Batch @(@{ tool = 'ping' }, @{ tool = 'get_drawing_info' }, @{ tool = 'list_layers' })
    Check 'lot de lecture : 3 réussis' (-not $b.isError -and $b.summary.succeeded -eq 3) "($($b.summary.succeeded))"
    Check 'résultat JSON repris tel quel' ($b.summary.results[1].result.modelSpaceEntityCount -eq $start.modelSpaceEntityCount)

    Write-Host "Création"
    $b = Batch @(
        @{ tool = 'create_layer'; arguments = @{ name = $layer; color = '1' } },
        @{ tool = 'create_line'; arguments = @{ start = @(0, 700); end = @(10, 705); layer = $layer } },
        @{ tool = 'create_circle'; arguments = @{ center = @(5, 710); radius = 2; layer = $layer } })
    $script:writes += 3
    Check 'lot de création : 3 réussis' (-not $b.isError -and $b.summary.succeeded -eq 3) "($($b.summary.succeeded))"
    $handles = @($b.summary.results[1].result.handle, $b.summary.results[2].result.handle)
    $onLayer = (Call 'list_entities' "{`"layers`":[`"$layer`"]}").entities
    Check 'objets créés sur le calque du lot' (@($onLayer).Count -eq 2 -and ($onLayer.handle -contains $handles[0]) -and ($onLayer.handle -contains $handles[1]))

    Write-Host "Arrêt à la première erreur"
    $before = Count-Entities
    $b = Batch @(
        @{ tool = 'create_line'; arguments = @{ start = @(0, 720); end = @(5, 720); layer = 'CALQUE_ABSENT_LOT' } },
        @{ tool = 'create_line'; arguments = @{ start = @(0, 725); end = @(5, 725) } })
    $script:writes += 1   # l'appel en échec est passé par le contexte commande : une étape d'annulation vide
    Check 'lot signalé en erreur' $b.isError
    Check '1 échec, 1 appel sauté' ($b.summary.failed -eq 1 -and $b.summary.skipped -eq 1) "($($b.summary.results[0].error))"
    Check 'aucun objet créé' ((Count-Entities) -eq $before)

    Write-Host "Lot complet malgré une erreur (stopOnError=false)"
    $b = Batch @(
        @{ tool = 'create_line'; arguments = @{ start = @(0, 730); end = @(5, 730); layer = 'CALQUE_ABSENT_LOT' } },
        @{ tool = 'create_line'; arguments = @{ start = @(0, 735); end = @(5, 735); layer = $layer } }) -stopOnError $false
    $script:writes += 2
    Check '1 échec, 1 réussi, rien de sauté' ($b.isError -and $b.summary.failed -eq 1 -and $b.summary.succeeded -eq 1 -and $b.summary.skipped -eq 0)
    Check 'second appel exécuté' ((Count-Entities) -eq $before + 1)

    Write-Host "Lots refusés avant tout appel"
    $before = Count-Entities
    $unknown = Batch @(@{ tool = 'create_line'; arguments = @{ start = @(0, 740); end = @(5, 740) } }, @{ tool = 'outil_inconnu' })
    Check 'outil inconnu refusé' ($unknown.isError -and "$($unknown.summary)" -like '*outil inconnu*') "($($unknown.summary))"
    $self = Batch @(@{ tool = 'run_batch'; arguments = @{ calls = @() } })
    Check 'run_batch dans un lot refusé' ($self.isError -and "$($self.summary)" -like '*lui-même*')
    $tooMany = Batch @(1..51 | ForEach-Object { @{ tool = 'ping' } })
    Check 'lot de 51 appels refusé' ($tooMany.isError -and "$($tooMany.summary)" -like '*50 au plus*')
    Check 'aucun objet créé par les lots refusés' ((Count-Entities) -eq $before)
    $bad = Batch @(@{ tool = 'create_line'; arguments = @{ start = @(0, 750) } })
    Check 'argument obligatoire manquant signalé' ($bad.isError -and $bad.summary.failed -eq 1) "($($bad.summary.results[0].error))"

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 700 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $layerLeft = (Call 'list_layers').layers | Where-Object name -eq $layer
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and -not $layerLeft
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur" -ForegroundColor Yellow
    } else {
        Check 'retour à l''état initial' $restored "($($final.modelSpaceEntityCount) objets, calque $layer $(if ($layerLeft) { 'présent' } else { 'absent' }))"
    }
} finally {
    $mcpIn.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill() }
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
