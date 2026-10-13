<#
.SYNOPSIS
    Scénario de non-régression des outils : lecture, édition, données d'objet, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert dans AutoCAD, puis annule chacune de ses modifications avec U.
    Par sécurité, il refuse de s'exécuter si le nom du dessin ouvert ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
#>
param(
    [string]$DrawingNameLike = '*test*',
    [switch]$KeepChanges
)

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8)
$writer.NewLine = "`n"
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$requestId = 0
$failures = 0
$writes = 0
# Nom unique : un calque déjà identique ne produirait aucune modification, donc aucune annulation visible.
$testLayer = "MCP_AUTOTEST_$(Get-Date -Format HHmmss)"

function Invoke-Tool([string]$method, [string]$paramsJson = '{}') {
    $script:requestId++
    $writer.WriteLine("{`"id`":`"$script:requestId`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":20000}")
    $reader.ReadLine() | ConvertFrom-Json
}

function Test-Tool([string]$label, [string]$method, [string]$paramsJson, [scriptblock]$check) {
    $response = Invoke-Tool $method $paramsJson
    if (-not $response.ok) {
        Write-Host "  ÉCHEC $label : [$($response.error.code)] $($response.error.message)" -ForegroundColor Red
        $script:failures++
        return $null
    }

    if ($check -and -not (& $check $response.result)) {
        Write-Host "  ÉCHEC $label : résultat inattendu" -ForegroundColor Red
        $script:failures++
        return $response.result
    }

    Write-Host "  OK    $label" -ForegroundColor Green
    return $response.result
}

function Test-Error([string]$label, [string]$method, [string]$paramsJson, [string]$expectedCode, [switch]$IsWrite) {
    # Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
    if ($IsWrite) { $script:writes++ }
    $response = Invoke-Tool $method $paramsJson
    if ($response.ok -or $response.error.code -ne $expectedCode) {
        Write-Host "  ÉCHEC $label : $expectedCode attendu" -ForegroundColor Red
        $script:failures++
    } else {
        Write-Host "  OK    $label -> $($response.error.code)" -ForegroundColor Green
    }
}

try {
    $info = (Invoke-Tool 'get_drawing_info').result
    if ($info.fileName -notlike $DrawingNameLike) {
        throw ("Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike ». " +
            "Ouvrez une copie de test, ou passez -DrawingNameLike.")
    }

    Write-Host "Dessin : $($info.fileName) ($($info.modelSpaceEntityCount) objets, $($info.layerCount) calques)"
    $initialLinetypes = (Invoke-Tool 'list_linetypes').result.loaded.Count

    Write-Host "Lecture"
    Test-Tool 'get_drawing_info' 'get_drawing_info' '{}' { param($r) $r.fileName } | Out-Null
    Test-Tool 'list_layers' 'list_layers' '{}' { param($r) $r.count -gt 0 } | Out-Null
    Test-Tool 'list_entities (pagination)' 'list_entities' '{"limit":5}' { param($r) $r.count -le 5 -and $r.total -ge $r.count } | Out-Null
    Test-Tool 'list_entities (filtre)' 'list_entities' '{"types":["LWPOLYLINE"],"limit":1}' { param($r) $r.count -eq 0 -or $r.entities[0].type -eq 'LWPOLYLINE' } | Out-Null

    Write-Host "Édition"
    Test-Tool 'create_layer' 'create_layer' "{`"name`":`"$testLayer`",`"color`":`"5`"}" { param($r) $r.name -eq $testLayer } | Out-Null; $writes++
    $line = Test-Tool 'create_line' 'create_line' "{`"start`":[0,0],`"end`":[10,10],`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    $polyline = Test-Tool 'create_polyline' 'create_polyline' "{`"points`":[[0,0],[5,0],[5,5]],`"closed`":true,`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    $text = Test-Tool 'create_text' 'create_text' "{`"text`":`"Contrôle é à`",`"position`":[1,1],`"height`":2,`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    Test-Tool 'move_entities' 'move_entities' "{`"handles`":[`"$($line.handle)`"],`"displacement`":[5,5]}" { param($r) $r.moved -eq 1 } | Out-Null; $writes++
    Test-Tool 'change_layer' 'change_layer' "{`"handles`":[`"$($polyline.handle)`"],`"layer`":`"0`"}" { param($r) $r.changed -eq 1 } | Out-Null; $writes++
    Test-Tool 'erase_entities' 'erase_entities' "{`"handles`":[`"$($text.handle)`"]}" { param($r) $r.erased -eq 1 } | Out-Null; $writes++

    Write-Host "Propriétés graphiques"
    $linetypes = Test-Tool 'list_linetypes' 'list_linetypes' '{"includeAvailable":true}' { param($r) $r.loaded.Count -gt 0 }
    # Premier type du fichier .lin non encore chargé : le nom dépend de la langue d'AutoCAD.
    $linetype = ($linetypes.available | Select-Object -First 1).name
    $styled = Test-Tool "create_line (rouge, $linetype, 0.35 mm)" 'create_line' "{`"start`":[0,20],`"end`":[20,20],`"color`":`"rouge`",`"linetype`":`"$linetype`",`"lineWeight`":`"0.35`"}" { param($r) $r.handle }; $writes++
    Test-Tool 'set_entity_properties' 'set_entity_properties' "{`"handles`":[`"$($styled.handle)`"],`"lineWeight`":0.5,`"linetypeScale`":2,`"thickness`":3}" { param($r) $r.changed -eq 1 } | Out-Null; $writes++
    Test-Tool 'list_entities (propriétés relues)' 'list_entities' "{`"handles`":[`"$($styled.handle)`"]}" {
        param($r)
        $e = $r.entities | Where-Object { $_.handle -eq $styled.handle }
        $e.color -eq '1' -and $e.linetype -eq $linetype -and $e.lineWeight -eq '0.50 mm' -and $e.linetypeScale -eq 2 -and $e.thickness -eq 3
    } | Out-Null
    Test-Error 'épaisseur de ligne non normalisée' 'set_entity_properties' "{`"handles`":[`"$($styled.handle)`"],`"lineWeight`":0.33}" 'invalid_params' -IsWrite

    Write-Host "Données d'objet"
    $tables = Test-Tool 'list_od_tables' 'list_od_tables' '{}' { param($r) $null -ne $r.count }
    $table = $tables.tables | Select-Object -First 1
    if (-not $table) {
        # Aucune table dans ce dessin : on en crée une pour le test (méthode de diagnostic du pipe).
        Invoke-Tool '_create_od_table' '{"name":"MCP_AUTOTEST_OD","fields":[{"name":"NOM","type":"Character"},{"name":"VALEUR","type":"Real"}]}' | Out-Null
        $writes++
        $tables = Test-Tool 'list_od_tables (après création)' 'list_od_tables' '{}' { param($r) $r.count -gt 0 }
        $table = $tables.tables | Select-Object -First 1
    }

    if ($table) {
        $field = $table.fields | Where-Object { $_.type -eq 'Character' } | Select-Object -First 1
        Test-Tool 'get_od_records' 'get_od_records' "{`"handles`":[`"$($line.handle)`"]}" { param($r) $null -ne $r.recordCount } | Out-Null
        if ($field) {
            Test-Tool 'set_od_value' 'set_od_value' "{`"handles`":[`"$($line.handle)`"],`"table`":`"$($table.name)`",`"field`":`"$($field.name)`",`"value`":`"autotest`"}" { param($r) $r.updated + $r.created -eq 1 } | Out-Null; $writes++
            Test-Tool 'get_od_records (relecture)' 'get_od_records' "{`"handles`":[`"$($line.handle)`"]}" { param($r) $r.objects[0].records[0].values.$($field.name) -eq 'autotest' } | Out-Null
        }
    }

    Write-Host "Tables de données d'objet, filtres et métrés"
    $res = 'MCP_AUTOTEST_RES'
    $fields = '[{"name":"DIAMETRE","type":"Integer","default":0},{"name":"MATERIAU","type":"Character"},{"name":"PRIX","type":"Real","description":"Prix au mètre"}]'
    Test-Tool 'create_od_table' 'create_od_table' "{`"name`":`"$res`",`"description`":`"Réseau de test`",`"fields`":$fields}" { param($r) $r.created -and $r.fields.Count -eq 3 } | Out-Null; $writes++
    Test-Error 'create_od_table : table déjà existante' 'create_od_table' "{`"name`":`"$res`",`"fields`":$fields}" 'invalid_params' -IsWrite
    $p1 = Test-Tool 'conduite de 10' 'create_polyline' "{`"points`":[[100,0],[110,0]],`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    $p2 = Test-Tool 'conduite de 20' 'create_polyline' "{`"points`":[[100,10],[120,10]],`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    $p3 = Test-Tool 'conduite de 30' 'create_polyline' "{`"points`":[[100,20],[130,20]],`"layer`":`"$testLayer`"}" { param($r) $r.handle }; $writes++
    $pvc = "[`"$($p1.handle)`",`"$($p2.handle)`"]"
    $all = "[`"$($p1.handle)`",`"$($p2.handle)`",`"$($p3.handle)`"]"
    Test-Tool 'DIAMETRE 100' 'set_od_value' "{`"handles`":$pvc,`"table`":`"$res`",`"field`":`"DIAMETRE`",`"value`":100}" { param($r) $r.created -eq 2 } | Out-Null; $writes++
    Test-Tool 'DIAMETRE 150' 'set_od_value' "{`"handles`":[`"$($p3.handle)`"],`"table`":`"$res`",`"field`":`"DIAMETRE`",`"value`":150}" { param($r) $r.created -eq 1 } | Out-Null; $writes++
    Test-Tool 'MATERIAU PVC' 'set_od_value' "{`"handles`":$pvc,`"table`":`"$res`",`"field`":`"MATERIAU`",`"value`":`"PVC`"}" { param($r) $r.updated -eq 2 } | Out-Null; $writes++
    Test-Tool 'MATERIAU FONTE' 'set_od_value' "{`"handles`":[`"$($p3.handle)`"],`"table`":`"$res`",`"field`":`"MATERIAU`",`"value`":`"FONTE`"}" { param($r) $r.updated -eq 1 } | Out-Null; $writes++
    Test-Tool 'PRIX 12,5' 'set_od_value' "{`"handles`":$all,`"table`":`"$res`",`"field`":`"PRIX`",`"value`":12.5}" { param($r) $r.updated -eq 3 } | Out-Null; $writes++

    $scope = "`"layers`":[`"$testLayer`"],`"types`":[`"LWPOLYLINE`"]"
    $summary = Test-Tool 'summarize_entities (par diamètre)' 'summarize_entities' "{$scope,`"groupBy`":[`"od:$res.DIAMETRE`"],`"sum`":[`"od:$res.PRIX`"]}" {
        param($r)
        $g100 = $r.groups | Where-Object { $_.key."od:$res.DIAMETRE" -eq 100 }
        $g150 = $r.groups | Where-Object { $_.key."od:$res.DIAMETRE" -eq 150 }
        $r.count -eq 3 -and $r.length -eq 60 -and $r.groupCount -eq 2 -and
            $g100.count -eq 2 -and $g100.length -eq 30 -and $g100.sums."od:$res.PRIX" -eq 25 -and
            $g150.count -eq 1 -and $g150.length -eq 30
    }
    if ($summary) { Write-Host "        $($summary.groups | ForEach-Object { "D$($_.key."od:$res.DIAMETRE") : $($_.count) objet(s), $($_.length) m" })" }
    Test-Tool 'summarize_entities (par calque, sans OD)' 'summarize_entities' "{`"layers`":[`"$testLayer`"],`"groupBy`":[`"type`"]}" { param($r) ($r.groups | Where-Object { $_.key.type -eq 'LWPOLYLINE' }).count -eq 3 } | Out-Null
    Test-Tool 'list_entities where >= 120' 'list_entities' "{$scope,`"where`":[{`"field`":`"od:$res.DIAMETRE`",`"op`":`">=`",`"value`":120}]}" { param($r) $r.total -eq 1 -and $r.entities[0].handle -eq $p3.handle } | Out-Null
    Test-Tool 'list_entities where like' 'list_entities' "{$scope,`"where`":[{`"field`":`"od:$res.MATERIAU`",`"op`":`"like`",`"value`":`"pv*`"}]}" { param($r) $r.total -eq 2 } | Out-Null
    Test-Tool 'list_entities where = liste' 'list_entities' "{$scope,`"where`":[{`"field`":`"od:$res.DIAMETRE`",`"value`":[100,150]}]}" { param($r) $r.total -eq 3 } | Out-Null
    Test-Tool 'list_entities where length' 'list_entities' "{$scope,`"where`":[{`"field`":`"length`",`"op`":`"<`",`"value`":25}]}" { param($r) $r.total -eq 2 } | Out-Null
    Test-Tool 'list_entities where missing' 'list_entities' "{`"layers`":[`"$testLayer`"],`"where`":[{`"field`":`"od:$res.DIAMETRE`",`"op`":`"missing`"}]}" { param($r) $r.entities.handle -notcontains $p1.handle } | Out-Null
    Test-Error 'champ inconnu' 'summarize_entities' '{"groupBy":["couleur2"]}' 'invalid_params'
    Test-Error 'opérateur inconnu' 'list_entities' '{"where":[{"field":"layer","op":"~","value":"0"}]}' 'invalid_params'
    Test-Error 'handles et sélection ensemble' 'summarize_entities' "{`"handles`":[`"$($p1.handle)`"],`"selection`":true}" 'invalid_params'
    Test-Tool 'get_selection' 'get_selection' '{}' { param($r) $r.count -gt 0 -or $r.note } | Out-Null

    Test-Tool 'edit_od_table (champs)' 'edit_od_table' "{`"table`":`"$res`",`"addFields`":[{`"name`":`"ETAT`",`"default`":`"neuf`"}],`"removeFields`":[`"MATERIAU`"]}" {
        param($r) ($r.fields.name -join ',') -eq 'DIAMETRE,PRIX,ETAT'
    } | Out-Null; $writes++
    Test-Tool 'champ ajouté sur les enregistrements' 'get_od_records' "{`"handles`":[`"$($p1.handle)`"],`"tables`":[`"$res`"]}" {
        param($r) $v = $r.objects[0].records[0].values; $v.ETAT -eq 'neuf' -and $v.DIAMETRE -eq 100 -and $null -eq $v.MATERIAU
    } | Out-Null
    Test-Tool 'edit_od_table (renommage)' 'edit_od_table' "{`"table`":`"$res`",`"newName`":`"${res}2`"}" { param($r) $r.name -eq "${res}2" } | Out-Null; $writes++
    Test-Error 'delete_od_table sans force' 'delete_od_table' "{`"table`":`"${res}2`"}" 'invalid_params' -IsWrite
    Test-Tool 'delete_od_table (force)' 'delete_od_table' "{`"table`":`"${res}2`",`"force`":true}" { param($r) $r.objectsWithRecords -eq 3 } | Out-Null; $writes++
    Test-Tool 'table supprimée' 'list_od_tables' '{"includeFields":false}' { param($r) $r.tables.name -notcontains "${res}2" } | Out-Null

    Write-Host "Annulation par l'outil undo"
    $temp = Test-Tool 'objet à annuler' 'create_line' "{`"start`":[200,0],`"end`":[210,0],`"layer`":`"$testLayer`"}" { param($r) $r.handle }
    $undone = Test-Tool 'undo' 'undo' '{"count":1}' { param($r) $r.undone -eq 1 }
    # Comme le serveur MCP : attendre qu'AutoCAD ait fini d'exécuter les U.
    foreach ($i in 1..100) { if ((Invoke-Tool '_undo_progress').result.inProgress -eq 0) { break }; Start-Sleep -Milliseconds 100 }
    $gone = Invoke-Tool 'list_entities' "{`"handles`":[`"$($temp.handle)`"]}"
    if (-not $gone.ok) { Write-Host "  OK    objet annulé avant l'appel suivant" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC objet toujours présent après undo" -ForegroundColor Red; $failures++ }
    if ($undone -and $undone.remainingConnectorSteps -lt 20) {
        Test-Error 'undo au-delà des appels du connecteur' 'undo' "{`"count`":$($undone.remainingConnectorSteps + 1)}" 'invalid_params'
    }

    Write-Host "Système de coordonnées"
    $initialCs = (Invoke-Tool 'get_coordinate_system').result
    Test-Tool 'search_coordinate_systems (EPSG 2154)' 'search_coordinate_systems' '{"query":"2154","limit":3}' { param($r) $r.results[0].code -eq 'Lambert93' } | Out-Null
    Test-Tool 'search_coordinate_systems (mots-clés)' 'search_coordinate_systems' '{"query":"RGF93 CC46","limit":3}' { param($r) $r.results[0].code -eq 'RGF93.CC46' } | Out-Null
    Test-Tool 'set_coordinate_system (forcé)' 'set_coordinate_system' '{"code":"RGF93.CC46","force":true}' { param($r) $r.assigned -eq 'RGF93.CC46' } | Out-Null; $writes++
    Test-Tool 'get_coordinate_system' 'get_coordinate_system' '{}' { param($r) $r.system.code -eq 'RGF93.CC46' } | Out-Null
    Test-Error 'système inconnu' 'set_coordinate_system' '{"code":"PAS.UN.CODE"}' 'invalid_params' -IsWrite
    # Origines des projections : longitude 3°, latitude 46,5° -> Lambert-93 (700 000 ; 6 600 000) ;
    # longitude 3°, latitude 46° -> CC46 (1 700 000 ; 5 200 000).
    Test-Tool 'convert_coordinates lonlat -> EPSG:2154' 'convert_coordinates' '{"points":[[3,46.5]],"from":"lonlat","to":"EPSG:2154"}' {
        param($r) [math]::Abs($r.points[0][0] - 700000) -lt 0.01 -and [math]::Abs($r.points[0][1] - 6600000) -lt 0.01 -and $r.to.code -eq 'Lambert93'
    } | Out-Null
    Test-Tool 'convert_coordinates lonlat -> drawing (CC46)' 'convert_coordinates' '{"points":[[3,46,12.5]],"from":"lonlat","to":"drawing"}' {
        param($r) [math]::Abs($r.points[0][0] - 1700000) -lt 0.01 -and [math]::Abs($r.points[0][1] - 5200000) -lt 0.01 -and $r.points[0][2] -eq 12.5
    } | Out-Null
    $l93 = '[[850000,6550000],[700000,6480000]]'
    $cc46 = (Invoke-Tool 'convert_coordinates' "{`"points`":$l93,`"from`":`"Lambert93`",`"to`":`"RGF93.CC46`"}").result
    Test-Tool 'convert_coordinates aller-retour CC46' 'convert_coordinates' "{`"points`":[$(($cc46.points | ForEach-Object { '[' + (($_ | ForEach-Object { $_.ToString([cultureinfo]::InvariantCulture) }) -join ',') + ']' }) -join ',')],`"from`":`"RGF93.CC46`",`"to`":`"2154`"}" {
        param($r) [math]::Abs($r.points[0][0] - 850000) -lt 0.001 -and [math]::Abs($r.points[1][1] - 6480000) -lt 0.001 -and -not $r.warning
    } | Out-Null
    Test-Tool 'convert_coordinates : longitude et latitude inversées signalées' 'convert_coordinates' '{"points":[[46.5,3]],"from":"lonlat","to":"Lambert93"}' { param($r) $r.warning } | Out-Null
    Test-Error 'convert_coordinates : système inconnu' 'convert_coordinates' '{"points":[[0,0]],"from":"PAS.UN.CODE","to":"lonlat"}' 'invalid_params'

    Write-Host "Erreurs attendues"
    Test-Error 'méthode inconnue' 'nexiste_pas' '{}' 'unknown_method'
    Test-Error 'calque absent' 'create_line' '{"start":[0,0],"end":[1,1],"layer":"ZZ_INEXISTANT"}' 'invalid_params' -IsWrite
    Test-Error 'handle invalide' 'erase_entities' '{"handles":["ZZZZ"]}' 'invalid_params' -IsWrite
    Test-Error 'espace inconnu' 'list_entities' '{"space":"nimporte"}' 'invalid_params'

    if (-not $KeepChanges) {
        Write-Host "Annulation des $writes modification(s)"
        # Un U à la fois : enchaînés en rafale, certains sont absorbés quand l'annulation déclenche
        # une opération Map (table de données d'objet).
        foreach ($step in 1..$writes) {
            Invoke-Tool '_undo' '{"count":1}' | Out-Null
            Start-Sleep -Milliseconds 900
        }

        # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au
        # démarrage d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
        $extra = 0
        while ($true) {
            $final = (Invoke-Tool 'get_drawing_info').result
            $restored = $final.modelSpaceEntityCount -eq $info.modelSpaceEntityCount -and $final.layerCount -eq $info.layerCount
            $finalCs = (Invoke-Tool 'get_coordinate_system').result
            $restored = $restored -and ($finalCs.assigned -eq $initialCs.assigned) -and ($finalCs.system.code -eq $initialCs.system.code)
            $restored = $restored -and ((Invoke-Tool 'list_linetypes').result.loaded.Count -eq $initialLinetypes)
            if ($restored -or $extra -ge 3) { break }
            Invoke-Tool '_undo' '{"count":1}' | Out-Null
            Start-Sleep -Milliseconds 900
            $extra++
        }

        if ($restored -and $extra -gt 0) {
            Write-Host "  AVERT dessin revenu à son état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur (par exemple COVANEWS de Covadis au démarrage)" -ForegroundColor Yellow
        } elseif ($restored) {
            Write-Host "  OK    dessin revenu à son état initial ($($final.modelSpaceEntityCount) objets, $($final.layerCount) calques)" -ForegroundColor Green
        } else {
            Write-Host "  ÉCHEC annulation : $($final.modelSpaceEntityCount) objets / $($final.layerCount) calques, attendu $($info.modelSpaceEntityCount) / $($info.layerCount)" -ForegroundColor Red
            $failures++
        }
    }
} finally {
    $client.Dispose()
}

if ($failures -gt 0) { Write-Host "$failures échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
