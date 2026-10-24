<#
  RadialFlow - gera o pacote para distribuição.
  Uso (na raiz do projeto, onde está RadialFlow.csproj):
      powershell -ExecutionPolicy Bypass -File .\publish.ps1

  Saídas (pasta .\dist):
      RadialFlow-<versão>-win-x64\RadialFlow.exe   exe único, autocontido (não exige instalar o .NET)
      RadialFlow-<versão>-win-x64.zip              versão portátil, pronta para enviar
      RadialFlow-<versão>-Setup.exe                instalador (somente se o Inno Setup estiver instalado)
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Lê a versão do próprio csproj (fonte única de verdade)
[xml]$csproj = Get-Content .\RadialFlow.csproj
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version }).Version
if (-not $version) { throw 'Não achei <Version> no RadialFlow.csproj.' }

$name   = "RadialFlow-$version-win-x64"
$dist   = Join-Path $PSScriptRoot 'dist'
$outDir = Join-Path $dist $name
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item $outDir -ItemType Directory | Out-Null

Write-Host "==> Rodando os testes..." -ForegroundColor Cyan
dotnet test -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam; nada foi publicado.' }

Write-Host "==> Publicando $name (autocontido, exe único)..." -ForegroundColor Cyan
dotnet publish .\RadialFlow.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }

# Arquivos de apoio para quem recebe o zip portátil
Copy-Item .\README.md, .\CHANGELOG.md $outDir -ErrorAction SilentlyContinue

# Zip portátil
$zip = Join-Path $dist "$name.zip"
Compress-Archive -Path "$outDir\*" -DestinationPath $zip
Write-Host "==> Zip portátil: $zip" -ForegroundColor Green

# Instalador (opcional) - precisa do Inno Setup 6: https://jrsoftware.org/isinfo.php
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    Write-Host "==> Gerando instalador..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" "/DSourceDir=$outDir" "/O$dist" .\RadialFlow.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou.' }
    Write-Host "==> Instalador: $dist\RadialFlow-$version-Setup.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup não encontrado: pulei o instalador (o zip portátil já serve)." -ForegroundColor Yellow
}

# Hashes para o seu amigo conferir a integridade
Get-ChildItem $dist -File | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "$h  $($_.Name)"
} | Set-Content (Join-Path $dist 'SHA256SUMS.txt')
Write-Host "==> Pronto. Envie o conteúdo de $dist" -ForegroundColor Green
