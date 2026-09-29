<#
.SYNOPSIS
    Non-régression de la gestion des dessins : liste, création, ouverture, enregistrement, fermeture.
.DESCRIPTION
    Le script crée ses propres dessins dans un dossier temporaire, les enregistre, les rouvre et les ferme, puis
    réactive le dessin de départ, auquel il ne touche pas. Il refuse de s'exécuter si le nom du dessin actif ne
    correspond pas à -DrawingNameLike : n'utilisez que des copies de fichiers DWG.
#>
param([string]$DrawingNameLike = '*test*')
$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$client.Connect(3000)
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:failures = 0

function Call([string]$method, [string]$paramsJson = '{}') {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

function Expect-Error([string]$label, [string]$method, [string]$paramsJson) {
    $script:id++
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
function Drawings { Call 'list_drawings' }
function Find([string]$name) { @((Drawings).drawings | Where-Object { $_.name -eq $name })[0] }

# Avant chaque écriture : arrêt si le dessin actif n'est pas le dessin de test attendu, pour ne jamais modifier,
# enregistrer ou fermer le dessin de départ ni un autre dessin ouvert.
function Assert-Active([string]$name) {
    $active = @((Drawings).drawings | Where-Object { $_.active })[0]
    if (-not $active -or $active.name -ne $name) { throw "Dessin actif « $($active.name) » au lieu de « $name » : arrêt du test." }
}

$start = Call 'get_drawing_info'
if ($start.fileName -notlike $DrawingNameLike) {
    $client.Dispose()
    throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
}

$startList = Drawings
$startName = @($startList.drawings | Where-Object { $_.active })[0].name
$folder = Join-Path $env:TEMP "mcp-test-dessins-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
# Chemin long : %TEMP% est souvent un chemin court (ADMINN~1), alors qu'AutoCAD renvoie le chemin long.
Add-Type -Namespace McpTest -Name Paths -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetLongPathName(string s, System.Text.StringBuilder l, int n);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetShortPathName(string l, System.Text.StringBuilder s, int n);
'@
New-Item -ItemType Directory $folder | Out-Null
$buffer = New-Object System.Text.StringBuilder 1024
[McpTest.Paths]::GetLongPathName($folder, $buffer, 1024) | Out-Null; $folder = $buffer.ToString()
[McpTest.Paths]::GetShortPathName($folder, $buffer, 1024) | Out-Null; $shortFolder = $buffer.ToString()
$pathA = Join-Path $folder 'mcp-test-a.dwg'
$pathB = Join-Path $folder 'mcp-test-b.dwg'
# Dessins sans nom créés par le test, fermés à la fin s'il s'interrompt avant de les enregistrer.
$created = New-Object System.Collections.Generic.List[string]
"Départ : $($startList.count) dessin(s) ouvert(s), actif $startName ; dossier $folder"

try {
    Write-Host "Liste"
    Check 'dessin actif listé' ($startList.active -like "*$startName") True

    Write-Host "Création"
    $new = Call 'new_drawing'
    if (-not $new) { throw "new_drawing a échoué : arrêt du test." }
    $created.Add($new.drawing.name)
    "  gabarit : $($new.template)"
    Check 'nouveau dessin : actif' $new.drawing.active True
    Check 'nouveau dessin : jamais enregistré' ($null -eq $new.drawing.path) True
    Check 'nouveau dessin : un de plus' (Drawings).count ($startList.count + 1)
    $newName = $new.drawing.name
    Assert-Active $newName
    Call 'create_line' '{"start":[0,0],"end":[10,0]}' | Out-Null
    Check 'après une ligne : modifié' (Find $newName).modified True
    Expect-Error 'fermeture avec des modifications' 'close_drawing' "{`"drawing`":$(Json $newName)}"
    Expect-Error 'enregistrement sans chemin d''un dessin jamais enregistré' 'save_drawing' '{}'
    Expect-Error 'enregistrement en DXF' 'save_drawing' "{`"filePath`":$(Json (Join-Path $folder 'x.dxf'))}"

    Write-Host "Enregistrement"
    Assert-Active $newName
    $saved = Call 'save_drawing' "{`"filePath`":$(Json (Join-Path $folder 'mcp-test-a'))}"
    Check 'enregistrer sous : extension .dwg ajoutée' $saved.drawing.path $pathA
    Check 'enregistrer sous : fichier écrit' (Test-Path $pathA) True
    Check 'enregistrer sous : dessin renommé' $saved.drawing.name 'mcp-test-a.dwg'
    Check 'enregistrer sous : plus de modification' $saved.drawing.modified False
    Assert-Active 'mcp-test-a.dwg'
    Call 'create_line' '{"start":[0,5],"end":[10,5]}' | Out-Null
    $resaved = Call 'save_drawing' '{}'
    Check 'enregistrer : plus de modification' $resaved.drawing.modified False
    Check 'enregistrer : même fichier' $resaved.drawing.path $pathA

    $newB = Call 'new_drawing' "{`"filePath`":$(Json $pathB)}"
    Check 'nouveau dessin enregistré aussitôt : fichier' (Test-Path $pathB) True
    Check 'nouveau dessin enregistré aussitôt : nom' $newB.drawing.name 'mcp-test-b.dwg'
    $count = (Drawings).count
    Expect-Error 'nouveau dessin sur un fichier existant' 'new_drawing' "{`"filePath`":$(Json $pathA)}"
    Check 'pas de dessin orphelin après le refus' (Drawings).count $count
    Expect-Error 'enregistrer sous le nom d''un autre dessin ouvert' 'save_drawing' "{`"filePath`":$(Json $pathA),`"overwrite`":true}"

    Write-Host "Ouverture et activation"
    $back = Call 'open_drawing' "{`"filePath`":$(Json $startName)}"
    Check 'activation du dessin de départ par son nom' $back.alreadyOpen True
    Check 'dessin de départ actif' ((Call 'get_drawing_info').fileName -like "*$startName") True
    $short = Call 'open_drawing' "{`"filePath`":$(Json (Join-Path $shortFolder 'mcp-test-b.dwg'))}"
    Check 'chemin court (8.3) reconnu comme le dessin déjà ouvert' $short.alreadyOpen True
    $relative = Call 'open_drawing' '{"filePath":".\\mcp-test-a.dwg"}'
    Check 'chemin relatif d''un dessin déjà ouvert' $relative.alreadyOpen True
    $toA = Call 'open_drawing' '{"filePath":"mcp-test-a"}'
    Check 'activation par le nom sans extension' $toA.drawing.name 'mcp-test-a.dwg'
    $closedB = Call 'close_drawing' '{"drawing":"mcp-test-b.dwg"}'
    Check 'fermeture d''un dessin non actif et non modifié' $closedB.closed $pathB
    Check 'le dessin actif reste mcp-test-a' ($closedB.active -eq $pathA) True
    $openB = Call 'open_drawing' "{`"filePath`":$(Json $pathB),`"readOnly`":true}"
    Check 'ouverture en lecture seule' $openB.drawing.readOnly True
    Check 'ouverture : pas déjà ouvert' $openB.alreadyOpen False
    Expect-Error 'enregistrer un dessin en lecture seule' 'save_drawing' "{`"drawing`":$(Json $pathB)}"
    Call 'close_drawing' "{`"drawing`":$(Json $pathB)}" | Out-Null
    Expect-Error 'ouverture d''un fichier absent' 'open_drawing' "{`"filePath`":$(Json (Join-Path $folder 'absent.dwg'))}"
    Expect-Error 'ouverture d''un fichier qui n''est pas un dessin' 'open_drawing' "{`"filePath`":$(Json (Join-Path $folder 'note.txt'))}"
    Expect-Error 'dessin ouvert inconnu' 'close_drawing' '{"drawing":"dessin-inexistant.dwg"}'

    Write-Host "Fermeture"
    Call 'open_drawing' "{`"filePath`":$(Json $pathA)}" | Out-Null
    Assert-Active 'mcp-test-a.dwg'
    Call 'create_line' '{"start":[0,10],"end":[10,10]}' | Out-Null
    $discard = Call 'close_drawing' "{`"drawing`":$(Json $pathA),`"discardChanges`":true}"
    Check 'fermeture sans enregistrer : modifications abandonnées' $discard.changesDiscarded True
    Call 'open_drawing' "{`"filePath`":$(Json $pathA)}" | Out-Null
    Assert-Active 'mcp-test-a.dwg'
    Check 'réouvert : seules les 2 lignes enregistrées' (Call 'get_drawing_info').modelSpaceEntityCount 2
    Call 'create_line' '{"start":[0,15],"end":[10,15]}' | Out-Null
    $closeSave = Call 'close_drawing' "{`"drawing`":$(Json $pathA),`"save`":true}"
    Check 'fermeture avec enregistrement' $closeSave.saved True
    Call 'open_drawing' "{`"filePath`":$(Json $pathA)}" | Out-Null
    Assert-Active 'mcp-test-a.dwg'
    Check 'réouvert : 3 lignes enregistrées' (Call 'get_drawing_info').modelSpaceEntityCount 3
    Call 'close_drawing' "{`"drawing`":$(Json $pathA)}" | Out-Null

    Write-Host "Gabarit désigné par son nom"
    $named = Call 'new_drawing' "{`"template`":$(Json (Split-Path $new.template -Leaf))}"
    if ($named) {
        $created.Add($named.drawing.name)
        Check 'gabarit trouvé par son nom de fichier' ($named.template -eq $new.template) True
        Call 'close_drawing' "{`"drawing`":$(Json $named.drawing.name)}" | Out-Null
    }
}
finally {
    # Retour au dessin de départ ; les dessins de test encore ouverts sont fermés sans enregistrer.
    foreach ($d in @((Drawings).drawings | Where-Object { $_.path -like "$folder*" -or (-not $_.path -and $created -contains $_.name) })) {
        $target = if ($d.path) { $d.path } else { $d.name }
        Call 'close_drawing' "{`"drawing`":$(Json $target),`"discardChanges`":true}" | Out-Null
    }
    Call 'open_drawing' "{`"filePath`":$(Json $startName)}" | Out-Null
    $end = Drawings
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
    $client.Dispose()
}

if ($end.count -eq $startList.count -and $end.active -like "*$startName") {
    Write-Host "  OK    retour à l'état initial ($($end.count) dessin(s), actif $startName)" -ForegroundColor Green
} else {
    Write-Host "  ÉCHEC état final : $($end.count) dessin(s), actif $($end.active)" -ForegroundColor Red; $script:failures++
}

if ($script:failures -eq 0) { Write-Host "Tous les tests sont passés." -ForegroundColor Green }
else { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
