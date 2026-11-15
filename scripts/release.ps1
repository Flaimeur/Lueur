# Publie une nouvelle version de Lueur sur GitHub (les installations se mettent a jour toutes seules).
#   1. monte <Version> dans Lueur.csproj (ex. 1.2.0)
#   2. powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Notes "Ce qui change"
# Produit un Lueur.exe autonome (sans .NET a installer) + Lueur.exe.sha256, pousse le code et cree la Release v<Version>.
param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content Lueur.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version introuvable dans Lueur.csproj" }
$tag = "v$version"
if (gh release view $tag 2>$null) { throw "La release $tag existe deja : monte la version dans Lueur.csproj." }
if (git status --porcelain -- Core *.cs *.xaml Lueur.csproj) { throw "Des fichiers de l'appli ne sont pas commites." }

$out = Join-Path $root 'release'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Lueur.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "La compilation a echoue." }

$exe = Join-Path $out 'Lueur.exe'
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
"$sha  Lueur.exe" | Set-Content (Join-Path $out 'Lueur.exe.sha256') -Encoding ascii -NoNewline

git push origin main
if (-not $Notes) { $Notes = "Lueur $version" }
gh release create $tag $exe (Join-Path $out 'Lueur.exe.sha256') --title "Lueur $version" --notes $Notes
"Release $tag publiee : $sha"
