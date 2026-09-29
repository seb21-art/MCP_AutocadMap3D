<#
.SYNOPSIS
    Test de non-régression des outils de présentation (Layouts) et fenêtres (Viewports).
.DESCRIPTION
    Ce script vérifie la consultation, création, activation, renommage et suppression
    d'onglets de présentation, ainsi que la création et modification de fenêtres de présentation.
    N'utilisez que des copies de fichiers DWG (nom contenant 'test').
#>
param([string]$DrawingNameLike = '*test*')

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:writes = 0; $script:failures = 0

function Call([string]$method, [string]$paramsJson = '{}', [switch]$Write) {
    $script:id++
    if ($Write) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

function Expect-Error([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++; $script:writes++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue mais l'opération a réussi" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected, [double]$tolerance = 0.001) {
    $ok = $null -ne $actual -and [math]::Abs([double]$actual - [double]$expected) -le $tolerance * [math]::Max(1, [math]::Abs([double]$expected))
    if ($ok) { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function CheckText([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : $actual" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

try {
    $info = Call 'get_drawing_info'
    if ($info.file -notlike $DrawingNameLike) {
        throw "Dessin « $($info.file) » : n'exécutez ce script que sur une copie (nom contenant '$DrawingNameLike')."
    }
    Write-Host "Dessin : $($info.file) (espace $($info.currentSpace), calque $($info.currentLayer))`n" -ForegroundColor Cyan

    Write-Host "1. Consultation des présentations" -ForegroundColor White
    $list = Call 'list_layouts'
    Write-Host "  Présentations trouvées : $($list.count)" -ForegroundColor Gray
    Check 'présentations >= 2 (Model + au moins un layout)' ($list.count -ge 2) $true
    $model = $list.layouts | Where-Object { $_.isModel }
    CheckText 'onglet Model trouvé' $model.name 'Model'

    Write-Host "`n2. Création et activation de présentation" -ForegroundColor White
    $newLayout = Call 'create_layout' '{"name":"Test_MCP_A3","current":true}' -Write
    CheckText 'nom de la nouvelle présentation' $newLayout.name 'Test_MCP_A3'
    Check 'présentation rendue courante' $newLayout.isCurrent $true
    Check 'largeur feuille A3 420 mm' ([math]::Round([double]$newLayout.paperWidth)) 420
    Check 'hauteur feuille A3 297 mm' ([math]::Round([double]$newLayout.paperHeight)) 297

    $infoAfter = Call 'get_drawing_info'
    CheckText 'espace courant est bien paper' $infoAfter.currentSpace 'paper'

    Write-Host "`n3. Dessin d''objets dans l''espace papier" -ForegroundColor White
    $rect = Call 'create_rectangle' '{"corner1":[0,0],"corner2":[420,297]}' -Write
    CheckText 'rectangle créé dans l''espace papier' $rect.rectangle.space 'paper'

    $text = Call 'create_text' '{"text":"PLAN DE TEST MCP","position":[20,20],"height":5.0}' -Write
    CheckText 'texte créé dans l''espace papier' $text.space 'paper'

    Write-Host "`n4. Création d''une fenêtre de présentation (Viewport)" -ForegroundColor White
    $vp = Call 'create_viewport' '{"center":[210,150],"width":360,"height":240,"scale":"1:500","locked":true}' -Write
    Write-Host "  Handle fenêtre : $($vp.handle), échelle : $($vp.scale)" -ForegroundColor Gray
    Check 'largeur fenêtre' $vp.width 360
    Check 'hauteur fenêtre' $vp.height 240
    Check 'fenêtre verrouillée' $vp.locked $true
    Check 'fenêtre active' $vp.on $true

    Write-Host "`n5. Liste et modification des fenêtres" -ForegroundColor White
    $vps = Call 'list_viewports'
    $foundVp = $vps.viewports | Where-Object { $_.handle -eq $vp.handle }
    CheckText 'handle fenêtre trouvé' $foundVp.handle $vp.handle

    $vpMod = Call 'set_viewport' ("{`"handle`":`"$($vp.handle)`",`"locked`":false,`"scale`":`"1:1000`"}") -Write
    Check 'fenêtre déverrouillée' $vpMod.locked $false
    CheckText 'nouvelle échelle 1:1000' $vpMod.scale '1:1000'

    Write-Host "`n6. Renommage et suppression de présentation" -ForegroundColor White
    $ren = Call 'rename_layout' '{"oldName":"Test_MCP_A3","newName":"Test_MCP_A3_Renomme"}' -Write
    CheckText 'ancien nom' $ren.oldName 'Test_MCP_A3'
    CheckText 'nouveau nom' $ren.newName 'Test_MCP_A3_Renomme'

    # Revenir à l'espace Objet
    $back = Call 'set_current_layout' '{"name":"Model"}' -Write
    CheckText 'retour à Model' $back.layout 'Model'
    CheckText 'espace est model' $back.space 'model'

    # Supprimer la présentation créée
    $del = Call 'delete_layout' '{"name":"Test_MCP_A3_Renomme"}' -Write
    CheckText 'présentation supprimée' $del.deleted 'Test_MCP_A3_Renomme'

    # Vérification des erreurs attendues
    Write-Host "`n7. Contrôles de validation et erreurs attendues" -ForegroundColor White
    Expect-Error 'refus de supprimer l''espace Objet' 'delete_layout' '{"name":"Model"}'
    Expect-Error 'refus de renommer l''espace Objet' 'rename_layout' '{"oldName":"Model","newName":"Autre"}'

    $finalColor = if ($script:failures -eq 0) { 'Green' } else { 'Red' }
    Write-Host "`n--- Résultat : $($script:failures) échec(s) ---" -ForegroundColor $finalColor
}
finally {
    $client.Dispose()
}
