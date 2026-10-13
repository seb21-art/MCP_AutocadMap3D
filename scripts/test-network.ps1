<#
.SYNOPSIS
    Non-régression des points topographiques (import_points) et des topologies de réseau (create_network_topology,
    trace_network, delete_topology), puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (objets créés autour de (1000, 2000) et de (5000, 5000), et un point importé en
    longitude/latitude), puis annule chacune de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne
    correspond pas à -DrawingNameLike : n'utilisez que des copies de fichiers DWG. Map 3D requis (topologie, données
    d'objet).
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
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":120000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Un appel d'écriture en échec crée tout de même une étape d'annulation, pas un appel de lecture (-Read).
function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*', [switch]$Read) {
    $script:id++
    if (-not $Read) { $script:writes++ }
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

$folder = Join-Path $env:TEMP "mcp-test-network-$(Get-Random)"
New-Item -ItemType Directory -Force $folder | Out-Null
try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) { throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test." }
    if (-not $start.map.available) { throw "Ce script demande AutoCAD Map 3D (topologie, données d'objet)." }
    $startCs = Call 'get_coordinate_system'
    "Départ : $($start.modelSpaceEntityCount) objets, $($start.layerCount) calques"
    $stamp = Get-Date -Format HHmmss
    $layer = "MCP_NET_$stamp"; $odTable = "MCP_PTS_$stamp"; $prefix = "MCP_PT${stamp}_"

    Write-Host "Points topographiques"
    Call 'create_layer' "{`"name`":`"$layer`",`"color`":`"5`"}" -Write | Out-Null
    $csv = Join-Path $folder 'points.csv'
    Set-Content $csv -Encoding UTF8 -Value @('Matricule;X;Y;Z;Code', '1;1000,00;2000,00;100,5;VANNE', '2;1010;2000;101;VANNE', '3;1020;2005;102.5;REGARD', 'ligne;cassée;;;', '4;1030;2010;103;REGARD')
    $csvPath = $csv.Replace('\', '\\')
    $points = Call 'import_points' "{`"filePath`":`"$csvPath`",`"layer`":`"$prefix`",`"layerFromCode`":true,`"odTable`":`"$odTable`"}" -Write
    if ($null -ne $points) {
        Check 'points importés' $points.count 4
        Check 'en-tête ignoré, ligne cassée signalée' $points.skipped 1
        Check 'un calque par code' (($points.createdLayers | Sort-Object) -join ',') "${prefix}REGARD,${prefix}VANNE"
        Check 'étendue (virgule décimale)' "$(($points.extents.min | ForEach-Object { $_.ToString([cultureinfo]::InvariantCulture) }) -join ' ') / $(($points.extents.max | ForEach-Object { $_.ToString([cultureinfo]::InvariantCulture) }) -join ' ')" '1000 2000 100.5 / 1030 2010 103'
        $record = (Call 'get_od_records' "{`"handles`":[`"$($points.handles[0])`"],`"tables`":[`"$odTable`"]}").objects[0].records[0].values
        Check 'données d''objet du point 1' "$($record.NUMERO) $($record.CODE) $($record.ALTITUDE)" '1 VANNE 100.5'
        $byCode = Call 'summarize_entities' "{`"types`":[`"POINT`"],`"layers`":[`"$prefix*`"],`"groupBy`":[`"od:$odTable.CODE`"]}"
        Check 'métré par code' (($byCode.groups | ForEach-Object { "$($_.key."od:$odTable.CODE")=$($_.count)" }) -join ',') 'REGARD=2,VANNE=2'
    }

    $marker = Call 'create_circle' '{"center":[1100,2100],"radius":0.5}' -Write
    $block = Call 'create_block' "{`"name`":`"MCP_PT_$stamp`",`"basePoint`":[1100,2100],`"handles`":[`"$($marker.handle)`"],`"attributes`":[{`"tag`":`"ALT`",`"position`":[1101,2100]},{`"tag`":`"NUM`",`"position`":[1101,2101]}]}" -Write
    if ($null -ne $block) {
        $blocks = Call 'import_points' "{`"filePath`":`"$csvPath`",`"block`":`"MCP_PT_$stamp`",`"attributes`":{`"ALT`":`"Z`",`"NUM`":`"P`"},`"layer`":`"$layer`"}" -Write
        if ($null -ne $blocks) {
            $first = (Call 'list_entities' "{`"handles`":[`"$($blocks.handles[0])`"]}").entities[0]
            Check 'bloc inséré avec ses attributs' "$($first.block) $($first.attributes.NUM) $($first.attributes.ALT)" "MCP_PT_$stamp 1 100.50"
        }
    }

    Call 'set_coordinate_system' '{"code":"Lambert93","force":true}' -Write | Out-Null
    $xyz = Join-Path $folder 'gps.xyz'
    Set-Content $xyz -Encoding ASCII -Value @('3 46.5 250')
    $gps = Call 'import_points' "{`"filePath`":`"$($xyz.Replace('\', '\\'))`",`"columns`":`"XYZ`",`"from`":`"lonlat`",`"layer`":`"$layer`"}" -Write
    if ($null -ne $gps) { Check 'point GPS converti en Lambert-93' (($gps.extents.min | ForEach-Object { [math]::Round($_, 2) }) -join ' ') '700000 6600000 250' }
    Expect-Error 'fichier absent' 'import_points' '{"filePath":"C:\\mcp-absent\\points.csv"}' '*existe pas*'
    Expect-Error 'colonnes sans Y' 'import_points' "{`"filePath`":`"$csvPath`",`"columns`":`"PXZD`"}" '*X et Y*'

    Write-Host "Topologie de réseau"
    # Source S (5000,5000) - A (5100,5000) - B (5200,5000) - C (5300,5000), et une boucle A - D (5100,5100) - B.
    $links = @{}
    foreach ($segment in @(@('SA', 5000, 5000, 5100, 5000), @('AB', 5100, 5000, 5200, 5000), @('BC', 5200, 5000, 5300, 5000), @('AD', 5100, 5000, 5100, 5100), @('DB', 5100, 5100, 5200, 5000))) {
        $links[$segment[0]] = (Call 'create_line' "{`"start`":[$($segment[1]),$($segment[2])],`"end`":[$($segment[3]),$($segment[4])],`"layer`":`"$layer`"}" -Write).handle
    }
    $topology = "MCPNET$stamp"
    $created = Call 'create_network_topology' "{`"name`":`"$topology`",`"layers`":[`"$layer`"],`"types`":[`"LINE`"]}" -Write
    if ($null -ne $created) { Check 'topologie : liens et nœuds' "$($created.links) / $($created.nodes)" '5 / 5' }
    Expect-Error 'topologie déjà existante' 'create_network_topology' "{`"name`":`"$topology`",`"layers`":[`"$layer`"],`"types`":[`"LINE`"]}" '*existe déjà*'

    function Linked($result) { (($result.linkHandles | ForEach-Object { $h = $_; ($links.GetEnumerator() | Where-Object Value -eq $h).Key } | Sort-Object) -join ',') }
    $path = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"path`",`"start`":[5000,5000],`"end`":[5300,5000]}"
    if ($null -ne $path) { Check 'plus court chemin S -> C' "$(Linked $path) ($($path.length))" 'AB,BC,SA (300)' }
    $up = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"upstream`",`"start`":[5300,5000],`"source`":[5000,5000]}"
    if ($null -ne $up) { Check 'amont de C' (Linked $up) 'AB,BC,SA' }
    $down = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"downstream`",`"start`":`"$($links.BC)`",`"source`":`"$($links.SA)`"}"
    if ($null -ne $down) { Check 'aval de B (C n''est plus alimenté)' (Linked $down) 'AB,BC,DB' }
    $isolated = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"isolated`",`"closed`":[[5100,5000]],`"source`":[5000,5000]}"
    if ($null -ne $isolated) { Check 'vanne fermée en A : B, C et D isolés' (Linked $isolated) 'AB,AD,BC,DB' }
    $ring = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"isolated`",`"closed`":[[5100,5100]],`"source`":[5000,5000]}"
    if ($null -ne $ring) { Check 'boucle : une vanne en D ne coupe rien' $ring.links 0 }
    $flood = Call 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"flood`",`"start`":[5000,5000],`"maxDistance`":150,`"select`":true}"
    if ($null -ne $flood) { Check 'propagation à 150 depuis S' (Linked $flood) 'AB,AD,SA' }
    Check 'résultat sélectionné dans AutoCAD' (Call 'get_selection' '{"limit":5}').count 3
    Expect-Error 'topologie inconnue' 'trace_network' '{"topology":"PAS_DE_TOPO","mode":"flood","start":[0,0]}' '*Aucune topologie*' -Read
    Expect-Error 'mode inconnu' 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"amont`",`"start`":[5000,5000]}" '*mode*' -Read
    Expect-Error 'chemin sans arrivée' 'trace_network' "{`"topology`":`"$topology`",`"mode`":`"path`",`"start`":[5000,5000]}" '*end*' -Read
    $deleted = Call 'delete_topology' "{`"name`":`"$topology`"}" -Write
    if ($null -ne $deleted) { Check 'topologie supprimée' $deleted.deleted $topology }

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 900 }
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $finalCs = Call 'get_coordinate_system'
        $tableLeft = (Call 'list_od_tables' '{"includeFields":false}').tables | Where-Object name -eq $odTable
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $final.layerCount -eq $start.layerCount -and
            $finalCs.assigned -eq $startCs.assigned -and -not $tableLeft
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 900; $extra++
    }
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur" -ForegroundColor Yellow
    } else {
        Check 'retour à l''état initial (objets, calques, système, table)' $restored 'True'
    }
} finally {
    $client.Dispose()
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
