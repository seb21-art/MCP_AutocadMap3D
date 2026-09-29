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
    "Départ : $startEntities objets"

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
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
