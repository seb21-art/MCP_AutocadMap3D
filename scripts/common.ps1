# Fonctions partagées par les scripts du projet.

$McpServerName = 'map3d'

function Get-DotNetSdk([int]$MinimumVersion = 8) {
    # Renvoie le chemin d'un dotnet.exe disposant d'un SDK de la version demandée ou plus récent.
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge $MinimumVersion }) {
            return $candidate
        }
    }

    throw "Aucun SDK .NET $MinimumVersion (ou plus récent) trouvé. Installez-le : winget install Microsoft.DotNet.SDK.$MinimumVersion"
}

# Versions d'AutoCAD prises en charge et version de .NET qu'exige chacune (voir PackageContents.xml).
$AcadVersions = [ordered]@{ '2026' = 8; '2027' = 10 }

function Get-InstalledAcadVersions {
    # Versions installées à l'emplacement par défaut (Map 3D et Civil 3D s'installent dans le dossier AutoCAD).
    $AcadVersions.Keys | Where-Object {
        Test-Path (Join-Path $env:ProgramFiles "Autodesk\AutoCAD $_\acdbmgd.dll")
    }
}

function Test-ClaudeCodeServer([string]$claude) {
    # Windows PowerShell 5.1 transforme la sortie d'erreur redirigée d'un exécutable en erreur bloquante
    # quand $ErrorActionPreference vaut Stop : on l'assouplit le temps de l'appel.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $claude mcp get $McpServerName *> $null
        return $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Test-ClaudeDesktopRunning {
    # Claude Desktop (Microsoft Store ou installation classique) ; la CLI Claude Code porte le même nom de processus.
    [bool](Get-Process -Name claude -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -match '\\WindowsApps\\Claude_|\\AnthropicClaude\\' })
}

# Hors d'AppData : Claude Desktop (MSIX) virtualise les nouveaux dossiers AppData créés par ses processus enfants.
# Même emplacement que InstallPaths.Root côté C#.
function Get-InstallRoot {
    Join-Path $env:USERPROFILE '.mcpmap3d'
}

function Get-ServerDirectory {
    Join-Path (Get-InstallRoot) 'server'
}

function Get-ClaudeCli {
    # CLI de Claude Code : dans le PATH, sinon celle embarquée par Claude Desktop (version la plus récente).
    # Hors de Claude Desktop, la version Microsoft Store n'est visible que dans le dossier de son paquet.
    $command = Get-Command claude -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    @(
        (Join-Path $env:APPDATA 'Claude\claude-code\*\claude.exe'),
        (Join-Path $env:LOCALAPPDATA 'Packages\Claude_*\LocalCache\Roaming\Claude\claude-code\*\claude.exe')
    ) | ForEach-Object { Get-ChildItem $_ -ErrorAction SilentlyContinue } |
        Sort-Object { try { [version]$_.Directory.Name } catch { [version]'0.0' } } |
        Select-Object -Last 1 -ExpandProperty FullName
}
