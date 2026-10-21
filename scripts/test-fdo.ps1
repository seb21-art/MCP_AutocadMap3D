<#
.SYNOPSIS
    Non-régression des connexions FDO : fournisseurs, SDF, WMS (cadastre de l'IGN, Internet requis), refus, déconnexion.
.DESCRIPTION
    Ce script ne crée pas d'objets AutoCAD. Il enregistre des sources FDO dans le dépôt du dessin (Library://MCP_…),
    puis les retire. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike.
#>
param([string]$DrawingNameLike = '*test*')

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:failures = 0

function Call([string]$method, [string]$paramsJson = '{}') {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $response = $reader.ReadLine() | ConvertFrom-Json
    if (-not $response.ok) { Write-Host "  ÉCHEC $method : [$($response.error.code)] $($response.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $response.result
}

function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*') {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $response = $reader.ReadLine() | ConvertFrom-Json
    if ($response.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($response.error.message -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($response.error.message) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($response.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : « $actual »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

try {
    $info = Call 'get_drawing_info'
    if ($info.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $startEntities = $info.modelSpaceEntityCount
    # Map 3D donne au dessin sans système celui du premier calque FDO ajouté, et ce n'est pas annulé par U :
    # le système de départ est rétabli à la fin.
    function Cs { $s = Call 'get_coordinate_system'; if ($s.assigned) { "$($s.system.code)" } else { '' } }
    $startCs = Cs
    "Départ : $startEntities objets, système « $startCs »"

    Write-Host "Fournisseurs"
    $providers = Call 'list_fdo_providers'
    if ($providers.count -gt 0) { Write-Host "  OK    $($providers.count) fournisseur(s)" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC aucun fournisseur FDO" -ForegroundColor Red; $script:failures++ }
    $sdf = @($providers.providers | Where-Object { $_.name -eq 'OSGeo.SDF' })
    Check 'SDF présent' ($sdf.Count -gt 0) 'True'

    Write-Host "Carte"
    $layers = Call 'list_fdo_layers'
    Write-Host "  OK    $($layers.count) calque(s) FDO" -ForegroundColor Green
    $connections = Call 'list_fdo_connections'
    Write-Host "  OK    $($connections.count) connexion(s)" -ForegroundColor Green
    $selection = Call 'get_fdo_selection'
    if ($null -ne $selection) { Write-Host "  OK    sélection : $($selection.count) objet(s)" -ForegroundColor Green }

    Write-Host "Refus"
    Expect-Error 'chaîne vide' 'connect_fdo' '{"provider":"OSGeo.SDF"}'
    Expect-Error 'fichier absent' 'connect_fdo' '{"provider":"OSGeo.SDF","connectionString":"File=C:\\\\Windows\\\\Temp\\\\mcp-fdo-absent.sdf"}'
    Expect-Error 'source inconnue' 'describe_fdo' '{"resource":"MCP_FDO_ABSENT"}'
    Expect-Error 'calque inconnu' 'add_fdo_layer' '{"resource":"MCP_FDO_ABSENT","className":"Default:Aucune"}'
    Expect-Error 'requête sans cible' 'query_fdo' '{}'
    Expect-Error 'déconnexion inconnue' 'disconnect_fdo' '{"resource":"MCP_FDO_ABSENT"}'

    if ($sdf.Count -gt 0) {
        Write-Host "SDF"
        $folder = Join-Path $PSScriptRoot '..\artifacts\test'
        New-Item -ItemType Directory -Force $folder | Out-Null
        $file = Join-Path $folder "mcp-fdo-$(Get-Random).sdf"
        $escaped = $file.Replace('\', '\\')
        $created = Call 'connect_fdo' "{`"provider`":`"OSGeo.SDF`",`"connectionString`":`"File=$escaped`",`"createFile`":true,`"name`":`"McpFdo`"}"
        if ($null -ne $created) {
            Check 'fichier créé' (Test-Path $file) 'True'
            Check 'nom de ressource' ($created.resource -like '*McpFdo.FeatureSource') 'True'
            $described = Call 'describe_fdo' "{`"resource`":`"$($created.resource)`"}"
            if ($null -ne $described) { Check 'décrite' ($described.resource -eq $created.resource) 'True' }

            Write-Host "Pas d'écrasement silencieux"
            $again = "{`"provider`":`"OSGeo.SDF`",`"connectionString`":`"File=$escaped`",`"name`":`"McpFdo`""
            Expect-Error 'nom déjà pris' 'connect_fdo' "$again}" '*existe déjà*'
            $replaced = Call 'connect_fdo' "$again,`"replace`":true}"
            if ($null -ne $replaced) { Check 'remplacée sur demande' $replaced.replaced 'True' }

            $layer = Call 'add_fdo_layer' "{`"resource`":`"McpFdo`",`"className`":`"Default:Empty`",`"name`":`"McpFdo`"}"
            if ($null -ne $layer) { Check 'calque' $layer.layer.name 'McpFdo' }
            Expect-Error 'remplacement refusé tant qu''un calque l''affiche' 'connect_fdo' "$again,`"replace`":true}" '*affichée par le calque*'
            # « Mcp Fdo » et « Mcp_Fdo » donnent le même nom sûr : chacun doit garder sa propre définition.
            $spaced = Call 'add_fdo_layer' '{"resource":"McpFdo","className":"Default:Empty","name":"Mcp Fdo"}'
            $underscored = Call 'add_fdo_layer' '{"resource":"McpFdo","className":"Default:Empty","name":"Mcp_Fdo"}'
            if ($null -ne $spaced -and $null -ne $underscored) {
                $names = @((Call 'list_fdo_layers' '{"names":["Mcp*"]}').layers | ForEach-Object { $_.name })
                Check 'trois calques distincts' (($names | Sort-Object) -join ',') 'Mcp Fdo,Mcp_Fdo,McpFdo'
                $bothRead = (Call 'query_fdo' '{"layer":"Mcp Fdo","limit":5}') -and (Call 'query_fdo' '{"layer":"Mcp_Fdo","limit":5}')
                Check 'les deux calques de même nom sûr restent lisibles' ([bool]$bothRead) 'True'
            }

            $queried = Call 'query_fdo' '{"layer":"McpFdo","limit":5}'
            if ($null -ne $queried) { Check 'lecture' $queried.count 0 }
            $hidden = Call 'set_fdo_layer' '{"name":"McpFdo","visible":false}'
            if ($null -ne $hidden) { Check 'masqué' $hidden.visible 'False' }
            # Calque vide : Map 3D peut refuser de le cadrer, l'outil ne doit pas échouer pour autant.
            $zoomEmpty = Call 'set_fdo_layer' '{"name":"McpFdo","zoom":true}'
            if ($null -ne $zoomEmpty) { Check 'cadrage d''un calque vide sans échec' ($zoomEmpty.zoom.method -in 'layer', 'spatialContext', 'none') 'True' }

            Write-Host "Sources protégées"
            Expect-Error 'source extérieure au connecteur refusée' 'disconnect_fdo' '{"resource":"Library://Externe/Autre.FeatureSource"}' '*pas été créée par connect_fdo*'
            $first = Call 'remove_fdo_layer' '{"name":"Mcp Fdo","disconnect":true}'
            if ($null -ne $first) {
                Check 'source gardée tant qu''un autre calque l''affiche' ($first.sourceKept -like '*encore affichée*') 'True'
                Check 'rien de déconnecté' "$($first.disconnected)" ''
            }
            Call 'remove_fdo_layer' '{"name":"Mcp_Fdo"}' | Out-Null
            $removed = Call 'remove_fdo_layer' '{"name":"McpFdo","disconnect":true}'
            if ($null -ne $removed) { Check 'retirée avec le dernier calque' $removed.disconnected ($created.resource) }
            else {
                $gone = Call 'disconnect_fdo' '{"resource":"McpFdo"}'
                if ($null -ne $gone) { Write-Host "  OK    déconnectée" -ForegroundColor Green }
            }
            $left = @((Call 'list_fdo_connections').connections | Where-Object { $_.resource -eq $created.resource })
            Check 'plus de source de test' $left.Count 0
        }
        if (Test-Path $file) { Remove-Item $file -Force }
    }

    if ($sdf.Count -gt 0) {
        Write-Host "Écriture dans un SDF et style"
        $folder = Join-Path $PSScriptRoot '..\artifacts\test'
        $file = Join-Path $folder "mcp-vannes-$(Get-Random).sdf"
        $escaped = $file.Replace('\', '\\')
        $made = Call 'connect_fdo' "{`"provider`":`"OSGeo.SDF`",`"connectionString`":`"File=$escaped`",`"createFile`":true,`"name`":`"McpVannes`",`"className`":`"VANNES`",`"geometry`":`"point`",`"fields`":[{`"name`":`"NOM`"},{`"name`":`"DIAMETRE`",`"type`":`"int`"}]}"
        if ($null -ne $made) {
            Check 'classe créée' ($made.classes -contains 'Default:VANNES') 'True'
            Call 'add_fdo_layer' '{"resource":"McpVannes","className":"VANNES","name":"McpVannes"}' | Out-Null
            $point = Call 'create_point' '{"points":[[850020,6550010]]}'
            $features = "[{`"properties`":{`"NOM`":`"V1`",`"DIAMETRE`":100},`"wkt`":`"POINT (850000 6550000)`"}," +
                "{`"properties`":{`"NOM`":`"V2`",`"DIAMETRE`":150},`"handle`":`"$($point.handles[0])`"}," +
                "{`"properties`":{`"NOM`":`"V3`",`"DIAMETRE`":100},`"wkt`":`"POINT (850040 6550000)`"}]"
            $inserted = Call 'insert_fdo_features' "{`"layer`":`"McpVannes`",`"features`":$features}"
            if ($null -ne $inserted) { Check 'objets ajoutés' $inserted.inserted 3 }
            Check 'lus dans la source' (Call 'query_fdo' '{"layer":"McpVannes"}').count 3
            $updated = Call 'update_fdo_features' '{"layer":"McpVannes","filter":"NOM = ''V1''","properties":{"DIAMETRE":200}}'
            if ($null -ne $updated) { Check 'objet modifié' $updated.updated 1 }
            $deleted = Call 'delete_fdo_features' '{"layer":"McpVannes","filter":"NOM = ''V3''"}'
            if ($null -ne $deleted) { Check 'objet supprimé' $deleted.deleted 1 }
            $left = (Call 'query_fdo' '{"layer":"McpVannes"}').features
            Check 'valeurs relues' (($left | ForEach-Object { "$($_.NOM)=$($_.DIAMETRE)" }) -join ',') 'V1=200,V2=150'
            Expect-Error 'suppression sans filtre refusée' 'delete_fdo_features' '{"layer":"McpVannes"}' '*filter*'
            Expect-Error 'propriété inconnue refusée' 'update_fdo_features' '{"layer":"McpVannes","filter":"NOM = ''V1''","properties":{"COULEUR":"rouge"}}' '*inconnue*'
            Expect-Error 'filtre sans résultat' 'delete_fdo_features' '{"layer":"McpVannes","filter":"NOM = ''ZZZ''"}' '*Aucun objet*'

            Call 'insert_fdo_features' '{"layer":"McpVannes","features":[{"properties":{"NOM":"V9"},"wkt":"POINT (850060 6550000)"}]}' | Out-Null
            $undo = Call 'undo' '{"count":1}'
            foreach ($i in 1..100) { if ((Call '_undo_progress').inProgress -eq 0) { break }; Start-Sleep -Milliseconds 100 }
            Check 'ajout annulé par undo (U)' (Call 'query_fdo' '{"layer":"McpVannes"}').count 2

            $styled = Call 'set_fdo_layer_style' '{"name":"McpVannes","themeProperty":"DIAMETRE","labelProperty":"NOM","pointSize":10}'
            if ($null -ne $styled) {
                Check 'thème par valeur : 2 valeurs + Autres' $styled.rules.Count 3
                Check 'étiquettes' $styled.labels 'NOM'
            }
            $ranges = Call 'set_fdo_layer_style' '{"name":"McpVannes","themeProperty":"DIAMETRE","themeRanges":[{"max":160,"color":"vert"},{"min":160,"color":"#FF0000","label":"gros"}]}'
            if ($null -ne $ranges) { Check 'thème par plages' (($ranges.rules | ForEach-Object { $_.label }) -join ',') '… à 160,gros,Autres' }
            Check 'calque toujours affiché après le style' (Call 'list_fdo_layers' '{"names":["McpVannes"]}').count 1
            Expect-Error 'thème sur une propriété inconnue' 'set_fdo_layer_style' '{"name":"McpVannes","themeProperty":"ZZZ"}' '*inconnue*'

            Call 'erase_entities' "{`"handles`":[`"$($point.handles[0])`"]}" | Out-Null
            $gone = Call 'remove_fdo_layer' '{"name":"McpVannes","disconnect":true}'
            if ($null -ne $gone) { Check 'source retirée avec le calque' $gone.disconnected $made.resource }
        }
        if (Test-Path $file) { Remove-Item $file -Force }
    }

    if (@($providers.providers | Where-Object { $_.name -eq 'Autodesk.Raster' }).Count -gt 0) {
        Write-Host "MNT raster (ASC) : surface ombrée et teintée"
        # Plan incliné de 40 x 30 mailles de 10 m : altitudes de 100 à 100 + 39 x 0,5 + 29 = 148,5 m.
        $file = Join-Path (Join-Path $PSScriptRoot '..\artifacts\test') "mcp-mnt-$(Get-Random).asc"
        $rows = foreach ($j in 29..0) { ((0..39 | ForEach-Object { (100 + $_ * 0.5 + $j).ToString([cultureinfo]::InvariantCulture) }) -join ' ') }
        [IO.File]::WriteAllLines($file, @('ncols 40', 'nrows 30', 'xllcorner 850000', 'yllcorner 6550000', 'cellsize 10', 'NODATA_value -9999') + $rows)
        $escaped = (Resolve-Path $file).Path.Replace('\', '\\')
        $mnt = Call 'connect_fdo' "{`"provider`":`"Autodesk.Raster`",`"connectionString`":`"DefaultRasterFileLocation=$escaped`",`"name`":`"McpMnt`"}"
        if ($null -ne $mnt) {
            $surface = Call 'add_fdo_layer' "{`"resource`":`"McpMnt`",`"className`":`"$($mnt.classes[0])`",`"name`":`"McpMnt`"}"
            if ($null -ne $surface) {
                Check 'MNT en calque raster' "$($surface.layer.vector)" 'False'
                Check 'MNT reconnu comme surface : 8 plages' $surface.surface.classes 8
                Check 'altitudes lues dans le fichier ASC' "$($surface.surface.minZ)-$($surface.surface.maxZ)" '100-148.5'
                $xml = ((Call '_fdo_diagnostic').layers | Where-Object name -eq 'McpMnt').layerDefinitionXml
                Check 'définition de surface avec ombrage' ($xml -match '<SurfaceStyle>' -and $xml -match '<HillShade>') 'True'
                Check 'règles de couleur sur Height(1) (8 plages + défaut)' ([regex]::Matches($xml, '<ColorRule>').Count) 9
            }
            $plain = Call 'add_fdo_layer' "{`"resource`":`"McpMnt`",`"className`":`"$($mnt.classes[0])`",`"name`":`"McpMnt2`",`"colorClasses`":3,`"elevationRange`":[0,300],`"hillshade`":false}"
            if ($null -ne $plain) {
                Check 'teinte imposée : 3 plages de 0 à 300' "$($plain.surface.classes):$($plain.surface.minZ)-$($plain.surface.maxZ):$($plain.surface.rangeSource)" '3:0-300:elevationRange'
                $xml2 = ((Call '_fdo_diagnostic').layers | Where-Object name -eq 'McpMnt2').layerDefinitionXml
                Check 'sans ombrage sur demande' ($xml2 -match '<HillShade>') 'False'
                Call 'remove_fdo_layer' '{"name":"McpMnt2"}' | Out-Null
            }
            $gone = Call 'remove_fdo_layer' '{"name":"McpMnt","disconnect":true}'
            if ($null -ne $gone) { Check 'source du MNT retirée avec le calque' $gone.disconnected $mnt.resource }
        }
        if (Test-Path $file) { Remove-Item $file -Force }
    }

    $wms = @($providers.providers | Where-Object { $_.name -eq 'OSGeo.WMS' })
    $cs = (Call 'get_coordinate_system').assigned
    if ($wms.Count -gt 0 -and $cs) {
        Write-Host "WMS (cadastre de l'IGN)"
        $source = Call 'connect_fdo' '{"provider":"OSGeo.WMS","connectionString":"FeatureServer=https://data.geopf.fr/wms-r/wms","name":"McpWms"}'
        if ($null -ne $source) {
            Check 'source à la racine du dépôt, visible dans MAPCONNECT' $source.resource 'Library://MCP_McpWms.FeatureSource'
            $raster = Call 'add_fdo_layer' '{"resource":"McpWms","className":"WMS_Schema:CADASTRALPARCELS PARCELLAIRE_EXPRESS","name":"McpWms cadastre"}'
            if ($null -ne $raster) {
                Check 'calque raster (pas vectoriel)' "$($raster.layer.vector)" 'False'
                Check 'source dédiée à la couche' ($raster.dedicatedSource -like 'Library://MCP_McpWms_cadastre_WMS*.FeatureSource') 'True'
                Check 'classe de la source dédiée' $raster.layer.className 'WMSLayers:CADASTRALPARCELS PARCELLAIRE_EXPRESS'
                Check 'aucun avertissement de Map 3D' "$($raster.warning)" ''
                Expect-Error 'écriture dans un WMS refusée' 'insert_fdo_features' '{"layer":"McpWms cadastre","features":[{"wkt":"POINT (0 0)"}]}' '*WMS*'
                Expect-Error 'style d''un WMS refusé' 'set_fdo_layer_style' '{"name":"McpWms cadastre","color":"rouge"}' '*vectoriel*'
                # Sur un calque raster, ZoomToLayer de Map 3D échoue ou cadre le monde entier : le cadrage passe par
                # l'étendue de la source ou, à défaut (étendue nulle de la source dédiée), par le domaine du système du dessin.
                $zoomed = Call 'set_fdo_layer' '{"name":"McpWms cadastre","zoom":true}'
                if ($null -ne $zoomed) {
                    Check 'calque toujours présent après le cadrage' $zoomed.name 'McpWms cadastre'
                    if ($zoomed.zoom.method -in 'spatialContext', 'coordinateSystemDomain') {
                        Write-Host "  OK    WMS cadré ($($zoomed.zoom.method)) : $($zoomed.zoom.window -join ', ')" -ForegroundColor Green
                        Write-Host "        $($zoomed.zoom.note)"
                        if ((Call 'get_coordinate_system').system.code -eq 'Lambert93') {
                            $centerY = ($zoomed.zoom.window[1] + $zoomed.zoom.window[3]) / 2
                            Check 'cadrage WMS limité à la France (Y du centre en Lambert-93)' ($centerY -gt 6000000 -and $centerY -lt 7300000) 'True'
                        }
                    } else {
                        Write-Host "  AVERT WMS cadré par $($zoomed.zoom.method) : $($zoomed.zoom.note)" -ForegroundColor Yellow
                    }
                }
                $gone = Call 'remove_fdo_layer' '{"name":"McpWms cadastre","disconnect":true}'
                if ($null -ne $gone) { Check 'source dédiée retirée avec le calque' $gone.disconnected $raster.dedicatedSource }
            }
            Call 'disconnect_fdo' '{"resource":"McpWms"}' | Out-Null
            $leftWms = @((Call 'list_fdo_connections').connections | Where-Object { $_.resource -like 'Library://MCP_McpWms*' })
            Check 'plus de source WMS de test' $leftWms.Count 0
        }
    } elseif ($wms.Count -gt 0) {
        Write-Host "  AVERT WMS non testé : le dessin n'a pas de système de coordonnées" -ForegroundColor Yellow
    }

    $end = (Call 'get_drawing_info').modelSpaceEntityCount
    Check 'objets inchangés' $end $startEntities
    if ((Cs) -ne $startCs) {
        Call 'set_coordinate_system' "{`"code`":`"$startCs`",`"force`":true}" | Out-Null
    }
    Check 'système de départ rétabli' "$(Cs)" "$startCs"
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
