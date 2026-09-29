<#
.SYNOPSIS
    Non-régression des outils de modélisation 3D, avec contrôles géométriques chiffrés, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (objets créés loin des données, autour de l'origine), puis annule chacune
    de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
#>
param([string]$DrawingNameLike = '*test*')
$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$client.Connect(3000)
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

# Un appel d'écriture en échec crée tout de même une étape d'annulation, pas un appel de lecture (-Read).
function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [switch]$Read) {
    $script:id++
    if (-not $Read) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected, [double]$tolerance = 0.01) {
    $ok = [math]::Abs([double]$actual - [double]$expected) -le $tolerance * [math]::Max(1, [math]::Abs([double]$expected))
    if ($ok) { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function Props([string]$handle) { (Call 'get_solid_properties' "{`"handles`":[`"$handle`"]}").objects[0] }
function Topo([string]$handle) { Call 'get_solid_topology' "{`"handle`":`"$handle`"}" }
function Pass([string]$label) { Write-Host "  OK    $label" -ForegroundColor Green }
function Fail([string]$label) { Write-Host "  ÉCHEC $label" -ForegroundColor Red; $script:failures++ }

$start = Call 'get_drawing_info'
if ($start.fileName -notlike $DrawingNameLike) {
    $client.Dispose()
    throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
}
"Départ : $($start.modelSpaceEntityCount) objets, $($start.layerCount) calques"

Write-Host "Primitives"
$box = Call 'create_box' '{"corner":[0,0,0],"length":10,"width":20,"height":30}' -Write
Check 'boîte : volume' $box.volume 6000
$p = Props $box.solid.handle
Check 'boîte : centre X' $p.centroid[0] 5; Check 'boîte : centre Y' $p.centroid[1] 10; Check 'boîte : centre Z' $p.centroid[2] 15

$rot = Call 'create_box' '{"corner":[100,0,0],"length":10,"width":20,"height":5,"rotation":90}' -Write
$p = Props $rot.solid.handle
Check 'boîte tournée 90° : Xmin' $p.extents.min[0] 80; Check 'boîte tournée 90° : Ymax' $p.extents.max[1] 10

$wedge = Call 'create_wedge' '{"corner":[0,50,0],"length":10,"width":10,"height":10}' -Write
Check 'biseau : volume' $wedge.volume 500

$cyl = Call 'create_cylinder' '{"center":[50,0,0],"radius":5,"height":10}' -Write
Check 'cylindre : volume' $cyl.volume ([math]::PI * 25 * 10)
Check 'cylindre : centre Z' (Props $cyl.solid.handle).centroid[2] 5

$cone = Call 'create_cylinder' '{"center":[70,0,0],"radius":5,"height":10,"topRadius":0}' -Write
Check 'cône : volume' $cone.volume ([math]::PI * 25 * 10 / 3)

$hcyl = Call 'create_cylinder' '{"center":[50,30,0],"radius":2,"height":10,"axis":[1,0,0]}' -Write
$p = Props $hcyl.solid.handle
Check 'cylindre horizontal : Xmin' $p.extents.min[0] 50; Check 'cylindre horizontal : Xmax' $p.extents.max[0] 60

$sphere = Call 'create_sphere' '{"center":[0,-50,10],"radius":3}' -Write
Check 'sphère : volume' $sphere.volume (4 / 3 * [math]::PI * 27)

$torus = Call 'create_torus' '{"center":[0,-100,0],"majorRadius":10,"minorRadius":2}' -Write
Check 'tore : volume' $torus.volume (2 * [math]::PI * [math]::PI * 10 * 4)

Write-Host "Polyligne 3D"
$p3d = Call 'create_3d_polyline' '{"points":[[0,200,0],[3,200,4],[3,210,4]]}' -Write
$listed = (Call 'list_entities' "{`"handles`":[`"$($p3d.handle)`"],`"includeGeometry`":true}").entities[0]
Check 'polyligne 3D : longueur' $listed.length 15
Check 'polyligne 3D : Z du 2e sommet' $listed.points[1][2] 4

Write-Host "Hélice"
# 4 tours de rayon 5 sur 20 : pas de 5, longueur 4 x racine((2 pi r)² + pas²).
$helix = Call 'create_helix' '{"center":[0,300,0],"baseRadius":5,"height":20,"turns":4}' -Write
Check 'hélice : tours' $helix.turns 4
Check 'hélice : pas' $helix.turnHeight 5
Check 'hélice : longueur' $helix.length (4 * [math]::Sqrt([math]::Pow(2 * [math]::PI * 5, 2) + 25))
Check 'hélice : départ X (angle 0)' $helix.startPoint[0] 5
Check 'hélice : arrivée Z' $helix.endPoint[2] 20
# Un quart de tour depuis l'angle 0 : le sens trigonométrique finit en +Y, le sens horaire en -Y.
$ccw = Call 'create_helix' '{"center":[20,300,0],"baseRadius":5,"height":2,"turns":0.25}' -Write
Check 'hélice sens trigonométrique : arrivée Y' $ccw.endPoint[1] 305
$cw = Call 'create_helix' '{"center":[20,300,0],"baseRadius":5,"height":2,"turns":0.25,"clockwise":true}' -Write
Check 'hélice sens horaire : arrivée Y' $cw.endPoint[1] 295
$angled = Call 'create_helix' '{"center":[20,300,0],"baseRadius":5,"height":2,"turns":0.25,"startAngle":90}' -Write
Check 'hélice départ à 90° : départ Y' $angled.startPoint[1] 305
$pitch = Call 'create_helix' '{"center":[0,330,0],"baseRadius":3,"height":20,"turnHeight":4}' -Write
Check 'hélice par le pas : tours' $pitch.turns 5
$cone = Call 'create_helix' '{"center":[0,360,0],"baseRadius":6,"topRadius":2,"height":10,"turns":2}' -Write
Check 'hélice conique : rayon du sommet' $cone.topRadius 2
Check 'hélice conique : arrivée Z' $cone.endPoint[2] 10
Check 'hélice conique : distance de l''arrivée à l''axe' ([math]::Sqrt([math]::Pow($cone.endPoint[0], 2) + [math]::Pow($cone.endPoint[1] - 360, 2))) 2
$lying = Call 'create_helix' '{"center":[40,300,0],"baseRadius":2,"height":10,"turns":3,"axis":[1,0,0]}' -Write
Check 'hélice couchée : arrivée X' $lying.endPoint[0] 50
Expect-Error 'hélice : turns et turnHeight ensemble refusés' 'create_helix' '{"center":[0,0,0],"baseRadius":5,"height":20,"turns":4,"turnHeight":5}'
Expect-Error 'hélice : plus de 500 tours refusés' 'create_helix' '{"center":[0,0,0],"baseRadius":5,"height":20,"turns":600}'
Expect-Error 'hélice : rayon nul refusé' 'create_helix' '{"center":[0,0,0],"baseRadius":0,"height":20}'
# Ressort : un cercle de rayon 0,5 balayé le long de la première hélice, volume proche de pi r² x longueur.
$wire = Call 'create_circle' '{"center":[5,300,0],"radius":0.5}' -Write
$spring = Call 'extrude' "{`"handles`":[`"$($wire.handle)`"],`"path`":`"$($helix.helix.handle)`",`"alignProfile`":true,`"eraseProfiles`":true}" -Write
if ($spring) { Check 'ressort : volume' $spring.solids[0].volume ([math]::PI * 0.25 * $helix.length) 0.05 }
# Filetage M10 x 1,5 sur 3 tours : peigne ISO (fond P/4 sur d1, flancs à 60°) dans le plan axial, coupé à r = 5,1,
# balayé sans alignement. Volume exact : aire x 2 pi x̄ x tours.
$thread = Call 'create_helix' '{"center":[80,300,0],"baseRadius":5,"height":4.5,"turns":3}' -Write
$comb = Call 'create_3d_polyline' '{"points":[[84.188101,300,-0.1875],[84.188101,300,0.1875],[85.1,300,0.713985],[85.1,300,-0.713985]],"closed":true}' -Write
$cutter = Call 'extrude' "{`"handles`":[`"$($comb.handle)`"],`"path`":`"$($thread.helix.handle)`",`"alignProfile`":false,`"basePoint`":[85,300,0],`"eraseProfiles`":true}" -Write
if ($cutter) { Check 'filetage : volume du peigne balayé' $cutter.solids[0].volume 73.3374 0.001 }
# Un peigne de 2,4 pas de large (coupé à r = 7) recouvre plusieurs spires : refus avec explication.
$wideComb = Call 'create_3d_polyline' '{"points":[[84.188101,300,-0.1875],[84.188101,300,0.1875],[87,300,1.810951],[87,300,-1.810951]],"closed":true}' -Write
Expect-Error 'filetage : peigne plus large que deux pas refusé' 'extrude' "{`"handles`":[`"$($wideComb.handle)`"],`"path`":`"$($thread.helix.handle)`",`"alignProfile`":false,`"basePoint`":[85,300,0]}"

Write-Host "Extrusion"
$square = Call 'create_polyline' '{"points":[[200,0],[210,0],[210,10],[200,10]],"closed":true}' -Write
$ext = Call 'extrude' "{`"handles`":[`"$($square.handle)`"],`"height`":5}" -Write
Check 'extrusion : volume' $ext.solids[0].volume 500
$taper = Call 'extrude' "{`"handles`":[`"$($square.handle)`"],`"height`":5,`"taperAngle`":10}" -Write
if ($taper -and $taper.solids[0].volume -lt 500 -and $taper.solids[0].volume -gt 300) { Write-Host "  OK    extrusion avec dépouille : volume $($taper.solids[0].volume) < 500" -ForegroundColor Green } else { Write-Host "  ÉCHEC dépouille" -ForegroundColor Red; $script:failures++ }
$down = Call 'extrude' "{`"handles`":[`"$($square.handle)`"],`"height`":-3}" -Write
Check 'extrusion vers le bas : Zmin' (Props $down.solids[0].solid.handle).extents.min[2] -3

$path = Call 'create_3d_polyline' '{"points":[[300,0,0],[300,0,20]]}' -Write
$profile = Call 'create_polyline' '{"points":[[295,-5],[305,-5],[305,5],[295,5]],"closed":true}' -Write
$swept = Call 'extrude' "{`"handles`":[`"$($profile.handle)`"],`"path`":`"$($path.handle)`",`"eraseProfiles`":true}" -Write
Check 'extrusion le long d''un chemin : volume' $swept.solids[0].volume 2000

Write-Host "Révolution"
$ring = Call 'create_polyline' '{"points":[[405,0],[407,0],[407,10],[405,10]],"closed":true}' -Write
$rev = Call 'revolve' "{`"handles`":[`"$($ring.handle)`"],`"axisStart`":[400,0,0],`"axisEnd`":[400,10,0]}" -Write
Check 'révolution 360° : volume' $rev.solids[0].volume ([math]::PI * (49 - 25) * 10)
$half = Call 'revolve' "{`"handles`":[`"$($ring.handle)`"],`"axisStart`":[400,0,0],`"axisEnd`":[400,10,0],`"angle`":180}" -Write
Check 'révolution 180° : volume' $half.solids[0].volume ([math]::PI * (49 - 25) * 10 / 2)

Write-Host "Opérations booléennes"
$a = Call 'create_box' '{"corner":[500,0,0],"length":10,"width":10,"height":10}' -Write
$b = Call 'create_box' '{"corner":[505,0,0],"length":10,"width":10,"height":10}' -Write
$union = Call 'boolean_solids' "{`"operation`":`"union`",`"target`":`"$($a.solid.handle)`",`"tools`":[`"$($b.solid.handle)`"]}" -Write
Check 'union : volume' $union.volume 1500
$c = Call 'create_box' '{"corner":[600,0,0],"length":10,"width":10,"height":10}' -Write
$d = Call 'create_cylinder' '{"center":[605,5,0],"radius":2,"height":10}' -Write
$sub = Call 'boolean_solids' "{`"operation`":`"subtract`",`"target`":`"$($c.solid.handle)`",`"tools`":[`"$($d.solid.handle)`"]}" -Write
Check 'soustraction (perçage) : volume' $sub.volume (1000 - [math]::PI * 4 * 10)
$e = Call 'create_box' '{"corner":[700,0,0],"length":10,"width":10,"height":10}' -Write
$f = Call 'create_box' '{"corner":[705,5,5],"length":10,"width":10,"height":10}' -Write
$inter = Call 'boolean_solids' "{`"operation`":`"intersect`",`"target`":`"$($e.solid.handle)`",`"tools`":[`"$($f.solid.handle)`"]}" -Write
Check 'intersection : volume' $inter.volume 125

Write-Host "Rotation 3D"
$r = Call 'create_box' '{"corner":[800,0,0],"length":10,"width":10,"height":30}' -Write
Call 'rotate_entities' "{`"handles`":[`"$($r.solid.handle)`"],`"basePoint`":[800,0,0],`"angle`":90,`"axis`":[1,0,0]}" -Write | Out-Null
$p = Props $r.solid.handle
Check 'rotation 90° autour de X : Zmax' $p.extents.max[2] 10
Check 'rotation 90° autour de X : Ymin' $p.extents.min[1] -30

Write-Host "Vue"
$view = Call 'set_view' '{"view":"se_iso","visualStyle":"realistic"}' -Write
"  vue : direction $($view.direction -join ', ') | style $($view.visualStyle)"
if ($view.visualStyle -eq 'Realistic') { Write-Host "  OK    style visuel appliqué" -ForegroundColor Green } else { Write-Host "  ÉCHEC style visuel" -ForegroundColor Red; $script:failures++ }

Write-Host "Cadrage sans objet"
$win = Call 'set_view' '{"view":"top","window":[1000,2000,1400,2100]}' -Write
if ($null -ne $win) {
    Check 'fenêtre : centre X' $win.center[0] 1200; Check 'fenêtre : centre Y' $win.center[1] 2050
    if ($win.width -ge 399.9 -and $win.height -ge 99.9) { Pass "fenêtre entière visible ($($win.width) x $($win.height))" }
    else { Fail "fenêtre tronquée : $($win.width) x $($win.height)" }
}
$centered = Call 'set_view' '{"center":[500,-300],"width":50}' -Write
if ($null -ne $centered) {
    Check 'centre : X' $centered.center[0] 500; Check 'centre : Y' $centered.center[1] -300
    Check 'centre : largeur demandée' $centered.width 50
    Check 'direction gardée sans view' $centered.direction[2] 1
}
$panned = Call 'set_view' '{"center":[0,0]}' -Write
if ($null -ne $panned) {
    Check 'recentrage : X' $panned.center[0] 0; Check 'recentrage : Y' $panned.center[1] 0
    Check 'recentrage : taille gardée' $panned.width $centered.width
}
$style = Call 'set_view' '{"visualStyle":"2dwireframe"}' -Write
if ($null -ne $style) { Check 'style seul : cadrage gardé' $style.width $centered.width }
$drawingCs = Call 'get_coordinate_system'
if ($drawingCs.assigned -and $drawingCs.system.code -eq 'Lambert93') {
    # Origine de la projection Lambert-93 : longitude 3°, latitude 46,5° -> X 700 000, Y 6 600 000.
    $ll = Call 'set_view' '{"center":[3,46.5],"width":100,"lonLat":true}' -Write
    if ($null -ne $ll) { Check 'lonLat : X' $ll.center[0] 700000 0.0000001; Check 'lonLat : Y' $ll.center[1] 6600000 0.0000001 }
    $llWin = Call 'set_view' '{"window":[2.9,46.4,3.1,46.6],"lonLat":true}' -Write
    if ($null -ne $llWin) {
        Check 'fenêtre lonLat : X' $llWin.center[0] 700000 0.0001
        if ($llWin.width -ge 15000) { Pass "fenêtre lonLat : largeur $($llWin.width) m" } else { Fail "fenêtre lonLat trop étroite : $($llWin.width)" }
    }
    Expect-Error 'lonLat hors du domaine' 'set_view' '{"center":[-120,40],"width":100,"lonLat":true}'
    Expect-Error 'latitude et longitude inversées' 'set_view' '{"center":[46.5,3],"width":100,"lonLat":true}'
} elseif (-not $drawingCs.assigned -and $drawingCs.mapAvailable -ne $false) {
    Expect-Error 'lonLat sans système de coordonnées' 'set_view' '{"center":[3,46.5],"width":100,"lonLat":true}'
} else {
    Write-Host "  AVERT lonLat non testé : le dessin est en $($drawingCs.system.code)" -ForegroundColor Yellow
}
Expect-Error 'window et center ensemble' 'set_view' '{"window":[0,0,10,10],"center":[5,5]}'
Expect-Error 'window sans hauteur' 'set_view' '{"window":[0,0,10,0]}'
Expect-Error 'window incomplète' 'set_view' '{"window":[0,0,10]}'
Expect-Error 'width sans center' 'set_view' '{"width":10}'
Expect-Error 'largeur négative' 'set_view' '{"center":[0,0],"width":-5}'
Expect-Error 'lonLat sans cadrage' 'set_view' '{"lonLat":true}'
Expect-Error 'zoomExtents avec window' 'set_view' '{"window":[0,0,10,10],"zoomExtents":true}'
Expect-Error 'aucun réglage' 'set_view' '{}'

Write-Host "Régions"
$l1 = Call 'create_line' '{"start":[1200,0],"end":[1210,0]}' -Write
$l2 = Call 'create_line' '{"start":[1210,0],"end":[1210,10]}' -Write
$l3 = Call 'create_line' '{"start":[1210,10],"end":[1200,10]}' -Write
$l4 = Call 'create_line' '{"start":[1200,10],"end":[1200,0]}' -Write
$reg = Call 'create_region' "{`"handles`":[`"$($l1.handle)`",`"$($l2.handle)`",`"$($l3.handle)`",`"$($l4.handle)`"],`"eraseCurves`":true}" -Write
Check 'région de 4 lignes : aire' $reg.regions[0].area 100
$regExt = Call 'extrude' "{`"handles`":[`"$($reg.regions[0].region.handle)`"],`"height`":2}" -Write
Check 'extrusion de la région : volume' $regExt.solids[0].volume 200
$ptReg = Call 'create_region' '{"points":[[1220,0,0],[1230,0,0],[1230,0,10],[1220,0,10]]}' -Write
Check 'région par points 3D (plan XZ) : aire' $ptReg.regions[0].area 100
Check 'région par points 3D : normale +Y' $ptReg.normal[1] 1
$ptExt = Call 'extrude' "{`"handles`":[`"$($ptReg.regions[0].region.handle)`"],`"height`":2}" -Write
Check 'extrusion de la région verticale : volume' $ptExt.solids[0].volume 200
$comb = Call 'create_region' '{"points":[[1244.08,-0.875],[1245.10,-1.464],[1245.10,-0.036],[1244.08,-0.625]],"plane":"xz","planeOffset":5}' -Write
Check 'peigne en plan xz : aire' $comb.regions[0].area 0.85578
$yz = Call 'create_region' '{"points":[[0,0],[4,0],[4,5],[0,5]],"plane":"yz","planeOffset":1260}' -Write
Check 'région en plan yz : aire' $yz.regions[0].area 20
Check 'région en plan yz : normale +X' $yz.normal[0] 1
$tilted = Call 'create_region' '{"points":[[1270,0,0],[1280,0,0],[1280,10,10],[1270,10,10]],"normal":[0,-1,1]}' -Write
Check 'région inclinée, normale imposée : aire' $tilted.regions[0].area 141.42136
$vpoly = Call 'create_polyline' '{"points":[[1290,0,0],[1300,0,0],[1300,0,10],[1290,0,10]],"closed":true}' -Write
$vpolyExt = Call 'extrude' "{`"handles`":[`"$($vpoly.handle)`"],`"height`":3}" -Write
Check 'polyligne 3D verticale extrudée : volume' $vpolyExt.solids[0].volume 300
$xzPoly = Call 'create_polyline' '{"points":[[1310,0],[1320,0],[1320,10],[1310,10]],"closed":true,"plane":"xz","planeOffset":7}' -Write
$xzPolyExt = Call 'extrude' "{`"handles`":[`"$($xzPoly.handle)`"],`"height`":4}" -Write
Check 'polyligne en plan xz extrudée : volume' $xzPolyExt.solids[0].volume 400

Write-Host "Coupe et section"
$s1 = Call 'create_box' '{"corner":[1300,0,0],"length":10,"width":10,"height":10}' -Write
$slice = Call 'slice_solid' "{`"handles`":[`"$($s1.solid.handle)`"],`"planePoint`":[1303,0,0],`"planeNormal`":[1,0,0]}" -Write
Check 'coupe, côté positif (gardé dans l''original)' $slice.solids[0].volume 700
Check 'coupe, côté négatif (nouveau solide)' $slice.solids[0].otherPart.volume 300
$s2 = Call 'create_box' '{"corner":[1320,0,0],"length":10,"width":10,"height":10}' -Write
$sliceNeg = Call 'slice_solid' "{`"handles`":[`"$($s2.solid.handle)`"],`"planePoints`":[[1320,0,4],[1330,0,4],[1320,10,4]],`"keep`":`"negative`"}" -Write
Check 'coupe par 3 points, côté négatif seul' $sliceNeg.solids[0].volume 400
$s3 = Call 'create_box' '{"corner":[1400,0,0],"length":10,"width":8,"height":10}' -Write
$section = Call 'get_section' "{`"handles`":[`"$($s3.solid.handle)`"],`"planePoint`":[0,0,5],`"planeNormal`":[0,0,1]}" -Write
Check 'section horizontale : aire' $section.sections[0].area 80
Check 'solide intact après section' (Props $s3.solid.handle).volume 800

Write-Host "Lissage et balayage"
$bottom = Call 'create_polyline' '{"points":[[1500,0,0],[1510,0,0],[1510,10,0],[1500,10,0]],"closed":true}' -Write
$top = Call 'create_polyline' '{"points":[[1502.5,2.5,10],[1507.5,2.5,10],[1507.5,7.5,10],[1502.5,7.5,10]],"closed":true}' -Write
$loft = Call 'loft' "{`"sections`":[`"$($bottom.handle)`",`"$($top.handle)`"],`"ruled`":true}" -Write
Check 'lissage réglé (tronc de pyramide) : volume' $loft.loft.volume (10 / 3 * (100 + 25 + 50))
$twPath = Call 'create_3d_polyline' '{"points":[[1600,0,0],[1600,0,20]]}' -Write
$twProfile = Call 'create_polyline' '{"points":[[1595,-5],[1605,-5],[1605,5],[1595,5]],"closed":true}' -Write
$twist = Call 'extrude' "{`"handles`":[`"$($twProfile.handle)`"],`"path`":`"$($twPath.handle)`",`"twistAngle`":90}" -Write
Check 'balayage avec torsion : volume conservé' $twist.solids[0].volume 2000
Expect-Error 'torsion sans chemin' 'extrude' "{`"handles`":[`"$($twProfile.handle)`"],`"height`":5,`"twistAngle`":90}"

Write-Host "Symétrie 3D, alignement, réseau 3D"
$m = Call 'create_box' '{"corner":[1700,0,0],"length":10,"width":10,"height":10}' -Write
$mir = Call 'mirror_3d' "{`"handles`":[`"$($m.solid.handle)`"],`"planePoint`":[0,0,0],`"planeNormal`":[0,0,1]}" -Write
$p = Props $mir.handles[0]
Check 'symétrie par le plan Z=0 : Zmin' $p.extents.min[2] -10
Check 'symétrie par le plan Z=0 : Zmax' $p.extents.max[2] 0
$al = Call 'create_box' '{"corner":[1800,0,0],"length":10,"width":20,"height":30}' -Write
Call 'align_3d' "{`"handles`":[`"$($al.solid.handle)`"],`"sourcePoints`":[[1800,0,0],[1810,0,0],[1800,20,0]],`"targetPoints`":[[1900,0,0],[1900,10,0],[1890,0,0]]}" -Write | Out-Null
$p = Props $al.solid.handle
Check 'alignement 3 points (rotation 90°) : Xmin' $p.extents.min[0] 1880
Check 'alignement 3 points (rotation 90°) : Ymax' $p.extents.max[1] 10
Check 'alignement 3 points : volume conservé' $p.volume 6000
$lv = Call 'create_box' '{"corner":[2000,0,0],"length":1,"width":1,"height":1}' -Write
$arr = Call 'array_rectangular' "{`"handles`":[`"$($lv.solid.handle)`"],`"levels`":3,`"levelSpacing`":5}" -Write
Check 'réseau sur 3 niveaux : copies' $arr.created 2
Check 'réseau sur 3 niveaux : Z du dernier' (Props $arr.handles[1]).extents.min[2] 10

Write-Host "Interférences"
$i1 = Call 'create_box' '{"corner":[2100,0,0],"length":10,"width":10,"height":10}' -Write
$i2 = Call 'create_box' '{"corner":[2105,0,0],"length":10,"width":10,"height":10}' -Write
$i3 = Call 'create_box' '{"corner":[2115,0,0],"length":10,"width":10,"height":10}' -Write
$clash = Call 'check_interference' "{`"setA`":[`"$($i1.solid.handle)`",`"$($i2.solid.handle)`",`"$($i3.solid.handle)`"]}" -Write
Check 'interférences : une seule paire (contact ignoré)' $clash.count 1
Check 'interférences : volume commun' $clash.clashes[0].volume 500

Write-Host "Bâtiments en solides"
$outer = Call 'create_polyline' '{"points":[[2200,0,100],[2210,0,100],[2210,10,100],[2200,10,100]],"closed":true}' -Write
$court = Call 'create_polyline' '{"points":[[2203,3,100],[2207,3,100],[2207,7,100],[2203,7,100]],"closed":true}' -Write
Call 'set_entity_properties' "{`"handles`":[`"$($outer.handle)`",`"$($court.handle)`"],`"thickness`":6}" -Write | Out-Null
# Avec Map 3D (ou Civil 3D), une donnée d'objet sur la polyligne doit passer au solide.
$withOd = [bool]$start.map.available
if ($withOd) {
    Call '_create_od_table' '{"name":"MCP_TEST_BATI","fields":[{"name":"ID","type":"Character"}]}' -Write | Out-Null
    Call 'set_od_value' "{`"handles`":[`"$($outer.handle)`"],`"table`":`"MCP_TEST_BATI`",`"field`":`"ID`",`"value`":`"BAT-1`"}" -Write | Out-Null
}
$bld = Call 'buildings_to_solids' "{`"handles`":[`"$($outer.handle)`",`"$($court.handle)`"]}" -Write
Check 'bâtiment : solides créés' $bld.converted 1
Check 'bâtiment : cour soustraite, volume' $bld.solids[0].volume ((100 - 16) * 6)
if ($bld.solids[0].handle -eq $outer.handle) { Write-Host "  OK    bâtiment : handle de la polyligne conservé" -ForegroundColor Green } else { Write-Host "  ÉCHEC handle du solide $($bld.solids[0].handle), attendu $($outer.handle)" -ForegroundColor Red; $script:failures++ }
Check 'bâtiment : Zmin à l''altitude du sol' (Props $outer.handle).extents.min[2] 100
if ($withOd) {
    $odResult = Call 'get_od_records' "{`"handles`":[`"$($outer.handle)`"]}"
    $od = if ($odResult) { ($odResult.objects[0].records | Where-Object { $_.table -eq 'MCP_TEST_BATI' }).values }
    if ($od.ID -eq 'BAT-1') { Write-Host "  OK    bâtiment : données d'objet conservées sur le solide" -ForegroundColor Green } else { Write-Host "  ÉCHEC bâtiment : données d'objet perdues ($($od | ConvertTo-Json -Compress))" -ForegroundColor Red; $script:failures++ }
}

Write-Host "Topologie des solides"
$t1 = Call 'create_box' '{"corner":[2300,0,0],"length":10,"width":10,"height":10}' -Write
$topo = Topo $t1.solid.handle
Check 'boîte : faces' $topo.faceCount 6
Check 'boîte : arêtes' $topo.edgeCount 12
Check 'boîte : faces planes' @($topo.faces | Where-Object { $_.type -eq 'plane' }).Count 6
$topFace = @($topo.faces | Where-Object { $_.normal -and $_.normal[2] -gt 0.999 })
Check 'boîte : une seule face vers le haut' $topFace.Count 1
Check 'face du dessus : aire' $topFace[0].area 100
Check 'face du dessus : Z du centre' $topFace[0].center[2] 10
Check 'face du dessus : arêtes' @($topFace[0].edges).Count 4
Check 'boîte : arêtes verticales' @($topo.edges | Where-Object { $_.orientation -eq 'vertical' }).Count 4
Check 'boîte : arêtes horizontales' @($topo.edges | Where-Object { $_.orientation -eq 'horizontal' }).Count 8
Check 'boîte : chaque arête borde deux faces' @($topo.edges | Where-Object { @($_.faces).Count -eq 2 }).Count 12
Expect-Error 'topologie d''un objet non solide' 'get_solid_topology' "{`"handle`":`"$($square.handle)`"}" -Read
Expect-Error 'topologie de deux solides à la fois' 'get_solid_topology' "{`"handle`":[`"$($t1.solid.handle)`",`"$($box.solid.handle)`"]}" -Read

Write-Host "Congés et chanfreins"
$f1 = Call 'create_box' '{"corner":[2320,0,0],"length":10,"width":10,"height":10}' -Write
$fil = Call 'fillet_edges' "{`"handle`":`"$($f1.solid.handle)`",`"radius`":1,`"edgeFilter`":`"vertical`"}" -Write
Check 'congés R1 : arêtes raccordées' $fil.edges 4
Check 'congés R1 sur les 4 arêtes verticales : volume' $fil.volume (1000 - 4 * (1 - [math]::PI / 4) * 10) 1e-4
$topoF = Topo $f1.solid.handle
Check 'après congés : faces cylindriques' @($topoF.faces | Where-Object { $_.type -eq 'cylinder' }).Count 4
Check 'après congés : arcs de rayon 1' @($topoF.edges | Where-Object { $_.kind -eq 'arc' -and [math]::Abs($_.radius - 1) -lt 1e-6 }).Count 8
Expect-Error 'congé : rayon trop grand' 'fillet_edges' "{`"handle`":`"$($t1.solid.handle)`",`"radius`":20,`"edgeFilter`":`"vertical`"}"
Expect-Error 'congé : indice d''arête hors limites' 'fillet_edges' "{`"handle`":`"$($t1.solid.handle)`",`"radius`":1,`"edges`":[99]}"
Check 'solide intact après les erreurs' (Props $t1.solid.handle).volume 1000 1e-4

$c1 = Call 'create_box' '{"corner":[2340,0,0],"length":10,"width":10,"height":10}' -Write
$cha = Call 'chamfer_edges' "{`"handle`":`"$($c1.solid.handle)`",`"distance`":1,`"baseFaceFilter`":`"top`"}" -Write
Check 'chanfrein de 1 autour du dessus : arêtes' $cha.edges 4
Check 'chanfrein de 1 autour du dessus : volume' $cha.volume (900 + (100 + 64 + 80) / 3) 1e-4
Expect-Error 'chanfrein : face de base ambiguë' 'chamfer_edges' "{`"handle`":`"$($t1.solid.handle)`",`"distance`":1,`"baseFaceFilter`":`"sides`"}"
$bottomFace = @($topo.faces | Where-Object { $_.normal -and $_.normal[2] -lt -0.999 })[0]
$foreignEdge = @($topo.edges | Where-Object { @($_.faces) -notcontains $bottomFace.index })[0].index
Expect-Error 'chanfrein : arête hors de la face de base' 'chamfer_edges' "{`"handle`":`"$($t1.solid.handle)`",`"distance`":1,`"baseFace`":$($bottomFace.index),`"edges`":[$foreignEdge]}"

Write-Host "Coque"
$sh = Call 'create_box' '{"corner":[2360,0,0],"length":10,"width":10,"height":10}' -Write
$shell = Call 'shell_solid' "{`"handle`":`"$($sh.solid.handle)`",`"thickness`":1,`"removeFaceFilter`":`"top`"}" -Write
Check 'coque de 1, dessus ouvert : volume' $shell.volume 424 1e-4
$p = Props $sh.solid.handle
Check 'coque vers l''intérieur : largeur inchangée' ($p.extents.max[0] - $p.extents.min[0]) 10 1e-4
Check 'coque vers l''intérieur : Zmin inchangé' $p.extents.min[2] 0
$sh2 = Call 'create_box' '{"corner":[2380,0,0],"length":10,"width":10,"height":10}' -Write
$shellOut = Call 'shell_solid' "{`"handle`":`"$($sh2.solid.handle)`",`"thickness`":1,`"removeFaceFilter`":`"top`",`"outward`":true}" -Write
Check 'coque vers l''extérieur : volume' $shellOut.volume 584 1e-4
$p = Props $sh2.solid.handle
Check 'coque vers l''extérieur : largeur' ($p.extents.max[0] - $p.extents.min[0]) 12 1e-4
Check 'coque vers l''extérieur : Zmin' $p.extents.min[2] -1
$sh3 = Call 'create_box' '{"corner":[2385,20,0],"length":10,"width":10,"height":10}' -Write
$closed = Call 'shell_solid' "{`"handle`":`"$($sh3.solid.handle)`",`"thickness`":1}" -Write
Check 'coque fermée vers l''intérieur : volume' $closed.volume 488 1e-4
Check 'coque fermée : aucune face ouverte' @($closed.openFaces).Count 0
$p = Props $sh3.solid.handle
Check 'coque fermée vers l''intérieur : largeur inchangée' ($p.extents.max[0] - $p.extents.min[0]) 10 1e-4
$sh4 = Call 'create_box' '{"corner":[2400,20,0],"length":10,"width":10,"height":10}' -Write
$closedOut = Call 'shell_solid' "{`"handle`":`"$($sh4.solid.handle)`",`"thickness`":1,`"outward`":true}" -Write
Check 'coque fermée vers l''extérieur : volume' $closedOut.volume 728 1e-4
$p = Props $sh4.solid.handle
Check 'coque fermée vers l''extérieur : largeur' ($p.extents.max[0] - $p.extents.min[0]) 12 1e-4
Expect-Error 'coque : toutes les faces ouvertes' 'shell_solid' "{`"handle`":`"$($t1.solid.handle)`",`"thickness`":1,`"removeFaceFilter`":`"all`"}"

Write-Host "Édition de faces"
$e1 = Call 'create_box' '{"corner":[2400,0,0],"length":10,"width":10,"height":10}' -Write
$ex = Call 'edit_solid_faces' "{`"handle`":`"$($e1.solid.handle)`",`"operation`":`"extrude`",`"faceFilter`":`"top`",`"distance`":5}" -Write
Check 'face du dessus tirée de 5 : volume' $ex.volume 1500 1e-4
Check 'face du dessus tirée de 5 : Zmax' (Props $e1.solid.handle).extents.max[2] 15 1e-4
$e2 = Call 'create_box' '{"corner":[2420,0,0],"length":10,"width":10,"height":10}' -Write
# Dépouille de atan(0,2) sur 5 : la face du dessus passe de 10 × 10 à 8 × 8 (tronc de pyramide).
$taperAngle = ([math]::Atan(0.2) * 180 / [math]::PI).ToString([Globalization.CultureInfo]::InvariantCulture)
$exTaper = Call 'edit_solid_faces' "{`"handle`":`"$($e2.solid.handle)`",`"operation`":`"extrude`",`"faceFilter`":`"top`",`"distance`":5,`"taperAngle`":$taperAngle}" -Write
Check 'face tirée de 5 avec dépouille positive (rétrécie) : volume' $exTaper.volume (1000 + 5 / 3 * (100 + 64 + 80)) 1e-4
$e3 = Call 'create_box' '{"corner":[2440,0,0],"length":10,"width":10,"height":10}' -Write
$off = Call 'edit_solid_faces' "{`"handle`":`"$($e3.solid.handle)`",`"operation`":`"offset`",`"faceFilter`":`"top`",`"distance`":2}" -Write
Check 'face du dessus décalée de 2 : volume' $off.volume 1200 1e-4
$e4 = Call 'create_box' '{"corner":[2460,0,0],"length":10,"width":10,"height":10}' -Write
$mv = Call 'edit_solid_faces' "{`"handle`":`"$($e4.solid.handle)`",`"operation`":`"move`",`"faceFilter`":`"top`",`"displacement`":[0,0,3]}" -Write
Check 'face du dessus déplacée de 3 : volume' $mv.volume 1300 1e-4
$e5 = Call 'create_box' '{"corner":[2480,0,0],"length":10,"width":10,"height":10}' -Write
$vEdge = @((Topo $e5.solid.handle).edges | Where-Object { $_.orientation -eq 'vertical' })[0].index
Call 'fillet_edges' "{`"handle`":`"$($e5.solid.handle)`",`"radius`":2,`"edges`":[$vEdge]}" -Write | Out-Null
$cylFace = @((Topo $e5.solid.handle).faces | Where-Object { $_.type -eq 'cylinder' })[0].index
$del = Call 'edit_solid_faces' "{`"handle`":`"$($e5.solid.handle)`",`"operation`":`"delete`",`"faces`":[$cylFace]}" -Write
Check 'congé supprimé : volume revenu à 1000' $del.volume 1000 1e-4
Expect-Error 'édition de faces : opération inconnue' 'edit_solid_faces' "{`"handle`":`"$($t1.solid.handle)`",`"operation`":`"twist`",`"faceFilter`":`"top`"}"
Expect-Error 'édition de faces : dépouille hors extrusion' 'edit_solid_faces' "{`"handle`":`"$($t1.solid.handle)`",`"operation`":`"offset`",`"faceFilter`":`"top`",`"distance`":1,`"taperAngle`":5}"

Write-Host "Épaississement et séparation"
$tp = Call 'create_polyline' '{"points":[[2500,0],[2510,0],[2510,10],[2500,10]],"closed":true}' -Write
$treg = Call 'create_region' "{`"handles`":[`"$($tp.handle)`"]}" -Write
$thick = Call 'thicken_surface' "{`"handles`":[`"$($treg.regions[0].region.handle)`"],`"thickness`":2}" -Write
Check 'région épaissie de 2 : volume' $thick.solids[0].volume 200 1e-4
$p = Props $thick.solids[0].solid.handle
Check 'région épaissie : Zmin' $p.extents.min[2] 0
Check 'région épaissie : Zmax, côté de la normale' $p.extents.max[2] 2
$both = Call 'thicken_surface' "{`"handles`":[`"$($treg.regions[0].region.handle)`"],`"thickness`":2,`"bothSides`":true}" -Write
$p = Props $both.solids[0].solid.handle
if ($p -and $p.extents.min[2] -lt -0.5 -and $p.extents.max[2] -gt 0.5) { Pass "épaissie des deux côtés : Z de $($p.extents.min[2]) à $($p.extents.max[2]), volume $($both.solids[0].volume)" }
else { Fail "épaississement des deux côtés : $($p.extents | ConvertTo-Json -Compress)" }
Expect-Error 'épaississement d''un objet ni surface ni région' 'thicken_surface' "{`"handles`":[`"$($tp.handle)`"],`"thickness`":1}"

$sp1 = Call 'create_box' '{"corner":[2520,0,0],"length":10,"width":10,"height":10}' -Write
$sp2 = Call 'create_box' '{"corner":[2540,0,0],"length":10,"width":10,"height":10}' -Write
$spU = Call 'boolean_solids' "{`"operation`":`"union`",`"target`":`"$($sp1.solid.handle)`",`"tools`":[`"$($sp2.solid.handle)`"]}" -Write
Check 'union de deux boîtes disjointes : volume' $spU.volume 2000 1e-4
$sep = Call 'separate_solid' "{`"handles`":[`"$($sp1.solid.handle)`"]}" -Write
Check 'séparation : nouvelles parties' @($sep.solids[0].newParts).Count 1
Check 'séparation : volume gardé par le solide' $sep.solids[0].volume 1000 1e-4
Check 'séparation : volume de la nouvelle partie' @($sep.solids[0].newParts)[0].volume 1000 1e-4
$single = Call 'separate_solid' "{`"handles`":[`"$($t1.solid.handle)`"]}" -Write
Check 'séparation d''un solide d''un seul tenant : aucune partie' @($single.solids[0].newParts).Count 0

Write-Host "Pyramides"
$pyr = Call 'create_pyramid' '{"center":[2600,0,0],"radius":5,"height":12}' -Write
Check 'pyramide à 4 côtés, rayon inscrit 5, hauteur 12 : volume' $pyr.volume 400 1e-4
$p = Props $pyr.solid.handle
Check 'pyramide : Zmin' $p.extents.min[2] 0; Check 'pyramide : Zmax' $p.extents.max[2] 12
"  pyramide sans rotation : largeur en X $($p.extents.max[0] - $p.extents.min[0]) (10 si côtés parallèles aux axes)"
$frustum = Call 'create_pyramid' '{"center":[2620,0,0],"radius":5,"height":6,"topRadius":2.5}' -Write
Check 'tronc de pyramide : volume' $frustum.volume (6 / 3 * (100 + 25 + 50)) 1e-4
$hex = Call 'create_pyramid' '{"center":[2640,0,0],"radius":4,"height":3,"sides":6,"radiusToVertex":true}' -Write
Check 'pyramide hexagonale de rayon 4 aux sommets : volume' $hex.volume (3 * [math]::Sqrt(3) / 2 * 16) 1e-4
$lying = Call 'create_pyramid' '{"center":[2660,0,5],"radius":2,"height":8,"axis":[1,0,0]}' -Write
$p = Props $lying.solid.handle
Check 'pyramide couchée : Xmin' $p.extents.min[0] 2660; Check 'pyramide couchée : Xmax' $p.extents.max[0] 2668
Expect-Error 'pyramide à 2 côtés' 'create_pyramid' '{"center":[0,0,0],"radius":1,"height":1,"sides":2}'

Write-Host "Dépouille, rotation, couleur et copie de faces"
$tp1 = Call 'create_box' '{"corner":[2700,0,0],"length":10,"width":10,"height":10}' -Write
# Dépouille de atan(0,2) vers l'intérieur, charnière au pied : le dessus passe de 10 × 10 à 6 × 6.
$dep = Call 'edit_solid_faces' "{`"handle`":`"$($tp1.solid.handle)`",`"operation`":`"taper`",`"faceFilter`":`"sides`",`"taperAngle`":$taperAngle}" -Write
Check 'dépouille positive des côtés : volume (tronc de pyramide)' $dep.volume (10 / 3 * (100 + 36 + 60)) 1e-4
Check 'dépouille : charnière au pied des faces' $dep.basePoint[2] 0
Check 'dépouille : base inchangée' ((Props $tp1.solid.handle).extents.max[0] - 2700) 10 1e-4
$rt1 = Call 'create_box' '{"corner":[2720,0,0],"length":10,"width":10,"height":10}' -Write
$rotF = Call 'edit_solid_faces' "{`"handle`":`"$($rt1.solid.handle)`",`"operation`":`"rotate`",`"faceFilter`":`"top`",`"angle`":$taperAngle,`"axis`":[1,0,0],`"basePoint`":[2720,0,10]}" -Write
Check 'face du dessus pivotée autour de son arête : volume' $rotF.volume 1100 1e-4
Check 'face pivotée : Zmax' (Props $rt1.solid.handle).extents.max[2] 12 1e-4
$col = Call 'create_box' '{"corner":[2740,0,0],"length":10,"width":10,"height":10}' -Write
$colored = Call 'edit_solid_faces' "{`"handle`":`"$($col.solid.handle)`",`"operation`":`"color`",`"faceFilter`":`"top`",`"color`":`"1`"}" -Write
Check 'face colorée : une face' @($colored.faces).Count 1
Check 'face colorée : volume inchangé' $colored.volume 1000 1e-4
$copied = Call 'edit_solid_faces' "{`"handle`":`"$($col.solid.handle)`",`"operation`":`"copy`",`"faceFilter`":`"top`"}" -Write
Check 'copie de la face du dessus : une copie' $copied.count 1
Check 'copie de la face du dessus : région de 100' $copied.copies[0].area 100 1e-4
Check 'copie de face : solide intact' (Props $col.solid.handle).volume 1000 1e-4
Expect-Error 'dépouille sans angle' 'edit_solid_faces' "{`"handle`":`"$($t1.solid.handle)`",`"operation`":`"taper`",`"faceFilter`":`"sides`"}"
Expect-Error 'rotation de face sans point de base' 'edit_solid_faces' "{`"handle`":`"$($t1.solid.handle)`",`"operation`":`"rotate`",`"faceFilter`":`"top`",`"angle`":5}"
Expect-Error 'couleur de face sans couleur' 'edit_solid_faces' "{`"handle`":`"$($t1.solid.handle)`",`"operation`":`"color`",`"faceFilter`":`"top`"}"

Write-Host "Empreinte et nettoyage"
$im = Call 'create_box' '{"corner":[2800,0,0],"length":10,"width":10,"height":10}' -Write
$imLine = Call 'create_line' '{"start":[2805,0,10],"end":[2805,10,10]}' -Write
$imp = Call 'imprint_solid' "{`"handle`":`"$($im.solid.handle)`",`"sources`":[`"$($imLine.handle)`"],`"eraseSources`":true}" -Write
Check 'empreinte d''une ligne en travers du dessus : faces' $imp.faceCount 7
Check 'empreinte : faces avant' $imp.facesBefore 6
$halves = @((Topo $im.solid.handle).faces | Where-Object { $_.normal -and $_.normal[2] -gt 0.999 })
Check 'empreinte : deux faces vers le haut' $halves.Count 2
$pull = Call 'edit_solid_faces' "{`"handle`":`"$($im.solid.handle)`",`"operation`":`"extrude`",`"faces`":[$($halves[0].index)],`"distance`":4}" -Write
Check 'moitié du dessus tirée de 4 : volume' $pull.volume 1200 1e-4
Expect-Error 'empreinte du solide sur lui-même' 'imprint_solid' "{`"handle`":`"$($t1.solid.handle)`",`"sources`":[`"$($t1.solid.handle)`"]}"
$cl1 = Call 'create_box' '{"corner":[2820,0,0],"length":10,"width":10,"height":10}' -Write
$cl2 = Call 'create_box' '{"corner":[2830,0,0],"length":10,"width":10,"height":10}' -Write
Call 'boolean_solids' "{`"operation`":`"union`",`"target`":`"$($cl1.solid.handle)`",`"tools`":[`"$($cl2.solid.handle)`"]}" -Write | Out-Null
$clean = Call 'clean_solid' "{`"handles`":[`"$($cl1.solid.handle)`"]}" -Write
Check 'nettoyage de l''union de deux boîtes accolées : faces' $clean.solids[0].faces 6
Check 'nettoyage : volume' $clean.solids[0].volume 2000 1e-4
"  faces avant nettoyage : $($clean.solids[0].facesBefore)"

Write-Host "Surfaces"
$sq = Call 'create_polyline' '{"points":[[2900,0],[2910,0],[2910,10],[2900,10]],"closed":true}' -Write
$wall = Call 'extrude' "{`"handles`":[`"$($sq.handle)`"],`"height`":10,`"surface`":true}" -Write
Check 'surface extrudée (4 parois de 10 × 10) : aire' $wall.surfaces[0].area 400 1e-4
$cap1 = Call 'create_surface' '{"type":"planar","corners":[[2900,0,0],[2910,10,0]]}' -Write
$cap2 = Call 'create_surface' '{"type":"planar","corners":[[2900,0,10],[2910,10,10]]}' -Write
Check 'surface plane par deux coins : aire' $cap1.surfaces[0].area 100 1e-4
$sculpt = Call 'sculpt_solid' "{`"handles`":[`"$($wall.surfaces[0].surface.handle)`",`"$($cap1.surfaces[0].surface.handle)`",`"$($cap2.surfaces[0].surface.handle)`"]}" -Write
Check 'solide sculpté dans le tube et ses deux couvercles : volume' $sculpt.volume 1000 1e-4
Expect-Error 'sculpture d''une surface qui n''enferme rien' 'sculpt_solid' "{`"handles`":[`"$($cap1.surfaces[0].surface.handle)`"]}"
$wallLine = Call 'create_polyline' '{"points":[[2920,0],[2930,0],[2930,5]]}' -Write
$openWall = Call 'extrude' "{`"handles`":[`"$($wallLine.handle)`"],`"height`":3,`"surface`":true}" -Write
Check 'surface extrudée d''une polyligne ouverte : aire' $openWall.surfaces[0].area 45 1e-4
$gen = Call 'create_line' '{"start":[2945,0,0],"end":[2945,0,10]}' -Write
$revS = Call 'revolve' "{`"handles`":[`"$($gen.handle)`"],`"axisStart`":[2940,0,0],`"axisEnd`":[2940,0,10],`"surface`":true}" -Write
Check 'surface de révolution d''un segment vertical (cylindre R5 H10) : aire' $revS.surfaces[0].area (2 * [math]::PI * 5 * 10) 1e-4
$la = Call 'create_line' '{"start":[2960,0,0],"end":[2970,0,0]}' -Write
$lb = Call 'create_line' '{"start":[2960,10,5],"end":[2970,10,5]}' -Write
$loftS = Call 'loft' "{`"sections`":[`"$($la.handle)`",`"$($lb.handle)`"],`"surface`":true,`"ruled`":true}" -Write
Check 'surface lissée entre deux segments (talus) : aire' $loftS.loft.area (10 * [math]::Sqrt(125)) 1e-4
$pc = Call 'create_circle' '{"center":[2985,5],"radius":5}' -Write
$pl = Call 'create_surface' "{`"type`":`"planar`",`"handles`":[`"$($pc.handle)`"]}" -Write
Check 'surface plane dans un cercle : aire' $pl.surfaces[0].area ([math]::PI * 25) 1e-4
$u1 = Call 'create_line' '{"start":[3000,0,0],"end":[3010,0,0]}' -Write
$u2 = Call 'create_line' '{"start":[3000,10,0],"end":[3010,10,0]}' -Write
$v1 = Call 'create_line' '{"start":[3000,0,0],"end":[3000,10,0]}' -Write
$v2 = Call 'create_line' '{"start":[3010,0,0],"end":[3010,10,0]}' -Write
$net = Call 'create_surface' "{`"type`":`"network`",`"uCurves`":[`"$($u1.handle)`",`"$($u2.handle)`"],`"vCurves`":[`"$($v1.handle)`",`"$($v2.handle)`"]}" -Write
Check 'surface de réseau sur un cadre de 10 × 10 : aire' $net.surface.area 100 1e-3
$nurbs = Call 'create_surface' '{"type":"nurbs","controlPoints":[[[3020,0,0],[3020,10,0]],[[3030,0,0],[3030,10,0]]]}' -Write
Check 'surface NURBS plane de 2 × 2 points : aire' $nurbs.surface.area 100 1e-4
$nurbs2 = Call 'create_surface' '{"type":"nurbs","controlPoints":[[[3040,0,0],[3040,5,0],[3040,10,0]],[[3050,0,2],[3050,5,2],[3050,10,2]]]}' -Write
Check 'surface NURBS inclinée de 2 × 3 points : aire' $nurbs2.surface.area (10 * [math]::Sqrt(104)) 1e-4
Check 'surface NURBS : degré en V' $nurbs2.degreeV 2
Expect-Error 'surface NURBS : rangées inégales' 'create_surface' '{"type":"nurbs","controlPoints":[[[0,0,0],[1,0,0]],[[0,1,0]]]}'
$offS = Call 'create_surface' "{`"type`":`"offset`",`"handles`":[`"$($nurbs.surface.surface.handle)`"],`"distance`":3}" -Write
Check 'surface décalée de 3 : aire' $offS.surfaces[0].area 100 1e-4
$p = Props $offS.surfaces[0].surface.handle
if ($p -and [math]::Abs([math]::Abs($p.extents.min[2]) - 3) -lt 0.01) { Pass "surface décalée à Z = $($p.extents.min[2])" } else { Fail "surface décalée : Z $($p.extents.min[2]), attendu ±3" }
$prT = Call 'create_box' '{"corner":[3060,0,0],"length":10,"width":10,"height":10}' -Write
$prL = Call 'create_line' '{"start":[3062,2,20],"end":[3068,8,20]}' -Write
$proj = Call 'project_curves' "{`"handles`":[`"$($prL.handle)`"],`"target`":`"$($prT.solid.handle)`"}" -Write
Check 'projection d''une ligne sur le dessus d''une boîte : courbes' $proj.count 1
$p = Props $proj.projections[0].projected.handle
Check 'ligne projetée : Z' $p.extents.min[2] 10 1e-4
Check 'ligne projetée : longueur' $p.length ([math]::Sqrt(72)) 1e-4
# Coordonnées de l'ordre du Lambert-93, hors de la métropole pour rester loin des données : une boîte de 5 m de
# haut ne doit pas garder la copie posée sur son dessous.
$prT93 = Call 'create_box' '{"corner":[2000000,8000000,0],"length":10,"width":10,"height":5}' -Write
$prL93 = Call 'create_line' '{"start":[2000002,8000002,20],"end":[2000008,8000008,20]}' -Write
$proj93 = Call 'project_curves' "{`"handles`":[`"$($prL93.handle)`"],`"target`":`"$($prT93.solid.handle)`"}" -Write
Check 'projection loin de l''origine sur une boîte de 5 m : courbes' $proj93.count 1
$p = Props $proj93.projections[0].projected.handle
Check 'ligne projetée loin de l''origine : Z' $p.extents.min[2] 5 1e-4
$prMiss = Call 'create_line' '{"start":[3100,0,20],"end":[3105,0,20]}' -Write
Expect-Error 'projection d''une ligne qui manque la cible' 'project_curves' "{`"handles`":[`"$($prMiss.handle)`"],`"target`":`"$($prT.solid.handle)`"}"

Write-Host "Maillages"
# Tétraèdre fermé, faces orientées vers l'extérieur.
$tetJson = '"vertices":[[3200,0,0],[3210,0,0],[3200,10,0],[3200,0,10]],"faces":[[0,2,1],[0,1,3],[1,2,3],[0,3,2]]'
$tet = Call 'create_mesh' "{$tetJson}" -Write
Check 'maillage tétraèdre : faces' $tet.faces 4
if ($tet.watertight) { Pass 'maillage tétraèdre fermé' } else { Fail 'maillage tétraèdre : non fermé' }
Check 'maillage tétraèdre : volume' $tet.volume (1000 / 6) 1e-4
$tetSolid = Call 'convert_mesh' "{`"handles`":[`"$($tet.mesh.handle)`"],`"to`":`"solid`"}" -Write
Check 'tétraèdre converti en solide : volume' $tetSolid.objects[0].volume (1000 / 6) 1e-4
$sm = Call 'create_mesh' "{$($tetJson.Replace('[3200,', '[3220,').Replace('[3210,', '[3230,'))}" -Write
$smooth = Call 'smooth_mesh' "{`"handles`":[`"$($sm.mesh.handle)`"],`"level`":2}" -Write
Check 'lissage : niveau' $smooth.meshes[0].level 2
if ($smooth.meshes[0].smoothedFaces -gt 4) { Pass "lissage : $($smooth.meshes[0].smoothedFaces) faces subdivisées" } else { Fail "lissage : faces subdivisées $($smooth.meshes[0].smoothedFaces)" }
$flat = Call 'smooth_mesh' "{`"handles`":[`"$($sm.mesh.handle)`"],`"level`":0}" -Write
Check 'lissage ramené à 0' $flat.meshes[0].level 0
$bm = Call 'create_box' '{"corner":[3240,0,0],"length":10,"width":10,"height":10}' -Write
$toMesh = Call 'convert_mesh' "{`"handles`":[`"$($bm.solid.handle)`"],`"to`":`"mesh`"}" -Write
$meshHandle = $toMesh.objects[0].converted.handle
$mp = Props $meshHandle
if ($mp.watertight) { Pass "boîte maillée : $($mp.faces) faces, fermée" } else { Fail "boîte maillée non fermée" }
Check 'boîte maillée : volume' $mp.volume 1000 1e-4
$back = Call 'convert_mesh' "{`"handles`":[`"$meshHandle`"],`"to`":`"solid`"}" -Write
Check 'maillage reconverti en solide : volume' $back.objects[0].volume 1000 1e-4
$sph = Call 'create_sphere' '{"center":[3260,5,5],"radius":5}' -Write
$sphMesh = Call 'convert_mesh' "{`"handles`":[`"$($sph.solid.handle)`"],`"to`":`"mesh`",`"meshType`":`"triangles`",`"keepOriginals`":true}" -Write
$sp = Props $sphMesh.objects[0].converted.handle
$ratio = $sp.volume / (4 / 3 * [math]::PI * 125)
if ($ratio -gt 0.8 -and $ratio -lt 1.001) { Pass "sphère maillée en triangles : $($sp.faces) faces, $([math]::Round(100 * $ratio, 1)) % du volume" } else { Fail "sphère maillée : volume $($sp.volume)" }
Expect-Error 'maillage : indice de sommet hors limites' 'create_mesh' '{"vertices":[[0,0,0],[1,0,0],[0,1,0]],"faces":[[0,1,3]]}'
Expect-Error 'conversion en solide d''un objet qui n''est pas un maillage' 'convert_mesh' "{`"handles`":[`"$($t1.solid.handle)`"],`"to`":`"solid`"}"
Expect-Error 'option de maillage pour une conversion en solide' 'convert_mesh' "{`"handles`":[`"$meshHandle`"],`"to`":`"solid`",`"meshType`":`"quads`"}"

Write-Host "Maillages de terrain"
# Grille de 5 × 5 points au pas de 10, pente de 10 % en X.
$grid = foreach ($i in 0..4) { foreach ($j in 0..4) { "[$(3300 + 10 * $i),$(10 * $j),$(100 + $i)]" } }
$terrain = Call 'create_terrain_mesh' "{`"points`":[$($grid -join ',')]}" -Write
Check 'terrain de 25 points en grille : triangles' $terrain.triangles 32
Check 'terrain : aire en plan' $terrain.planArea 1600 1e-6
Check 'terrain : aire réelle (pente de 10 %)' $terrain.area (1600 * [math]::Sqrt(1.01)) 1e-6
Check 'terrain : altitude mini' $terrain.minZ 100; Check 'terrain : altitude maxi' $terrain.maxZ 104
# Grille de 5 × 3 points privée de deux points au milieu du bord : les triangles qui comblent l'échancrure
# ont un côté de 20 au moins et sont retirés par maxEdgeLength = 15.
$notchPoints = foreach ($i in 0..4) { foreach ($j in 0..2) { if (-not ($i -eq 2 -and $j -gt 0)) { "[$(3400 + 10 * $i),$(10 * $j),50]" } } }
$notch = Call 'create_terrain_mesh' "{`"points`":[$($notchPoints -join ',')],`"maxEdgeLength`":15,`"meshType`":`"polyface`"}" -Write
Check 'terrain échancré : triangles gardés' $notch.triangles 10
Check 'terrain échancré : grands triangles retirés' $notch.removedTriangles 3
Check 'terrain échancré : aire en plan' $notch.planArea 500 1e-6
if ($notch.mesh.type -eq 'POLYLINE') { Pass 'terrain échancré : maillage polyface' } else { Fail "terrain échancré : type $($notch.mesh.type)" }
Call 'create_layer' '{"name":"MCP_TEST_COURBES"}' -Write | Out-Null
foreach ($k in 0..2) { Call 'create_3d_polyline' "{`"points`":[[3500,$(10 * $k),$(10 + $k)],[3520,$(10 * $k),$(10 + $k)]],`"layer`":`"MCP_TEST_COURBES`"}" -Write | Out-Null }
$fromLayer = Call 'create_terrain_mesh' '{"layers":["MCP_TEST_COURBES"]}' -Write
Check 'terrain lu sur un calque (3 courbes de niveau) : points' $fromLayer.points 6
Check 'terrain lu sur un calque : aire en plan' $fromLayer.planArea 400 1e-6
Check 'terrain lu sur un calque : altitude maxi' $fromLayer.maxZ 12
Expect-Error 'terrain de points alignés' 'create_terrain_mesh' '{"points":[[0,0,0],[1,1,1],[2,2,2]]}'
Expect-Error 'terrain sans point' 'create_terrain_mesh' '{}'

Write-Host "Export STL et SAT"
$stlPath = Join-Path $env:TEMP 'mcpmap3d-test.stl'
$satPath = Join-Path $env:TEMP 'mcpmap3d-test.sat'
$xb = Call 'create_box' '{"corner":[3600,0,0],"length":10,"width":10,"height":10}' -Write
$stl = Call 'export_stl' "{`"handles`":[`"$($xb.solid.handle)`"],`"filePath`":$(ConvertTo-Json $stlPath)}"
Check 'STL binaire d''une boîte (12 triangles) : octets' $stl.bytes 684 0
Check 'STL : modèle ramené à l''origine (décalage X)' $stl.offset[0] -3600
$stlText = Call 'export_stl' "{`"handles`":[`"$($xb.solid.handle)`"],`"filePath`":$(ConvertTo-Json $stlPath),`"ascii`":true}"
Check 'STL texte : facettes' @(Select-String -Path $stlText.file -Pattern 'facet normal').Count 12
$xb2 = Call 'create_box' '{"corner":[3620,0,0],"length":10,"width":10,"height":10}' -Write
$stl2 = Call 'export_stl' "{`"handles`":[`"$($xb.solid.handle)`",`"$($xb2.solid.handle)`"],`"filePath`":$(ConvertTo-Json $stlPath)}"
Check 'STL de deux boîtes disjointes : octets' $stl2.bytes (84 + 50 * 24) 0
Check 'export STL : dessin intact' (Props $xb2.solid.handle).volume 1000 1e-4
$sat = Call 'export_sat' "{`"handles`":[`"$($xb.solid.handle)`"],`"filePath`":$(ConvertTo-Json $satPath)}"
if ($sat -and $sat.bytes -gt 100) { Pass "SAT écrit : $($sat.bytes) octets" } else { Fail 'SAT non écrit' }
$imported = Call 'import_sat' "{`"filePath`":$(ConvertTo-Json $satPath)}" -Write
Check 'SAT réimporté : objets' $imported.count 1
Check 'SAT réimporté : volume' $imported.objects[0].volume 1000 1e-4
Check 'SAT réimporté : à la même place (Xmin)' (Props $imported.objects[0].object.handle).extents.min[0] 3600
Remove-Item $stlPath, $satPath -ErrorAction SilentlyContinue
Expect-Error 'export STL d''un objet non solide' 'export_stl' "{`"handles`":[`"$($square.handle)`"]}" -Read
Expect-Error 'import d''un fichier SAT absent' 'import_sat' '{"filePath":"C:\\mcpmap3d-absent\\absent.sat"}'

Write-Host "Inerties"
$inertia = (Call 'get_solid_properties' "{`"handles`":[`"$($box.solid.handle)`"],`"inertia`":true}").objects[0].inertia
$principal = @($inertia.principalMoments | Sort-Object)
# Boîte 10 × 20 × 30 : moments principaux V(b²+c²)/12 au centre de gravité, Ixx à l'origine V(b²+c²)/3.
Check 'moment principal mini (axe de 30)' $principal[0] 250000 1e-4
Check 'moment principal médian' $principal[1] 500000 1e-4
Check 'moment principal maxi' $principal[2] 650000 1e-4
Check 'moment d''inertie Ixx par rapport à l''origine' $inertia.momentsOfInertia[0] 2600000 1e-4
Check 'rayons de giration' @($inertia.radiiOfGyration).Count 3
Check 'axes principaux' @($inertia.principalAxes).Count 3

Write-Host "Capture de la vue"
$shot = Call 'capture_view' '{"width":320,"height":240}'
if ($shot -and $shot.mimeType -eq 'image/png' -and $shot.data.Length -gt 1000) { Write-Host "  OK    capture : $($shot.width)×$($shot.height), $($shot.data.Length) caractères base64" -ForegroundColor Green } else { Write-Host "  ÉCHEC capture" -ForegroundColor Red; $script:failures++ }

Write-Host "SCU"
$ucsStart = Call 'list_ucs'
$u = Call 'set_ucs' '{"preset":"front","origin":[10,0,0]}' -Write
Check 'SCU de face : Y = +Z' $u.ucs.yAxis[2] 1
Check 'SCU de face : Z = -Y' $u.ucs.zAxis[1] -1
$conv = Call 'convert_ucs_points' '{"points":[[1,2,3]]}'
Check 'SCU de face vers SCG : X' $conv.points[0][0] 11
Check 'SCU de face vers SCG : Y' $conv.points[0][1] -3
Check 'SCU de face vers SCG : Z' $conv.points[0][2] 2
$u = Call 'set_ucs' '{"points":[[100,0,0],[100,10,0],[90,0,0]],"saveAs":"MCP_TEST_SCU"}' -Write
Check 'SCU par 3 points : X = +Y' $u.ucs.xAxis[1] 1
Check 'SCU par 3 points : Y = -X' $u.ucs.yAxis[0] -1
Check 'SCU par 3 points : Z = +Z' $u.ucs.zAxis[2] 1
$listed = Call 'list_ucs'
if ($listed.named.name -contains 'MCP_TEST_SCU') { Pass 'SCU nommé enregistré' } else { Fail 'SCU nommé enregistré' }
if ($listed.current.matchingNamedUcs -contains 'MCP_TEST_SCU') { Pass 'SCU courant reconnu comme MCP_TEST_SCU' } else { Fail 'SCU courant reconnu comme MCP_TEST_SCU' }
$u = Call 'set_ucs' '{"preset":"world","rotateAxis":"x","rotateAngle":90}' -Write
Check 'SCG tourné de 90° autour de X : Y = +Z' $u.ucs.yAxis[2] 1
Check 'SCG tourné de 90° autour de X : Z = -Y' $u.ucs.zAxis[1] -1
$u = Call 'set_ucs' '{"zAxis":[1,0,0],"origin":[0,0,0]}' -Write
Check 'SCU par Z = +X (axe arbitraire) : X = +Y' $u.ucs.xAxis[1] 1
Check 'SCU par Z = +X (axe arbitraire) : Y = +Z' $u.ucs.yAxis[2] 1
$ucsLine = Call 'create_line' '{"start":[5000,0,0],"end":[5000,10,0]}' -Write
$u = Call 'set_ucs' "{`"handle`":`"$($ucsLine.handle)`"}" -Write
Check 'SCU sur une ligne : origine X' $u.ucs.origin[0] 5000
Check 'SCU sur une ligne : X le long de la ligne' $u.ucs.xAxis[1] 1
$u = Call 'set_ucs' '{"origin":[1,2,3]}' -Write
Check 'SCU déplacé : origine Z' $u.ucs.origin[2] 3
Check 'SCU déplacé : axes gardés' $u.ucs.xAxis[1] 1
$u = Call 'set_ucs' '{"name":"MCP_TEST_SCU"}' -Write
Check 'SCU nommé restauré : origine X' $u.ucs.origin[0] 100
$conv = Call 'convert_ucs_points' '{"points":[[100,10,0]],"from":"world","to":"MCP_TEST_SCU"}'
Check 'SCG vers SCU nommé : x local' $conv.points[0][0] 10
Check 'SCG vers SCU nommé : y local' $conv.points[0][1] 0 1e-6
$u = Call 'set_ucs' '{"preset":"right","saveAs":"MCP_TEST_SCU2","setCurrent":false}' -Write
if ((Call 'list_ucs').current.matchingNamedUcs -contains 'MCP_TEST_SCU') { Pass 'SCU nommé sans le rendre courant' } else { Fail 'SCU nommé sans le rendre courant' }
Check 'suppression d''un SCU nommé' (Call 'delete_ucs' '{"names":["MCP_TEST_SCU2"]}' -Write).deleted 1
Expect-Error 'SCU prédéfini inconnu' 'set_ucs' '{"preset":"dessous"}'
Expect-Error 'SCU : deux modes à la fois' 'set_ucs' '{"preset":"top","points":[[0,0,0],[1,0,0],[0,1,0]]}'
Expect-Error 'SCU par 3 points alignés' 'set_ucs' '{"points":[[0,0,0],[1,0,0],[2,0,0]]}'
Expect-Error 'SCU non courant sans nom' 'set_ucs' '{"preset":"top","setCurrent":false}'
Expect-Error 'suppression d''un SCU absent' 'delete_ucs' '{"names":["MCP_SCU_ABSENT"]}'
Expect-Error 'conversion vers un SCU absent' 'convert_ucs_points' '{"points":[[0,0,0]],"to":"MCP_SCU_ABSENT"}' -Read

Write-Host "Erreurs attendues"
$open =Call 'create_polyline' '{"points":[[900,0],[910,0],[910,10]]}' -Write
Expect-Error 'extrusion d''un profil ouvert' 'extrude' "{`"handles`":[`"$($open.handle)`"],`"height`":5}"
Expect-Error 'booléen sur un objet non solide' 'boolean_solids' "{`"operation`":`"union`",`"target`":`"$($box.solid.handle)`",`"tools`":[`"$($open.handle)`"]}"
$g = Call 'create_box' '{"corner":[1000,0,0],"length":1,"width":1,"height":1}' -Write
$h = Call 'create_box' '{"corner":[1100,0,0],"length":1,"width":1,"height":1}' -Write
Expect-Error 'intersection de solides disjoints' 'boolean_solids' "{`"operation`":`"intersect`",`"target`":`"$($g.solid.handle)`",`"tools`":[`"$($h.solid.handle)`"]}"
Expect-Error 'rayon négatif' 'create_sphere' '{"center":[0,0,0],"radius":-1}'
Expect-Error 'vue inconnue' 'set_view' '{"view":"dessous_de_table"}'
Expect-Error 'coupe sans plan' 'slice_solid' "{`"handles`":[`"$($box.solid.handle)`"]}"
Expect-Error 'plan par 3 points alignés' 'get_section' "{`"handles`":[`"$($box.solid.handle)`"],`"planePoints`":[[0,0,0],[1,0,0],[2,0,0]]}"
Expect-Error 'région sans contour fermé' 'create_region' "{`"handles`":[`"$($open.handle)`"]}"
Expect-Error 'polyligne à sommets non coplanaires' 'create_polyline' '{"points":[[0,0,0],[10,0,0],[10,10,0],[0,10,5]],"closed":true}'
Expect-Error 'région par points et handles à la fois' 'create_region' "{`"handles`":[`"$($open.handle)`"],`"points`":[[0,0],[1,0],[1,1]]}"
Expect-Error 'plan inconnu' 'create_region' '{"points":[[0,0],[1,0],[1,1]],"plane":"xw"}'

Write-Host "Annulation de $($script:writes) opération(s), une à une"
foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800 }
# D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
# d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
$extra = 0
while ($true) {
    $final = Call 'get_drawing_info'
    $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $final.layerCount -eq $start.layerCount
    if ($restored -or $extra -ge 3) { break }
    Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
}
if ($restored -and $extra -gt 0) {
    Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur (par exemple COVANEWS de Covadis au démarrage)" -ForegroundColor Yellow
} elseif ($restored) {
    Write-Host "  OK    retour à l'état initial ($($final.modelSpaceEntityCount) objets)" -ForegroundColor Green
} else {
    Write-Host "  ÉCHEC état final $($final.modelSpaceEntityCount) objets / $($final.layerCount) calques, attendu $($start.modelSpaceEntityCount) / $($start.layerCount)" -ForegroundColor Red
    $script:failures++
}
$ucsEnd = Call 'list_ucs'
if (($ucsEnd.current | ConvertTo-Json -Compress) -eq ($ucsStart.current | ConvertTo-Json -Compress) -and $ucsEnd.namedCount -eq $ucsStart.namedCount) {
    Write-Host "  OK    SCU courant et SCU nommés revenus à l'état initial" -ForegroundColor Green
} else {
    Write-Host "  ÉCHEC SCU après annulation : $($ucsEnd.current | ConvertTo-Json -Compress), $($ucsEnd.namedCount) SCU nommé(s) ; attendu $($ucsStart.current | ConvertTo-Json -Compress), $($ucsStart.namedCount)" -ForegroundColor Red
    $script:failures++
}
$client.Dispose()
if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
