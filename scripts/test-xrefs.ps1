<#
.SYNOPSIS
    Non-régression des références externes (Xref) : attache, superposition, Xref imbriquée, déchargement,
    rechargement, réparation d'un chemin, renommage, liaison, détachement, puis annulation.
.DESCRIPTION
    Le script crée ses dessins sources dans un sous-dossier du dossier du dessin ouvert (même lecteur, pour les
    chemins relatifs), les attache au dessin ouvert, puis annule chacune de ses opérations avec U et supprime le
    sous-dossier. Le dessin ouvert doit être enregistré. Il refuse de s'exécuter si le nom du dessin ne correspond pas
    à -DrawingNameLike : n'utilisez que des copies de fichiers DWG.
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
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
function Expect-Error([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++; $script:writes++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Check([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2}" -f $label, $actual, $expected) -ForegroundColor Red; $script:failures++ }
}

function Json([string]$value) { $value | ConvertTo-Json }
function Xrefs { @((Call 'list_xrefs').xrefs) }
function Xref([string]$pattern) { @((Call 'list_xrefs' "{`"names`":[$(Json $pattern)]}").xrefs)[0] }
function Undo { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $script:writes-- }

function Assert-Active([string]$name) {
    $active = @((Call 'list_drawings').drawings | Where-Object { $_.active })[0]
    if (-not $active -or $active.name -ne $name) { throw "Dessin actif « $($active.name) » au lieu de « $name » : arrêt du test." }
}

# Dessin source enregistré puis fermé ; $attach : Xref à y attacher avant l'enregistrement.
function New-Source([string]$path, [string]$entity, [string]$paramsJson, [string]$attach) {
    $new = Call 'new_drawing' "{`"filePath`":$(Json $path)}"
    if (-not $new) { throw "Création de $path impossible : arrêt du test." }
    Assert-Active (Split-Path $path -Leaf)
    Call $entity $paramsJson | Out-Null
    if ($attach) { Call 'attach_xref' "{`"filePath`":$(Json $attach)}" | Out-Null }
    Call 'save_drawing' '{}' | Out-Null
    Call 'close_drawing' "{`"drawing`":$(Json $path)}" | Out-Null
}

$start = Call 'get_drawing_info'
if ($start.fileName -notlike $DrawingNameLike) {
    $client.Dispose()
    throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
}

$hostDrawing = @((Call 'list_drawings').drawings | Where-Object { $_.active })[0]
if (-not $hostDrawing.path) { $client.Dispose(); throw "Le dessin ouvert n'a jamais été enregistré : les chemins relatifs demandent un dessin enregistré." }
$hostName = $hostDrawing.name
$startXrefs = @(Xrefs).Count
$startTopLevel = @(Xrefs | Where-Object { -not $_.nested }).Count
$folderName = "mcp-test-xref-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$folder = Join-Path (Split-Path $hostDrawing.path) $folderName
New-Item -ItemType Directory $folder | Out-Null
$pathA = Join-Path $folder 'mcp-xref-a.dwg'
$pathB = Join-Path $folder 'mcp-xref-b.dwg'
$pathN = Join-Path $folder 'mcp-xref-n.dwg'
$pathC = Join-Path $folder 'mcp-xref-c.dwg'
$moved = Join-Path $folder 'deplace'
"Départ : $($start.modelSpaceEntityCount) objets, $startXrefs Xref ; dossier $folder"

try {
    Write-Host "Dessins sources"
    New-Source $pathA 'create_line' '{"start":[0,0],"end":[10,0]}'
    New-Source $pathB 'create_circle' '{"center":[0,0],"radius":5}'
    New-Source $pathN 'create_line' '{"start":[0,0],"end":[0,10]}'
    New-Source $pathC 'create_circle' '{"center":[0,0],"radius":2}' $pathN
    Call 'open_drawing' "{`"filePath`":$(Json $hostDrawing.path)}" | Out-Null
    Assert-Active $hostName
    Check 'quatre dessins sources' ((Get-ChildItem $folder -Filter *.dwg).Count) 4

    Write-Host "Attache"
    $a = Call 'attach_xref' "{`"filePath`":$(Json $pathA)}" -Write
    Check 'attache : nom tiré du fichier' $a.xref.name 'mcp-xref-a'
    Check 'attache : chargée' $a.xref.status 'Resolved'
    Check 'attache : type' $a.xref.type 'attach'
    Check 'attache : chemin relatif par défaut' $a.xref.savedPath ".\$folderName\mcp-xref-a.dwg"
    Check 'attache : fichier chargé' $a.xref.loadedFrom $pathA
    Check 'attache : une insertion' $a.xref.referenceCount 1
    $again = Call 'attach_xref' "{`"filePath`":$(Json $pathA),`"position`":[20,0]}" -Write
    Check 'même fichier, même nom : définition réutilisée' $again.existingDefinition True
    Check 'même fichier, même nom : deux insertions' $again.xref.referenceCount 2
    $b = Call 'attach_xref' "{`"filePath`":$(Json $pathB),`"name`":`"MCP-XREF-B`",`"type`":`"overlay`",`"pathType`":`"full`",`"position`":[100,0,0],`"scale`":2,`"rotation`":90}" -Write
    Check 'superposition : type' $b.xref.type 'overlay'
    Check 'superposition : chemin complet' $b.xref.savedPath $pathB
    $ref = @($b.xref.references)[0]
    Check 'insertion : position' ($ref.position -join ',') '100,0,0'
    Check 'insertion : échelle' ($ref.scale -join ',') '2,2,2'
    Check 'insertion : rotation' $ref.rotation 90
    Check 'insertion : espace objet' $ref.space 'model'
    Expect-Error 'fichier absent' 'attach_xref' "{`"filePath`":$(Json (Join-Path $folder 'absent.dwg'))}"
    Expect-Error 'fichier qui n''est pas un DWG' 'attach_xref' "{`"filePath`":$(Json (Join-Path $folder 'plan.dxf'))}"
    Expect-Error 'le dessin lui-même' 'attach_xref' "{`"filePath`":$(Json $hostDrawing.path)}"
    Expect-Error 'nom déjà pris par une Xref d''un autre fichier' 'attach_xref' "{`"filePath`":$(Json $pathB),`"name`":`"mcp-xref-a`"}"
    Expect-Error 'type inconnu' 'attach_xref' "{`"filePath`":$(Json $pathB),`"name`":`"x`",`"type`":`"lier`"}"
    $relative = Call 'attach_xref' "{`"filePath`":$(Json ".\$folderName\mcp-xref-c.dwg")}" -Write
    Check 'chemin relatif au dessin hôte' $relative.xref.loadedFrom $pathC

    Write-Host "Xref imbriquée"
    $nested = Xref '*mcp-xref-n'
    Check 'imbriquée : listée' ($null -ne $nested) True
    Check 'imbriquée : marquée' $nested.nested True
    Check 'imbriquée : parent' (@($nested.parents) -join ',') 'mcp-xref-c'
    Check 'imbriquée : chargée' $nested.status 'Resolved'
    Expect-Error 'détacher une Xref imbriquée' 'detach_xrefs' "{`"names`":[$(Json $nested.name)]}"
    Expect-Error 'décharger une Xref imbriquée' 'unload_xrefs' "{`"names`":[$(Json $nested.name)]}"
    Check 'filtre avec joker' @((Call 'list_xrefs' '{"names":["mcp-xref-*"]}').xrefs).Count 4

    Write-Host "Déchargement et rechargement"
    $unloaded = Call 'unload_xrefs' '{"names":["MCP-XREF-B"]}' -Write
    Check 'déchargée' @($unloaded.xrefs)[0].status 'Unloaded'
    Check 'déchargée : insertion conservée' (Xref 'MCP-XREF-B').referenceCount 1
    $reloaded = Call 'reload_xrefs' '{"names":["MCP-XREF-B"]}' -Write
    Check 'rechargée' @($reloaded.xrefs)[0].status 'Resolved'
    $all = Call 'reload_xrefs' -Write
    Check 'tout recharger (Xref de premier niveau)' $all.count ($startTopLevel + 3)
    Expect-Error 'recharger une Xref imbriquée' 'reload_xrefs' "{`"names`":[$(Json $nested.name)]}"
    Expect-Error 'nom inconnu' 'reload_xrefs' '{"names":["absente"]}'

    Write-Host "Chemin introuvable puis réparé"
    Call 'unload_xrefs' '{"names":["MCP-XREF-B"]}' -Write | Out-Null
    New-Item -ItemType Directory $moved | Out-Null
    Move-Item $pathB $moved
    $lost = Call 'reload_xrefs' '{"names":["MCP-XREF-B"]}' -Write
    Check 'fichier déplacé : pas chargée' (@($lost.xrefs)[0].status -ne 'Resolved') True
    Check 'fichier déplacé : erreur d''AutoCAD renvoyée' ([bool]@($lost.xrefs)[0].reloadError) True
    Check 'fichier déplacé : signalé' (@($lost.notLoaded) -join ',') 'MCP-XREF-B'
    "  (état $(@($lost.xrefs)[0].status), erreur $(@($lost.xrefs)[0].reloadError))"
    Expect-Error 'nouveau chemin absent' 'edit_xref' "{`"name`":`"MCP-XREF-B`",`"filePath`":$(Json (Join-Path $folder 'absent.dwg'))}"
    $fixed = Call 'edit_xref' "{`"name`":`"MCP-XREF-B`",`"filePath`":$(Json (Join-Path $moved 'mcp-xref-b.dwg'))}" -Write
    Check 'réparée : chargée' $fixed.xref.status 'Resolved'
    Check 'réparée : rechargée' $fixed.reloaded True
    Check 'réparée : chemin relatif' $fixed.xref.savedPath ".\$folderName\deplace\mcp-xref-b.dwg"

    Write-Host "Modification"
    $full = Call 'edit_xref' '{"name":"mcp-xref-a","pathType":"full"}' -Write
    Check 'chemin complet' $full.xref.savedPath $pathA
    $none = Call 'edit_xref' '{"name":"mcp-xref-a","pathType":"none"}' -Write
    Check 'nom de fichier seul' $none.xref.savedPath 'mcp-xref-a.dwg'
    "  (nom seul, fichier hors du dossier du dessin et des chemins de recherche : état $($none.xref.status))"
    # AutoCAD peut retrouver le fichier malgré le nom seul (dossier déjà chargé) : la forme du chemin reste alors possible.
    if ($none.xref.status -eq 'Resolved') {
        $relativeAgain = Call 'edit_xref' '{"name":"mcp-xref-a","pathType":"relative"}' -Write
        Check 'forme du chemin, fichier retrouvé par AutoCAD' $relativeAgain.xref.savedPath ".\$folderName\mcp-xref-a.dwg"
    } else {
        Expect-Error 'forme du chemin sans fichier retrouvable' 'edit_xref' '{"name":"mcp-xref-a","pathType":"relative"}'
    }
    $back = Call 'edit_xref' "{`"name`":`"mcp-xref-a`",`"filePath`":$(Json $pathA)}" -Write
    Check 'retour au chemin relatif' $back.xref.savedPath ".\$folderName\mcp-xref-a.dwg"
    Check 'retour au chemin relatif : chargée' $back.xref.status 'Resolved'
    $renamed = Call 'edit_xref' '{"name":"MCP-XREF-B","newName":"MCP-XREF-B2","type":"attach"}' -Write
    Check 'renommée' $renamed.xref.name 'MCP-XREF-B2'
    Check 'passée en attache' $renamed.xref.type 'attach'
    Check 'renommée : pas de rechargement' $renamed.reloaded False
    Expect-Error 'nouveau nom déjà pris' 'edit_xref' '{"name":"MCP-XREF-B2","newName":"mcp-xref-a"}'
    Expect-Error 'rien à modifier' 'edit_xref' '{"name":"MCP-XREF-B2"}'

    Write-Host "Liaison"
    Call 'unload_xrefs' '{"names":["MCP-XREF-B2"]}' -Write | Out-Null
    Expect-Error 'lier une Xref déchargée' 'bind_xrefs' '{"names":["MCP-XREF-B2"]}'
    Call 'reload_xrefs' '{"names":["MCP-XREF-B2"]}' -Write | Out-Null
    $bound = Call 'bind_xrefs' '{"names":["mcp-xref-c"]}' -Write
    Check 'liée : bloc ordinaire' @($bound.bound)[0].stillXref $null
    Check 'liée : insertion conservée' @($bound.bound)[0].insertions 1
    Check 'liée : Xref imbriquée liée avec elle' (@($bound.remainingXrefs | Where-Object { $_ -like '*mcp-xref-n' }).Count) 0
    Check 'liée : plus de Xref C' ($null -eq (Xref 'mcp-xref-c')) True
    Undo
    Check 'U : Xref C de retour' (Xref 'mcp-xref-c').status 'Resolved'
    $insert = Call 'bind_xrefs' '{"names":["mcp-xref-a"],"mode":"insert"}' -Write
    Check 'liée en insertion : mode' $insert.mode 'insert'
    Check 'liée en insertion : deux insertions' @($insert.bound)[0].insertions 2
    Undo

    Write-Host "Détachement"
    $detached = Call 'detach_xrefs' '{"names":["MCP-XREF-B2"]}' -Write
    Check 'détachée : insertions supprimées' $detached.erasedReferences 1
    Check 'détachée : absente' ($null -eq (Xref 'MCP-XREF-B2')) True
    Undo
    Check 'U : Xref B2 de retour' (Xref 'MCP-XREF-B2').referenceCount 1

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation : jusqu'à trois U supplémentaires
    # sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $finalXrefs = @(Xrefs).Count
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $finalXrefs -eq $startXrefs
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($($final.modelSpaceEntityCount) objets, $finalXrefs Xref)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $($final.modelSpaceEntityCount) objets / $finalXrefs Xref, attendu $($start.modelSpaceEntityCount) / $startXrefs" -ForegroundColor Red
        $script:failures++
    }
}
finally {
    # Filet de sécurité : Xref de test restantes détachées, dessins sources fermés, dossier supprimé.
    $left = @((Call 'list_xrefs' '{"names":["mcp-xref-*"]}').xrefs | Where-Object { -not $_.nested })
    if ($left.Count -gt 0) {
        Write-Host "  AVERT Xref de test encore présentes, détachées : $(($left | ForEach-Object { $_.name }) -join ', ')" -ForegroundColor Yellow
        Call 'detach_xrefs' "{`"names`":[$(($left | ForEach-Object { Json $_.name }) -join ',')]}" | Out-Null
    }
    foreach ($d in @((Call 'list_drawings').drawings | Where-Object { $_.path -like "$folder*" })) {
        Call 'close_drawing' "{`"drawing`":$(Json $d.path),`"discardChanges`":true}" | Out-Null
    }
    Call 'open_drawing' "{`"filePath`":$(Json $hostDrawing.path)}" | Out-Null
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
