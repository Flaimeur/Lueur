# Configure Apollo pour Moonlight. A lancer en administrateur (Apollo est dans Program Files) :
#   powershell -ExecutionPolicy Bypass -File moonlight-apollo.ps1
# Relancable a volonte (apres avoir installe ou desinstalle des jeux Steam) : il reconstruit la liste.
# Apollo n'est jamais redemarre pendant un stream : la nouvelle liste s'applique apres la deconnexion
# (au prochain lancement du script, ou au prochain demarrage du PC).
#
# Liste Moonlight, dans cet ordre :
#   Bureau · lumières éteintes   (tout s'eteint, le PC reste allume)
#   Bureau · sans rien changer   (aucune commande : lumieres et ecrans tels quels)
#   Bureau · lumières allumées
#   Ambiance écran               (les lumieres suivent l'image du stream)
#   Steam Big Picture
#   Mettre en veille
#   puis tous les jeux Steam par ordre alphabetique, avec leur jaquette Steam
# Par defaut chaque connexion eteint les lumieres (global_prep_cmd) ; les applis "sans rien changer",
# "lumières allumées", "Ambiance écran" et "Mettre en veille" en sont exclues (exclude-global-prep-cmd).
param(
    [string]$Lueur = (Join-Path (Split-Path $PSScriptRoot) 'dist\Lueur.exe'),
    [string]$Apollo = 'C:\Program Files\Apollo',
    [switch]$NoRestart  # pour tester sur une copie de la config
)

$ErrorActionPreference = 'Stop'
$config = Join-Path $Apollo 'config'
$assets = Join-Path $Apollo 'assets'
$appsPath = Join-Path $config 'apps.json'
$confPath = Join-Path $config 'sunshine.conf'
$pendingFlag = Join-Path $config 'lueur-restart-pending'
$utf8 = New-Object System.Text.UTF8Encoding $false
if (-not (Test-Path $Lueur)) { throw "Lueur.exe introuvable : $Lueur" }

foreach ($f in 'apps.json', 'sunshine.conf') {
    $bak = Join-Path $config "$f.lueur-bak"
    if (-not (Test-Path $bak)) { Copy-Item (Join-Path $config $f) $bak }
}

# ------------------------------------------------------------------ Jaquettes au style Lueur (600x800)
Add-Type -AssemblyName System.Drawing
function New-Cover([string]$file, [string]$title, [string]$subtitle, [int[]]$c1, [int[]]$c2) {
    $w = 600; $h = 800
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($w, $h)), ([System.Drawing.Color]::FromArgb(255, 20, 18, 30)), ([System.Drawing.Color]::FromArgb(255, 8, 8, 12))
    $g.FillRectangle($bg, 0, 0, $w, $h)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(60, 120, 480, 480)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(150, $c1[0], $c1[1], $c1[2])
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $c2[0], $c2[1], $c2[2]))
    $g.FillPath($glow, $glowPath)
    $ring = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(170, 230)), ([System.Drawing.Point]::new(430, 490)), ([System.Drawing.Color]::FromArgb(255, $c1[0], $c1[1], $c1[2])), ([System.Drawing.Color]::FromArgb(255, $c2[0], $c2[1], $c2[2]))
    $g.DrawEllipse((New-Object System.Drawing.Pen $ring, 22), 185, 245, 230, 230)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
    $g.DrawString($title, (New-Object System.Drawing.Font 'Segoe UI Semibold', 40), [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 20, 600, 560, 70), $fmt)
    $g.DrawString($subtitle, (New-Object System.Drawing.Font 'Segoe UI', 20), (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 200, 200, 210))), (New-Object System.Drawing.RectangleF 20, 672, 560, 60), $fmt)
    $g.DrawString('LUEUR', (New-Object System.Drawing.Font 'Segoe UI Semibold', 14), (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 255, 255, 255))), (New-Object System.Drawing.RectangleF 20, 40, 560, 30), $fmt)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Jaquette Steam (cache local : library_600x900.jpg, ou sa version localisee library_600x900_french.jpg...).
function Get-GameCover([string]$steam, [string]$id, [string]$name) {
    $file = "steam-$id.png"
    $src = Get-ChildItem (Join-Path $steam "appcache\librarycache\$id") -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'library_600x900*.jpg' -or $_.Name -like 'library_capsule*.jpg' } |
        Sort-Object { $_.Name -notlike 'library_600x900*' }, Name | Select-Object -First 1
    if ($src) {
        $img = [System.Drawing.Image]::FromFile($src.FullName)
        try { $img.Save((Join-Path $assets $file), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $img.Dispose() }
    }
    else { New-Cover $file $name 'Steam' @(102, 192, 244) @(23, 26, 33) }
    return $file
}

New-Cover 'lueur-bureau.png' 'Bureau' 'Lumières éteintes' @(167, 139, 250) @(236, 72, 153)
New-Cover 'lueur-tel-quel.png' 'Bureau' 'Sans rien changer' @(148, 163, 184) @(51, 65, 85)
New-Cover 'lueur-allume.png' 'Bureau' 'Lumières allumées' @(251, 191, 36) @(249, 115, 22)
New-Cover 'lueur-ecran.png' 'Ambiance écran' 'Les lumières suivent le stream' @(56, 189, 248) @(217, 70, 239)
New-Cover 'lueur-steam.png' 'Steam' 'Big Picture' @(102, 192, 244) @(23, 26, 33)
New-Cover 'lueur-veille.png' 'Mettre en veille' 'Réveil avec Moonlight' @(129, 140, 248) @(30, 27, 75)

# ------------------------------------------------------------------ Liste des applis
$sleep = 'powershell -NoProfile -WindowStyle Hidden -Command "Start-Sleep 4; Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState(''Suspend'', $false, $false)"'
$current = Get-Content $appsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$old = @($current.apps)

# Anciens noms -> nouveaux, pour garder les uuid (Moonlight garde ainsi les favoris / applis masquees).
$renamed = @{
    'Desktop' = '1 · Bureau, lumières éteintes'
    'Bureau · lumières éteintes' = '1 · Bureau, lumières éteintes'
    'Bureau · sans rien changer' = '2 · Bureau, sans rien changer'
    'Bureau - lumieres allumees' = '3 · Bureau, lumières allumées'
    'Bureau · lumières allumées' = '3 · Bureau, lumières allumées'
    'Ambiance ecran' = '4 · Ambiance écran'
    'Ambiance écran' = '4 · Ambiance écran'
    'Steam Big Picture' = '5 · Steam Big Picture'
    'Mettre en veille' = '6 · Mettre en veille'
}
$retired = @('Tout eteint (PC allume)')

function Get-Uuid([string]$name) {
    $match = $old | Where-Object { $_.name -eq $name -or $renamed[$_.name] -eq $name } | Select-Object -First 1
    if ($match -and $match.uuid) { return $match.uuid }
    return [guid]::NewGuid().ToString().ToUpper()
}
function New-App([string]$name, [string]$image, [hashtable]$extra) {
    $app = [ordered]@{ name = $name; 'image-path' = $image; uuid = (Get-Uuid $name) }
    foreach ($k in $extra.Keys) { $app[$k] = $extra[$k] }
    return [pscustomobject]$app
}
function Prep([string]$do, [string]$undo) { , @([pscustomobject]@{ do = $do; undo = $undo; elevated = $false }) }

# Moonlight/Artemis trient les applis par ordre alphabetique : le numero garde les modes en tete, dans
# cet ordre (les chiffres passent avant les lettres), et les jeux suivent.
$apps = [System.Collections.ArrayList]@()
# Les modes se ferment a la deconnexion (terminate-on-pause) : sinon Moonlight "reprend" la session au
# lieu de relancer l'appli, et Apollo ne rejoue pas les commandes (lumieres) a la reprise.
# Les jeux ne l'ont pas : une deconnexion ne doit pas fermer la partie.
$mode = @{ 'terminate-on-pause' = $true }
[void]$apps.Add((New-App '1 · Bureau, lumières éteintes' 'lueur-bureau.png' ($mode + @{})))
[void]$apps.Add((New-App '2 · Bureau, sans rien changer' 'lueur-tel-quel.png' ($mode + @{ 'exclude-global-prep-cmd' = $true })))
[void]$apps.Add((New-App '3 · Bureau, lumières allumées' 'lueur-allume.png' ($mode + @{ 'exclude-global-prep-cmd' = $true; 'prep-cmd' = (Prep "$Lueur --on" '') })))
[void]$apps.Add((New-App '4 · Ambiance écran' 'lueur-ecran.png' ($mode + @{ 'exclude-global-prep-cmd' = $true; 'prep-cmd' = (Prep "$Lueur --screen" "$Lueur --off") })))
[void]$apps.Add((New-App '5 · Steam Big Picture' 'lueur-steam.png' @{ detached = @('steam://open/bigpicture'); 'prep-cmd' = (Prep '' 'steam://close/bigpicture') }))
[void]$apps.Add((New-App '6 · Mettre en veille' 'lueur-veille.png' ($mode + @{ 'exclude-global-prep-cmd' = $true; detached = @($sleep) })))

# Jeux Steam installes (toutes les bibliotheques), sauf les outils.
$notGames = '228980', '431960', '4465640' # Redistributables, Wallpaper Engine, Game in Picture
$steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if (-not $steamPath) { $steamPath = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
if ($steamPath) {
    $steamPath = $steamPath -replace '/', '\'
    $vdf = Get-Content (Join-Path $steamPath 'steamapps\libraryfolders.vdf') -Raw -Encoding UTF8
    $libs = [regex]::Matches($vdf, '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    $games = foreach ($lib in $libs) {
        Get-ChildItem (Join-Path $lib 'steamapps') -Filter 'appmanifest_*.acf' -ErrorAction SilentlyContinue | ForEach-Object {
            $acf = Get-Content $_.FullName -Raw -Encoding UTF8
            [pscustomobject]@{
                Id   = [regex]::Match($acf, '"appid"\s+"(\d+)"').Groups[1].Value
                Name = [regex]::Match($acf, '"name"\s+"([^"]+)"').Groups[1].Value
            }
        }
    }
    foreach ($game in ($games | Where-Object { $_.Id -and $_.Id -notin $notGames } | Sort-Object Name -Unique)) {
        $cover = Get-GameCover $steamPath $game.Id $game.Name
        [void]$apps.Add((New-App $game.Name $cover @{ detached = @("steam://rungameid/$($game.Id)") }))
    }
}

# Les applis ajoutees a la main dans Apollo (ni gerees ici, ni jeu Steam) sont gardees a la fin.
$managed = @($apps | ForEach-Object { $_.name }) + @($renamed.Keys) + $retired
foreach ($app in $old) {
    $isSteamGame = @($app.detached) -match '^steam://rungameid/'
    if ($app.name -notin $managed -and -not $isSteamGame) { [void]$apps.Add($app) }
}

$current.apps = $apps.ToArray()
$newApps = $current | ConvertTo-Json -Depth 10
$changed = $newApps.Trim() -ne (Get-Content $appsPath -Raw -Encoding UTF8).Trim()
if ($changed) { [System.IO.File]::WriteAllText($appsPath, $newApps, $utf8) }

# ------------------------------------------------------------------ sunshine.conf
function ConvertTo-CompactJson($value) { ConvertTo-Json -InputObject @($value) -Depth 5 -Compress }
$confLines = @(Get-Content $confPath -Encoding UTF8)
# dd_configuration_option est retire : "ensure_only_display" gardait un ecran virtuel tiers comme seul ecran.
$lines = [System.Collections.ArrayList]@($confLines | Where-Object { $_ -notmatch '^\s*(global_prep_cmd|server_cmd|dd_configuration_option)\s*=' })
[void]$lines.Add('global_prep_cmd = ' + (ConvertTo-CompactJson ([pscustomobject]@{ do = "$Lueur --off"; undo = ''; elevated = $false })))
$serverCmds = @(
    [pscustomobject]@{ name = 'Bubbles'; cmd = 'bubbles.scr'; elevated = $false },
    [pscustomobject]@{ name = 'Lumières : tout éteindre'; cmd = "$Lueur --off"; elevated = $false },
    [pscustomobject]@{ name = 'Lumières : tout allumer'; cmd = "$Lueur --on"; elevated = $false },
    [pscustomobject]@{ name = 'Lumières : ambiance écran'; cmd = "$Lueur --screen"; elevated = $false },
    [pscustomobject]@{ name = 'Mettre le PC en veille'; cmd = $sleep; elevated = $false }
)
[void]$lines.Add('server_cmd = ' + (ConvertTo-CompactJson $serverCmds))
if ((($confLines -join "`n").Trim()) -ne ((@($lines) -join "`n").Trim())) {
    [System.IO.File]::WriteAllLines($confPath, [string[]]$lines, $utf8)
    $changed = $true
}

# ------------------------------------------------------------------ Redemarrage d'Apollo (jamais pendant un stream)
function Test-StreamActive {
    $last = Get-Content (Join-Path $config 'sunshine.log') -ErrorAction SilentlyContinue |
        Select-String 'CLIENT CONNECTED|CLIENT DISCONNECTED' | Select-Object -Last 1
    return [bool]($last -and $last.Line -match 'CLIENT CONNECTED')
}

if ($NoRestart) { return }
if ($changed) { New-Item $pendingFlag -ItemType File -Force | Out-Null }
if (-not (Test-Path $pendingFlag)) { 'Rien a changer. Termine.'; Start-Sleep 3; return }
if (Test-StreamActive) {
    'Un stream est en cours : la nouvelle liste sera active apres ta deconnexion (relance ce script, ou redemarre le PC).'
    Start-Sleep 6
    return
}
Restart-Service ApolloService
Remove-Item $pendingFlag -Force
'Apollo redemarre. Termine.'
Start-Sleep 3
