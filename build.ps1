# Compile Lueur en un seul Lueur.exe dans .\dist (necessite le SDK .NET 10).
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish Lueur.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw "La compilation a echoue." }

Copy-Item scripts\*.bat dist -Force
Write-Host "`nOK : $PSScriptRoot\dist\Lueur.exe" -ForegroundColor Green
