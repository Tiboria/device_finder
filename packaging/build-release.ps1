# Device Finder — génération des deux livrables de release (à exécuter sur Windows)
#   publish\DeviceFinder-Setup.exe        : installeur hors-ligne (Inno Setup)
#   publish\DeviceFinder-portable-win-x64.zip : version portable pour clé USB
#
# Prérequis : SDK .NET 8 + Inno Setup 6 (ISCC.exe). Aucune connexion internet
# n'est nécessaire au moment de l'installation ni à l'exécution de l'application.
#
# Usage : powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1

$ErrorActionPreference = "Stop"

$repo   = Split-Path -Parent $PSScriptRoot
$pubDir = Join-Path $repo "publish"
$appDir = Join-Path $pubDir "DeviceFinder-win-x64"
$zipPath = Join-Path $pubDir "DeviceFinder-portable-win-x64.zip"

# --- 1. Publication self-contained, sans PDB ------------------------------
# -p:DebugType=none : aucun fichier .pdb n'est émis, donc aucun chemin de la
# machine de build ne peut se retrouver dans les livrables (neutralité).
Write-Host "==> Publication self-contained win-x64..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\DeviceFinder.App\DeviceFinder.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=none `
    -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué (code $LASTEXITCODE)." }

# --- 2. Version lue depuis l'assembly publié (source unique : csproj) ------
$dll = Get-Item (Join-Path $appDir "DeviceFinder.dll")
$version = $dll.VersionInfo.ProductVersion
Write-Host "==> Version : $version" -ForegroundColor Cyan

if (Test-Path (Join-Path $appDir "*.pdb")) { throw "Des fichiers .pdb sont présents dans la sortie." }

# --- 3. Installeur Inno Setup ----------------------------------------------
$iscc = $null
foreach ($candidat in @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe")) {
    if (Test-Path $candidat) { $iscc = $candidat; break }
}
if (-not $iscc) { $iscc = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "ISCC.exe introuvable : installe Inno Setup (https://jrsoftware.org/isdl.php) ou ajoute-le au PATH." }

Write-Host "==> Installeur Inno Setup ($([System.IO.Path]::GetFileName($iscc)))..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot "DeviceFinder.iss") "/DAppVersion=$version"
if ($LASTEXITCODE -ne 0) { throw "ISCC a échoué (code $LASTEXITCODE)." }

# --- 4. Zip portable --------------------------------------------------------
Write-Host "==> Zip portable..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# Contenu : un dossier DeviceFinder-win-x64\ décompressable tel quel sur une clé,
# exécutable directement (DeviceFinder.exe), sans installation ni prérequis.
Compress-Archive -Path $appDir -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Livrables générés :" -ForegroundColor Green
Get-Item (Join-Path $pubDir "DeviceFinder-Setup.exe") | ForEach-Object { "  $($_.FullName)  ($([math]::Round($_.Length/1MB,1)) Mo)" }
Get-Item $zipPath | ForEach-Object { "  $($_.FullName)  ($([math]::Round($_.Length/1MB,1)) Mo)" }
