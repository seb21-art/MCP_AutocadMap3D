<#
.SYNOPSIS
    Non-régression de l'impression et de l'export PDF : périphériques, formats, styles, export de l'espace objet et
    d'une présentation quelle que soit la présentation courante, format A0, échelle, tracé vers fichier.
.DESCRIPTION
    Les PDF sont créés dans le dossier temporaire puis supprimés. Le script change de présentation courante et crée au
    besoin une présentation de test ; il rétablit tout à la fin. Il refuse de s'exécuter si le nom du dessin ne
    correspond pas à -DrawingNameLike : n'utilisez que des copies de fichiers DWG.
#>
param([string]$DrawingNameLike = '*test*')

$ErrorActionPreference = 'Stop'
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:failures = 0; $script:pdfs = @()

function Call([string]$method, [string]$paramsJson = '{}') {
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

function Check([string]$label, $condition, $detail = '') {
    if ($condition) { Write-Host "  OK    $label $detail" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label $detail" -ForegroundColor Red; $script:failures++ }
}

function CheckText([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Host "  OK    $label : « $actual »" -ForegroundColor Green }
    else { Write-Host "  ÉCHEC $label : « $actual », attendu « $expected »" -ForegroundColor Red; $script:failures++ }
}

function NewPdf([string]$name) {
    $path = [IO.Path]::Combine([IO.Path]::GetTempPath(), "mcpmap3d_$($name)_$([Guid]::NewGuid().ToString('N').Substring(0, 8)).pdf")
    $script:pdfs += $path
    return $path
}

# Vrai PDF : fichier non vide qui commence par « %PDF ».
function CheckPdf([string]$label, [string]$path) {
    if (-not (Test-Path $path)) { Check $label $false '(fichier absent)'; return }
    $bytes = [IO.File]::ReadAllBytes($path)
    Check $label ($bytes.Length -gt 1000 -and [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -eq '%PDF') "($([math]::Round($bytes.Length / 1024, 1)) Ko)"
}

function Json([string]$path) { $path.Replace('\', '/') }

$createdLayout = $null
$startLayout = $null
try {
    $info = Call 'get_drawing_info'
    if ($info.fileName -notlike $DrawingNameLike) {
        throw "Le dessin ouvert est « $($info.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test."
    }
    $layouts = Call 'list_layouts'
    $startLayout = $layouts.currentLayout
    "Départ : $($info.fileName), présentation courante « $startLayout », unités $($info.units)"

    Write-Host "Périphériques et styles"
    $devices = Call 'list_plot_devices'
    Check 'pilote PDF disponible' ($devices.pdfDevices.Count -gt 0) "($($devices.pdfDevices -join ', '))"
    $pdfDevice = $devices.pdfDevices | Where-Object { $_ -eq 'DWG To PDF.pc3' } | Select-Object -First 1
    if (-not $pdfDevice) { $pdfDevice = $devices.pdfDevices[0] }
    $media = Call 'list_plot_devices' "{`"device`":`"$pdfDevice`"}"
    Check 'formats A4 et A0 proposés' ((@($media.mediaSizes | Where-Object { $_.canonicalName -match '(^|_)A4_' }).Count -gt 0) -and
        (@($media.mediaSizes | Where-Object { $_.canonicalName -match '(^|_)A0_' }).Count -gt 0))
    $styles = Call 'list_plot_styles'
    Check 'monochrome.ctb disponible' (@($styles.colorDependent | Where-Object { $_ -eq 'monochrome.ctb' }).Count -eq 1)

    $paper = $layouts.layouts | Where-Object { -not $_.isModel } | Select-Object -First 1
    if (-not $paper) {
        $createdLayout = "MCP_TEST_PDF_$(Get-Random -Maximum 9999)"
        Call 'create_layout' "{`"name`":`"$createdLayout`"}" | Out-Null
        $paper = (Call 'list_layouts').layouts | Where-Object { $_.name -eq $createdLayout }
    }
    "Présentation papier utilisée : « $($paper.name) » ($($paper.canonicalMediaName))"

    Write-Host "Espace objet exporté depuis une présentation (défaut eLayoutNotCurrent)"
    Call 'set_current_layout' "{`"name`":`"$($paper.name)`"}" | Out-Null
    $model = NewPdf 'objet'
    $res = Call 'export_pdf' "{`"layout`":`"Model`",`"filePath`":`"$(Json $model)`",`"paperSize`":`"A3`",`"orientation`":`"landscape`",`"plotStyle`":`"monochrome.ctb`"}"
    if ($res) {
        CheckPdf 'PDF de l''espace objet' $model
        CheckText 'présentation activée le temps du tracé' $res.temporarilyActivatedFrom $paper.name
        Check 'format A3' ($res.paperSize -match '(^|_)A3_') "($($res.paperSize))"
    }
    CheckText 'présentation de l''utilisateur rétablie' (Call 'list_layouts').currentLayout $paper.name

    Write-Host "Présentation exportée depuis l'espace objet"
    Call 'set_current_layout' '{"name":"Model"}' | Out-Null
    $sheet = NewPdf 'presentation'
    $res = Call 'export_pdf' "{`"layout`":`"$($paper.name)`",`"filePath`":`"$(Json $sheet)`"}"
    if ($res) {
        CheckPdf 'PDF de la présentation' $sheet
        CheckText 'zone tracée' $res.plotArea 'Layout'
        Check 'format de la présentation conservé' ([math]::Abs([double]$res.paperWidth * [double]$res.paperHeight - [double]$paper.paperWidth * [double]$paper.paperHeight) -lt 100) `
            "($($res.paperSize) ; présentation $($paper.paperWidth) × $($paper.paperHeight))"
    }
    CheckText 'espace objet rétabli' (Call 'list_layouts').currentLayout 'Model'

    Write-Host "Formats et échelle"
    $a0 = NewPdf 'a0'
    $res = Call 'export_pdf' "{`"layout`":`"Model`",`"filePath`":`"$(Json $a0)`",`"paperSize`":`"A0`"}"
    if ($res) { Check 'A0 reconnu, pas 2A0 ni 4A0' ($res.paperSize -match '(^|_)A0_' -and $res.paperSize -notmatch '[24]A0') "($($res.paperSize))" }
    $window = Call 'list_entities' '{"limit":1,"includeGeometry":true}'
    $x = [double]$window.entities[0].extents.min[0]; $y = [double]$window.entities[0].extents.min[1]
    $scaled = NewPdf 'echelle'
    $res = Call 'export_pdf' ("{`"layout`":`"Model`",`"filePath`":`"$(Json $scaled)`",`"paperSize`":`"A4`",`"plotArea`":`"window`"," +
        "`"window`":[$x,$y,$($x + 100),$($y + 100)],`"scale`":`"1:500`"}")
    if ($res) {
        CheckPdf 'PDF à l''échelle' $scaled
        Check 'échelle en millimètres de papier' ($res.scale -like '* Millimeters = 1 unité(s) du dessin') "($($res.scale))"
    }

    Write-Host "Tracé vers fichier (plot_drawing)"
    $plotted = NewPdf 'trace'
    $res = Call 'plot_drawing' "{`"device`":`"$pdfDevice`",`"layout`":`"Model`",`"filePath`":`"$(Json $plotted)`",`"paperSize`":`"A4`"}"
    if ($res) { CheckPdf 'fichier tracé' $plotted }

    Write-Host "Erreurs attendues"
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"export_pdf`",`"params`":{`"layout`":`"Model`",`"paperSize`":`"Z9`"},`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    Check 'format inconnu refusé' (-not $r.ok -and $r.error.message -like '*introuvable*') "($($r.error.message))"
    $script:id++
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"export_pdf`",`"params`":{`"layout`":`"PRESENTATION_INEXISTANTE`"},`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    Check 'présentation inconnue refusée' (-not $r.ok -and $r.error.message -like '*n''existe pas*') "($($r.error.message))"
} finally {
    if ($startLayout) { Call 'set_current_layout' "{`"name`":`"$startLayout`"}" | Out-Null }
    if ($createdLayout) { Call 'delete_layout' "{`"name`":`"$createdLayout`"}" | Out-Null }
    $final = Call 'list_layouts'
    if ($final.currentLayout -eq $startLayout -and -not ($final.layouts | Where-Object { $_.name -eq $createdLayout })) {
        Write-Host "  OK    présentations rétablies (courante « $startLayout »)" -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC présentations non rétablies (courante « $($final.currentLayout) »)" -ForegroundColor Red; $script:failures++
    }
    $script:pdfs | Where-Object { Test-Path $_ } | ForEach-Object { [IO.File]::Delete($_) }
    $client.Dispose()
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
