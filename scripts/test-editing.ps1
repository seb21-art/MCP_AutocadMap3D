<#
.SYNOPSIS
    Non-régression de l'édition avancée : réseaux, ajuster/prolonger, raccords et chanfreins, décomposition,
    lignes de repère. Contrôles chiffrés, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (objets créés autour de l'origine, loin des données), puis annule chacune
    de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
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

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
function Expect-Error([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++; $script:writes++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected, [double]$tolerance = 0.001) {
    $ok = $null -ne $actual -and "$actual" -ne '' -and [math]::Abs([double]$actual - [double]$expected) -le $tolerance * [math]::Max(1, [math]::Abs([double]$expected))
    if ($ok) { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function CheckText([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : $actual" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

function Props([string]$handle) { (Call 'get_solid_properties' "{`"handles`":[`"$handle`"]}").objects[0] }
function Entity([string]$handle) { (Call 'list_entities' "{`"handles`":[`"$handle`"],`"includeGeometry`":true}").entities[0] }
function CenterX($p) { ($p.extents.min[0] + $p.extents.max[0]) / 2 }
function CenterY($p) { ($p.extents.min[1] + $p.extents.max[1]) / 2 }
function Line([double]$x1, [double]$y1, [double]$x2, [double]$y2) { (Call 'create_line' "{`"start`":[$x1,$y1],`"end`":[$x2,$y2]}" -Write).handle }

try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $startBlocks = (Call 'list_blocks').count
    "Départ : $($start.modelSpaceEntityCount) objets, $startBlocks bloc(s)"

    Write-Host "Réseaux"
    $c = (Call 'create_circle' '{"center":[0,500],"radius":1}' -Write).handle
    $rect = Call 'array_rectangular' "{`"handles`":[`"$c`"],`"rows`":3,`"columns`":4,`"rowSpacing`":10,`"columnSpacing`":5}" -Write
    Check 'réseau 3×4 : copies' $rect.created 11
    $last = Props $rect.handles[-1]
    Check 'réseau 3×4 : dernière copie, X' (CenterX $last) 15
    Check 'réseau 3×4 : dernière copie, Y' (CenterY $last) 520
    $turned = Call 'array_rectangular' "{`"handles`":[`"$c`"],`"columns`":3,`"columnSpacing`":10,`"angle`":90}" -Write
    Check 'réseau tourné de 90° : copie 2, Y' (CenterY (Props $turned.handles[-1])) 520
    $spoke = Line 110 500 115 500
    $polar = Call 'array_polar' "{`"handles`":[`"$spoke`"],`"center`":[100,500],`"count`":4}" -Write
    Check 'réseau polaire complet : pas angulaire' $polar.layout.angleBetweenItems 90
    Check 'réseau polaire : copie à 180°, X min' (Props $polar.handles[1]).extents.min[0] 85
    $dot = (Call 'create_circle' '{"center":[110,560],"radius":1}' -Write).handle
    $half = Call 'array_polar' "{`"handles`":[`"$dot`"],`"center`":[100,560],`"count`":3,`"fillAngle`":180,`"rotateItems`":false}" -Write
    Check 'réseau polaire sur 180° : pas' $half.layout.angleBetweenItems 90
    Check 'réseau polaire sur 180° : dernier élément, X' (CenterX (Props $half.handles[-1])) 90

    Write-Host "Ajuster"
    $t1 = Line 0 600 100 600
    $b1 = Line 30 590 30 610
    $b2 = Line 70 590 70 610
    $trim = Call 'trim_entities' "{`"targets`":[{`"handle`":`"$t1`",`"pick`":[50,600]}],`"boundaries`":[`"$b1`",`"$b2`"]}" -Write
    CheckText 'milieu retiré : morceaux restants' $trim.results[0].kept.Count 2
    CheckText 'milieu retiré : l''original est conservé' $trim.results[0].kept[0] $t1
    Check 'milieu retiré : longueur de l''original' (Props $t1).length 30
    $t2 = Line 0 650 100 650
    $b3 = Line 30 640 30 660
    Call 'trim_entities' "{`"targets`":[{`"handle`":`"$t2`",`"pick`":[10,650]}],`"boundaries`":[`"$b3`"]}" -Write | Out-Null
    Check 'extrémité retirée : longueur' (Props $t2).length 70
    $circle = (Call 'create_circle' '{"center":[200,600],"radius":10}' -Write).handle
    $b4 = Line 185 600 215 600
    $trimmedCircle = Call 'trim_entities' "{`"targets`":[{`"handle`":`"$circle`",`"pick`":[200,610]}],`"boundaries`":[`"$b4`"]}" -Write
    Check 'cercle ajusté : demi-arc restant, longueur' (Props $trimmedCircle.results[0].kept[0]).length ([math]::PI * 10)
    $t3 = Line 0 710 100 710
    $raised = (Call 'create_polyline' '{"points":[[50,700,5],[50,720,5]]}' -Write).handle
    Call 'trim_entities' "{`"targets`":[{`"handle`":`"$t3`",`"pick`":[80,710]}],`"boundaries`":[`"$raised`"]}" -Write | Out-Null
    Check 'ajustement sur une limite à une autre altitude : longueur' (Props $t3).length 50

    Write-Host "Prolonger"
    $e1 = Line 0 800 20 800
    $wallRight = Line 50 790 50 810
    $wallLeft = Line -30 790 -30 810
    $ext = Call 'extend_entities' "{`"targets`":[{`"handle`":`"$e1`",`"pick`":[19,800]}],`"boundaries`":[`"$wallRight`",`"$wallLeft`"]}" -Write
    Check 'ligne prolongée par la fin : allongement' $ext.results[0].extendedBy 30
    Call 'extend_entities' "{`"targets`":[{`"handle`":`"$e1`",`"pick`":[1,800]}],`"boundaries`":[`"$wallRight`",`"$wallLeft`"]}" -Write | Out-Null
    Check 'ligne prolongée par le début : longueur totale' (Props $e1).length 80
    $arc = (Call 'create_arc' '{"center":[200,800],"radius":10,"startAngle":0,"endAngle":90}' -Write).arc.handle
    $floor = Line 180 795 220 795
    Call 'extend_entities' "{`"targets`":[{`"handle`":`"$arc`",`"pick`":[200,810]}],`"boundaries`":[`"$floor`"]}" -Write | Out-Null
    Check 'arc prolongé : angle d''arrivée' (Entity $arc).endAngle 210
    $open = (Call 'create_polyline' '{"points":[[0,900],[20,900],[20,910]]}' -Write).handle
    $ceiling = Line 10 950 30 950
    Call 'extend_entities' "{`"targets`":[{`"handle`":`"$open`",`"pick`":[20,909]}],`"boundaries`":[`"$ceiling`"]}" -Write | Out-Null
    Check 'polyligne prolongée : Y du dernier sommet' (Entity $open).points[2][1] 950

    Write-Host "Raccords et chanfreins"
    $f1 = Line 0 1000 40 1000
    $f2 = Line 50 1010 50 1050
    $fillet = Call 'fillet' "{`"handles`":[`"$f1`",`"$f2`"],`"radius`":5}" -Write
    Check 'raccord R5 : longueur de l''arc' $fillet.addedLength (5 * [math]::PI / 2)
    Check 'raccord R5 : première ligne' (Props $f1).length 45
    Check 'raccord R5 : seconde ligne' (Props $f2).length 45
    $f3 = Line 0 1100 40 1100
    $f4 = Line 50 1110 50 1150
    $sharp = Call 'fillet' "{`"handles`":[`"$f3`",`"$f4`"],`"radius`":0}" -Write
    CheckText 'raccord R0 : pas d''arc ajouté' $sharp.added ''
    Check 'raccord R0 : lignes jointes au coin' ((Props $f3).length + (Props $f4).length) 100
    $roundRect = (Call 'create_rectangle' '{"corner1":[100,1000],"corner2":[120,1010]}' -Write).rectangle.handle
    $rounded = Call 'fillet' "{`"handles`":[`"$roundRect`"],`"radius`":2}" -Write
    Check 'rectangle arrondi R2 : aire' $rounded.area (200 - 4 * (4 - [math]::PI))
    CheckText 'rectangle arrondi : sommets' $rounded.vertices 8
    $c1 = Line 0 1300 40 1300
    $c2 = Line 50 1310 50 1350
    $chamfer = Call 'chamfer' "{`"handles`":[`"$c1`",`"$c2`"],`"distance1`":5}" -Write
    Check 'chanfrein 5 : longueur du pan coupé' $chamfer.addedLength ([math]::Sqrt(50))
    $chamferRect = (Call 'create_rectangle' '{"corner1":[100,1300],"corner2":[120,1310]}' -Write).rectangle.handle
    Check 'rectangle chanfreiné 2 : aire' (Call 'chamfer' "{`"handles`":[`"$chamferRect`"],`"distance1`":2}" -Write).area 192

    Write-Host "Décomposition"
    $bl = Line 400 1200 400 1210
    $bc = (Call 'create_circle' '{"center":[400,1205],"radius":3}' -Write).handle
    $blockName = "MCP_ECLATE_$(Get-Random)"
    Call 'create_block' "{`"name`":`"$blockName`",`"basePoint`":[400,1205],`"handles`":[`"$bl`",`"$bc`"],`"attributes`":[{`"tag`":`"REF`",`"default`":`"A1`",`"position`":[404,1205]}],`"mode`":`"delete`"}" -Write | Out-Null
    $ins = (Call 'insert_block' "{`"name`":`"$blockName`",`"position`":[450,1205],`"attributes`":{`"REF`":`"Z9`"}}" -Write).handle
    $exploded = Call 'explode_entities' "{`"handles`":[`"$ins`"]}" -Write
    CheckText 'bloc décomposé : objets créés' $exploded.results[0].created 3
    $explodedHandles = ($exploded.results[0].handles | ForEach-Object { '"' + $_ + '"' }) -join ','
    $texts = (Call 'list_entities' "{`"handles`":[$explodedHandles]}").entities | Where-Object { $_.type -eq 'TEXT' }
    CheckText 'bloc décomposé : attribut devenu texte avec sa valeur' $texts.text 'Z9'
    $poly = (Call 'create_polyline' '{"points":[[0,1200],[10,1200],[10,1210],[0,1210]]}' -Write).handle
    CheckText 'polyligne décomposée : segments' (Call 'explode_entities' "{`"handles`":[`"$poly`"]}" -Write).results[0].created 3

    Write-Host "Lignes de repère"
    $leader = Call 'create_leader' '{"points":[[300,1200],[310,1210],[320,1210]],"text":"Regard EU\nprofondeur 1,20 m","height":2}' -Write
    CheckText 'repère : pointe de flèche' ($leader.arrow[0..1] -join ',') '300,1200'
    CheckText 'repère : point côté texte' ($leader.landing[0..1] -join ',') '320,1210'
    if ((Entity $leader.leader.handle).text -like 'Regard EU*profondeur 1,20 m') { Write-Host "  OK    repère : texte relu" -ForegroundColor Green } else { Write-Host "  ÉCHEC repère : texte relu" -ForegroundColor Red; $script:failures++ }
    CheckText 'repère : texte modifié' (Call 'edit_text' "{`"handle`":`"$($leader.leader.handle)`",`"text`":`"Regard EP`"}" -Write).text 'Regard EP'
    $leftLeader = Call 'create_leader' '{"points":[[400,1200],[390,1210],[380,1210]],"text":"vers la gauche","height":2}' -Write
    CheckText 'repère vers la gauche : point côté texte' ($leftLeader.landing[0..1] -join ',') '380,1210'
    $leftExtents = (Entity $leftLeader.leader.handle).extents
    if ($leftExtents.min[0] -lt 370) { Write-Host "  OK    repère vers la gauche : texte à gauche du palier (X min $($leftExtents.min[0]))" -ForegroundColor Green } else { Write-Host "  ÉCHEC repère vers la gauche : texte du mauvais côté" -ForegroundColor Red; $script:failures++ }

    Write-Host "Erreurs attendues"
    Expect-Error 'raccord sur une seule ligne' 'fillet' "{`"handles`":[`"$f1`"],`"radius`":1}"
    $p1 = Line 0 1400 10 1400
    $p2 = Line 0 1405 10 1405
    Expect-Error 'raccord de lignes parallèles' 'fillet' "{`"handles`":[`"$p1`",`"$p2`"],`"radius`":1}"
    $r1 = Line 0 1500 10 1500
    $r2 = Line 12 1502 12 1512
    Expect-Error 'rayon trop grand' 'fillet' "{`"handles`":[`"$r1`",`"$r2`"],`"radius`":50}"
    Expect-Error 'ajustement sans intersection' 'trim_entities' "{`"targets`":[{`"handle`":`"$p1`",`"pick`":[5,1400]}],`"boundaries`":[`"$p2`"]}"
    Expect-Error 'prolongement sans limite atteinte' 'extend_entities' "{`"targets`":[{`"handle`":`"$p1`",`"pick`":[9,1400]}],`"boundaries`":[`"$p2`"]}"
    Expect-Error 'réseau polaire sans nombre' 'array_polar' "{`"handles`":[`"$p1`"],`"center`":[0,0]}"
    Expect-Error 'réseau 1 × 1' 'array_rectangular' "{`"handles`":[`"$p1`"]}"
    Expect-Error 'décomposition d''une ligne' 'explode_entities' "{`"handles`":[`"$p1`"]}"

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 700 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $finalBlocks = (Call 'list_blocks').count
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $finalBlocks -eq $startBlocks
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur (par exemple COVANEWS de Covadis au démarrage)" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($($final.modelSpaceEntityCount) objets, $finalBlocks bloc(s))" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $($final.modelSpaceEntityCount) objets / $finalBlocks bloc(s), attendu $($start.modelSpaceEntityCount) / $startBlocks" -ForegroundColor Red
        $script:failures++
    }
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
