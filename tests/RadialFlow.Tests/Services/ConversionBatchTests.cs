using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class ConversionBatchTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ConversionOption Option(string format) => new(format, format.ToUpperInvariant(), "Teste");

    // ------------------------------------------------------------------
    // Formatos comuns
    // ------------------------------------------------------------------

    [Fact]
    public void GetCommonOptions_SoMantemOQueTodosAceitam()
    {
        var byName = new Dictionary<string, IReadOnlyList<ConversionOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["foto.png"] = [Option("jpg"), Option("gif"), Option("webp")],
            ["video.mp4"] = [Option("mp4"), Option("gif")],
        };

        var common = ConversionBatch.GetCommonOptions(
            [@"C:\a\foto.png", @"C:\a\video.mp4"],
            path => byName[Path.GetFileName(path)]);

        Assert.Equal(new[] { "gif" }, common.Select(o => o.TargetFormat));
    }

    [Fact]
    public void GetCommonOptions_ArquivoJaNoFormatoNaoBloqueiaOsDemais()
    {
        var byName = new Dictionary<string, IReadOnlyList<ConversionOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["foto.jpg"] = [Option("png")],
            ["imagem.png"] = [Option("jpg"), Option("webp")],
        };

        var common = ConversionBatch.GetCommonOptions(
            [@"C:\a\foto.jpg", @"C:\a\imagem.png"],
            path => byName[Path.GetFileName(path)]);

        // PNG converte a foto JPG; JPG converte a imagem PNG (a foto já está no formato e será ignorada).
        // WEBP não serve para a foto JPG.
        Assert.Equal(new[] { "png", "jpg" }, common.Select(o => o.TargetFormat));
    }

    [Fact]
    public void GetCommonOptions_SemCaminhos_NaoConsultaNada()
    {
        bool called = false;

        var common = ConversionBatch.GetCommonOptions([], _ =>
        {
            called = true;
            return [Option("jpg")];
        });

        Assert.Empty(common);
        Assert.False(called);
    }

    // ------------------------------------------------------------------
    // Plano de destinos
    // ------------------------------------------------------------------

    [Fact]
    public void TryPlan_MesmoNomeEmPastasDiferentes_ReservaDestinosDistintos()
    {
        string first = _temp.CreateFile("A", "foto.png");
        string second = _temp.CreateFile("B", "foto.png");
        string output = _temp.CreateDirectory("Saida");

        Assert.True(ConversionBatch.TryPlan([first, second], "webp", output, out var plan, out string? error), error);

        Assert.Null(error);
        Assert.Equal(new[] { first, second }, plan!.Jobs.Select(j => j.Source));
        Assert.Equal(_temp.Combine("Saida", "foto.webp"), plan.Jobs[0].Destination);
        Assert.Equal(_temp.Combine("Saida", "foto (1).webp"), plan.Jobs[1].Destination);
        Assert.Equal("webp", plan.TargetFormat);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void TryPlan_SemPastaDeDestino_SalvaAoLadoDeCadaOrigem()
    {
        string first = _temp.CreateFile("A", "foto.png");
        string second = _temp.CreateFile("B", "foto.png");

        Assert.True(ConversionBatch.TryPlan([first, second], "jpg", null, out var plan, out _));

        Assert.Null(plan!.DestinationFolder);
        Assert.Equal(_temp.Combine("A", "foto.jpg"), plan.Jobs[0].Destination);
        Assert.Equal(_temp.Combine("B", "foto.jpg"), plan.Jobs[1].Destination);
    }

    [Fact]
    public void TryPlan_ArquivoJaNoFormato_EhIgnoradoComSeguranca()
    {
        string jpg = _temp.CreateFile("foto.jpg");
        string png = _temp.CreateFile("imagem.png");
        string output = _temp.CreateDirectory("Saida");

        Assert.True(ConversionBatch.TryPlan([jpg, png], "jpg", output, out var plan, out _));

        var job = Assert.Single(plan!.Jobs);
        Assert.Equal(png, job.Source);
        Assert.Equal(_temp.Combine("Saida", "imagem.jpg"), job.Destination);
        Assert.Equal(new[] { jpg }, plan.Skipped);
    }

    [Fact]
    public void TryPlan_TodosJaNoFormato_EhRecusado()
    {
        string jpg = _temp.CreateFile("foto.jpg");

        Assert.False(ConversionBatch.TryPlan([jpg], "JPEG", null, out var plan, out string? error));

        Assert.Null(plan);
        Assert.Contains("já estão em .jpg", error);
    }

    [Fact]
    public void TryPlan_Duplicatas_SaoContadasUmaVez()
    {
        string file = _temp.CreateFile("foto.png");
        string output = _temp.CreateDirectory("Saida");

        Assert.True(ConversionBatch.TryPlan([file, file], "jpg", output, out var plan, out _));

        Assert.Single(plan!.Jobs);
    }

    [Fact]
    public void TryPlan_ArquivoInexistente_RecusaOLoteInteiro()
    {
        string exists = _temp.CreateFile("foto.png");
        string missing = _temp.Combine("sumiu.png");

        Assert.False(ConversionBatch.TryPlan([exists, missing], "jpg", null, out var plan, out string? error));

        Assert.Null(plan);
        Assert.Contains("inexistente", error);
    }

    [Fact]
    public void TryPlan_PastaNaSelecao_EhRecusada()
    {
        string folder = _temp.CreateDirectory("Fotos");

        Assert.False(ConversionBatch.TryPlan([folder], "jpg", null, out _, out string? error));
        Assert.Contains("inválido ou inexistente", error);
    }

    [Fact]
    public void TryPlan_OrigemComTravessia_EhRecusada()
    {
        Assert.False(ConversionBatch.TryPlan([@"..\foto.png"], "jpg", null, out _, out string? error));
        Assert.Contains("inválido", error);
    }

    [Fact]
    public void TryPlan_PastaDeDestinoInexistente_EhRecusada()
    {
        string file = _temp.CreateFile("foto.png");

        Assert.False(ConversionBatch.TryPlan([file], "jpg", _temp.Combine("NaoExiste"), out _, out string? error));
        Assert.Contains("pasta de destino", error);
    }

    [Theory]
    [InlineData(@"..\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(null)]
    public void TryPlan_FormatoInvalido_EhRecusado(string? format)
    {
        string file = _temp.CreateFile("foto.png");

        Assert.False(ConversionBatch.TryPlan([file], format, null, out _, out string? error));
        Assert.Contains("Formato de destino", error);
    }

    [Fact]
    public void TryPlan_SemArquivos_EhRecusado()
    {
        Assert.False(ConversionBatch.TryPlan([], "jpg", null, out var plan, out string? error));

        Assert.Null(plan);
        Assert.Contains("Nenhum arquivo", error);
    }
}
