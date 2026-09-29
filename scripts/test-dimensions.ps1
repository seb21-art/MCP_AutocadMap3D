<#
.SYNOPSIS
    Non-régression du format des cotations : styles de cote, remplacements sur des cotes, effacement des
    remplacements, mise à jour des cotes d'un style modifié. Contrôles chiffrés, puis annulation.
.DESCRIPTION
    Ce script MODIFIE le dessin ouvert (style de cote créé, cotes créées loin des données), puis annule chacune
    de ses opérations avec U. Il refuse de s'exécuter si le nom du dessin ne correspond pas à -DrawingNameLike :
    n'utilisez que des copies de fichiers DWG.
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

# Un outil d'écriture en échec passe quand même par le contexte commande : il consomme une étape d'annulation.
# Une lecture en échec, non (-Read).
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

function DimFormat([string]$handle) { (Call 'get_dimension_format' "{`"handles`":[`"$handle`"]}").dimensions[0] }
function Overrides($dimension) { (@($dimension.overrides) | Sort-Object) -join ',' }
function Style([string]$name) { (Call 'list_dimension_styles' "{`"names`":[`"$name`"]}").styles[0] }
function State {
    $styles = Call 'list_dimension_styles'
    [pscustomobject]@{
        Entities = (Call 'get_drawing_info').modelSpaceEntityCount
        Blocks = (Call 'list_blocks').count
        Styles = $styles.count
        Current = ($styles.styles | Where-Object { $_.current }).name
    }
}

try {
    $info = Call 'get_drawing_info'
    if ($info.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $start = State
    $source = Style $start.Current
    "Départ : $($start.Entities) objets, $($start.Blocks) bloc(s), $($start.Styles) style(s) de cote, style courant « $($start.Current) »"

    Write-Host "Création d'un style"
    $style = "MCP_COTES_$(Get-Random)"
    $created = Call 'set_dimension_style' "{`"name`":`"$style`",`"format`":{`"textHeight`":0.5,`"arrowhead`":`"oblique`",`"precision`":2,`"suffix`":`" m`",`"decimalSeparator`":`",`",`"textPosition`":`"above`",`"zeroSuppression`":`"none`"}}" -Write
    CheckText 'style créé' $created.created 'True'
    Check 'hauteur de texte' $created.format.textHeight 0.5
    CheckText 'flèches' $created.format.arrowhead 'oblique'
    CheckText 'précision' $created.format.precision 2
    CheckText 'suffixe' $created.format.suffix ' m'
    CheckText 'séparateur décimal' $created.format.decimalSeparator ','
    CheckText 'texte au-dessus de la ligne' $created.format.textPosition 'above'
    CheckText 'copié du style courant : échelle globale' $created.format.scale $source.format.scale
    CheckText 'copié du style courant : style de texte' $created.format.textStyle $source.format.textStyle
    CheckText 'le style courant ne change pas' $created.current 'False'

    Write-Host "Cote dans le nouveau style"
    $dim = Call 'create_dimension' "{`"type`":`"aligned`",`"start`":[0,2000],`"end`":[30,2040],`"offset`":5,`"style`":`"$style`"}" -Write
    $d = $dim.dimension.handle
    CheckText 'cote créée dans le style' $dim.style $style
    $f = DimFormat $d
    CheckText 'texte affiché' $f.displayedText '50,00 m'
    CheckText 'aucun remplacement' (Overrides $f) ''

    Write-Host "Remplacements sur une cote"
    $over = (Call 'set_dimension_format' "{`"handles`":[`"$d`"],`"format`":{`"precision`":0,`"textColor`":`"rouge`",`"arrowhead`":`"dot`"}}" -Write).dimensions[0]
    CheckText 'remplacement : texte affiché' $over.displayedText '50 m'
    CheckText 'remplacements listés' (Overrides $over) 'arrowhead,precision,textColor'
    CheckText 'remplacement : flèches' $over.format.arrowhead 'dot'
    CheckText 'le style n''est pas touché' (Style $style).format.precision 2
    $cleared = (Call 'set_dimension_format' "{`"handles`":[`"$d`"],`"clearOverrides`":true}" -Write).dimensions[0]
    CheckText 'remplacements effacés' (Overrides $cleared) ''
    CheckText 'remplacements effacés : texte affiché' $cleared.displayedText '50,00 m'
    CheckText 'remplacements effacés : flèches du style' $cleared.format.arrowhead 'oblique'

    Write-Host "Modification du style"
    $modified = Call 'set_dimension_style' "{`"name`":`"$style`",`"format`":{`"precision`":1,`"prefix`":`"L=`"}}" -Write
    CheckText 'style modifié, pas recréé' $modified.created 'False'
    CheckText 'cotes du style mises à jour' $modified.updatedDimensions 1
    CheckText 'préfixe ajouté' $modified.format.prefix 'L='
    CheckText 'suffixe conservé' $modified.format.suffix ' m'
    CheckText 'la cote suit le style' (DimFormat $d).displayedText 'L=50,0 m'
    $lines = (Call 'set_dimension_style' "{`"name`":`"$style`",`"format`":{`"extensionOffset`":0.2,`"extensionBeyond`":0.3,`"textGap`":0.1,`"lineWeight`":`"0.25`",`"lineColor`":`"bleu`"}}" -Write).format
    Check 'décalage des lignes d''attache' $lines.extensionOffset 0.2
    Check 'dépassement des lignes d''attache' $lines.extensionBeyond 0.3
    Check 'écart du texte' $lines.textGap 0.1
    CheckText 'épaisseur des lignes de cote' $lines.lineWeight '0.25 mm'
    CheckText 'couleur des lignes de cote' $lines.lineColor '5'

    Write-Host "Style courant"
    CheckText 'style rendu courant' (Call 'set_dimension_style' "{`"name`":`"$style`",`"current`":true}" -Write).current 'True'
    $dim2 = Call 'create_dimension' '{"type":"horizontal","start":[100,2000],"end":[125,2010],"offset":-5}' -Write
    $d2 = $dim2.dimension.handle
    CheckText 'nouvelle cote dans le style courant' $dim2.style $style
    CheckText 'nouvelle cote : texte affiché' (DimFormat $d2).displayedText 'L=25,0 m'
    CheckText 'nombre de cotes du style' (Style $style).dimensions 2

    Write-Host "Changement de style et facteur de mesure"
    CheckText 'cote passée dans l''ancien style' (Call 'set_dimension_format' "{`"handles`":[`"$d2`"],`"style`":`"$($start.Current)`"}" -Write).dimensions[0].style $start.Current
    CheckText 'nombre de cotes du style après changement' (Style $style).dimensions 1
    # Alignement opposé à celui du style copié, pour qu'il compte comme un remplacement quel que soit le dessin.
    $alignment = if ($source.format.textAlignment -eq 'horizontal') { 'aligned' } else { 'horizontal' }
    $scaled = (Call 'set_dimension_format' "{`"handles`":[`"$d`"],`"format`":{`"measurementScale`":1000,`"suffix`":`" mm`",`"precision`":0,`"textAlignment`":`"$alignment`"}}" -Write).dimensions[0]
    CheckText 'facteur de mesure 1000 : texte affiché' $scaled.displayedText 'L=50000 mm'
    Check 'facteur de mesure 1000 : mesure géométrique inchangée' $scaled.measurement 50
    Check 'list_entities : mesure géométrique' (Call 'list_entities' "{`"handles`":[`"$d`"]}").entities[0].measurement 50
    CheckText "texte $alignment" $scaled.format.textAlignment $alignment
    CheckText 'remplacements listés' (Overrides $scaled) 'measurementScale,precision,suffix,textAlignment'

    Write-Host "Cote angulaire"
    $angle = Call 'create_dimension' "{`"type`":`"angular`",`"center`":[200,2000],`"start`":[210,2000],`"end`":[200,2010],`"radius`":5,`"style`":`"$($start.Current)`"}" -Write
    Check 'cote angulaire : mesure' $angle.measurement 90
    $angular = (Call 'set_dimension_format' "{`"handles`":[`"$($angle.dimension.handle)`"],`"format`":{`"angularPrecision`":1,`"angularUnit`":`"degrees`"}}" -Write).dimensions[0]
    CheckText 'cote angulaire : remplacement' ((@($angular.overrides) -contains 'angularPrecision')) 'True'
    if ($angular.displayedText -like '90?0*') { Write-Host "  OK    cote angulaire : texte affiché « $($angular.displayedText) »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC cote angulaire : texte affiché « $($angular.displayedText) », attendu 90,0°" -ForegroundColor Red; $script:failures++ }

    Write-Host "Erreurs attendues"
    $line = (Call 'create_line' '{"start":[0,2100],"end":[10,2100]}' -Write).handle
    Expect-Error 'format d''une ligne' 'set_dimension_format' "{`"handles`":[`"$line`"],`"format`":{`"precision`":1}}"
    Expect-Error 'lecture du format d''une ligne' 'get_dimension_format' "{`"handles`":[`"$line`"]}" -Read
    Expect-Error 'flèche inconnue' 'set_dimension_format' "{`"handles`":[`"$d`"],`"format`":{`"arrowhead`":`"banane`"}}"
    Expect-Error 'précision hors bornes' 'set_dimension_style' "{`"name`":`"$style`",`"format`":{`"precision`":12}}"
    Expect-Error 'réglage inconnu' 'set_dimension_style' "{`"name`":`"$style`",`"format`":{`"couleur`":`"rouge`"}}"
    Expect-Error 'style de base inexistant' 'set_dimension_style' '{"name":"MCP_AUTRE","basedOn":"STYLE_INEXISTANT"}'
    Expect-Error 'basedOn sur un style existant' 'set_dimension_style' "{`"name`":`"$style`",`"basedOn`":`"$($start.Current)`"}"
    Expect-Error 'nom de style invalide' 'set_dimension_style' '{"name":"a<b>c"}'
    Expect-Error 'aucune action demandée' 'set_dimension_format' "{`"handles`":[`"$d`"]}"

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 700 }

    # D'autres applications peuvent glisser leurs propres étapes d'annulation (COVANEWS de Covadis au démarrage
    # d'AutoCAD, par exemple) : jusqu'à trois U supplémentaires sont tolérés, et signalés.
    $extra = 0
    while ($true) {
        $final = State
        $restored = $final.Entities -eq $start.Entities -and $final.Blocks -eq $start.Blocks -and
            $final.Styles -eq $start.Styles -and $final.Current -eq $start.Current
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 800; $extra++
    }
    $summary = "$($final.Entities) objets, $($final.Blocks) bloc(s), $($final.Styles) style(s), courant « $($final.Current) »"
    if ($restored -and $extra -gt 0) {
        Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s) : étape(s) d'annulation créée(s) hors du connecteur (par exemple COVANEWS de Covadis au démarrage)" -ForegroundColor Yellow
    } elseif ($restored) {
        Write-Host "  OK    retour à l'état initial ($summary)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC état final $summary ; attendu $($start.Entities) objets, $($start.Blocks) bloc(s), $($start.Styles) style(s), courant « $($start.Current) »" -ForegroundColor Red
        $script:failures++
    }
} finally {
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
