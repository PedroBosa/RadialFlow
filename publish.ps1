<#
.SYNOPSIS
  Gera os arquivos de distribuição do RadialFlow na pasta .\dist.

.DESCRIPTION
  1. Roda os testes (se falharem, nada é publicado).
  2. Publica o programa autocontido (não exige o .NET instalado).
  3. Assina o RadialFlow.exe, se houver certificado (opcional).
  4. Gera o zip portátil.
  5. Gera o instalador com o Inno Setup 6, se estiver instalado (assinando instalador e desinstalador, se houver certificado).
  6. Grava SHA256SUMS.txt com os hashes de tudo.
  7. Apaga a pasta de publicação, deixando no dist só o zip, o instalador e os hashes (use -KeepStaging para mantê-la).

.PARAMETER CertThumbprint
  Impressão digital de um certificado de assinatura de código instalado no Windows (Cert:\CurrentUser\My ou
  Cert:\LocalMachine\My). Também pode vir da variável de ambiente RADIALFLOW_SIGN_THUMBPRINT. Sem isso, os arquivos
  saem sem assinatura e o Windows mostra o aviso do SmartScreen.

.PARAMETER TimestampUrl
  Servidor de carimbo de tempo (RFC 3161). Mantém a assinatura válida depois que o certificado vence.

.PARAMETER SkipTests
  Pula os testes. Use só se você acabou de rodá-los.

.PARAMETER KeepStaging
  Mantém a pasta RadialFlow-<versão>-win-x64 que o zip e o instalador empacotam. Por padrão, ela é apagada no fim
  para o dist ficar só com o zip, o instalador e os hashes. Use quando quiser inspecionar os arquivos publicados.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\publish.ps1

.EXAMPLE
  .\publish.ps1 -CertThumbprint "0123456789ABCDEF0123456789ABCDEF01234567"
#>
param(
    [string]$CertThumbprint = $env:RADIALFLOW_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipTests,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Done([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Note([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Versão: uma só fonte (o <Version> do src\RadialFlow\RadialFlow.csproj)
# ---------------------------------------------------------------------------
[xml]$csproj = Get-Content .\src\RadialFlow\RadialFlow.csproj
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'Não achei <Version> no src\RadialFlow\RadialFlow.csproj.' }

# O .iss precisa de um valor de reserva para compilar à mão; se ele divergir do csproj, o instalador sairia com a
# versão errada. Melhor falhar aqui.
$issAppVersion = Select-String -Path .\RadialFlow.iss -Pattern '#define AppVersion "([^"]+)"' | Select-Object -First 1
if (-not $issAppVersion) { throw 'Não achei #define AppVersion no RadialFlow.iss.' }
if ($issAppVersion.Matches[0].Groups[1].Value -ne $version) {
    throw "Versão divergente: csproj $version, RadialFlow.iss $($issAppVersion.Matches[0].Groups[1].Value). Atualize o #define AppVersion."
}

$name   = "RadialFlow-$version-win-x64"
$dist   = Join-Path $PSScriptRoot 'dist'
$outDir = Join-Path $dist $name

# ---------------------------------------------------------------------------
# Assinatura (opcional)
# ---------------------------------------------------------------------------
$signtool = $null
if ($CertThumbprint) {
    $CertThumbprint = ($CertThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $CertThumbprint } | Select-Object -First 1
    if (-not $cert) { throw "Certificado de assinatura de código $CertThumbprint não encontrado (ou não serve para assinar código)." }
    if ($cert.NotAfter -lt (Get-Date)) { throw "O certificado $CertThumbprint venceu em $($cert.NotAfter.ToString('d'))." }
    if (-not $cert.HasPrivateKey) { throw "O certificado $CertThumbprint está sem a chave privada." }

    $signtool = @(
        Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName
    )[0]
    if (-not $signtool) { throw 'signtool.exe não encontrado. Instale o Windows SDK (componente "Signing Tools").' }

    Write-Host "Assinando com: $($cert.Subject) (vence em $($cert.NotAfter.ToString('d')))" -ForegroundColor Cyan
}

function Sign-File([string]$path) {
    & $signtool sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'RadialFlow' $path
    if ($LASTEXITCODE -ne 0) { throw "signtool falhou ao assinar $path." }
}

# ---------------------------------------------------------------------------
# 1. Testes
# ---------------------------------------------------------------------------
if ($SkipTests) {
    Note 'Testes pulados (-SkipTests).'
} else {
    Step 'Rodando os testes'
    dotnet test -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam; nada foi publicado.' }
}

# ---------------------------------------------------------------------------
# 2. Publicação autocontida
# ---------------------------------------------------------------------------
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item $outDir -ItemType Directory | Out-Null

# Sem "arquivo único": o WPF extrairia DLLs nativas para uma pasta temporária a cada execução, o que é mais lento e,
# num programa que roda como administrador, carregaria código de uma pasta que outros programas podem alterar.
Step "Publicando $name (autocontido)"
dotnet publish .\src\RadialFlow\RadialFlow.csproj -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }

Copy-Item .\README.md, .\CHANGELOG.md $outDir
Copy-Item .\LICENSE $outDir
Copy-Item .\docs -Destination $outDir -Recurse

if ($signtool) {
    Step 'Assinando RadialFlow.exe'
    Sign-File (Join-Path $outDir 'RadialFlow.exe')
}

# ---------------------------------------------------------------------------
# 3. Zip portátil
# ---------------------------------------------------------------------------
$zip = Join-Path $dist "$name.zip"
Step 'Gerando o zip portátil'
Compress-Archive -Path "$outDir\*" -DestinationPath $zip
Done $zip

# ---------------------------------------------------------------------------
# 4. Instalador (opcional: precisa do Inno Setup 6 - https://jrsoftware.org/isinfo.php)
# ---------------------------------------------------------------------------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    Step 'Gerando o instalador'
    $isccArgs = @("/DAppVersion=$version", "/DSourceDir=$outDir", "/O$dist", '/Q')
    if ($signtool) {
        # O Inno Setup chama o signtool para o instalador e para o desinstalador ($f = arquivo a assinar).
        $isccArgs += "/Sradialflowsign=`"$signtool`" sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d RadialFlow `$f"
        $isccArgs += '/DSignTool=1'
    }
    & $iscc @isccArgs .\RadialFlow.iss
    if ($LASTEXITCODE -ne 0) { throw 'O Inno Setup falhou.' }
    Done (Join-Path $dist "RadialFlow-$version-Setup.exe")
} else {
    Note 'Inno Setup 6 não encontrado: pulei o instalador (o zip portátil já serve).'
}

# ---------------------------------------------------------------------------
# 5. Hashes
# ---------------------------------------------------------------------------
Step 'Calculando os hashes'
Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

# ---------------------------------------------------------------------------
# 6. Limpeza da pasta de publicação
# ---------------------------------------------------------------------------
if ($KeepStaging) {
    Note "Pasta de publicação mantida: $outDir"
} else {
    Remove-Item $outDir -Recurse -Force
    Done 'Pasta de publicação removida (use -KeepStaging para mantê-la).'
}

if (-not $signtool) {
    Note 'Os arquivos NÃO foram assinados: o Windows vai mostrar o aviso "O Windows protegeu o computador".'
}
Write-Host "Pronto. Arquivos em $dist" -ForegroundColor Green
