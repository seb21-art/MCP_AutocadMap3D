<#
.SYNOPSIS
    Non-régression du cadastre : recherche d'adresse (BAN), import APICARTO dans les trois modes, données d'objet,
    étiquettes, dédoublonnage, erreurs. Contrôles chiffrés, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (calques TCAD*, TPARC*, TADR*, TEXT*, tables de données d'objet du même nom),
    puis annule chacune de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à
    -DrawingNameLike : n'utilisez que des copies de fichiers DWG. Il interroge les services en ligne de l'IGN et de
    la BAN : une connexion Internet est nécessaire.
#>
param([string]$DrawingNameLike = '*test*', [string]$Server)

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:writes = 0; $script:failures = 0

if (-not $Server) {
    $Server = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\server\McpMap3D.Server.exe'
    if (-not (Test-Path $Server)) { $Server = Join-Path $env:USERPROFILE '.mcpmap3d\server\McpMap3D.Server.exe' }
}
if (-not (Test-Path $Server)) { throw "Serveur MCP introuvable ($Server) : lancez d'abord install.ps1." }

function Call([string]$method, [string]$paramsJson = '{}', [switch]$Write) {
    $script:id++
    if ($Write) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*') {
    $script:id++; $script:writes++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":30000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($r.error.message -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($r.error.message) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

# Appel d'un outil par le serveur MCP (stdio), comme le ferait Claude. Renvoie { error ; data }.
function Mcp([string]$tool, [string]$argsJson) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo($Server)
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.BeginErrorReadLine()
    try {
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-cadastre","version":"1.0"}}}')
        $null = $proc.StandardOutput.ReadLine()
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"' + $tool + '","arguments":' + $argsJson + '}}')
        do { $line = $proc.StandardOutput.ReadLine() } while ($line -and $line -notmatch '"id":2')
        $resp = $line | ConvertFrom-Json
        $text = ($resp.result.content | ForEach-Object { $_.text }) -join "`n"
        if ($resp.result.isError) { return [pscustomobject]@{ Error = $text; Data = $null } }
        return [pscustomobject]@{ Error = $null; Data = ($text | ConvertFrom-Json) }
    } finally {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
    }
}

# $isWrite : l'appel atteint l'écriture dans AutoCAD (réussite, ou échec dans le plug-in) et consomme une étape.
function McpOk([string]$label, [string]$tool, [string]$argsJson, [switch]$Write) {
    $r = Mcp $tool $argsJson
    if ($Write -and -not $r.Error) { $script:writes++ }
    if ($r.Error) { Write-Host "  ÉCHEC $label : $($r.Error)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.Data
}

function McpError([string]$label, [string]$tool, [string]$argsJson, [string]$like) {
    $r = Mcp $tool $argsJson
    if (-not $r.Error) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($r.Error -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($r.Error) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.Error)" -ForegroundColor Green }
}

function Check([string]$label, $condition, $detail = '') {
    if ($condition) { Write-Host "  OK    $label $detail" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label $detail" -ForegroundColor Red; $script:failures++ }
}

function CheckText([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : « $actual »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

function Entities([string]$layer) { (Call 'list_entities' "{`"layers`":[`"$layer`"],`"includeGeometry`":true,`"limit`":500}").entities }

function InPolygon([double]$x, [double]$y, $points) {
    $inside = $false
    $j = $points.Count - 1
    for ($i = 0; $i -lt $points.Count; $i++) {
        $a = $points[$i]; $b = $points[$j]
        if ((($a[1] -gt $y) -ne ($b[1] -gt $y)) -and ($x -lt ($b[0] - $a[0]) * ($y - $a[1]) / ($b[1] - $a[1]) + $a[0])) { $inside = -not $inside }
        $j = $i
    }
    return $inside
}

function State {
    $info = Call 'get_drawing_info'
    [pscustomobject]@{
        Entities = $info.modelSpaceEntityCount
        Layers = $info.layerCount
        OdTables = (Call 'list_od_tables' '{"includeFields":false}').count
        Cs = (Call 'get_coordinate_system').system.code
    }
}

# Parcelle en L de 700 m² avec un trou de 20 m², près de Bordeaux, décrite en mètres puis convertie en lon/lat.
# Son centre de gravité (13,6 ; 13,6) tombe hors du L : l'étiquette doit pourtant être dedans.
$lon0 = -0.5705; $lat0 = 44.8415; $kx = 1 / (111320 * [math]::Cos($lat0 * [math]::PI / 180)); $ky = 1 / 110574
function Ring($meters) { ($meters | ForEach-Object { '[' + ($lon0 + $_[0] * $kx).ToString('R', [cultureinfo]::InvariantCulture) + ',' + ($lat0 + $_[1] * $ky).ToString('R', [cultureinfo]::InvariantCulture) + ']' }) -join ',' }
$outer = Ring @(@(0,0), @(40,0), @(40,10), @(10,10), @(10,40), @(0,40), @(0,0))
$hole = Ring @(@(25,3), @(30,3), @(30,7), @(25,7), @(25,3))
$sheet = Ring @(@(-5,-5), @(60,-5), @(60,60), @(-5,60), @(-5,-5))
function Payload([string]$prefix, [string]$extra = '') {
    '{"layerPrefix":"' + $prefix + '","zoomExtents":false' + $extra + ',' +
    '"parcels":[{"id":"33063000KM0111","numero":"0111","section":"KM","commune":"Bordeaux","codeInsee":"33063","contenance":700,' +
    '"polygons":[[[' + $outer + '],[' + $hole + ']]]}],' +
    '"sections":[{"id":"33063000KM01","section":"KM","feuille":1,"commune":"Bordeaux","codeInsee":"33063","polygons":[[[' + $sheet + ']]]}]}'
}

# Bâtiments fictifs dans la parcelle en L : l'un en dur, 7,9 m de haut posé à 6,5 m ; l'autre léger, sans hauteur connue.
$building1 = Ring @(@(2,2), @(8,2), @(8,8), @(2,8), @(2,2))
$building2 = Ring @(@(20,2), @(24,2), @(24,6), @(20,6), @(20,2))
function BuildingPayload([string]$prefix) {
    $base = Payload $prefix ',"buildingSource":"bdtopo","buildings3d":true'
    $base.Substring(0, $base.Length - 1) + ',"buildings":[' +
    '{"id":"BATIMENT_T1","source":"bdtopo","type":"Indifférenciée","light":false,"usage":"Résidentiel","height":7.9,"floors":2,' +
    '"groundAltitude":6.5,"roofAltitude":14.4,"rnb":"TEST","polygons":[[[' + $building1 + ']]]},' +
    '{"id":"BATIMENT_T2","source":"bdtopo","type":"Indifférenciée","light":true,"polygons":[[[' + $building2 + ']]]}]}'
}

try {
    $info = Call 'get_drawing_info'
    if ($info.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $start = State
    "Départ : $($start.Entities) objets, $($start.Layers) calques, $($start.OdTables) table(s) OD, système « $($start.Cs) »"
    "Serveur MCP : $Server"

    Write-Host "Recherche d'adresse (sans modification du dessin)"
    $found = McpOk 'recherche' 'search_cadastre_address' '{"address":"1 cours de l''Intendance 33000 Bordeaux","radius":30}'
    if ($found) {
        CheckText 'parcelle de l''adresse (point en façade)' $found.parcelAtAddress.id '33063000KO0146'
        Check 'parcelles triées par distance' ($found.parcels.Count -ge 2 -and $found.parcels[0].distanceMeters -le $found.parcels[1].distanceMeters) "($($found.parcelCount) parcelles)"
        Check 'toutes dans le rayon' (@($found.parcels | Where-Object { $_.distanceMeters -gt 30 }).Count -eq 0)
    }
    $ambiguous = McpOk 'adresse ambiguë' 'search_cadastre_address' '{"address":"rue de la Paix"}'
    if ($ambiguous) {
        Check 'autres communes proposées' ($ambiguous.otherCandidates.Count -ge 2) "($(($ambiguous.otherCandidates | ForEach-Object { $_.city }) -join ', '))"
        Check 'ambiguïté signalée' (($ambiguous.notes -join ' ') -like '*ambiguë*')
    }
    McpError 'rayon hors bornes' 'search_cadastre_address' '{"address":"Bordeaux","radius":1000}' '*entre 1 et 500*'

    if (-not $start.Cs) {
        Write-Host "Système attribué d'après la zone (dessin sans système)"
        $utm = Call 'import_cadastre_geometry' '{"layerPrefix":"TDOM","zoomExtents":false,"epsg":2975,"parcels":[{"id":"97411000AB0001","numero":"0001","section":"AB","polygons":[[[[55.45,-20.88],[55.4502,-20.88],[55.4502,-20.8798],[55.45,-20.88]]]]}]}'
        if ($utm) {
            Check 'La Réunion : système UTM 40S attribué' ($utm.coordinateSystemAssigned -and $utm.coordinateSystem -match '40S') "($($utm.coordinateSystem))"
            Check 'coordonnées UTM plausibles' ($utm.extents.min[0] -gt 300000 -and $utm.extents.min[0] -lt 400000 -and $utm.extents.min[1] -gt 7600000 -and $utm.extents.min[1] -lt 7700000) "($($utm.extents.min[0..1] -join ', '))"
        }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800
        CheckText 'U retire aussi le système attribué' "$((Call 'get_coordinate_system').assigned)" 'False'

        Write-Host "  Attribution de Lambert93 au dessin de test" -ForegroundColor Gray
        Call 'set_coordinate_system' '{"code":"Lambert93","force":true}' -Write | Out-Null
    }

    Write-Host "Import direct : géométrie, étiquettes, données d'objet"
    $imported = Call 'import_cadastre_geometry' (Payload 'TCAD') -Write
    if ($imported) {
        CheckText 'parcelles importées' $imported.importedParcels 1
        CheckText 'feuilles importées' $imported.importedSections 1
        CheckText 'tables de données d''objet' "$($imported.objectData.parcels)/$($imported.objectData.sections)" 'TCAD/TCAD_SECTIONS'
        $rings = @(Entities 'TCAD_PARCELLES' | Sort-Object { -$_.vertices })
        CheckText 'contours de la parcelle (extérieur et trou)' $rings.Count 2
        CheckText 'sommets du contour, sans le point de fermeture répété' $rings[0].vertices 6
        $area = $rings[0].area - $rings[1].area
        Check 'surface nette ≈ 680 m²' ([math]::Abs($area - 680) -lt 680 * 0.02) "($([math]::Round($area, 1)) m²)"
        $od = Call 'get_od_records' "{`"handles`":[`"$($rings[0].handle)`",`"$($rings[1].handle)`"]}"
        CheckText 'données d''objet sur les deux contours' (@($od.objects | Where-Object { $_.records.table -contains 'TCAD' }).Count) 2
        $values = ($od.objects[0].records | Where-Object { $_.table -eq 'TCAD' }).values
        CheckText 'OD : ID' $values.ID '33063000KM0111'
        CheckText 'OD : CONTENANCE' $values.CONTENANCE 700
        $labels = @(Entities 'TCAD_NUMEROS')
        CheckText 'étiquette : numéro sans zéros (section dessinée à part)' $labels[0].text '111'
        $cx = ($labels[0].extents.min[0] + $labels[0].extents.max[0]) / 2; $cy = ($labels[0].extents.min[1] + $labels[0].extents.max[1]) / 2
        Check 'étiquette dans le L, hors du trou' ((InPolygon $cx $cy $rings[0].points) -and -not (InPolygon $cx $cy $rings[1].points))
        $sheetLabel = @(Entities 'TCAD_SECTIONS' | Where-Object { $_.type -eq 'TEXT' })
        CheckText 'étiquette de section' $sheetLabel[0].text 'KM'
    }
    $again = Call 'import_cadastre_geometry' (Payload 'TCAD') -Write
    if ($again) {
        CheckText 'réimport : parcelle déjà présente ignorée' "$($again.importedParcels)/$($again.skippedExistingParcels)" '0/1'
        CheckText 'réimport : feuille déjà présente ignorée' "$($again.importedSections)/$($again.skippedExistingSections)" '0/1'
    }

    Write-Host "Import direct : bâtiments en 3D"
    $withBuildings = Call 'import_cadastre_geometry' (BuildingPayload 'TBAT') -Write
    if ($withBuildings) {
        CheckText 'bâtiments importés' $withBuildings.importedBuildings 2
        CheckText 'table des bâtiments' $withBuildings.objectData.buildings 'TBAT_BATIMENTS'
        Check 'bâtiment sans hauteur signalé' (($withBuildings.warnings -join ' ') -like '*dessinés à plat*')
        $hard = @(Entities 'TBAT_BATI_DUR'); $light = @(Entities 'TBAT_BATI_LEGER')
        CheckText 'bâti dur et bâti léger sur leurs calques' "$($hard.Count)/$($light.Count)" '1/1'
        $props = (Call 'get_solid_properties' "{`"handles`":[`"$($hard[0].handle)`"]}").objects[0]
        CheckText 'bâtiment posé à l''altitude du sol' $props.extents.min[2] 6.5
        CheckText 'extrudé de sa hauteur jusqu''au toit' $props.extents.max[2] 14.4
        $od = Call 'get_od_records' "{`"handles`":[`"$($hard[0].handle)`",`"$($light[0].handle)`"]}"
        $v1 = ($od.objects[0].records | Where-Object { $_.table -eq 'TBAT_BATIMENTS' }).values
        $v2 = ($od.objects[1].records | Where-Object { $_.table -eq 'TBAT_BATIMENTS' }).values
        CheckText 'OD : hauteur, étages, léger' "$($v1.HAUTEUR)/$($v1.ETAGES)/$($v1.LEGER)" '7.9/2/0'
        CheckText 'OD : construction légère' $v2.LEGER 1
    }
    $againBuildings = Call 'import_cadastre_geometry' (BuildingPayload 'TBAT') -Write
    if ($againBuildings) {
        CheckText 'réimport : bâtiments déjà présents ignorés' "$($againBuildings.importedBuildings)/$($againBuildings.skippedExistingBuildings)" '0/2'
    }

    Write-Host "Import direct : erreurs"
    Call '_create_od_table' '{"name":"TCONF","fields":[{"name":"ID","type":"Integer"}]}' -Write | Out-Null
    Expect-Error 'table OD existante de structure différente' 'import_cadastre_geometry' (Payload 'TCONF') '*autre structure*'
    $reunion = '{"layerPrefix":"TOUT","zoomExtents":false,"parcels":[{"id":"97411000AB0001","numero":"0001","section":"AB","polygons":[[[[55.45,-20.88],[55.4501,-20.88],[55.4501,-20.8799],[55.45,-20.88]]]]}]}'
    Expect-Error 'zone hors du domaine du système du dessin' 'import_cadastre_geometry' $reunion '*hors du domaine*'
    Expect-Error 'préfixe de calque invalide' 'import_cadastre_geometry' (Payload 'A<B') '*préfixe*'

    Write-Host "Import par parcelle (serveur MCP, APICARTO)"
    $parcel = McpOk 'section « A » complétée en « 0A »' 'import_cadastre' '{"codeInsee":"33001","section":"A","numero":"673","layerPrefix":"TPARC","zoomExtents":false}' -Write
    if ($parcel) {
        CheckText 'mode déduit' $parcel.mode 'parcel'
        CheckText 'parcelle importée' $parcel.import.parcels[0].id '330010000A0673'
        Check 'feuille(s) de la section importée(s)' ($parcel.import.importedSections -ge 1) "($($parcel.import.importedSections))"
    }
    $neighbours = McpOk 'voisines à 15 m' 'import_cadastre' '{"codeInsee":"33001","section":"0A","numero":"0673","buffer":15,"layerPrefix":"TPARC","zoomExtents":false}' -Write
    if ($neighbours) {
        Check 'voisines importées' ($neighbours.import.importedParcels -ge 1) "($($neighbours.import.importedParcels))"
        CheckText 'la parcelle déjà présente est ignorée' $neighbours.import.skippedExistingParcels 1
    }

    Write-Host "Import par adresse (serveur MCP, BAN puis APICARTO)"
    $address = McpOk 'import autour d''une adresse' 'import_cadastre' '{"address":"1 cours de l''Intendance 33000 Bordeaux","buffer":20,"layerPrefix":"TADR","zoomExtents":false}' -Write
    if ($address) {
        CheckText 'parcelle de l''adresse' $address.address.parcelAtAddress '33063000KO0146'
        Check 'parcelles importées' ($address.import.importedParcels -ge 1) "($($address.import.importedParcels))"
        CheckText 'système du dessin conservé' $address.import.coordinateSystem ($(if ($start.Cs) { $start.Cs } else { 'Lambert93' }))
        $twice = McpOk 'second import identique' 'import_cadastre' '{"address":"1 cours de l''Intendance 33000 Bordeaux","buffer":20,"layerPrefix":"TADR","zoomExtents":false}' -Write
        if ($twice) { CheckText 'second import : tout est déjà présent' "$($twice.import.importedParcels)/$($twice.import.skippedExistingParcels)" "0/$($address.import.importedParcels)" }
    }

    Write-Host "Bâtiments (serveur MCP, Géoplateforme de l'IGN)"
    $cadBuildings = McpOk 'bâti cadastral autour d''une adresse' 'import_cadastre' '{"address":"1 cours de l''Intendance 33000 Bordeaux","buffer":20,"includeBuildings":true,"layerPrefix":"TBADR","zoomExtents":false}' -Write
    if ($cadBuildings) {
        Check 'bâtiments importés' ($cadBuildings.import.importedBuildings -ge 1) "($($cadBuildings.import.importedBuildings))"
        CheckText 'source par défaut' $cadBuildings.import.buildingSource 'cadastre'
        $b = @(Entities 'TBADR_BATI_DUR')[0]
        $v = ((Call 'get_od_records' "{`"handles`":[`"$($b.handle)`"]}").objects[0].records | Where-Object { $_.table -eq 'TBADR_BATIMENTS' }).values
        CheckText 'OD : bâti cadastral en dur' "$($v.SOURCE) / $($v.TYPE)" 'cadastre / Bâtiment en dur'
    }
    $topo = McpOk 'BD TOPO en 3D sur une parcelle' 'import_cadastre' '{"codeInsee":"33063","section":"KO","numero":"146","includeBuildings":true,"buildings3d":true,"layerPrefix":"TBKO","zoomExtents":false}' -Write
    if ($topo) {
        CheckText 'BD TOPO choisie d''office pour la 3D' $topo.import.buildingSource 'bdtopo'
        Check 'seulement les bâtiments de la parcelle' ($topo.import.importedBuildings -ge 1 -and $topo.import.importedBuildings -le 10) "($($topo.import.importedBuildings))"
        $tb = @(Entities 'TBKO_BATI_DUR')[0]
        $tp = (Call 'get_solid_properties' "{`"handles`":[`"$($tb.handle)`"]}").objects[0]
        Check 'bâtiment réel extrudé' ($tp.extents.max[2] -gt $tp.extents.min[2] + 1) "(Z de $($tp.extents.min[2]) à $($tp.extents.max[2]))"
    }

    Write-Host "Import sur l'emprise du dessin (serveur MCP)"
    $extent = Mcp 'import_cadastre' '{"mode":"extent","maxParcels":30,"layerPrefix":"TEXT","zoomExtents":false}'
    if (-not $extent.Error) {
        $script:writes++
        Write-Host "  OK    emprise lue et importée ($($extent.Data.import.importedParcels) parcelles)" -ForegroundColor Green
    } elseif ($extent.Error -like '*maxParcels*' -or $extent.Error -like '*km de côté*') {
        Write-Host "  OK    emprise lue, import refusé comme prévu -> $($extent.Error)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC mode extent : $($extent.Error)" -ForegroundColor Red; $script:failures++
    }

    Write-Host "Erreurs attendues du serveur MCP (dessin non modifié)"
    McpError 'adresse ambiguë refusée à l''import' 'import_cadastre' '{"address":"rue de la Paix"}' '*ambiguë*'
    McpError 'section invalide' 'import_cadastre' '{"codeInsee":"33001","section":"ABC"}' '*section cadastrale*'
    McpError 'code INSEE invalide' 'import_cadastre' '{"codeInsee":"3306","section":"KM"}' '*code INSEE*'
    McpError 'section inexistante (filtrage local)' 'import_cadastre' '{"codeInsee":"33001","section":"ZZ","numero":"1"}' '*Aucune parcelle*'
    McpError 'plafond de parcelles' 'import_cadastre' '{"address":"Place de la Bourse 33000 Bordeaux","buffer":200,"maxParcels":5}' '*maxParcels*'
    McpError 'zone trop étendue' 'import_cadastre' '{"address":"Place de la Bourse 33000 Bordeaux","buffer":8000}' '*km de côté*'
    McpError 'bâtiments 3D sans BD TOPO' 'import_cadastre' '{"codeInsee":"33063","section":"KO","numero":"146","includeBuildings":true,"buildings3d":true,"buildingSource":"cadastre"}' '*BD TOPO*'
    McpError 'source de bâtiments inconnue' 'import_cadastre' '{"codeInsee":"33063","section":"KO","includeBuildings":true,"buildingSource":"osm"}' '*inconnue*'
    McpError 'plafond de bâtiments' 'import_cadastre' '{"address":"1 cours de l''Intendance 33000 Bordeaux","buffer":60,"includeBuildings":true,"maxBuildings":3}' '*maxBuildings*'

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = State
        $restored = $final.Entities -eq $start.Entities -and $final.Layers -eq $start.Layers -and
            $final.OdTables -eq $start.OdTables -and "$($final.Cs)" -eq "$($start.Cs)"
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    $summary = "$($final.Entities) objets, $($final.Layers) calques, $($final.OdTables) table(s) OD, système « $($final.Cs) »"
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur (par exemple COVANEWS de Covadis au démarrage)" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($summary)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $summary ; attendu $($start.Entities) objets, $($start.Layers) calques, $($start.OdTables) table(s) OD, système « $($start.Cs) »" -ForegroundColor Red
        $script:failures++
    }
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
