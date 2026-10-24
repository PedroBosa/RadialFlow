using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

public sealed class SliceFactoryTests
{
    private const string SourceFile = @"C:\Users\Public\foto.png";
    private static readonly IntPtr Window = new(0x1234);

    private readonly RecordingSliceActions _actions = new();
    private readonly List<string> _optionRequests = new();

    private SliceFactoryDependencies Deps(
        IReadOnlyList<ConversionOption>? options = null,
        IReadOnlyList<FavoriteAppEntry>? favorites = null,
        Func<string, bool>? fileExists = null) =>
        new(_actions,
            path =>
            {
                _optionRequests.Add(path);
                return options ?? Array.Empty<ConversionOption>();
            },
            () => favorites,
            fileExists ?? (_ => true));

    private static ContextInfo Context(RadialContextKind kind, params string[] paths) =>
        new(kind, Window, "explorer", 100, 200, paths.Length == 0 ? ContextInfo.NoPaths : paths);

    private static ConversionOption Option(string format) => new(format, $"{format.ToUpperInvariant()} — teste", "Teste");

    // ------------------------------------------------------------------
    // Fatias por contexto detectado
    // ------------------------------------------------------------------

    public static TheoryData<RadialContextKind, string[], RadialAction[]> ExpectedSlices => new()
    {
        {
            RadialContextKind.FileOrFolder,
            ["Comprimir", "Converter", "Copiar caminho", "Mover para", "Enviar para", "Renomear"],
            [RadialAction.Compress, RadialAction.Convert, RadialAction.CopyPath, RadialAction.MoveTo, RadialAction.SendTo, RadialAction.Rename]
        },
        {
            RadialContextKind.TitleBar,
            ["Maximizar", "Snap dir.", "Fechar", "Transparência", "Sempre no topo", "Minimizar", "Snap esq."],
            [RadialAction.Maximize, RadialAction.SnapRight, RadialAction.Close, RadialAction.Transparency, RadialAction.AlwaysOnTop, RadialAction.Minimize, RadialAction.SnapLeft]
        },
        {
            RadialContextKind.TextField,
            ["Colar", "Traduzir", "Pesquisar", "Formatar", "Limpar"],
            [RadialAction.Paste, RadialAction.Translate, RadialAction.SearchWeb, RadialAction.Format, RadialAction.Clear]
        },
        {
            RadialContextKind.DesktopBackground,
            ["Apps favoritos", "Ajustes rápidos", "Área de transf.", "Bloquear"],
            [RadialAction.FavoriteApps, RadialAction.SystemControls, RadialAction.ClipboardHistory, RadialAction.LockScreen]
        },
        {
            RadialContextKind.Generic,
            ["Apps favoritos", "Ajustes rápidos", "Área de transf.", "Bloquear"],
            [RadialAction.FavoriteApps, RadialAction.SystemControls, RadialAction.ClipboardHistory, RadialAction.LockScreen]
        },
    };

    [Theory]
    [MemberData(nameof(ExpectedSlices))]
    public void Build_GeraAsFatiasDoContextoNaOrdemCerta(RadialContextKind kind, string[] labels, RadialAction[] actions)
    {
        var ctx = Context(kind, SourceFile);

        var slices = SliceFactory.Build(ctx, Deps());

        Assert.Equal(labels, slices.Select(s => s.Label));

        foreach (var slice in slices) slice.Execute!();

        Assert.Equal(actions, _actions.Runs.Select(r => r.Action));
        Assert.All(_actions.Runs, r => Assert.Same(ctx, r.Context));   // o contexto detectado chega intacto ao roteador
    }

    [Theory]
    [InlineData(RadialContextKind.Generic)]
    [InlineData(RadialContextKind.FileOrFolder)]
    [InlineData(RadialContextKind.DesktopBackground)]
    [InlineData(RadialContextKind.TitleBar)]
    [InlineData(RadialContextKind.TextField)]
    public void Build_TodaFatiaCabeNaRodaETemConteudo(RadialContextKind kind)
    {
        var slices = SliceFactory.Build(Context(kind, SourceFile), Deps());

        Assert.InRange(slices.Count, RadialGeometry.MinSlices, RadialGeometry.MaxSlices);
        Assert.All(slices, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrEmpty(s.Glyph));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.NotNull(s.Execute);
        });
    }

    [Fact]
    public void Build_TodosOsTiposDeContextoSaoCobertos()
    {
        foreach (RadialContextKind kind in Enum.GetValues<RadialContextKind>())
            Assert.NotEmpty(SliceFactory.Build(Context(kind), Deps()));
    }

    [Fact]
    public void Build_NaoExecutaNenhumaAcaoAoMontar()
    {
        foreach (RadialContextKind kind in Enum.GetValues<RadialContextKind>())
        {
            SliceFactory.Build(Context(kind, SourceFile),
                Deps([Option("jpg")], [new FavoriteAppEntry("App", @"C:\app.exe")]));
        }

        Assert.Equal(0, _actions.TotalCalls);
    }

    // ------------------------------------------------------------------
    // Sub-fatias de conversão
    // ------------------------------------------------------------------

    [Fact]
    public void Build_ArquivoComOpcoes_ExpandeConverterComOsFormatosReais()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile),
            Deps([Option("jpg"), Option("webp"), Option("mp3")]));

        var convert = Assert.Single(slices, s => s.Label == "Converter");
        Assert.NotNull(convert.SubSlices);
        Assert.Equal(new[] { "JPG", "WEBP", "MP3" }, convert.SubSlices!.Select(s => s.Label));
        Assert.Equal(new[] { "JPG — teste", "WEBP — teste", "MP3 — teste" }, convert.SubSlices!.Select(s => s.Description));
        Assert.Equal(SliceFactory.GetFormatGlyph("jpg"), convert.SubSlices![0].Glyph);
        Assert.Equal(new[] { SourceFile }, _optionRequests);

        convert.SubSlices![1].Execute!();

        Assert.Equal((SourceFile, "webp"), Assert.Single(_actions.Conversions));
        Assert.Empty(_actions.Runs);   // a sub-fatia converte direto, sem passar pelo diálogo
    }

    [Fact]
    public void Build_UsaApenasOPrimeiroCaminhoParaAsOpcoes()
    {
        SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile, @"C:\Users\Public\outro.png"),
            Deps([Option("jpg")]));

        Assert.Equal(new[] { SourceFile }, _optionRequests);
    }

    [Fact]
    public void Build_Pasta_NaoConsultaOpcoesDeConversao()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, @"C:\Users\Public\Fotos"),
            Deps([Option("jpg")], fileExists: _ => false));

        Assert.Null(slices.Single(s => s.Label == "Converter").SubSlices);
        Assert.Empty(_optionRequests);
    }

    [Fact]
    public void Build_SemOpcoes_ConverterNaoTemSubFatias()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile), Deps([]));

        Assert.Null(slices.Single(s => s.Label == "Converter").SubSlices);
    }

    [Fact]
    public void Build_SemCaminhos_AindaMostraAsFatiasDeArquivo()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder), Deps([Option("jpg")]));

        Assert.Equal(6, slices.Count);
        Assert.Null(slices.Single(s => s.Label == "Converter").SubSlices);
        Assert.Empty(_optionRequests);
    }

    [Fact]
    public void Build_MaisOpcoesQueARodaComporta_LimitaAoMaximo()
    {
        var options = Enumerable.Range(1, RadialGeometry.MaxSlices + 4).Select(i => Option($"f{i}")).ToArray();

        var convert = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile), Deps(options))
            .Single(s => s.Label == "Converter");

        Assert.Equal(RadialGeometry.MaxSlices, convert.SubSlices!.Count);
        Assert.Equal("F1", convert.SubSlices[0].Label);
    }

    // ------------------------------------------------------------------
    // Sub-fatias de apps favoritos
    // ------------------------------------------------------------------

    [Fact]
    public void Build_Favoritos_ViramSubFatiasQueLancamOAppCerto()
    {
        var notepad = new FavoriteAppEntry("Bloco de Notas", "notepad.exe", "", "\uE70F");
        var calc = new FavoriteAppEntry("Calculadora", "calc.exe", "", "");
        var slices = SliceFactory.Build(Context(RadialContextKind.DesktopBackground), Deps(favorites: [notepad, calc]));

        var favorites = slices.Single(s => s.Label == "Apps favoritos").SubSlices!;

        Assert.Equal(new[] { "Bloco de Notas", "Calculadora" }, favorites.Select(s => s.Label));
        Assert.Equal("\uE70F", favorites[0].Glyph);
        Assert.Equal("\uE734", favorites[1].Glyph);   // glifo padrão quando vazio
        Assert.Equal("Abrir Calculadora", favorites[1].Description);

        favorites[1].Execute!();

        Assert.Same(calc, Assert.Single(_actions.Launches));
    }

    [Fact]
    public void Build_SemFavoritos_FatiaPrincipalNaoTemSubFatias()
    {
        var withNull = SliceFactory.Build(Context(RadialContextKind.Generic), Deps(favorites: null));
        var withEmpty = SliceFactory.Build(Context(RadialContextKind.Generic), Deps(favorites: []));

        Assert.Null(withNull.Single(s => s.Label == "Apps favoritos").SubSlices);
        Assert.Null(withEmpty.Single(s => s.Label == "Apps favoritos").SubSlices);
    }

    [Fact]
    public void Build_FavoritosDemais_LimitaAoMaximoEIgnoraEntradasNulas()
    {
        var apps = new List<FavoriteAppEntry> { null! };
        apps.AddRange(Enumerable.Range(1, RadialGeometry.MaxSlices + 3).Select(i => new FavoriteAppEntry($"App {i}", $@"C:\app{i}.exe")));

        var favorites = SliceFactory.Build(Context(RadialContextKind.Generic), Deps(favorites: apps))
            .Single(s => s.Label == "Apps favoritos").SubSlices!;

        Assert.Equal(RadialGeometry.MaxSlices, favorites.Count);
        Assert.Equal("App 1", favorites[0].Label);
    }

    // ------------------------------------------------------------------
    // Glifos
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("png", "\uEB9F")]
    [InlineData("JPG", "\uEB9F")]
    [InlineData("mp4", "\uE714")]
    [InlineData("gif", "\uE714")]
    [InlineData("flac", "\uE8D6")]
    [InlineData("pdf", "\uE8A5")]
    [InlineData("xyz", "\uE8AB")]
    public void GetFormatGlyph_AgrupaPorTipoDeMidia(string format, string glyph)
    {
        Assert.Equal(glyph, SliceFactory.GetFormatGlyph(format));
    }
}
