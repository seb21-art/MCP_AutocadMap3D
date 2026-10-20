<#
.SYNOPSIS
    Non-régression de l'analyse bas niveau : get_entity_dxf, set_entity_dxf, export_dxf, list_classes,
    list_loaded_modules et list_dictionary, sur des objets AutoCAD standard (LINE, INSERT avec XDATA, dictionnaire nommé).
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (objets créés loin des données, dictionnaire MCP_TEST_DICT), puis annule chacune
    de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
    set_entity_dxf est désactivé par défaut : le script l'active le temps du test dans %USERPROFILE%\.mcpmap3d\settings.json
    et remet ensuite le fichier dans son état d'origine. -SkipSetEntityDxf ne teste que le refus de l'outil désactivé.
#>
param([string]$DrawingNameLike = '*test*', [switch]$SkipSetEntityDxf)

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
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*', [switch]$Write) {
    $script:id++
    if ($Write) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($r.error.message -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($r.error.message) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : « $actual »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

# Valeur de la n-ième paire d'un code dans la réponse de get_entity_dxf.
function Pair($object, [int]$code, [int]$occurrence = 1) { @($object.pairs | Where-Object { $_.code -eq $code })[$occurrence - 1] }
# Nombres joints en culture invariante (PowerShell écrirait 2,5 en français).
function Join-Numbers($values) { ($values | ForEach-Object { ([double]$_).ToString([cultureinfo]::InvariantCulture) }) -join ',' }
function Dxf([string]$handle, [string]$extra = '') { (Call 'get_entity_dxf' "{`"handles`":[`"$handle`"]$extra}").objects[0] }

$settings = Join-Path $env:USERPROFILE '.mcpmap3d\settings.json'
$settingsBackup = if (Test-Path $settings) { [IO.File]::ReadAllText($settings) } else { $null }
$export = $null

try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    if (((Call 'list_dictionary').entries | Where-Object key -eq 'MCP_TEST_DICT')) { throw "MCP_TEST_DICT existe déjà : annulez un test précédent." }
    Write-Host "Dessin : $($start.fileName) ($($start.modelSpaceEntityCount) objets, $($start.layerCount) calques)"

    Write-Host "Préparation : LINE, INSERT avec XDATA, dictionnaire nommé"
    Call 'create_layer' '{"name":"MCP_DXF_LAYER"}' -Write | Out-Null
    $line = (Call 'create_line' '{"start":[-90000,-90000,0],"end":[-89990,-89995,2.5],"layer":"0"}' -Write).handle
    $circle = (Call 'create_circle' '{"center":[-90000,-90100],"radius":2}' -Write).handle
    Call 'create_block' "{`"name`":`"MCP_DXF_BLK`",`"basePoint`":[-90000,-90100],`"handles`":[`"$circle`"],`"mode`":`"delete`"}" -Write | Out-Null
    $insert = (Call 'insert_block' '{"name":"MCP_DXF_BLK","position":[-90050,-90100]}' -Write).handle
    $fixture = Call '_dxf_test_fixture' "{`"handle`":`"$insert`"}" -Write

    Write-Host "get_entity_dxf : LINE"
    $l = Dxf $line ',"includeXdata":false'
    Check 'type DXF et classe C++' "$($l.dxfName) / $($l.className)" 'LINE / AcDbLine'
    Check 'code 0' (Pair $l 0).value 'LINE'
    Check 'code -1 : nom d''entité donné par son handle' (Pair $l -1).value $line
    Check 'code 10 : point [x, y, z]' (Join-Numbers (Pair $l 10).value) '-90000,-90000,0'
    Check 'code 11 : point [x, y, z]' (Join-Numbers (Pair $l 11).value) '-89990,-89995,2.5'
    Check 'code 8 : calque' (Pair $l 8).value '0'
    Check 'code 330 : propriétaire en handle' ((Pair $l 330).type) 'handle'

    Write-Host "get_entity_dxf : INSERT avec XDATA"
    $i = Dxf $insert
    Check 'type DXF' $i.dxfName 'INSERT'
    Check 'marqueur -3 des données étendues' (Pair $i -3).type 'marker'
    Check 'application 1001' (Pair $i 1001).value 'MCP_TEST'
    Check 'texte 1000' (Pair $i 1000).value 'Regard R1'
    Check 'réel 1040 en pleine précision (0,1 + 0,2)' ([double](Pair $i 1040).value -eq (0.1 + 0.2)) 'True'
    Check 'entier 1070' (Pair $i 1070).value 42
    Check 'sans données étendues (includeXdata false)' ($null -eq (Pair (Dxf $insert ',"includeXdata":false') 1001)) 'True'
    Check 'autre application seulement' ($null -eq (Pair (Dxf $insert ',"includeXdata":"AUTRE_APP"') 1001)) 'True'
    $withExtension = Dxf $insert ',"includeExtensionDictionary":true'
    Check 'dictionnaire d''extension : entrée et son contenu' "$($withExtension.extensionDictionary.entries[0].key) $((Pair $withExtension.extensionDictionary.entries[0].object 90).value)" 'EXT 2026'
    $followed = Call 'get_entity_dxf' "{`"handles`":[`"$line`"],`"followReferences`":1,`"includeXdata`":false}"
    $owner = (Pair $followed.objects[0] 330).value
    Check 'référence suivie : propriétaire décrit' $followed.referenced.$owner.dxfName 'BLOCK_RECORD'
    Expect-Error 'handle inexistant' 'get_entity_dxf' '{"handles":["FFFFFFF0"]}' '*Aucun objet*'

    Write-Host "list_dictionary"
    $root = Call 'list_dictionary'
    Check 'racine : MCP_TEST_DICT est un dictionnaire' (($root.entries | Where-Object key -eq 'MCP_TEST_DICT').type) 'DICTIONARY'
    $dict = Call 'list_dictionary' '{"path":"MCP_TEST_DICT"}'
    Check 'entrées par chemin' (($dict.entries | ForEach-Object { "$($_.key)=$($_.type)" }) -join ',') 'DATA=XRECORD,SUB=DICTIONARY'
    Check 'sous-dictionnaire : nombre d''entrées' (($dict.entries | Where-Object key -eq 'SUB').entries) 1
    Check 'par handle' ((Call 'list_dictionary' "{`"handle`":`"$($fixture.sub)`"}").entries[0].key) 'LEAF'
    Check 'dictionnaire d''extension par le handle de l''objet' ((Call 'list_dictionary' "{`"handle`":`"$insert`"}").entries[0].key) 'EXT'
    Expect-Error 'objet sans dictionnaire d''extension' 'list_dictionary' "{`"handle`":`"$line`"}" '*pas de dictionnaire d''extension*'
    Check 'chemin à deux niveaux' ((Call 'list_dictionary' '{"path":"MCP_TEST_DICT/SUB"}').entries[0].key) 'LEAF'
    Expect-Error 'clé absente' 'list_dictionary' '{"path":"MCP_TEST_DICT/ABSENT"}' '*absente*'
    Expect-Error 'entrée qui n''est pas un dictionnaire' 'list_dictionary' '{"path":"MCP_TEST_DICT/DATA"}' '*pas un dictionnaire*'

    Write-Host "get_entity_dxf : Xrecord (objet non graphique, binaire)"
    $x = Dxf $fixture.data ',"maxBinaryBytes":16'
    Check 'type DXF' $x.dxfName 'XRECORD'
    Check 'texte accentué' (Pair $x 1).value "réseau d'essai"
    Check 'réel 40 en pleine précision (pi)' ([double](Pair $x 40).value -eq [math]::PI) 'True'
    $binary = Pair $x 310
    Check 'binaire 310 : longueur réelle' $binary.length 100
    Check 'binaire 310 : tronqué à 16 octets' "$($binary.truncated) $($binary.value)" 'True 000102030405060708090A0B0C0D0E0F'

    Write-Host "list_classes"
    $classes = Call 'list_classes' '{"prefix":["AcDbLine","AcDbXrecord"]}'
    $lineClass = $classes.classes | Where-Object name -eq 'AcDbLine'
    Check 'AcDbLine : nom DXF et parent' "$($lineClass.dxfName) / $($lineClass.parent)" 'LINE / AcDbCurve'
    Check 'AcDbLine : objets comptés dans le dessin' ($lineClass.instances -ge 1) 'True'
    Check 'AcDbXrecord : objets comptés' (($classes.classes | Where-Object name -eq 'AcDbXrecord').instances -ge 2) 'True'
    $cova = Call 'list_classes' '{"prefix":["COVA","Cv"],"usedOnly":true}'
    Write-Host "        classes COVA/Cv utilisées dans ce dessin : $($cova.count)"

    Write-Host "list_loaded_modules"
    $own = (Call 'list_loaded_modules' '{"kind":"dotnet","names":["McpMap3D*"]}').modules | Where-Object name -eq 'McpMap3D.Plugin.dll'
    Check 'plug-in .NET : chemin et version' (($own.path -like '*MCPMap3D.bundle*') -and $own.assemblyVersion) 'True'
    $arx = Call 'list_loaded_modules' '{"kind":"arx"}'
    Check 'modules ARX chargés' ($arx.count -gt 0 -and @($arx.modules | Where-Object { $_.path }).Count -gt 0) 'True'
    Expect-Error 'genre de module inconnu' 'list_loaded_modules' '{"kind":"vba"}' '*kind*'

    Write-Host "export_dxf"
    $folder = (Resolve-Path (Join-Path $PSScriptRoot '..\artifacts\test')).Path
    $export = Join-Path $folder "export dxf é $(Get-Random).dxf"
    $escaped = $export.Replace('\', '\\')
    # export_dxf passe par le contexte commande : son étape d'annulation (vide) compte parmi celles du test.
    $out = Call 'export_dxf' "{`"filePath`":`"$escaped`",`"version`":`"R2013`",`"precision`":12}" -Write
    if ($null -ne $out) {
        Check 'fichier écrit (chemin accentué)' ((Test-Path -LiteralPath $export) -and $out.bytes -gt 0) 'True'
        $header = (Get-Content -LiteralPath $export -TotalCount 12) -join ' '
        Check 'version R2013 (AC1027) dans l''en-tête' ($header -match 'AC1027') 'True'
        Check 'nom du dessin inchangé' $out.drawingName (Call 'get_drawing_info').path
        Check 'état « modifié » inchangé' $out.drawingModifiedAfter $out.drawingModifiedBefore
    }
    Expect-Error 'fichier existant sans overwrite' 'export_dxf' "{`"filePath`":`"$escaped`"}" '*existe déjà*' -Write
    Check 'remplacement avec overwrite' ((Call 'export_dxf' "{`"filePath`":`"$escaped`",`"overwrite`":true}" -Write).version) 'AC1032'
    Expect-Error 'DXF binaire refusé' 'export_dxf' "{`"filePath`":`"$escaped`",`"ascii`":false,`"overwrite`":true}" '*binaire*' -Write
    Expect-Error 'version inconnue' 'export_dxf' "{`"filePath`":`"$escaped`",`"version`":`"R14`",`"overwrite`":true}" '*version*' -Write

    Write-Host "set_entity_dxf"
    if ($null -eq $settingsBackup) { Remove-Item $settings -ErrorAction SilentlyContinue } else { [IO.File]::WriteAllText($settings, '{}') }
    Expect-Error 'désactivé par défaut' 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":8,`"value`":`"MCP_DXF_LAYER`"}]}" '*désactivé*' -Write
    if (-not $SkipSetEntityDxf) {
        [IO.File]::WriteAllText($settings, '{"enableSetEntityDxf": true}')
        $dry = Call 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":8,`"value`":`"MCP_DXF_LAYER`"},{`"code`":11,`"value`":[-89980,-89995,2.5]}]}" -Write
        Check 'simulation : avant et après' "$($dry.dryRun) $($dry.changes[0].before.value)->$($dry.changes[0].after.value)" 'True 0->MCP_DXF_LAYER'
        Check 'simulation : rien écrit' (Pair (Dxf $line ',"includeXdata":false') 8).value '0'
        $applied = Call 'set_entity_dxf' "{`"handle`":`"$line`",`"dryRun`":false,`"changes`":[{`"code`":8,`"value`":`"MCP_DXF_LAYER`"},{`"code`":11,`"value`":[-89980,-89995,2.5]}]}" -Write
        Check 'appliqué : relecture du calque' (Pair $applied.after 8).value 'MCP_DXF_LAYER'
        Check 'appliqué : relecture du point 11' (Join-Numbers (Pair $applied.after 11).value) '-89980,-89995,2.5'
        $xd = Call 'set_entity_dxf' "{`"handle`":`"$insert`",`"dryRun`":false,`"changes`":[{`"code`":1000,`"value`":`"Regard R2`"},{`"code`":1040,`"value`":1.5}]}" -Write
        Check 'données étendues modifiées' "$((Pair $xd.after 1000).value) $((Pair $xd.after 1040).value)" 'Regard R2 1.5'
        Call 'set_entity_dxf' "{`"handle`":`"$line`",`"dryRun`":false,`"changes`":[{`"code`":8,`"value`":`"0`"}]}" -Write | Out-Null
        Call 'undo' '{"count":1}' | Out-Null; $script:writes--
        foreach ($k in 1..100) { if ((Call '_undo_progress').inProgress -eq 0) { break }; Start-Sleep -Milliseconds 100 }
        Check 'annulé par l''outil undo' (Pair (Dxf $line ',"includeXdata":false') 8).value 'MCP_DXF_LAYER'
        Expect-Error 'code non modifiable' 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":0,`"value`":`"CIRCLE`"}]}" '*ne se modifie pas*' -Write
        Expect-Error 'code absent' 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":62,`"value`":1}]}" '*n''apparaît pas*' -Write
        Expect-Error 'occurrence absente' 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":8,`"occurrence`":2,`"value`":`"0`"}]}" '*2 fois*' -Write
        Expect-Error 'type incompatible' 'set_entity_dxf' "{`"handle`":`"$line`",`"changes`":[{`"code`":11,`"value`":`"abc`"}]}" '*incompatible*' -Write
        Expect-Error 'handle inexistant' 'set_entity_dxf' '{"handle":"FFFFFFF0","changes":[{"code":8,"value":"0"}]}' '*Aucun objet*' -Write
        Call 'create_layer' '{"name":"MCP_DXF_LOCK"}' -Write | Out-Null
        $locked = (Call 'create_line' '{"start":[-90000,-90200],"end":[-89990,-90200],"layer":"MCP_DXF_LOCK"}' -Write).handle
        Call 'create_layer' '{"name":"MCP_DXF_LOCK","locked":true}' -Write | Out-Null
        Expect-Error 'calque verrouillé' 'set_entity_dxf' "{`"handle`":`"$locked`",`"changes`":[{`"code`":11,`"value`":[0,0,0]}]}" '*verrouillé*' -Write
    }

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($k in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 700 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $dictLeft = @((Call 'list_dictionary').entries | Where-Object key -eq 'MCP_TEST_DICT').Count
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $final.layerCount -eq $start.layerCount -and $dictLeft -eq 0
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($($final.modelSpaceEntityCount) objets, $($final.layerCount) calques, sans MCP_TEST_DICT)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $($final.modelSpaceEntityCount) objets / $($final.layerCount) calques / MCP_TEST_DICT $dictLeft, attendu $($start.modelSpaceEntityCount) / $($start.layerCount) / 0" -ForegroundColor Red
        $script:failures++
    }
} finally {
    $client.Dispose()
    if ($null -eq $settingsBackup) { Remove-Item $settings -ErrorAction SilentlyContinue } else { [IO.File]::WriteAllText($settings, $settingsBackup) }
    if ($export -and (Test-Path -LiteralPath $export)) { Remove-Item -LiteralPath $export -Force }
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
