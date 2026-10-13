<#
.SYNOPSIS
    Non-régression du terrain (create_terrain_mesh avec courbes, create_contours, analyze_terrain, import_terrain), des
    images géoréférencées (attach_image) et de la publication (export_geodata), puis annulation.
.DESCRIPTION
    Valeurs connues : un plan incliné à 10 % (z = 100 + 0,1 x) autour de (20 000, 20 000), une image PNG de 200 × 100
    pixels avec son fichier monde, un GeoTIFF de 2 × 2 pixels écrit octet par octet, et le point (700 000 ; 6 600 000)
    de Lambert-93, qui vaut longitude 3°, latitude 46,5°. import_terrain demande Internet (RGE ALTI de l'IGN).
    Ce script MODIFIE le dessin ouvert puis annule chacune de ses opérations avec U ; il refuse de s'exécuter si le nom
    du dessin ne correspond pas à -DrawingNameLike.
.EXAMPLE
    .\test-terrain.ps1 -Server "$env:USERPROFILE\.mcpmap3d\server\McpMap3D.Server.exe"
#>
param(
    [string]$DrawingNameLike = '*test*',
    [string]$Server = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\server\McpMap3D.Server.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$utf8 = New-Object System.Text.UTF8Encoding($false)
$pipeName = "McpMap3D.v1.s$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)"
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { throw "Plug-in injoignable : AutoCAD Map 3D est-il lancé ?" }
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
$script:id = 0; $script:writes = 0; $script:failures = 0
$inv = [cultureinfo]::InvariantCulture

function Call([string]$method, [string]$paramsJson = '{}', [switch]$Write) {
    $script:id++
    if ($Write) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":120000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "  ÉCHEC $method : [$($r.error.code)] $($r.error.message)" -ForegroundColor Red; $script:failures++; return $null }
    return $r.result
}

function Expect-Error([string]$label, [string]$method, [string]$paramsJson, [string]$like = '*', [switch]$Read) {
    $script:id++
    if (-not $Read) { $script:writes++ }
    $writer.WriteLine("{`"id`":`"$script:id`",`"method`":`"$method`",`"params`":$paramsJson,`"timeoutMs`":60000}")
    $r = $reader.ReadLine() | ConvertFrom-Json
    if ($r.ok) { Write-Host "  ÉCHEC $label : erreur attendue" -ForegroundColor Red; $script:failures++ }
    elseif ($r.error.message -notlike $like) { Write-Host "  ÉCHEC $label : message inattendu « $($r.error.message) »" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "  OK    $label -> $($r.error.message)" -ForegroundColor Green }
}

function Near([string]$label, $actual, [double]$expected, [double]$tolerance = 0.01) {
    if ($null -ne $actual -and [math]::Abs([double]$actual - $expected) -le $tolerance) { Write-Host ("  OK    {0} : {1}" -f $label, $actual) -ForegroundColor Green }
    else { Write-Host ("  ÉCHEC {0} : {1}, attendu {2} (± {3})" -f $label, $actual, $expected, $tolerance) -ForegroundColor Red; $script:failures++ }
}

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  OK    $label $detail" -ForegroundColor Green } else { Write-Host "  ÉCHEC $label $detail" -ForegroundColor Red; $script:failures++ }
}

function Json($value) { ConvertTo-Json -Compress -Depth 6 -InputObject $value }

$folder = Join-Path $env:TEMP "mcp-test-terrain-$(Get-Random)"
New-Item -ItemType Directory -Force $folder | Out-Null
try {
    $start = Call 'get_drawing_info'
    if ($start.fileName -notlike $DrawingNameLike) { throw "Le dessin ouvert est « $($start.fileName) », qui ne correspond pas à « $DrawingNameLike » : ouvrez une copie de test." }
    $startCs = Call 'get_coordinate_system'
    "Départ : $($start.modelSpaceEntityCount) objets, $($start.layerCount) calques"
    $stamp = Get-Date -Format HHmmss
    $layer = "MCP_MNT_$stamp"

    Write-Host "Terrain synthétique : plan à 10 % (z = 100 + 0,1 (x - 20 000))"
    $points = foreach ($i in 0..10) { foreach ($j in 0..10) { , @((20000 + 10 * $i), (20000 + 10 * $j), (100 + $i)) } }
    $mesh = Call 'create_terrain_mesh' (Json @{ points = @($points); layer = $layer; createLayer = $true; contourInterval = 1; majorEvery = 5; contourLayer = "${layer}_C"; majorLayer = "${layer}_CM" }) -Write
    if ($mesh) {
        Check 'maillage créé sur un calque créé pour lui' ($mesh.mesh.layer -eq $layer) "($($mesh.triangles) triangles)"
        Check 'courbes de niveau : 10 niveaux de 100 à 109' ($mesh.contours.contours -eq 10) "($($mesh.contours.contours))"
        Check 'dont 2 maîtresses (100 et 105)' ($mesh.contours.majorContours -eq 2) "($($mesh.contours.majorContours))"
    }
    $handle = $mesh.mesh.handle
    $c105 = (Call 'list_entities' "{`"layers`":[`"${layer}_CM`"],`"includeGeometry`":true}").entities | Where-Object { [math]::Abs($_.points[0][0] - 20050) -lt 0.01 }
    Check 'courbe maîtresse 105 en x = 20 050, longue de 100' ($null -ne $c105 -and [math]::Abs($c105.length - 100) -lt 0.01)

    $elevation = Call 'analyze_terrain' "{`"handle`":`"$handle`",`"mode`":`"elevation`",`"points`":[[20055,20050],[19000,19000]]}"
    if ($elevation) { Near 'altitude interpolée en (20 055 ; 20 050)' $elevation.elevations[0] 105.5; Check 'point hors du terrain' ($elevation.outside -eq 1) }
    $profile = Call 'analyze_terrain' "{`"handle`":`"$handle`",`"mode`":`"profile`",`"points`":[[20000,20050],[20100,20050]],`"step`":10}"
    if ($profile) {
        Near 'profil : longueur' $profile.length 100
        Near 'profil : dénivelé montant' $profile.climb 10
        Near 'profil : pente maximale (%)' $profile.maxSlopePercent 10
        Check 'profil : 11 échantillons' (@($profile.samples).Count -eq 11) "($(@($profile.samples).Count))"
    }
    $boundary = Call 'create_polyline' '{"points":[[20000,20000],[20100,20000],[20100,20100],[20000,20100]],"closed":true}' -Write
    $volume = Call 'analyze_terrain' "{`"handle`":`"$handle`",`"mode`":`"volume`",`"boundary`":`"$($boundary.handle)`",`"referenceZ`":105}"
    if ($volume) { Near 'déblai au-dessus de 105' $volume.cut 12500 125; Near 'remblai en dessous de 105' $volume.fill 12500 125 }
    $slope = Call 'analyze_terrain' "{`"handle`":`"$handle`",`"mode`":`"slope`"}"
    if ($slope) { Near 'pente moyenne (%)' $slope.meanSlopePercent 10 0.001; Near 'surface en plan' $slope.planArea 10000 }
    $contours = Call 'create_contours' "{`"handle`":`"$handle`",`"contourInterval`":2.5,`"majorEvery`":0,`"contourLayer`":`"${layer}_C25`"}" -Write
    if ($contours) { Check 'create_contours à 2,5 : niveaux 100, 102,5 … 107,5' ($contours.contours -eq 4) "($($contours.contours))" }
    Expect-Error 'équidistance nulle' 'create_contours' "{`"handle`":`"$handle`",`"contourInterval`":0}" '*équidistance*'
    Expect-Error 'objet qui n''est pas un maillage' 'analyze_terrain' "{`"handle`":`"$($boundary.handle)`",`"mode`":`"slope`"}" '*maillage*' -Read
    Expect-Error 'mode inconnu' 'analyze_terrain' "{`"handle`":`"$handle`",`"mode`":`"pente`"}" '*mode*' -Read

    Write-Host "Images géoréférencées"
    $png = Join-Path $folder 'ortho.png'
    $bitmap = New-Object System.Drawing.Bitmap 200, 100
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.Clear([System.Drawing.Color]::DarkGreen); $graphics.Dispose()
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    # Pixels de 0,5 : l'image couvre x 30 000 à 30 100, y 30 000 à 30 050 (C et F au centre du pixel en haut à gauche).
    Set-Content (Join-Path $folder 'ortho.pgw') -Value @('0.5', '0', '0', '-0.5', '30000.25', '30049.75')
    $image = Call 'attach_image' "{`"filePath`":`"$($png.Replace('\', '\\'))`"}" -Write
    if ($image) {
        Check 'géoréférencée par son fichier monde' ($image.georeference -like '*ortho.pgw*') "($($image.georeference))"
        Check 'coin en bas à gauche (30 000 ; 30 000)' ([math]::Abs($image.lowerLeft[0] - 30000) -lt 0.001 -and [math]::Abs($image.lowerLeft[1] - 30000) -lt 0.001) "($($image.lowerLeft -join ' '))"
        Check 'taille 100 × 50' ([math]::Abs($image.width - 100) -lt 0.001 -and [math]::Abs($image.height - 50) -lt 0.001) "($($image.width) × $($image.height))"
    }

    # GeoTIFF minimal, sans fichier monde : 2 × 2 pixels gris, pixels de 10, coin en haut à gauche en (40 000 ; 40 020).
    $tif = Join-Path $folder 'mnt.tif'
    $stream = [System.IO.File]::Create($tif); $bin = New-Object System.IO.BinaryWriter($stream)
    $tags = @(@(256, 3, 1, 2), @(257, 3, 1, 2), @(258, 3, 1, 8), @(259, 3, 1, 1), @(262, 3, 1, 1), @(273, 4, 1, 0), @(277, 3, 1, 1), @(278, 3, 1, 2), @(279, 4, 1, 4), @(33550, 12, 3, 0), @(33922, 12, 6, 0))
    $ifd = 8; $dataStart = $ifd + 2 + 12 * $tags.Count + 4; $scaleAt = $dataStart; $tieAt = $scaleAt + 24; $pixelsAt = $tieAt + 48
    $bin.Write([byte[]](0x49, 0x49)); $bin.Write([uint16]42); $bin.Write([uint32]$ifd); $bin.Write([uint16]$tags.Count)
    foreach ($t in $tags) {
        $value = switch ($t[0]) { 273 { $pixelsAt } 33550 { $scaleAt } 33922 { $tieAt } default { $t[3] } }
        $bin.Write([uint16]$t[0]); $bin.Write([uint16]$t[1]); $bin.Write([uint32]$t[2])
        if ($t[1] -eq 3) { $bin.Write([uint16]$value); $bin.Write([uint16]0) } else { $bin.Write([uint32]$value) }
    }
    $bin.Write([uint32]0)
    foreach ($v in 10.0, 10.0, 0.0) { $bin.Write([double]$v) }
    foreach ($v in 0.0, 0.0, 0.0, 40000.0, 40020.0, 0.0) { $bin.Write([double]$v) }
    $bin.Write([byte[]](40, 90, 140, 200)); $bin.Close()
    $geotiff = Call 'attach_image' "{`"filePath`":`"$($tif.Replace('\', '\\'))`"}" -Write
    if ($geotiff) {
        Check 'GeoTIFF lu par ses balises' ($geotiff.georeference -like '*GeoTIFF*') "($($geotiff.georeference))"
        Check 'GeoTIFF : coin en bas à gauche (40 000 ; 40 000), 20 × 20' ([math]::Abs($geotiff.lowerLeft[0] - 40000) -lt 0.001 -and [math]::Abs($geotiff.lowerLeft[1] - 40000) -lt 0.001 -and [math]::Abs($geotiff.width - 20) -lt 0.001) "($($geotiff.lowerLeft -join ' '), $($geotiff.width))"
    }
    $manual = Call 'attach_image' "{`"filePath`":`"$($png.Replace('\', '\\'))`",`"position`":[50000,50000],`"width`":20}" -Write
    if ($manual) { Check 'placement manuel : 20 × 10' ([math]::Abs($manual.height - 10) -lt 0.001) "($($manual.width) × $($manual.height))" }
    Remove-Item (Join-Path $folder 'ortho.pgw')
    Expect-Error 'image sans géoréférencement ni position' 'attach_image' "{`"filePath`":`"$($png.Replace('\', '\\'))`"}" '*fichier monde*'

    Write-Host "Publication GeoJSON, KML, KMZ"
    Call 'set_coordinate_system' '{"code":"Lambert93","force":true}' -Write | Out-Null
    $point = (Call 'create_point' '{"points":[[700000,6600000]]}' -Write).handles[0]
    $square = (Call 'create_polyline' '{"points":[[700000,6600000],[700100,6600000],[700100,6600100],[700000,6600100]],"closed":true}' -Write).handle
    $text = (Call 'create_text' '{"text":"Château d''eau","position":[700050,6600050],"height":2}' -Write).handle
    $scope = "`"handles`":[`"$point`",`"$square`",`"$text`"]"
    $geojson = Join-Path $folder 'export.geojson'
    $published = Call 'export_geodata' "{`"filePath`":`"$($geojson.Replace('\', '\\'))`",$scope}"
    if ($published) {
        Check 'trois objets publiés' ($published.features -eq 3) "($($published.features))"
        $doc = Get-Content $geojson -Raw -Encoding UTF8 | ConvertFrom-Json
        $pt = ($doc.features | Where-Object { $_.properties.handle -eq $point }).geometry.coordinates
        Check 'point en WGS84 : longitude 3°, latitude 46,5°' ([math]::Abs($pt[0] - 3) -lt 1e-7 -and [math]::Abs($pt[1] - 46.5) -lt 1e-7) "($($pt -join ', '))"
        $polygon = ($doc.features | Where-Object { $_.properties.handle -eq $square }).geometry
        Check 'polyligne fermée en polygone fermé' ($polygon.type -eq 'Polygon' -and @($polygon.coordinates[0]).Count -eq 5)
        Check 'texte publié avec son contenu (accents compris)' (($doc.features | Where-Object { $_.properties.handle -eq $text }).properties.text -eq "Château d'eau")
    }
    Expect-Error 'fichier existant sans overwrite' 'export_geodata' "{`"filePath`":`"$($geojson.Replace('\', '\\'))`",$scope}" '*overwrite*' -Read
    $kml = Join-Path $folder 'export.kml'
    if (Call 'export_geodata' "{`"filePath`":`"$($kml.Replace('\', '\\'))`",$scope}") {
        $content = Get-Content $kml -Raw -Encoding UTF8
        Check 'KML : point en 3,46.5' ($content -match '<Point><coordinates>3,46\.5</coordinates></Point>')
        Check 'KML : texte échappé' ($content -match 'Château d''eau|Château d&apos;eau')
    }
    $kmz = Join-Path $folder 'export.kmz'
    if (Call 'export_geodata' "{`"filePath`":`"$($kmz.Replace('\', '\\'))`",$scope}") {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($kmz)
        Check 'KMZ : doc.kml dans l''archive' (@($zip.Entries.Name) -contains 'doc.kml'); $zip.Dispose()
    }

    Write-Host "MNT de l'IGN (serveur MCP, Internet)"
    if (Test-Path $Server) {
        $start2 = New-Object System.Diagnostics.ProcessStartInfo($Server); $start2.UseShellExecute = $false
        $start2.RedirectStandardInput = $true; $start2.RedirectStandardOutput = $true; $start2.RedirectStandardError = $true; $start2.StandardOutputEncoding = $utf8
        $process = [System.Diagnostics.Process]::Start($start2); $null = $process.StandardError.ReadToEndAsync()
        $mcpIn = New-Object System.IO.StreamWriter($process.StandardInput.BaseStream, $utf8); $mcpIn.AutoFlush = $true; $script:mcpId = 0
        function Mcp([string]$tool, $arguments) {
            $script:mcpId++
            $mcpIn.WriteLine((@{ jsonrpc = '2.0'; id = $script:mcpId; method = 'tools/call'; params = @{ name = $tool; arguments = $arguments } } | ConvertTo-Json -Depth 10 -Compress))
            while ($true) { $m = $process.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $script:mcpId) { return $m.result } }
        }
        $mcpIn.WriteLine('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-terrain","version":"1.0"}}}')
        $null = $process.StandardOutput.ReadLine(); $mcpIn.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $r = Mcp 'import_terrain' @{ center = @(3, 46.5); size = 100; step = 10; layer = "${layer}_IGN"; contourInterval = 1 }
        if ($r.isError) { Check 'import_terrain' $false "($($r.content[0].text))" }
        else {
            $script:writes++
            $terrain = $r.content[0].text | ConvertFrom-Json
            Check 'grille de 11 × 11 points' ($terrain.grid.points -eq 121) "($($terrain.grid.points))"
            Check 'maillage triangulé' ($terrain.terrain.triangles -ge 150) "($($terrain.terrain.triangles) triangles, z $($terrain.terrain.minZ) à $($terrain.terrain.maxZ))"
            Check 'altitudes plausibles (Allier, 150 à 600 m)' ($terrain.terrain.minZ -gt 150 -and $terrain.terrain.maxZ -lt 600)
            Check 'centré sur (700 000 ; 6 600 000)' ($terrain.terrain.mesh.handle -and (Call 'analyze_terrain' "{`"handle`":`"$($terrain.terrain.mesh.handle)`",`"mode`":`"elevation`",`"points`":[[700000,6600000]]}").elevations[0] -gt 0)
        }
        $tooBig = Mcp 'import_terrain' @{ center = @(3, 46.5); size = 2000; step = 1 }
        Check 'zone trop grande refusée' ($tooBig.isError -and $tooBig.content[0].text -like '*au-delà de 100000*') "($($tooBig.content[0].text))"
        $mcpIn.Close(); if (-not $process.WaitForExit(5000)) { $process.Kill() }
    } else {
        Write-Host "  AVERT import_terrain non testé : serveur introuvable ($Server)" -ForegroundColor Yellow
    }

    Write-Host "Annulation de $($script:writes) opération(s), une à une"
    Write-Host "  NOTE  Map 3D va demander, pour chaque image annulée, s'il faut la dissocier (« Erasing Image ») : répondez Oui." -ForegroundColor Yellow
    foreach ($i in 1..$script:writes) { Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 900 }
    $extra = 0
    while ($true) {
        $final = Call 'get_drawing_info'
        $finalCs = Call 'get_coordinate_system'
        $restored = $final.modelSpaceEntityCount -eq $start.modelSpaceEntityCount -and $final.layerCount -eq $start.layerCount -and $finalCs.assigned -eq $startCs.assigned
        if ($restored -or $extra -ge 3) { break }
        Call '_undo' '{"count":1}' | Out-Null; Start-Sleep -Milliseconds 900; $extra++
    }
    if ($restored -and $extra -gt 0) { Write-Host "  AVERT retour à l'état initial après $extra U supplémentaire(s)" -ForegroundColor Yellow }
    else { Check 'retour à l''état initial (objets, calques, système)' $restored "($($final.modelSpaceEntityCount) objets, $($final.layerCount) calques)" }
} finally {
    $client.Dispose()
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) échec(s)" -ForegroundColor Red; exit 1 }
Write-Host "Tous les tests sont passés." -ForegroundColor Green
