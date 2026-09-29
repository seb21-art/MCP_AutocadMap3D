<#
.SYNOPSIS
    Non-régression des outils de dessin technique 2D, avec contrôles chiffrés, puis annulation.
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
$script:trace = New-Object System.Collections.Generic.List[object]   # (opération, objets après) pour localiser un écart d'annulation

function Call([string]$method, [string]$paramsJson = '{}', [switch]$Write) {
    $script:id++
    if ($Write) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($Write) { Trace-Write $method }
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
function Expect-Error([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++; $script:writes++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    Trace-Write "$method (refusé)"
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

# Un outil de lecture en échec ne crée pas d'étape d'annulation.
function Expect-ReadError([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Count {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"get_drawing_info`",`"params`":{},`"timeoutMs`":30000}")
    ($reader.ReadLine() | ConvertFrom-Json).result.modelSpaceEntityCount
}

function Trace-Write([string]$operation) { $script:trace.Add([pscustomobject]@{ Operation = $operation; After = (Count) }) }

function Check([string]$label, $actual, $expected, [double]$tolerance = 0.001) {
    $ok = $null -ne $actual -and [math]::Abs([double]$actual - [double]$expected) -le $tolerance * [math]::Max(1, [math]::Abs([double]$expected))
    if ($ok) { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function CheckText([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : $actual" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

function Pass([string]$label) { Write-Host "  OK    $label" -ForegroundColor Green }
function Fail([string]$label) { Write-Host "  ÉCHEC $label" -ForegroundColor Red; $script:failures++ }
function Props([string]$handle) { (Call 'get_solid_properties' "{`"handles`":[`"$handle`"]}").objects[0] }
function Entity([string]$handle, [string]$type) {
    (Call 'list_entities' "{`"handles`":[`"$handle`"],`"includeGeometry`":true}").entities[0]
}

try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $startBlocks = (Call 'list_blocks').count
    "Départ : $($start.modelSpaceEntityCount) objets, $($start.layerCount) calques, $startBlocks bloc(s)"

    Write-Host "Géométrie"
    $circle = Call 'create_circle' '{"center":[0,0],"radius":10}' -Write
    $p = Props $circle.handle
    Check 'cercle : aire' $p.area ([math]::PI * 100)
    Check 'cercle : périmètre' $p.length ([math]::PI * 20)
    $arc = Call 'create_arc' '{"center":[30,0],"radius":5,"startAngle":0,"endAngle":90}' -Write
    Check 'arc (centre, angles) : longueur' $arc.length (5 * [math]::PI / 2)
    $arc3 = Call 'create_arc' '{"start":[50,0],"mid":[55,5],"end":[60,0]}' -Write
    Check 'arc (3 points) : rayon' $arc3.radius 5
    Check 'arc (3 points) : centre X' $arc3.center[0] 55
    Check 'arc (3 points) : longueur' $arc3.length (5 * [math]::PI)
    $rect = Call 'create_rectangle' '{"corner1":[0,20],"corner2":[40,45]}' -Write
    Check 'rectangle : aire' $rect.area 1000

    Write-Host "Ellipses et splines"
    # Périmètre d'une ellipse de demi-axes 10 et 5, par intégration numérique.
    $perimeter = 0; $steps = 20000
    for ($i = 0; $i -lt $steps; $i++) { $t = 2 * [math]::PI * ($i + 0.5) / $steps; $perimeter += [math]::Sqrt(100 * [math]::Pow([math]::Sin($t), 2) + 25 * [math]::Pow([math]::Cos($t), 2)) * 2 * [math]::PI / $steps }
    $el = Call 'create_ellipse' '{"center":[0,700],"majorRadius":10,"minorRadius":5}' -Write
    Check 'ellipse : aire' $el.area (50 * [math]::PI)
    Check 'ellipse : périmètre' $el.length $perimeter
    $p = Props $el.ellipse.handle
    Check 'ellipse : largeur en X' ($p.extents.max[0] - $p.extents.min[0]) 20
    Check 'ellipse : hauteur en Y' ($p.extents.max[1] - $p.extents.min[1]) 10
    $elR = Call 'create_ellipse' '{"center":[40,700],"majorRadius":10,"minorRadius":5,"rotation":90}' -Write
    $p = Props $elR.ellipse.handle
    Check 'ellipse tournée de 90° : largeur en X' ($p.extents.max[0] - $p.extents.min[0]) 10
    Check 'ellipse tournée de 90° : hauteur en Y' ($p.extents.max[1] - $p.extents.min[1]) 20
    $elA = Call 'create_ellipse' '{"center":[80,700],"majorRadius":10,"minorRadius":5,"startAngle":0,"endAngle":180}' -Write
    Check 'demi-ellipse : ouverte' $elA.closed $false
    Check 'demi-ellipse : longueur' $elA.length ($perimeter / 2)
    $p = Props $elA.ellipse.handle
    Check 'demi-ellipse : Ymin sur le grand axe' $p.extents.min[1] 700

    $spF = Call 'create_spline' '{"points":[[0,750],[10,760],[20,750],[30,760]]}' -Write
    Check 'spline de lissage : degré' $spF.degree 3
    Check 'spline de lissage : ouverte' $spF.closed $false
    $p = Props $spF.spline.handle
    Check 'spline de lissage : passe par le premier point (Xmin)' $p.extents.min[0] 0
    Check 'spline de lissage : passe par le dernier point (Xmax)' $p.extents.max[0] 30
    if ($spF.length -gt 30 * 1.05) { Pass "spline de lissage : longueur $($spF.length) > corde" } else { Fail "spline de lissage : longueur $($spF.length)" }
    $spFC = Call 'create_spline' '{"points":[[50,750],[60,750],[60,760],[50,760]],"closed":true}' -Write
    Check 'spline de lissage fermée : fermée' $spFC.closed $true
    $p = Props $spFC.spline.handle
    if ($p.extents.min[0] -le 50 + 1e-6 -and $p.extents.max[0] -ge 60 - 1e-6) { Pass "spline fermée : passe par ses points (X de $($p.extents.min[0]) à $($p.extents.max[0]))" } else { Fail "spline fermée : X de $($p.extents.min[0]) à $($p.extents.max[0])" }
    $spC = Call 'create_spline' '{"points":[[0,800],[10,810],[20,790],[30,800]],"controlPoints":true}' -Write
    Check 'spline par sommets de contrôle : degré' $spC.degree 3
    $p = Props $spC.spline.handle
    Check 'spline par sommets : part du premier sommet (Xmin)' $p.extents.min[0] 0
    Check 'spline par sommets : finit au dernier sommet (Xmax)' $p.extents.max[0] 30
    if ($p.extents.max[1] -lt 810 - 1) { Pass "spline par sommets : ne traverse pas le 2e sommet (Ymax $($p.extents.max[1]))" } else { Fail "spline par sommets : Ymax $($p.extents.max[1])" }
    $spCC = Call 'create_spline' '{"points":[[50,800],[60,800],[60,810],[50,810]],"controlPoints":true,"closed":true}' -Write
    Check 'spline fermée par sommets : fermée' $spCC.closed $true
    # L'étendue d'une spline périodique est celle de ses sommets de contrôle : on contrôle plutôt son aire, comparée à
    # celle de la B-spline cubique uniforme périodique échantillonnée (formule de la base uniforme, puis Gauss).
    $cp = @(@(50, 800), @(60, 800), @(60, 810), @(50, 810)); $pts = @()
    foreach ($seg in 0..3) {
        $q = @($cp[$seg % 4], $cp[($seg + 1) % 4], $cp[($seg + 2) % 4], $cp[($seg + 3) % 4])
        foreach ($k in 0..499) {
            $t = $k / 500
            $w = @([math]::Pow(1 - $t, 3), (3 * [math]::Pow($t, 3) - 6 * $t * $t + 4), (-3 * [math]::Pow($t, 3) + 3 * $t * $t + 3 * $t + 1), [math]::Pow($t, 3))
            $pts += , @((($w[0] * $q[0][0] + $w[1] * $q[1][0] + $w[2] * $q[2][0] + $w[3] * $q[3][0]) / 6), (($w[0] * $q[0][1] + $w[1] * $q[1][1] + $w[2] * $q[2][1] + $w[3] * $q[3][1]) / 6))
        }
    }
    $bsplineArea = 0
    for ($k = 0; $k -lt $pts.Count; $k++) { $a = $pts[$k]; $b = $pts[($k + 1) % $pts.Count]; $bsplineArea += $a[0] * $b[1] - $b[0] * $a[1] }
    Check 'spline fermée par sommets : aire de la B-spline périodique' (Props $spCC.spline.handle).area ([math]::Abs($bsplineArea) / 2)
    $spD1 = Call 'create_spline' '{"points":[[0,850],[10,860],[20,850]],"controlPoints":true,"degree":1}' -Write
    Check 'spline de degré 1 : longueur de la polyligne de contrôle' $spD1.length (2 * [math]::Sqrt(200))

    Write-Host "Texte multiligne"
    $mtext = Call 'create_mtext' '{"text":"Ligne 1\nLigne 2 {accolades} é","position":[0,100],"height":2,"width":50}' -Write
    $e = Entity $mtext.handle 'MTEXT'
    if ($e.text -like 'Ligne 1*Ligne 2 {accolades} é') { Write-Host "  OK    MTEXT relu : $($e.text -replace "`r?`n", ' / ')" -ForegroundColor Green } else { Write-Host "  ÉCHEC MTEXT relu : « $($e.text) »" -ForegroundColor Red; $script:failures++ }

    Write-Host "Hachures"
    $h1 = Call 'create_hatch' "{`"boundaries`":[`"$($rect.rectangle.handle)`"]}" -Write
    Check 'hachure pleine sur rectangle : aire' $h1.area 1000
    CheckText 'hachure : associative' $h1.associative 'True'
    $h2 = Call 'create_hatch' '{"points":[[0,60],[10,60],[0,70]],"pattern":"ANSI31","scale":2}' -Write
    Check 'hachure ANSI31 par points (triangle) : aire' $h2.area 50
    $h3 = Call 'create_hatch' "{`"boundaries`":[`"$($circle.handle)`"],`"pattern`":`"AR-CONC`",`"scale`":0.05}" -Write
    Check 'hachure béton sur cercle : aire' $h3.area ([math]::PI * 100)

    Write-Host "Cotations"
    Check 'cote alignée' (Call 'create_dimension' '{"type":"aligned","start":[0,0],"end":[30,40],"offset":5}' -Write).measurement 50
    Check 'cote horizontale' (Call 'create_dimension' '{"type":"horizontal","start":[0,0],"end":[30,40],"offset":5}' -Write).measurement 30
    Check 'cote verticale' (Call 'create_dimension' '{"type":"vertical","start":[0,0],"end":[30,40],"offset":5}' -Write).measurement 40
    Check 'cote pivotée à 45°' (Call 'create_dimension' '{"type":"rotated","start":[0,0],"end":[30,40],"angle":45,"offset":5}' -Write).measurement (70 / [math]::Sqrt(2))
    $radius = Call 'create_dimension' "{`"type`":`"radius`",`"handle`":`"$($circle.handle)`"}" -Write
    Check 'cote de rayon' $radius.measurement 10
    Check 'cote de diamètre' (Call 'create_dimension' "{`"type`":`"diameter`",`"handle`":`"$($circle.handle)`",`"angle`":135}" -Write).measurement 20
    Check 'cote angulaire (degrés)' (Call 'create_dimension' '{"type":"angular","center":[100,0],"start":[110,0],"end":[100,10],"radius":6}' -Write).measurement 90
    "  hauteur réelle du texte de cote : $($radius.textHeight) (style $($radius.style))"

    Write-Host "Blocs"
    $bline = Call 'create_line' '{"start":[150,-5],"end":[150,5]}' -Write
    $bcircle = Call 'create_circle' '{"center":[150,0],"radius":3}' -Write
    $block = Call 'create_block' "{`"name`":`"MCP_REPERE`",`"basePoint`":[150,0],`"handles`":[`"$($bline.handle)`",`"$($bcircle.handle)`"],`"attributes`":[{`"tag`":`"ref`",`"prompt`":`"Repère`",`"default`":`"A1`",`"position`":[154,0],`"height`":1.5}]}" -Write
    CheckText 'create_block : attributs' ($block.attributes -join ',') 'REF'
    $listed = (Call 'list_blocks' '{"names":["MCP_REPERE"]}').blocks[0]
    CheckText 'list_blocks : insertions (bloc converti)' $listed.insertions 1
    CheckText 'list_blocks : valeur par défaut' $listed.attributes[0].default 'A1'
    $insert = Call 'insert_block' '{"name":"MCP_REPERE","position":[200,0],"scale":2,"rotation":30,"attributes":{"REF":"B2"}}' -Write
    CheckText 'insert_block : attribut' $insert.attributes.REF 'B2'
    $e = Entity $insert.handle 'INSERT'
    CheckText 'list_entities : attribut relu' $e.attributes.REF 'B2'
    Check 'list_entities : rotation' $e.rotation 30

    Write-Host "Édition de textes"
    CheckText 'edit_text sur MTEXT' (Call 'edit_text' "{`"handle`":`"$($mtext.handle)`",`"text`":`"Note corrigée`"}" -Write).text 'Note corrigée'
    CheckText 'edit_text sur attribut' (Call 'edit_text' "{`"handle`":`"$($insert.handle)`",`"attributes`":{`"ref`":`"C3`"}}" -Write).attributes.REF 'C3'
    CheckText 'edit_text sur cote' (Call 'edit_text' "{`"handle`":`"$($radius.dimension.handle)`",`"text`":`"R <> m`"}" -Write).text 'R <> m'

    Write-Host "Copie, symétrie, échelle, décalage"
    $copies = Call 'copy_entities' "{`"handles`":[`"$($circle.handle)`"],`"displacement`":[0,-30],`"count`":3}" -Write
    Check 'copie en réseau : objets créés' $copies.created 3
    $last = Props $copies.handles[2][0]
    Check 'copie n°3 : centre Y' (($last.extents.min[1] + $last.extents.max[1]) / 2) -90

    $text = Call 'create_text' '{"text":"ABC","position":[300,0],"height":2}' -Write
    $mline = Call 'create_line' '{"start":[300,10],"end":[310,10]}' -Write
    $mirror = Call 'mirror_entities' "{`"handles`":[`"$($text.handle)`",`"$($mline.handle)`"],`"axisStart`":[320,0],`"axisEnd`":[320,50]}" -Write
    CheckText 'symétrie : objets créés' $mirror.mirrored 2
    $mirrored = $mirror.handles | ForEach-Object { Props $_ }
    $mirroredLine = $mirrored | Where-Object { $_.type -eq 'LINE' }
    Check 'symétrie : ligne, X min' $mirroredLine.extents.min[0] 330
    Check 'symétrie : ligne, X max' $mirroredLine.extents.max[0] 340
    $mirroredText = $mirrored | Where-Object { $_.type -eq 'TEXT' }
    if ($mirroredText.extents.min[0] -ge 320) { Write-Host "  OK    symétrie : texte de l'autre côté de l'axe (X min $($mirroredText.extents.min[0]))" -ForegroundColor Green } else { Write-Host "  ÉCHEC symétrie : texte mal placé" -ForegroundColor Red; $script:failures++ }

    $srect = Call 'create_rectangle' '{"corner1":[400,0],"corner2":[410,5]}' -Write
    Call 'scale_entities' "{`"handles`":[`"$($srect.rectangle.handle)`"],`"basePoint`":[400,0],`"factor`":2}" -Write | Out-Null
    Check 'échelle x2 : aire' (Props $srect.rectangle.handle).area 200

    $oline = Call 'create_line' '{"start":[0,-200],"end":[50,-200]}' -Write
    $off = Call 'offset_entities' "{`"handles`":[`"$($oline.handle)`"],`"distance`":5,`"side`":[25,-190]}" -Write
    Check 'décalage de ligne vers le point : Y' (Props $off.handles[0]).extents.min[1] -195
    $ocircle = Call 'create_circle' '{"center":[100,-200],"radius":10}' -Write
    $off2 = Call 'offset_entities' "{`"handles`":[`"$($ocircle.handle)`"],`"distance`":2,`"side`":[130,-200]}" -Write
    Check 'décalage de cercle vers l''extérieur : aire' (Props $off2.handles[0]).area ([math]::PI * 144)

    Write-Host "Points et polygones réguliers"
    $pts = Call 'create_point' '{"points":[[2000,0],[2005,0],[2010,0,3]]}' -Write
    Check 'points : créés' $pts.created 3
    Check 'point 3D : Z relu' (Entity $pts.handles[2]).position[2] 3
    Check 'points : aspect PDMODE' (Call 'create_point' '{"points":[[2015,0]],"pointStyle":35,"pointSize":1}' -Write).pointStyle 35
    $hex = Call 'create_polygon' '{"sides":6,"center":[2000,50],"radius":10}' -Write
    Check 'hexagone inscrit r=10 : aire' $hex.area (1.5 * [math]::Sqrt(3) * 100)
    Check 'hexagone inscrit : côté' $hex.side 10
    $sq = Call 'create_polygon' '{"sides":4,"center":[2030,50],"radius":5,"mode":"circumscribed"}' -Write
    Check 'carré circonscrit r=5 : aire' $sq.area 100
    $p = Props $sq.polygon.handle
    Check 'carré circonscrit : côtés parallèles aux axes (largeur X)' ($p.extents.max[0] - $p.extents.min[0]) 10
    $tri = Call 'create_polygon' '{"sides":3,"edgeStart":[2050,50],"edgeEnd":[2060,50]}' -Write
    Check 'triangle par un côté : aire' $tri.area (25 * [math]::Sqrt(3))
    Check 'triangle par un côté : sommet à gauche du côté (Y max)' (Props $tri.polygon.handle).extents.max[1] (50 + 5 * [math]::Sqrt(3))

    Write-Host "Nuages de révision et masques"
    # Feston : arc de renflement 0,5 sur une corde de 5, soit un angle au centre de 4·atan(0,5).
    $theta = 4 * [math]::Atan(0.5); $festoonRadius = 2.5 / [math]::Sin($theta / 2)
    $segment = $festoonRadius * $festoonRadius / 2 * ($theta - [math]::Sin($theta))
    $cloud = Call 'create_revision_cloud' '{"corner1":[2000,100],"corner2":[2040,120],"arcLength":5}' -Write
    Check 'nuage sur rectangle 40 × 20 : festons' $cloud.arcs 24
    Check 'nuage : aire (festons vers l''extérieur)' $cloud.area (800 + 24 * $segment)
    $cloudIn = Call 'create_revision_cloud' '{"corner1":[2050,100],"corner2":[2090,120],"arcLength":5,"inward":true}' -Write
    if ($cloudIn.area -lt 800) { Pass "nuage vers l'intérieur : aire $($cloudIn.area) < 800" } else { Fail "nuage vers l'intérieur : aire $($cloudIn.area)" }
    $cloudOn = Call 'create_revision_cloud' "{`"handle`":`"$($hex.polygon.handle)`"}" -Write
    if ($cloudOn.arcs -ge 3) { Pass "nuage autour de l'hexagone existant : $($cloudOn.arcs) festons" } else { Fail "nuage autour de l'hexagone : $($cloudOn.arcs) festons" }
    $wipe = Call 'create_wipeout' '{"points":[[2100,100],[2120,100],[2120,110],[2100,110]]}' -Write
    CheckText 'masque : type' $wipe.wipeout.type 'WIPEOUT'
    Check 'masque : aire' $wipe.area 200

    Write-Host "Contour autour d'un point"
    # Quatre lignes qui se croisent (zone de 20 × 10) et un cercle intérieur formant un îlot.
    foreach ($segmentJson in '{"start":[2195,200],"end":[2225,200]}', '{"start":[2195,210],"end":[2225,210]}', '{"start":[2200,195],"end":[2200,215]}', '{"start":[2220,195],"end":[2220,215]}') {
        Call 'create_line' $segmentJson -Write | Out-Null
    }
    Call 'create_circle' '{"center":[2210,205],"radius":2}' -Write | Out-Null
    $bnd = Call 'create_boundary' '{"point":[2203,203]}' -Write
    Check 'contour : aire extérieure' $bnd.outerArea 200
    Check 'contour : aire nette, îlot soustrait' $bnd.netArea (200 - 4 * [math]::PI)
    Check 'contour : îlots' $bnd.islands 1
    if ($bnd.viewAdjusted) { "  vue ajustée le temps du calcul du contour" }
    $bndR = Call 'create_boundary' '{"point":[2203,203],"type":"region","detectIslands":false}' -Write
    Check 'contour en région, sans îlot : aire' (Props $bndR.handles[0]).area 200

    Write-Host "Tableaux et styles de texte"
    $table = Call 'create_table' '{"position":[2300,300],"title":"Surfaces","headers":["Lot","Surface"],"rows":[["A",12.5],["B","8,25 m²"]],"alignment":["middle_left","middle_right"]}' -Write
    Check 'tableau : lignes (titre, en-têtes, 2 lignes)' $table.rows 4
    Check 'tableau : colonnes' $table.columns 2
    $e = Entity $table.table.handle
    CheckText 'tableau : titre relu' $e.cells[0][0] 'Surfaces'
    CheckText 'tableau : en-tête relu' $e.cells[1][1] 'Surface'
    CheckText 'tableau : nombre écrit tel quel' $e.cells[2][1] '12.5'
    $edited = Call 'edit_text' "{`"handle`":`"$($table.table.handle)`",`"cells`":[{`"row`":3,`"column`":1,`"text`":`"8,50 m²`"},{`"row`":4,`"column`":0,`"text`":`"C`"}]}" -Write
    CheckText 'edit_text sur tableau : cellule relue' $edited.cells[0].text '8,50 m²'
    Check 'edit_text sur tableau : ligne ajoutée' $edited.rows 5
    $style = Call 'set_text_style' '{"name":"MCP_TEST_ARIAL","font":"Arial","bold":true,"widthFactor":0.8}' -Write
    CheckText 'style de texte : créé' $style.created 'True'
    CheckText 'style de texte : police' $style.style.font 'Arial'
    CheckText 'style de texte : gras' $style.style.bold 'True'
    Check 'style de texte : facteur de largeur' $style.style.widthFactor 0.8
    Check 'list_text_styles : filtre' (Call 'list_text_styles' '{"names":["MCP_TEST_*"]}').count 1
    Call 'create_text' '{"text":"Style","position":[2300,250],"height":2,"style":"MCP_TEST_ARIAL"}' -Write | Out-Null
    $shx = Call 'set_text_style' '{"name":"MCP_TEST_ARIAL","font":"romans.shx"}' -Write
    CheckText 'style passé en SHX : police' $shx.style.font 'romans.shx'
    CheckText 'style passé en SHX : plus en TrueType' $shx.style.trueType 'False'
    if ($null -eq $shx.style.bold) { Pass 'style passé en SHX : gras absent du résultat (sans objet en SHX)' } else { Fail "style passé en SHX : gras encore indiqué ($($shx.style.bold))" }
    if ([string]::IsNullOrEmpty($shx.warning)) { Pass 'style passé en SHX : romans.shx trouvé par AutoCAD (aucun avertissement)' } else { Fail "style passé en SHX : $($shx.warning)" }

    Write-Host "Jonction, coupure, sommets de polyligne"
    # Carré de 10 fermé en haut par un demi-cercle : trois lignes et un arc, dans le désordre et dans tous les sens.
    $j1 = Call 'create_line' '{"start":[2400,400],"end":[2410,400]}' -Write
    $j2 = Call 'create_line' '{"start":[2410,410],"end":[2410,400]}' -Write
    $j3 = Call 'create_line' '{"start":[2400,400],"end":[2400,410]}' -Write
    $j4 = Call 'create_arc' '{"center":[2405,410],"radius":5,"startAngle":0,"endAngle":180}' -Write
    $join = Call 'join_entities' "{`"handles`":[`"$($j2.handle)`",`"$($j4.arc.handle)`",`"$($j1.handle)`",`"$($j3.handle)`"]}" -Write
    Check 'jonction : une polyligne' $join.joined 1
    CheckText 'jonction : fermée' $join.results[0].closed 'True'
    Check 'jonction : aire (carré et demi-disque)' $join.results[0].area (100 + 12.5 * [math]::PI)
    Check 'jonction : longueur' (Props $join.results[0].handle).length (30 + 5 * [math]::PI)
    $jp = Call 'create_polyline' '{"points":[[2450,400],[2460,400]]}' -Write
    $jl = Call 'create_line' '{"start":[2460,410],"end":[2460,400]}' -Write
    $alone = Call 'create_line' '{"start":[2480,400],"end":[2490,400]}' -Write
    $join2 = Call 'join_entities' "{`"handles`":[`"$($jl.handle)`",`"$($jp.handle)`",`"$($alone.handle)`"]}" -Write
    CheckText 'jonction : la polyligne garde son handle' $join2.results[0].handle $jp.handle
    CheckText 'jonction : objet isolé signalé' ($join2.notJoined -join ',') $alone.handle
    Check 'jonction : longueur de la polyligne ouverte' (Props $jp.handle).length 20

    $bl = Call 'create_line' '{"start":[2500,400],"end":[2600,400]}' -Write
    $br = Call 'break_entities' "{`"handle`":`"$($bl.handle)`",`"point`":[2530,401],`"point2`":[2550,399]}" -Write
    Check 'coupure entre deux points : morceaux gardés' $br.kept.Count 2
    Check 'coupure : longueur supprimée' $br.removedLength 20
    CheckText 'coupure : le premier morceau garde le handle' $br.kept[0] $bl.handle
    Check 'coupure : premier morceau' (Props $br.kept[0]).length 30
    Check 'coupure : second morceau' (Props $br.kept[1]).length 50
    Check 'coupure en un point : deux morceaux' (Call 'break_entities' "{`"handle`":`"$($br.kept[1])`",`"point`":[2575,400]}" -Write).kept.Count 2
    $bc = Call 'create_circle' '{"center":[2650,400],"radius":10}' -Write
    $bcr = Call 'break_entities' "{`"handle`":`"$($bc.handle)`",`"point`":[2660,400],`"point2`":[2650,410]}" -Write
    Check 'coupure de cercle de 0 à 90° : arc restant' (Props $bcr.kept[0]).length (15 * [math]::PI)
    Check 'coupure de cercle : longueur supprimée' $bcr.removedLength (5 * [math]::PI)

    $epl = Call 'create_polyline' '{"points":[[2700,400],[2710,400],[2710,410]]}' -Write
    Check 'polyligne : sommet ajouté à la fin' (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"add_vertex`",`"point`":[2700,410]}" -Write).vertices 4
    Check 'polyligne fermée : aire' (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"close`"}" -Write).area 100
    Check 'segment arrondi vers l''extérieur (angle 180°) : aire' (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"set_bulge`",`"index`":0,`"angle`":180}" -Write).area (100 + 12.5 * [math]::PI)
    $ep = Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"add_vertex`",`"index`":1,`"point`":[2705,395]}" -Write
    Check 'sommet ajouté sur l''arc : sommets' $ep.vertices 5
    Check 'sommet ajouté sur l''arc : aire inchangée' $ep.area (100 + 12.5 * [math]::PI)
    Check 'dernier sommet déplacé : aire' (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"move_vertex`",`"index`":-1,`"point`":[2700,420]}" -Write).area (150 + 12.5 * [math]::PI)
    # Parcours inversé : (2700,420), qui précédait (2700,400) en fermant la polyligne, vient maintenant juste après.
    $rev = (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"reverse`"}" -Write).points
    $i = 0; while ($i -lt $rev.Count -and -not ($rev[$i][0] -eq 2700 -and $rev[$i][1] -eq 400)) { $i++ }
    Check 'polyligne inversée : sommet suivant (2700,400)' $rev[($i + 1) % $rev.Count][1] 420
    Check 'largeur constante : aire inchangée' (Call 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"set_width`",`"width`":0.5}" -Write).area (150 + 12.5 * [math]::PI)

    Write-Host "Étirement et ordre d'affichage"
    $srect2 = Call 'create_rectangle' '{"corner1":[2800,400],"corner2":[2810,410]}' -Write
    $scirc = Call 'create_circle' '{"center":[2811,405],"radius":0.5}' -Write
    $st = Call 'stretch_entities' '{"corner1":[2808,398],"corner2":[2813,412],"displacement":[5,0]}' -Write
    Check 'étirement : objets étirés' $st.stretched 1
    Check 'étirement : objets déplacés' $st.moved 1
    Check 'étirement : aire du rectangle allongé' (Props $srect2.rectangle.handle).area 150
    $p = Props $scirc.handle
    Check 'étirement : cercle déplacé (centre X)' (($p.extents.min[0] + $p.extents.max[0]) / 2) 2816
    $dh = Call 'create_hatch' '{"points":[[2800,450],[2820,450],[2820,460],[2800,460]]}' -Write
    $dl = Call 'create_line' '{"start":[2795,455],"end":[2825,455]}' -Write
    Check 'ordre : hachure à l''arrière-plan (rang 0)' (Call 'set_draw_order' "{`"handles`":[`"$($dh.hatch.handle)`"],`"order`":`"back`"}" -Write).ranks[0].rank 0
    $do = Call 'set_draw_order' "{`"handles`":[`"$($dh.hatch.handle)`"],`"order`":`"above`",`"reference`":`"$($dl.handle)`"}" -Write
    Check 'ordre : hachure juste au-dessus de la ligne' $do.ranks[0].rank ($do.referenceRank + 1)

    Write-Host "Réseau le long d'un chemin"
    $path = Call 'create_polyline' '{"points":[[2900,400],[3000,400],[3000,500]]}' -Write
    $item = Call 'create_line' '{"start":[2900,400],"end":[2900,402]}' -Write
    $ap = Call 'array_path' "{`"handles`":[`"$($item.handle)`"],`"path`":`"$($path.handle)`",`"count`":4}" -Write
    Check 'réseau sur chemin : copies' $ap.created 3
    Check 'réseau sur chemin : espacement' $ap.layout.spacing (200 / 3)
    $p = Props $ap.handles[1]
    Check 'réseau sur chemin : copie après le coin, tournée de 90° (largeur X)' ($p.extents.max[0] - $p.extents.min[0]) 2
    Check 'réseau sur chemin : copie après le coin (Y)' $p.extents.min[1] (400 + 400 / 3 - 100)

    Write-Host "Mesures"
    $m1 = Call 'create_line' '{"start":[3100,400],"end":[3110,410]}' -Write
    $m2 = Call 'create_line' '{"start":[3100,410,5],"end":[3110,400,5]}' -Write
    $ix = Call 'get_intersections' "{`"handles`":[`"$($m1.handle)`",`"$($m2.handle)`"]}"
    Check 'intersection vue de dessus (Z différents) : X' $ix.pairs[0].points[0][0] 3105
    Check 'intersection : Y' $ix.pairs[0].points[0][1] 405
    Check 'intersection : Z du premier objet' $ix.pairs[0].points[0][2] 0
    Check 'intersection en 3D : aucun point' (Call 'get_intersections' "{`"handles`":[`"$($m1.handle)`",`"$($m2.handle)`"],`"plan`":false}").count 0
    $mc = Call 'create_circle' '{"center":[3130,405],"radius":5}' -Write
    $m3 = Call 'create_line' '{"start":[3120,405],"end":[3122,405]}' -Write
    Check 'cercle × ligne prolongée : 2 points' (Call 'get_intersections' "{`"handles`":[`"$($mc.handle)`",`"$($m3.handle)`"],`"extend`":`"both`"}").count 2
    Check 'cercle × ligne sans prolongement : aucun point' (Call 'get_intersections' "{`"handles`":[`"$($mc.handle)`",`"$($m3.handle)`"]}").count 0
    $ml = Call 'create_line' '{"start":[3200,400],"end":[3300,400]}' -Write
    $mm = Call 'measure_curve' "{`"handle`":`"$($ml.handle)`",`"distances`":[25],`"points`":[[3240,405],[3260,397]],`"divide`":4,`"interval`":30}"
    Check 'measure_curve : longueur' $mm.length 100
    Check 'measure_curve : point à 25' $mm.atDistances[0].point[0] 3225
    Check 'measure_curve : abscisse d''un point' $mm.stations[0].distance 40
    Check 'measure_curve : décalage à gauche' $mm.stations[0].offset 5
    Check 'measure_curve : décalage à droite' $mm.stations[1].offset -3
    Check 'measure_curve : division en 4' $mm.divided.Count 3
    Check 'measure_curve : mesure tous les 30' $mm.measured.Count 3
    Check 'measure_curve : aire d''un cercle' (Call 'measure_curve' "{`"handle`":`"$($mc.handle)`"}").area (25 * [math]::PI)

    Write-Host "Lignes infinies, dégradé, cotes de coordonnée et d'arc, masque de texte"
    CheckText 'droite infinie : type' (Call 'create_line' '{"start":[3400,400],"end":[3401,401],"kind":"xline"}' -Write).type 'XLINE'
    $ray = Call 'create_line' '{"start":[3400,400],"end":[3400,401],"kind":"ray"}' -Write
    CheckText 'demi-droite : type' $ray.type 'RAY'
    Check 'demi-droite : direction Y relue' (Entity $ray.handle).direction[1] 1
    $gh = Call 'create_hatch' '{"points":[[3400,450],[3420,450],[3420,460],[3400,460]],"gradient":"linear","gradientColors":["1","255,255,0"],"angle":45}' -Write
    CheckText 'hachure en dégradé : nom' $gh.gradient 'LINEAR'
    Check 'hachure en dégradé : aire' $gh.area 200
    Check 'cote de coordonnée X (ligne de repère verticale)' (Call 'create_dimension' '{"type":"ordinate","start":[3530,420],"end":[3530,440],"origin":[3500,400]}' -Write).measurement 30
    Check 'cote de coordonnée Y (ligne de repère horizontale)' (Call 'create_dimension' '{"type":"ordinate","start":[3530,420],"end":[3550,420],"origin":[3500,400]}' -Write).measurement 20
    $darc = Call 'create_arc' '{"center":[3600,400],"radius":5,"startAngle":0,"endAngle":90}' -Write
    Check 'cote de longueur d''arc' (Call 'create_dimension' "{`"type`":`"arc_length`",`"handle`":`"$($darc.arc.handle)`"}" -Write).measurement (2.5 * [math]::PI)
    $masked = Call 'create_mtext' '{"text":"Masque","position":[3400,500],"backgroundMask":true}' -Write
    CheckText 'texte multiligne avec masque : type' $masked.type 'MTEXT'

    Write-Host "Erreurs attendues"
    Expect-Error 'arc sur trois points alignés' 'create_arc' '{"start":[0,0],"mid":[1,1],"end":[2,2]}'
    Expect-Error 'ellipse : petit axe plus grand que le grand' 'create_ellipse' '{"center":[0,0],"majorRadius":5,"minorRadius":10}'
    Expect-Error 'arc d''ellipse sans angle d''arrivée' 'create_ellipse' '{"center":[0,0],"majorRadius":10,"minorRadius":5,"startAngle":0}'
    Expect-Error 'spline : un seul point' 'create_spline' '{"points":[[0,0]]}'
    Expect-Error 'spline : points confondus' 'create_spline' '{"points":[[0,0],[0,0],[10,0]]}'
    Expect-Error 'spline : degré sans sommets de contrôle' 'create_spline' '{"points":[[0,0],[5,5],[10,0]],"degree":2}'
    Expect-Error 'spline : trop peu de sommets pour le degré' 'create_spline' '{"points":[[0,0],[5,5],[10,0]],"controlPoints":true,"degree":3}'
    Expect-Error 'cote sans position de ligne de cote' 'create_dimension' '{"type":"aligned","start":[0,0],"end":[10,0]}'
    Expect-Error 'hachure avec contours et points' 'create_hatch' "{`"boundaries`":[`"$($circle.handle)`"],`"points`":[[0,0],[1,0],[1,1]]}"
    Expect-Error 'motif de hachure inconnu' 'create_hatch' '{"points":[[0,0],[1,0],[1,1]],"pattern":"PASUNMOTIF"}'
    Expect-Error 'attribut inconnu' 'insert_block' '{"name":"MCP_REPERE","position":[0,0],"attributes":{"NIMPORTE":"x"}}'
    Expect-Error 'bloc inconnu' 'insert_block' '{"name":"BLOC_INEXISTANT","position":[0,0]}'
    Expect-Error 'bloc déjà défini' 'create_block' "{`"name`":`"MCP_REPERE`",`"basePoint`":[0,0],`"handles`":[`"$($oline.handle)`"]}"
    Expect-Error 'edit_text sur une ligne' 'edit_text' "{`"handle`":`"$($oline.handle)`",`"text`":`"x`"}"
    Expect-Error 'polygone à 2 côtés' 'create_polygon' '{"sides":2,"center":[0,0],"radius":1}'
    Expect-Error 'polygone par centre et par côté à la fois' 'create_polygon' '{"sides":4,"center":[0,0],"radius":1,"edgeStart":[0,0],"edgeEnd":[1,0]}'
    Expect-Error 'style de point invalide' 'create_point' '{"points":[[0,0]],"pointStyle":5}'
    Expect-Error 'contour autour d''un point isolé' 'create_boundary' '{"point":[-50000,-50000]}'
    Expect-Error 'tableau sans contenu' 'create_table' '{"position":[0,0]}'
    Expect-Error 'police d''extension inconnue' 'set_text_style' '{"name":"MCP_TEST_PDF","font":"arial.pdf"}'
    Expect-Error 'dégradé et motif à la fois' 'create_hatch' '{"points":[[0,0],[1,0],[1,1]],"pattern":"ANSI31","gradient":"linear"}'
    Expect-Error 'cote de longueur d''arc sur une ligne' 'create_dimension' "{`"type`":`"arc_length`",`"handle`":`"$($oline.handle)`"}"
    Expect-Error 'jonction d''une polyligne fermée' 'join_entities' "{`"handles`":[`"$($epl.handle)`",`"$($oline.handle)`"]}"
    Expect-Error 'coupure d''un cercle en un seul point' 'break_entities' "{`"handle`":`"$($mc.handle)`",`"point`":[3135,405]}"
    Expect-Error 'polyligne : sommet inexistant' 'edit_polyline' "{`"handle`":`"$($epl.handle)`",`"action`":`"remove_vertex`",`"index`":99}"
    Expect-Error 'étirement sans point dans la fenêtre' 'stretch_entities' '{"corner1":[-60000,-60000],"corner2":[-59990,-59990],"displacement":[1,0]}'
    Expect-Error 'ordre au-dessus sans référence' 'set_draw_order' "{`"handles`":[`"$($dh.hatch.handle)`"],`"order`":`"above`"}"
    Expect-Error 'réseau plus long que le chemin' 'array_path' "{`"handles`":[`"$($item.handle)`"],`"path`":`"$($path.handle)`",`"count`":3,`"spacing`":1000}"
    Expect-Error 'edit_text sur tableau sans cellules' 'edit_text' "{`"handle`":`"$($table.table.handle)`",`"text`":`"x`"}"
    Expect-ReadError 'mesure d''un texte' 'measure_curve' "{`"handle`":`"$($text.handle)`"}"
    Expect-ReadError 'intersections d''un seul objet' 'get_intersections' "{`"handles`":[`"$($m1.handle)`"]}"

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    # Après le U n° i, on doit retrouver le nombre d'objets d'avant l'opération annulée.
    $reported = $false
    for ($i = 1; $i -le $script:writes; $i++) {
        Call '_undo' '{"count":1}' | Out-Null
        Start-Sleep -Milliseconds 700
        $undone = $script:trace.Count - $i
        $expected = if ($undone -gt 0) { $script:trace[$undone - 1].After } else { $start.modelSpaceEntityCount }
        $actual = Count
        if ($actual -ne $expected -and -not $reported) {
            Write-Host "  note  premier écart au U n°$i (annulation de « $($script:trace[$undone].Operation) ») : $actual objets, attendu $expected" -ForegroundColor Yellow
            $reported = $true
        }
    }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $finalBlocks = (Call 'list_blocks').count
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $final.layerCount -eq $start.layerCount -and $finalBlocks -eq $startBlocks
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
