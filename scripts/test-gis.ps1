<#
.SYNOPSIS
    Non-régression de l'import-export SIG (Map 3D) : export SHP avec données d'objet, aperçu, réimport, refus,
    autres formats (MIF, TAB, GML, SDF), conversion de système de coordonnées, puis annulation.
.DESCRIPTION
    Le script dessine quelques objets de test, les exporte dans un dossier temporaire, réimporte les fichiers,
    compare géométrie et attributs, puis annule chacune de ses opérations avec U et supprime le dossier. Il refuse
    de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike : n'utilisez que des copies de fichiers DWG.
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

function Send([string]$method, [string]$paramsJson) {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":300000}")
    return $reader.ReadLine() | ConvertFrom-Json
}

# -Write : outil d'écriture, qui laisse une étape d'annulation, même en échec.
function Call([string]$method, [string]$paramsJson = '{}', [switch]$Write) {
    if ($Write) { $script:writes++ }
    $r = Send $method $paramsJson
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*', [switch]$Write) {
    if ($Write) { $script:writes++ }
    $r = Send $method $paramsJson
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($r.error.message -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($r.error.message) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function Near([string]$label, [double]$actual, [double]$expected, [double]$tolerance) {
    Check $label ([math]::Abs($actual - $expected) -le $tolerance) 'True'
    if ([math]::Abs($actual - $expected) -gt $tolerance) { Write-Host "        $actual au lieu de $expected" }
}

function Json([string]$value) { $value | ConvertTo-Json }
function Warnings($result) { if ($result.warnings) { $result.warnings | ForEach-Object { Write-Host "  AVERT $_" -ForegroundColor Yellow } } }

$start = Call 'get_drawing_info'
if ($start.fileName -notlike $DrawingNameLike) {
    $client.Dispose()
    throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
}

$cs = Call 'get_coordinate_system'
$code = if ($cs.assigned) { $cs.code } else { $null }
Write-Host "Système du dessin : $(if ($code) { $code } else { 'aucun' })"
$startTables = @((Call 'list_od_tables' '{"includeFields":false}').tables | ForEach-Object { $_.name })

# Coordonnées plausibles dans le système du dessin (Lambert-93 : Paris), sinon petites coordonnées.
$lambert = $code -like '*93*'
$x0 = if ($lambert) { 651000 } else { 1000 }
$y0 = if ($lambert) { 6862000 } else { 1000 }
$folder = Join-Path $env:TEMP "mcp-test-gis-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory $folder | Out-Null
$table = "MCP_GIS_$([guid]::NewGuid().ToString('N').Substring(0, 6))"

try {
    Write-Host "Objets de test"
    Call 'create_layer' '{"name":"MCP_GIS_TEST"}' -Write | Out-Null
    Call '_create_od_table' "{`"name`":`"$table`",`"fields`":[{`"name`":`"NOM`",`"type`":`"Character`"},{`"name`":`"SURF`",`"type`":`"Real`"},{`"name`":`"NUM`",`"type`":`"Integer`"}]}" -Write | Out-Null
    $sq1 = Call 'create_polyline' "{`"points`":[[$x0,$y0],[$($x0+10),$y0],[$($x0+10),$($y0+10)],[$x0,$($y0+10)]],`"closed`":true,`"layer`":`"MCP_GIS_TEST`"}" -Write
    $sq2 = Call 'create_polyline' "{`"points`":[[$($x0+20),$y0],[$($x0+40),$y0],[$($x0+40),$($y0+5)],[$($x0+20),$($y0+5)]],`"closed`":true,`"layer`":`"MCP_GIS_TEST`"}" -Write
    $line = Call 'create_polyline' "{`"points`":[[$x0,$($y0+20)],[$($x0+30),$($y0+25)]],`"layer`":`"MCP_GIS_TEST`"}" -Write
    foreach ($pair in @(@($sq1, 'Carré', 100, 1), @($sq2, 'Rectangle', 100, 2))) {
        $h = $pair[0].handle
        Call 'set_od_value' "{`"handles`":[`"$h`"],`"table`":`"$table`",`"field`":`"NOM`",`"value`":$(Json $pair[1])}" -Write | Out-Null
        Call 'set_od_value' "{`"handles`":[`"$h`"],`"table`":`"$table`",`"field`":`"SURF`",`"value`":$($pair[2])}" -Write | Out-Null
        Call 'set_od_value' "{`"handles`":[`"$h`"],`"table`":`"$table`",`"field`":`"NUM`",`"value`":$($pair[3])}" -Write | Out-Null
    }

    Write-Host "Export SHP"
    $shp = Join-Path $folder 'carres.shp'
    Expect-Error 'sélection mélangée en auto' 'export_gis' "{`"filePath`":$(Json $shp),`"layers`":[`"MCP_GIS_TEST`"]}" '*mélange*'
    $exp = Call 'export_gis' "{`"filePath`":$(Json $shp),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"polygon`"}"
    if ($exp) {
        Warnings $exp
        Check 'géométrie' $exp.geometry 'Polygon'
        Check 'objets exportés' $exp.entitiesExported 2
        Check 'ligne ignorée' $exp.skippedOtherGeometry.line 1
        Check 'colonnes' (($exp.columns | ForEach-Object { $_.column }) -join ',') 'NOM,SURF,NUM'
        Check 'fichiers .shp .shx .dbf' ((@('.shp', '.shx', '.dbf') | Where-Object { Test-Path ([IO.Path]::ChangeExtension($shp, $_)) }).Count) 3
        if ($code) { Check '.prj écrit' (Test-Path ([IO.Path]::ChangeExtension($shp, '.prj'))) 'True' }
    }
    Expect-Error 'fichier existant sans overwrite' 'export_gis' "{`"filePath`":$(Json $shp),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"polygon`"}" '*existe déjà*'
    $again = Call 'export_gis' "{`"filePath`":$(Json $shp),`"handles`":[`"$($sq1.handle)`"],`"overwrite`":true}"
    if ($again) { Check 'overwrite, par handle, géométrie auto' "$($again.replaced)/$($again.entitiesExported)/$($again.geometry)" 'True/1/Polygon' }
    Call 'export_gis' "{`"filePath`":$(Json $shp),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"polygon`",`"overwrite`":true}" | Out-Null
    $lines = Call 'export_gis' "{`"filePath`":$(Json (Join-Path $folder 'lignes.shp')),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"line`",`"attributes`":`"none`",`"dimension`":`"3d`"}"
    if ($lines) { Check 'lignes : courbes ouvertes et fermées' "$($lines.entitiesExported)/$($lines.dimension)/$(@($lines.columns).Count)" '3/3D/0' }
    Expect-Error 'extension inconnue' 'export_gis' "{`"filePath`":$(Json (Join-Path $folder 'x.abc')),`"layers`":[`"MCP_GIS_TEST`"]}" '*Format inconnu*'

    Write-Host "Aperçu et import SHP"
    $preview = Call 'import_gis' "{`"filePath`":$(Json $shp),`"preview`":true}" -Write
    if ($preview) {
        Warnings $preview
        Check 'aperçu : une couche' @($preview.inputLayers).Count 1
        Check 'couche nommée sans schéma FDO' $preview.inputLayers[0].inputLayer ([IO.Path]::GetFileNameWithoutExtension($shp))
        Write-Host "        couche $($preview.inputLayers[0].inputLayer), système $($preview.inputLayers[0].sourceCoordinateSystem), colonnes $(($preview.inputLayers[0].columns | ForEach-Object { $_.name }) -join ',')"
    }
    $imp = Call 'import_gis' "{`"filePath`":$(Json $shp),`"layer`":`"MCP_GIS_IMPORT`",`"odTable`":`"$($table)_IMP`"}" -Write
    if ($imp) {
        Warnings $imp
        Check 'objets importés' $imp.count 2
        Check 'calque' (($imp.byLayer.PSObject.Properties | ForEach-Object { $_.Name }) -join ',') 'MCP_GIS_IMPORT'
        Check 'type' (($imp.byType.PSObject.Properties | ForEach-Object { $_.Name }) -join ',') 'LWPOLYLINE'
        Near 'étendue X min' $imp.extents.min[0] $x0 0.001
        Near 'étendue Y max' $imp.extents.max[1] ($y0 + 10) 0.001
        $od = Call 'get_od_records' "{`"handles`":[$(($imp.handles | ForEach-Object { Json $_ }) -join ',')],`"tables`":[`"$($table)_IMP`"]}"
        $names = @($od.objects | ForEach-Object { $_.records[0].values.NOM }) | Sort-Object
        Check 'attribut NOM réimporté' ($names -join ',') 'Carré,Rectangle'
        $surf = @($od.objects | ForEach-Object { $_.records[0].values.SURF })
        Check 'attribut SURF réimporté' ($surf -join ',') '100,100'
    }
    $imp2 = Call 'import_gis' "{`"filePath`":$(Json $shp),`"layer`":`"MCP_GIS_IMPORT`",`"odTable`":`"$($table)_IMP`"}" -Write
    if ($imp2) { Check 'table OD existante : nom suffixé' $imp2.inputLayers[0].odTable "$($table)_IMP_1" }
    $bare = Call 'import_gis' "{`"filePath`":$(Json $shp),`"layer`":`"MCP_GIS_IMPORT`",`"attributes`":`"none`"}" -Write
    if ($bare) {
        Check 'sans attributs : pas de table' "$($bare.count)/$($bare.inputLayers[0].odTable)" '2/'
        $none = Call 'get_od_records' "{`"handles`":[$(($bare.handles | ForEach-Object { Json $_ }) -join ',')]}"
        if ($none) { Check 'sans attributs : aucun enregistrement' $none.recordCount 0 }
    }
    Expect-Error 'fichier absent' 'import_gis' "{`"filePath`":$(Json (Join-Path $folder 'absent.shp'))}" '*n''existe pas*' -Write
    Expect-Error 'aucune couche retenue' 'import_gis' "{`"filePath`":$(Json $shp),`"inputLayers`":[`"zzz*`"]}" '*Aucune couche*' -Write
    Expect-Error 'format non pris en charge refusé avant Map 3D' 'import_gis' "{`"filePath`":$(Json $shp),`"format`":`"EDIGEO`",`"preview`":true}" '*non pris en charge*' -Write

    Write-Host "Autres formats (export puis réimport des 2 polygones)"
    foreach ($ext in @('mif', 'tab', 'gml', 'sdf')) {
        $file = Join-Path $folder "carres.$ext"
        $r = Send 'export_gis' "{`"filePath`":$(Json $file),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"polygon`"}"
        if (-not $r.ok) { Write-Host "  ÉCHEC export $ext : $($r.error.message)" -ForegroundColor Red; $script:failures++; continue }
        Warnings $r.result
        $script:writes++
        $back = Send 'import_gis' "{`"filePath`":$(Json $file),`"layer`":`"MCP_GIS_$($ext.ToUpper())`"}"
        if (-not $back.ok) { Write-Host "  ÉCHEC import $ext : $($back.error.message)" -ForegroundColor Red; $script:failures++; continue }
        Warnings $back.result
        Check "$ext : exportés/importés" "$($r.result.entitiesExported)/$($back.result.count)" '2/2'
    }

    if ($lambert) {
        Write-Host "Conversion de système de coordonnées (export en LL84, réimport en $code)"
        $ll = Join-Path $folder 'carres_ll84.shp'
        $conv = Call 'export_gis' "{`"filePath`":$(Json $ll),`"layers`":[`"MCP_GIS_TEST`"],`"geometry`":`"polygon`",`"targetCoordinateSystem`":`"LL84`"}"
        if ($conv) { Warnings $conv; Check 'export converti' $conv.coordinateSystem "$code -> LL84" }
        $pre = Call 'import_gis' "{`"filePath`":$(Json $ll),`"preview`":true}" -Write
        if ($pre) { Check 'système lu dans le .prj' ($pre.inputLayers[0].sourceCoordinateSystem -ne $null -and $pre.inputLayers[0].sourceCoordinateSystem -ne '') 'True'; Write-Host "        $($pre.inputLayers[0].sourceCoordinateSystem) -> $($pre.inputLayers[0].conversion)" }
        $round = Call 'import_gis' "{`"filePath`":$(Json $ll),`"layer`":`"MCP_GIS_LL84`",`"attributes`":`"none`"}" -Write
        if ($round) {
            Warnings $round
            Near 'aller-retour LL84 : X min' $round.extents.min[0] $x0 0.01
            Near 'aller-retour LL84 : Y min' $round.extents.min[1] $y0 0.01
        }
    } else {
        Write-Host "  AVERT conversion de système non testée : attribuez Lambert93 au dessin de test pour la couvrir" -ForegroundColor Yellow
    }

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800 }
    $final = Call 'get_drawing_info'
    Check 'objets de l''espace objet' $final.modelSpaceEntityCount $start.modelSpaceEntityCount
    $finalTables = @((Call 'list_od_tables' '{"includeFields":false}').tables | ForEach-Object { $_.name })
    Check 'tables de données d''objet' ($finalTables -join ',') ($startTables -join ',')
}
finally {
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
