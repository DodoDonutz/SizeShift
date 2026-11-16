# Erzeugt die README-Screenshots mit ausschliesslich erfundenen Daten - auf Deutsch und Englisch.
#
# 1. Legt je Sprache einen Demo-PC aus Platzhalter-Dateien an (artifacts\demo-de, artifacts\demo-en).
#    Die Dateien sind "sparse": sie haben eine Groesse, belegen aber keinen Platz auf der Platte.
# 2. Haengt ihn per subst als S: (Systemplatte) und T: (Datenplatte) ein.
# 3. Startet SizeShift mit --screenshots: die App scannt S:, zeigt erfundene Hardware und einen
#    erfundenen Autostart und rendert jeden Bereich als PNG nach docs\screenshots\<sprache>.
# 4. Haengt S: und T: wieder aus.
#
# Aufruf aus dem Projektordner:  powershell -ExecutionPolicy Bypass -File installer\screenshots.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\TreeSizeBetter.App\bin\Release\net8.0-windows\SizeShift.exe'
$GB = 1GB; $MB = 1MB

foreach ($letter in 'S:', 'T:') {
    if (Test-Path "$letter\") { throw "$letter ist schon belegt - bitte erst freigeben (subst $letter /D)." }
}

function New-Placeholder([string]$demo, [string]$path, [long]$size, [int]$daysAgo) {
    $full = Join-Path $demo $path
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    if (-not (Test-Path $full)) {
        New-Item -ItemType File $full | Out-Null
        fsutil sparse setflag "$full" | Out-Null
        fsutil file seteof "$full" $size | Out-Null
    }
    $when = (Get-Date).AddDays(-$daysAgo)
    $item = Get-Item $full
    $item.CreationTime = $when; $item.LastWriteTime = $when; $item.LastAccessTime = $when
}

# Mehrere Dateien fuer einen Ordner, zusammen etwa $total gross.
function New-Folder([string]$demo, [string]$path, [long]$total, [int]$files, [int]$daysAgo, [string]$ext = 'bin') {
    $each = [long]($total / $files)
    for ($i = 1; $i -le $files; $i++) { New-Placeholder $demo "$path\datei$i.$ext" $each ($daysAgo + $i % 5) }
}

function New-DemoPc([string]$demo, [bool]$german) {
    # Ordnernamen, die man im Bild liest, in der jeweiligen Sprache.
    $n = if ($german) {
        @{ Vacation = 'Urlaub 2025'; Recordings = 'Aufnahmen'; Camera = 'Kamera'; Bills = 'Rechnungen'; Uni = 'Uni';
           OldInstallers = 'Alte Installer'; Projects = 'Projekte'; Games = 'Spiele'; Archive = 'Archiv';
           Backups = 'Backups 2024'; OldPhotos = 'Fotos bis 2023'; Movies = 'Filme' }
    } else {
        @{ Vacation = 'Vacation 2025'; Recordings = 'Recordings'; Camera = 'Camera'; Bills = 'Invoices'; Uni = 'University';
           OldInstallers = 'Old installers'; Projects = 'Projects'; Games = 'Games'; Archive = 'Archive';
           Backups = 'Backups 2024'; OldPhotos = 'Photos until 2023'; Movies = 'Movies' }
    }

    # ---------------------------------------------------------------- S: Systemplatte
    New-Folder $demo 'S\Program Files\Adobe\Adobe Photoshop 2026' (4.2 * $GB) 40 12 'dll'
    New-Folder $demo 'S\Program Files\Blender Foundation\Blender 4.5' (1.1 * $GB) 25 60 'dll'
    New-Folder $demo 'S\Program Files\Mozilla Firefox' (310 * $MB) 12 3 'dll'
    New-Folder $demo 'S\Program Files\NVIDIA Corporation' (2.4 * $GB) 30 9 'dll'
    New-Folder $demo 'S\Program Files (x86)\Steam\steamapps\common\Space Colony' (48 * $GB) 60 410 'pak'
    New-Folder $demo 'S\Program Files (x86)\Steam\steamapps\common\Racing Pro 24' (72 * $GB) 80 21 'pak'
    New-Folder $demo 'S\Program Files (x86)\Steam\steamapps\shadercache' (1.3 * $GB) 30 5 'bin'
    New-Folder $demo 'S\Program Files (x86)\Steam\bin' (900 * $MB) 20 5 'dll'
    New-Folder $demo 'S\ProgramData\Package Cache' (3.1 * $GB) 25 200 'msi'
    New-Folder $demo 'S\Windows\System32' (13.5 * $GB) 120 30 'dll'
    New-Folder $demo 'S\Windows\WinSxS' (9.8 * $GB) 80 30 'dll'
    New-Folder $demo 'S\Windows\SoftwareDistribution\Download' (2.6 * $GB) 10 45 'cab'
    New-Folder $demo 'S\Windows\Temp' (420 * $MB) 15 20 'tmp'
    New-Folder $demo "S\Users\Alex\Videos\$($n.Vacation)" (28 * $GB) 45 380 'mp4'
    New-Folder $demo "S\Users\Alex\Videos\$($n.Recordings)" (9.5 * $GB) 18 40 'mkv'
    New-Folder $demo "S\Users\Alex\Pictures\$($n.Camera)" (12.4 * $GB) 300 120 'jpg'
    New-Folder $demo "S\Users\Alex\Documents\$($n.Bills)" (380 * $MB) 120 30 'pdf'
    New-Folder $demo "S\Users\Alex\Documents\$($n.Uni)" (2.1 * $GB) 200 500 'docx'
    New-Folder $demo "S\Users\Alex\Downloads\$($n.OldInstallers)" (6.3 * $GB) 14 260 'exe'
    New-Folder $demo 'S\Users\Alex\Downloads' (1.2 * $GB) 25 8 'zip'
    New-Folder $demo 'S\Users\Alex\Music' (7.8 * $GB) 400 700 'flac'
    New-Folder $demo "S\Users\Alex\$($n.Projects)\webshop\node_modules" (950 * $MB) 400 220 'js'
    New-Placeholder $demo "S\Users\Alex\$($n.Projects)\webshop\package.json" 2048 220
    New-Folder $demo 'S\Users\Alex\.nuget\packages' (2.2 * $GB) 120 40 'nupkg'
    New-Folder $demo 'S\Users\Alex\AppData\Local\Temp' (2.3 * $GB) 60 15 'tmp'
    New-Folder $demo 'S\Users\Alex\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data' (1.6 * $GB) 200 1 'bin'
    New-Placeholder $demo 'S\Users\Alex\AppData\Local\Google\Chrome\User Data\Default\History' (48 * $MB) 0
    New-Placeholder $demo 'S\Users\Alex\AppData\Local\Google\Chrome\User Data\Default\Network\Cookies' (3 * $MB) 0
    New-Folder $demo 'S\Users\Alex\AppData\Local\D3DSCache' (850 * $MB) 40 2 'bin'
    New-Folder $demo 'S\Users\Alex\AppData\Local\npm-cache' (720 * $MB) 300 30 'tgz'
    New-Folder $demo 'S\Users\Alex\AppData\Roaming\discord\Cache\Cache_Data' (430 * $MB) 80 1 'bin'
    New-Folder $demo 'S\Users\Alex\AppData\Roaming\Microsoft\Windows\Recent' (2 * $MB) 120 3 'lnk'
    New-Folder $demo 'S\Users\Alex\AppData\Local\Microsoft\Windows\Explorer' (160 * $MB) 6 4 'db'
    Rename-Item (Join-Path $demo 'S\Users\Alex\AppData\Local\Microsoft\Windows\Explorer\datei1.db') 'thumbcache_256.db'

    # ---------------------------------------------------------------- T: Datenplatte
    New-Folder $demo "T\$($n.Games)\Steam\steamapps\common\Open World Saga" (118 * $GB) 120 14 'pak'
    New-Folder $demo "T\$($n.Games)\Steam\steamapps\common\Tactical Ops" (64 * $GB) 90 90 'pak'
    New-Folder $demo "T\$($n.Games)\Epic Games\Arena Legends" (96 * $GB) 100 6 'pak'
    New-Folder $demo "T\$($n.Archive)\$($n.Backups)" (240 * $GB) 30 330 'zip'
    New-Folder $demo "T\$($n.Archive)\$($n.OldPhotos)" (180 * $GB) 400 600 'jpg'
    New-Folder $demo "T\$($n.Movies)" (420 * $GB) 60 200 'mkv'
}

function Invoke-Screenshots([string]$lang, [string]$theme, [string]$target) {
    $p = Start-Process $exe -ArgumentList '--screenshots', "`"$target`"", '--lang', $lang, '--theme', $theme -PassThru
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "Screenshot-Lauf haengt ($lang, $theme)." }
}

$shots = Join-Path $root 'docs\screenshots'
if (Test-Path $shots) { Remove-Item $shots -Recurse -Force }

foreach ($lang in 'de', 'en') {
    $demo = Join-Path $root "artifacts\demo-$lang"
    if (-not (Test-Path $demo)) {
        Write-Host "Demo-PC ($lang) anlegen ..."
        New-DemoPc $demo ($lang -eq 'de')
    }

    Write-Host "Screenshots ($lang) ..."
    subst S: (Join-Path $demo 'S')
    subst T: (Join-Path $demo 'T')
    try {
        Invoke-Screenshots $lang 'Dunkel' (Join-Path $shots $lang)
        Invoke-Screenshots $lang 'Hell' (Join-Path $shots "$lang\light")
    }
    finally {
        subst S: /D
        subst T: /D
    }
}
Get-ChildItem $shots -Recurse -Filter *.png | Select-Object FullName, Length
