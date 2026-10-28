// ============================================================================
// RadialFlow - O que está instalado AGORA para converter
// ----------------------------------------------------------------------------
// Monta o ConversionToolset da máquina: codecs do Windows, ferramentas
// confiáveis (ToolResolver) e codificadores do FFmpeg. Tudo em cache nas
// camadas de baixo; Warm, chamado na partida, adianta as consultas lentas
// para a primeira roda não esperar por elas.
// ============================================================================

using System;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

internal static class ConversionTools
{
    /// <summary>Retrato atual. Não bloqueia por processos: os codificadores do FFmpeg chegam em segundo plano.</summary>
    public static ConversionToolset Current()
    {
        string? ffmpeg = ToolResolver.GetFFmpeg();
        return new ConversionToolset
        {
            WicReadable = WicCodecs.Readable,
            CanWriteHeic = WicCodecs.CanWriteHeic,
            HasFFmpeg = ffmpeg is not null,
            FfmpegEncoders = ffmpeg is null ? null : FfmpegEncoders.TryGet(ffmpeg),
            HasMagick = ToolResolver.GetImageMagick() is not null,
            HasPandoc = ToolResolver.GetPandoc() is not null,
            HasLibreOffice = ToolResolver.GetLibreOffice() is not null
        };
    }

    /// <summary>Adianta as consultas (codecs, ferramentas, FFmpeg). Nunca lança.</summary>
    public static void Warm()
    {
        try
        {
            ConversionToolset tools = Current();
            AppLog.Info("Conversion",
                $"Conversão: {tools.WicReadable.Count} formatos de imagem do Windows, HEIC {(tools.CanWriteHeic ? "sim" : "não")}, " +
                $"FFmpeg {(tools.HasFFmpeg ? "sim" : "não")}, ImageMagick {(tools.HasMagick ? "sim" : "não")}, " +
                $"Pandoc {(tools.HasPandoc ? "sim" : "não")}, LibreOffice {(tools.HasLibreOffice ? "sim" : "não")}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("Conversion", $"Falha ao verificar as ferramentas de conversão: {ex.Message}");
        }
    }
}
