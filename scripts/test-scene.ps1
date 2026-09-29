<#
.SYNOPSIS
    Non-régression du rendu : matériaux, affectation (objet, calque, face), lumières, soleil et ambiance
    de vue. Contrôles chiffrés, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert, puis annule chacune de ses opérations avec U. Il refuse de
    s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike : n'utilisez que des copies.
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

function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [switch]$Read) {
    $script:id++
    if (-not $Read) { $script:writes++ }
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
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : « $actual »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

function Material([string]$name) { @((Call 'list_materials' "{`"names`":[`"$name`"]}").materials)[0] }
function LightNamed([string]$name) { @((Call 'list_lights' "{`"names`":[`"$name`"]}").lights)[0] }
function State {
    [pscustomobject]@{
        Entities = (Call 'get_drawing_info').modelSpaceEntityCount
        Materials = (Call 'list_materials').count
        Lights = (Call 'list_lights').count
    }
}

try {
    $info = Call 'get_drawing_info'
    if ($info.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $start = State
    $startSun = Call 'get_sun'
    $startView = Call 'get_view_ambiance'
    "Départ : $($start.Entities) objets, $($start.Materials) matériau(x), $($start.Lights) lumière(s)"

    Write-Host "Matériau"
    $name = "MCP_MAT_$(Get-Random)"
    $created = Call 'set_material' "{`"name`":`"$name`",`"color`":`"255,128,0`",`"opacity`":0.4,`"shininess`":0.25,`"twoSided`":true,`"description`":`"essai`"}" -Write
    CheckText 'créé' $created.created 'True'
    CheckText 'couleur' $created.material.color 'RGB(255,128,0)'
    Check 'opacité' $created.material.opacity 0.4
    Check 'brillance' $created.material.shininess 0.25
    CheckText 'recto-verso' $created.material.twoSided 'True'
    $listed = Material $name
    CheckText 'relu' $listed.name $name
    Check 'relu : opacité' $listed.opacity 0.4
    $updated = Call 'set_material' "{`"name`":`"$name`",`"opacity`":0.8,`"reflectivity`":0.3}" -Write
    CheckText 'modifié, pas recréé' $updated.created 'False'
    Check 'opacité modifiée' $updated.material.opacity 0.8
    Check 'réflexion' $updated.material.reflectivity 0.3
    Check 'brillance conservée' $updated.material.shininess 0.25
    CheckText 'couleur conservée' $updated.material.color 'RGB(255,128,0)'

    Write-Host "Affectation"
    $layer = "MCP_RENDU_$(Get-Random)"
    Call 'create_layer' "{`"name`":`"$layer`"}" -Write | Out-Null
    $box = Call 'create_box' "{`"corner`":[8000,8000,0],`"length`":2,`"width`":1,`"height`":0.5,`"layer`":`"$layer`"}" -Write
    $handle = $box.solid.handle
    $assigned = Call 'assign_material' "{`"material`":`"$name`",`"handles`":[`"$handle`"],`"layers`":[`"$layer`"]}" -Write
    CheckText 'objet' @($assigned.objects)[0] $handle
    CheckText 'calque' @($assigned.layers)[0] $layer
    $topology = Call 'get_solid_topology' "{`"handle`":`"$handle`"}"
    $faceIndex = @($topology.faces)[0].index
    $face = Call 'assign_material' "{`"material`":`"$name`",`"handle`":`"$handle`",`"face`":$faceIndex}" -Write
    Check 'face' $face.face.index $faceIndex
    CheckText 'face : matériau' $face.face.material $name

    Write-Host "Lumières"
    $point = Call 'set_light' '{"type":"point","name":"MCP point","position":[8001,8001,3],"intensity":12,"color":"blanc","shadows":true}' -Write
    Write-Host "  (unités d'éclairage du dessin : $($point.lightingUnits))"
    CheckText 'ponctuelle créée' $point.created 'True'
    CheckText 'type' $point.light.type 'point'
    Check 'intensité' $point.light.intensity 12
    CheckText 'pas de facteur d''intensité' "$($point.light.intensityFactor)" ''
    CheckText 'espace objet' $point.light.space 'model'
    CheckText 'allumée' $point.light.isOn 'True'
    CheckText 'ombres' $point.light.shadows 'True'
    $spot = Call 'set_light' '{"type":"spot","position":[8000,8000,4],"target":[8001,8000.5,0.25],"hotspot":20,"falloff":35,"intensity":2}' -Write
    CheckText 'projecteur' $spot.light.type 'spot'
    Check 'faisceau vif' $spot.light.hotspot 20
    Check 'faisceau total' $spot.light.falloff 35
    $distant = Call 'set_light' '{"type":"distant","direction":[-1,-1,-2],"on":false}' -Write
    CheckText 'lointaine éteinte' $distant.light.isOn 'False'
    $renamed = Call 'set_light' "{`"handle`":`"$($point.light.handle)`",`"name`":`"MCP point 2`",`"intensity`":4}" -Write
    CheckText 'pas recréée' $renamed.created 'False'
    Check 'intensité modifiée' (LightNamed 'MCP point 2').intensity 4
    $removed = Call 'delete_lights' "{`"handles`":[`"$($distant.light.handle)`"]}" -Write
    Check 'supprimée' $removed.erased 1

    Write-Host "Soleil"
    $sun = Call 'set_sun' '{"date":"2026-06-21","time":"14:30","on":true,"shadows":true,"intensity":1.5}' -Write
    CheckText 'allumé' $sun.on 'True'
    CheckText 'date' $sun.date '2026-06-21'
    CheckText 'heure' $sun.time '14:30:00'
    CheckText 'ombres' $sun.shadows 'True'
    Check 'intensité' $sun.intensity 1.5
    $reread = Call 'get_sun'
    if ([math]::Abs([double]$reread.altitude) -le 90) { Write-Host "  OK    hauteur en degrés : $($reread.altitude)" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC hauteur hors de -90..90 : $($reread.altitude)" -ForegroundColor Red; $script:failures++ }
    Check 'hauteur à jour dans la réponse' $sun.altitude $reread.altitude
    Check 'azimut à jour dans la réponse' $sun.azimuth $reread.azimuth
    if ($sun.calibrated -eq $true) {
        CheckText 'calé' $sun.calibrated 'True'
        if ($sun.timeZone -eq 'Paris') { CheckText 'heure d''été' $sun.daylightSaving 'True' }
    } else {
        Write-Host "  OK    soleil non calé (pas de système situable) : $($sun.warning)" -ForegroundColor Green
    }
    $kept = Call 'set_sun' '{"date":"2026-01-15","time":"08:00","calibrate":false}' -Write
    CheckText 'date d''hiver' $kept.date '2026-01-15'
    Check 'latitude inchangée' $kept.latitude $sun.latitude
    $manual = Call 'set_sun' '{"latitude":45.764,"longitude":4.8357,"utcOffset":1,"north":2}' -Write
    Check 'lieu manuel : latitude' $manual.latitude 45.764
    Check 'lieu manuel : longitude' $manual.longitude 4.8357
    Check 'nord imposé' $manual.northDirection 2
    CheckText 'lieu manuel signalé' $manual.manualPlace 'True'
    $shadowsOnly = Call 'set_sun' '{"shadows":false,"time":"14h"}' -Write
    Check 'lieu manuel conservé' $shadowsOnly.latitude 45.764
    CheckText 'heure 14h' $shadowsOnly.time '14:00:00'
    CheckText 'ombres coupées' $shadowsOnly.shadows 'False'

    Write-Host "Ambiance de vue"
    $view = Call 'set_view_ambiance' '{"background":"solid","color":"5,10,40","brightness":1.5,"defaultLighting":"one"}' -Write
    CheckText 'fond uni' $view.background.type 'solid'
    Check 'luminosité' $view.brightness 1.5
    CheckText 'éclairage par défaut' $view.defaultLighting 'one'
    $read = Call 'get_view_ambiance'
    CheckText 'relu : fond' $read.background.type 'solid'
    CheckText 'relu : éclairage' $read.defaultLighting 'one'
    $gradient = Call 'set_view_ambiance' '{"background":"gradient","colorTop":"20,40,120","visualStyle":"realistic"}' -Write
    CheckText 'fond remplacé' $gradient.background.type 'gradient'
    CheckText 'style visuel réaliste' $gradient.visualStyle 'Realistic'
    CheckText 'pas d''avertissement en réaliste' "$($gradient.warning)" ''

    Write-Host "Refus"
    Expect-Error 'opacité hors bornes' 'set_material' "{`"name`":`"$name`",`"opacity`":2}"
    Expect-Error 'matériau inconnu' 'assign_material' "{`"material`":`"MCP_ABSENT`",`"handles`":[`"$handle`"]}"
    Expect-Error 'nom réservé' 'set_material' '{"name":"ByLayer","color":"1"}'
    Expect-Error 'ByBlock sur un calque' 'assign_material' "{`"material`":`"ByBlock`",`"layers`":[`"$layer`"]}"
    Expect-Error 'projecteur sans cible' 'set_light' '{"type":"spot","position":[0,0,1]}'
    Expect-Error 'faisceau incohérent' 'set_light' '{"type":"spot","position":[0,0,2],"target":[1,0,0],"hotspot":40,"falloff":10}'
    Expect-Error 'lumière à supprimer inconnue' 'delete_lights' "{`"handles`":[`"$handle`"]}"
    Expect-Error 'date illisible' 'set_sun' '{"date":"21/06/2026","time":"14:30"}'
    Expect-Error 'ambiance vide' 'set_view_ambiance' '{}'
    Expect-Error 'fond inconnu' 'set_view_ambiance' '{"background":"brume"}'

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 700 }

    $extra = 0
    while ($true) {
        $final = State
        $restored = $final.Entities -eq $start.Entities -and $final.Materials -eq $start.Materials -and $final.Lights -eq $start.Lights
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    $summary = "$($final.Entities) objets, $($final.Materials) matériau(x), $($final.Lights) lumière(s)"
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s)" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($summary)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $summary ; attendu $($start.Entities) objets, $($start.Materials) matériau(x), $($start.Lights) lumière(s)" -ForegroundColor Red
        $script:failures++
    }
    $endSun = Call 'get_sun'
    $endView = Call 'get_view_ambiance'
    CheckText 'soleil restauré' $endSun.date $startSun.date
    Check 'latitude restaurée' $endSun.latitude $startSun.latitude
    CheckText 'lieu manuel annulé' "$($endSun.manualPlace)" "$($startSun.manualPlace)"
    CheckText 'fond restauré' $endView.background.type $startView.background.type
    CheckText 'style visuel restauré' $endView.visualStyle $startView.visualStyle
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
