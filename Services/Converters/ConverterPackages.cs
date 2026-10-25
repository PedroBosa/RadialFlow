// ============================================================================
// RadialFlow - Conversores que o RadialFlow sabe instalar
// ----------------------------------------------------------------------------
// Cada pacote vem do site OFICIAL do projeto, numa versão fixa, com o SHA-256
// conferido antes de instalar (os mesmos valores dos manifestos do winget).
// Para atualizar uma versão: troque Version, Url, Sha256 e SizeBytes juntos
// (winget show --id <id> mostra os três primeiros).
//
//   FFmpeg       zip "essentials"  -> extraído em <app>\tools\ffmpeg
//   ImageMagick  instalador Inno   -> C:\Program Files\ImageMagick-*
//   Pandoc       MSI               -> C:\Program Files\Pandoc (para todos)
//   LibreOffice  MSI               -> C:\Program Files\LibreOffice
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RadialFlow.Services;

internal enum PackageKind
{
    /// <summary>Zip baixado e extraído pelo próprio RadialFlow na pasta tools dele.</summary>
    ZipToAppTools,

    /// <summary>Pacote do Windows Installer, instalado para todos os usuários.</summary>
    Msi,

    /// <summary>Instalador Inno Setup, modo silencioso.</summary>
    InnoSetup
}

/// <param name="Id">Identificador usado na linha de comando (<c>--install-converters ffmpeg,pandoc</c>).</param>
/// <param name="ToolExe">Executável que o <see cref="ToolResolver"/> procura para saber se está instalado.</param>
/// <param name="Purpose">Para que serve, na língua de quem usa.</param>
internal sealed record ConverterPackage(
    string Id,
    string Name,
    string Purpose,
    string Version,
    Uri Url,
    string Sha256,
    long SizeBytes,
    PackageKind Kind,
    string ToolExe,
    string License)
{
    public string SizeText => $"{Math.Round(SizeBytes / 1024.0 / 1024.0):0} MB";

    /// <summary>Nome do arquivo baixado (o da própria URL).</summary>
    public string FileName => Uri.UnescapeDataString(Url.Segments[^1]);
}

internal static class ConverterPackages
{
    public static readonly ConverterPackage FFmpeg = new(
        "ffmpeg", "FFmpeg", "Vídeo e áudio: MP4, MKV, WEBM, MP3, M4A, FLAC, GIF animado...",
        "9.0.1",
        new Uri("https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip"),
        "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9",
        111_253_802, PackageKind.ZipToAppTools, "ffmpeg.exe", "GPL-3.0");

    public static readonly ConverterPackage ImageMagick = new(
        "imagemagick", "ImageMagick", "Imagens para WEBP e formatos que o Windows não abre (SVG, PSD...)",
        "7.1.2-34",
        new Uri("https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-34/ImageMagick-7.1.2-34-Q16-HDRI-x64-dll.exe"),
        "eb32ded8b605795dfcb08b8cadbeef6732fe2252af933b54b38df586332e1007",
        24_548_448, PackageKind.InnoSetup, "magick.exe", "ImageMagick (Apache-2.0)");

    public static readonly ConverterPackage Pandoc = new(
        "pandoc", "Pandoc", "Markdown e EPUB, e Word para Markdown",
        "3.12.1",
        new Uri("https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.msi"),
        "b6bf9f2c8408a1689272be5722e45f93ed9da17ba74321d0f7ef4e5a196605e8",
        41_869_312, PackageKind.Msi, "pandoc.exe", "GPL-2.0");

    public static readonly ConverterPackage LibreOffice = new(
        "libreoffice", "LibreOffice", "Word, Excel e PowerPoint para PDF e outros formatos (instala a suíte completa)",
        "26.8.1",
        // Endereço do arquivo histórico: continua válido quando sair uma versão nova (o de "stable" some).
        new Uri("https://downloadarchive.documentfoundation.org/libreoffice/old/26.8.1.1/win/x86_64/LibreOffice_26.8.1.1_Win_x86-64.msi"),
        "c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312",
        374_870_016, PackageKind.Msi, "soffice.exe", "MPL-2.0");

    /// <summary>Na ordem em que aparecem (e são instalados): do mais útil e leve ao mais pesado.</summary>
    public static IReadOnlyList<ConverterPackage> All { get; } = [FFmpeg, Pandoc, ImageMagick, LibreOffice];

    public static ConverterPackage? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lê "ffmpeg,pandoc" (vírgulas ou espaços). Ids desconhecidos vão para <paramref name="unknown"/>;
    /// repetidos contam uma vez; a ordem segue <see cref="All"/>.
    /// </summary>
    public static List<ConverterPackage> Parse(string? list, out List<string> unknown)
    {
        unknown = new List<string>();
        var chosen = new HashSet<ConverterPackage>();

        foreach (string id in (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Find(id) is { } package) chosen.Add(package);
            else unknown.Add(id);
        }

        return All.Where(chosen.Contains).ToList();
    }

    /// <summary>Argumentos do instalador do pacote (sem interface, para todos os usuários, sem reiniciar).</summary>
    internal static List<string> InstallerArguments(ConverterPackage package, string installerPath, string logPath) => package.Kind switch
    {
        PackageKind.Msi => [
            "/i", installerPath, "/qn", "/norestart", "ALLUSERS=1",
            .. package == LibreOffice
                // Não toma o lugar do Office nos .docx/.xlsx, sem atalho na área de trabalho, sem verificador de
                // atualizações e sem o "início rápido" na inicialização.
                ? new[] { "REGISTER_NO_MSO_TYPES=1", "CREATEDESKTOPLINK=0", "ISCHECKFORPRODUCTUPDATES=0", "QUICKSTART=0", "RebootYesNo=No" }
                : [],
            "/l*v", logPath],
        PackageKind.InnoSetup => ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/ALLUSERS", $"/LOG={logPath}"],
        _ => throw new ArgumentOutOfRangeException(nameof(package), package.Kind, "Este pacote não usa instalador.")
    };

    /// <summary>Códigos de saída de instalação que contam como sucesso (3010/1641: pronto, reinicia depois).</summary>
    internal static bool IsSuccessExitCode(int code) => code is 0 or 3010 or 1641;

    /// <summary>Mensagem para um código de saída de falha do Windows Installer / Inno Setup.</summary>
    internal static string DescribeExitCode(ConverterPackage package, int code) => code switch
    {
        1618 => "outra instalação está em andamento no Windows. Espere ela terminar e tente de novo.",
        1602 or 1223 => "a instalação foi cancelada.",
        1603 when package.Kind == PackageKind.Msi => "o instalador do Windows encontrou um erro (código 1603).",
        1638 => "já existe outra versão instalada que impede esta.",
        _ => $"o instalador terminou com o código {code}."
    };
}
