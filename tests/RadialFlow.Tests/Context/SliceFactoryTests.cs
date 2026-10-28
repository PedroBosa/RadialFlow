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
        Func<string, bool>? fileExists = null,
        IReadOnlyList<CustomMenuProfile>? profiles = null,
        Func<string, IReadOnlyList<ConversionOption>>? optionsFor = null) =>
        new(_actions,
            path =>
            {
                _optionRequests.Add(path);
                return optionsFor?.Invoke(path) ?? options ?? Array.Empty<ConversionOption>();
            },
            () => favorites,
            fileExists ?? (_ => true),
            profiles is null ? null : () => profiles);

    private static ContextInfo Context(RadialContextKind kind, params string[] paths) =>
        new(kind, Window, "explorer", 100, 200, paths.Length == 0 ? ContextInfo.NoPaths : paths);

    private static ConversionOption Option(string format) => new(format, $"{format.ToUpperInvariant()} — teste", "Teste");

    private static CustomMenuProfile Profile(params CustomMenuItem[] items) => new()
    {
        Name = "Teste",
        Items = items.ToList(),
    };

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

        var slices = SliceFactory.Build(ctx, Deps([Option("jpg")]));

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
        var customItem = new CustomMenuItem { Label = "Notas", Kind = CustomMenuItemKind.Launch, Target = "notepad.exe" };

        foreach (RadialContextKind kind in Enum.GetValues<RadialContextKind>())
        {
            SliceFactory.Build(Context(kind, SourceFile),
                Deps([Option("jpg")], [new FavoriteAppEntry("App", @"C:\app.exe")], profiles: [Profile(customItem)]));
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

        var conversion = Assert.Single(_actions.Conversions);
        Assert.Equal(new[] { SourceFile }, conversion.Sources);
        Assert.Equal("webp", conversion.Format);
        Assert.Empty(_actions.Runs);   // a sub-fatia converte direto, sem passar pelo diálogo
    }

    [Fact]
    public void Build_ConsultaAsOpcoesDeTodosOsArquivosSelecionados()
    {
        string second = @"C:\Users\Public\outro.jpg";   // extensão diferente: as opções vêm de cada formato

        SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile, second), Deps([Option("jpg")]));

        Assert.Equal(new[] { SourceFile, second }, _optionRequests);
    }

    [Fact]
    public void Build_VariosArquivos_UsaSomenteOsFormatosAceitosPorTodos()
    {
        string video = @"C:\Users\Public\video.mp4";
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile, video),
            Deps(optionsFor: path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? [Option("jpg"), Option("gif")]
                : [Option("mp4"), Option("gif")]));

        var convert = Assert.Single(slices, s => s.Label == "Converter");
        Assert.Equal(new[] { "GIF" }, convert.SubSlices!.Select(s => s.Label));

        convert.SubSlices![0].Execute!();

        var conversion = Assert.Single(_actions.Conversions);
        Assert.Equal(new[] { SourceFile, video }, conversion.Sources);
        Assert.Equal("gif", conversion.Format);
    }

    [Fact]
    public void Build_ArquivoJaNoFormatoDeDestino_NaoImpedeOFormatoComum()
    {
        string photo = @"C:\Users\Public\foto.jpg";
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, photo, SourceFile),
            Deps(optionsFor: path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? [Option("jpg"), Option("webp")]
                : [Option("png")]));

        var convert = Assert.Single(slices, s => s.Label == "Converter");

        // O JPG já está no formato e será ignorado; o PNG ainda pode virar JPG. WEBP não serve para o JPG.
        Assert.Equal(new[] { "PNG", "JPG" }, convert.SubSlices!.Select(s => s.Label));
    }

    [Fact]
    public void Build_SelecaoComPasta_EscondeConverterSemConsultarOpcoes()
    {
        string folder = @"C:\Users\Public\Fotos";
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile, folder),
            Deps([Option("jpg")],
                fileExists: path => !string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain(slices, s => s.Label == "Converter");
        Assert.Empty(_optionRequests);
    }

    [Fact]
    public void Build_VariosItens_DescricaoDeRenomearIndicaSelecao()
    {
        var slices = SliceFactory.Build(
            Context(RadialContextKind.FileOrFolder, SourceFile, @"C:\Users\Public\outro.png"),
            Deps([Option("jpg")]));

        Assert.Equal("Renomear seleção", slices.Single(s => s.Label == "Renomear").Description);
    }

    [Fact]
    public void Build_Pasta_EscondeConverterSemConsultarOpcoes()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, @"C:\Users\Public\Fotos"),
            Deps([Option("jpg")], fileExists: _ => false));

        Assert.DoesNotContain(slices, s => s.Label == "Converter");
        Assert.Empty(_optionRequests);
    }

    [Fact]
    public void Build_ArquivoSemNenhumaConversao_EscondeConverter()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile), Deps([]));

        Assert.Equal(new[] { "Comprimir", "Copiar caminho", "Mover para", "Enviar para", "Renomear" }, slices.Select(s => s.Label));
        Assert.Equal(new[] { SourceFile }, _optionRequests);
    }

    [Fact]
    public void Build_SemCaminhos_MostraAsOutrasFatiasDeArquivo()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.FileOrFolder), Deps([Option("jpg")]));

        Assert.Equal(5, slices.Count);
        Assert.DoesNotContain(slices, s => s.Label == "Converter");
        Assert.Empty(_optionRequests);
    }

    [Fact]
    public void Build_MaisOpcoesQueARodaComporta_PaginaComMaisSemDescartar()
    {
        var options = Enumerable.Range(1, RadialGeometry.MaxSlices + 4).Select(i => Option($"f{i}")).ToArray();

        var convert = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile), Deps(options))
            .Single(s => s.Label == "Converter");

        // Primeira página: 7 formatos + "Mais…"; nenhum formato some.
        var page = convert.SubSlices!;
        Assert.Equal(
            new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", RadialSlicePaging.MoreLabel },
            page.Select(s => s.Label));
        Assert.Equal(RadialSlicePaging.MoreGlyph, page[7].Glyph);
        Assert.Equal("Mais 5 opções", page[7].Description);
        Assert.Null(page[7].Execute);

        Assert.Equal(
            new[] { "F8", "F9", "F10", "F11", "F12" },
            page[7].SubSlices!.Select(s => s.Label));
    }

    [Fact]
    public void Build_SegundaPagina_ExecutaOFormatoCerto()
    {
        var options = Enumerable.Range(1, 12).Select(i => Option($"f{i}")).ToArray();
        var convert = SliceFactory.Build(Context(RadialContextKind.FileOrFolder, SourceFile), Deps(options))
            .Single(s => s.Label == "Converter");

        convert.SubSlices![7].SubSlices![4].Execute!();   // F12, já na segunda página

        var conversion = Assert.Single(_actions.Conversions);
        Assert.Equal(new[] { SourceFile }, conversion.Sources);
        Assert.Equal("f12", conversion.Format);
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
    // Menus personalizados (ligação com a roda)
    // ------------------------------------------------------------------

    [Fact]
    public void Build_SemPerfis_MantemAsFatiasAutomaticas()
    {
        var slices = SliceFactory.Build(Context(RadialContextKind.TitleBar), Deps(profiles: []));

        Assert.Equal(
            new[] { "Maximizar", "Snap dir.", "Fechar", "Transparência", "Sempre no topo", "Minimizar", "Snap esq." },
            slices.Select(s => s.Label));
        Assert.Empty(_actions.CustomRuns);
    }

    [Fact]
    public void Build_ItemExtra_ViraUltimaFatiaEDisparaRunCustom()
    {
        var item = new CustomMenuItem
        {
            Label = "Bloco de Notas",
            Glyph = "\uE70F",
            Description = "Abrir o Bloco de Notas",
            Kind = CustomMenuItemKind.Launch,
            Target = "notepad.exe",
        };

        var slices = SliceFactory.Build(Context(RadialContextKind.Generic), Deps(profiles: [Profile(item)]));

        Assert.Equal(5, slices.Count);
        Assert.Equal("Bloco de Notas", slices[^1].Label);
        Assert.Equal(0, _actions.TotalCalls);

        slices[^1].Execute!();

        var run = Assert.Single(_actions.CustomRuns);
        Assert.Same(item, run.Item);
        Assert.Equal(RadialContextKind.Generic, run.Context.Kind);
        Assert.Empty(_actions.Runs);   // não passa pelo roteador de ações nativas
    }

    [Fact]
    public void Build_ItemDeAcao_SubstituiFatiaAutomaticaNaMesmaPosicao()
    {
        var item = new CustomMenuItem
        {
            Label = "Fechar sem medo",
            Description = "Fechar a janela atual",
            Kind = CustomMenuItemKind.Action,
            Action = RadialAction.Close,
        };

        var ctx = Context(RadialContextKind.TitleBar);
        var slices = SliceFactory.Build(ctx, Deps(profiles: [Profile(item)]));

        Assert.Equal(7, slices.Count);
        Assert.Equal("Fechar sem medo", slices[2].Label);
        Assert.Equal("Fechar a janela atual", slices[2].Description);
        Assert.DoesNotContain(slices, s => s.Label == "Fechar");

        foreach (var slice in slices) slice.Execute!();

        Assert.Equal(item, Assert.Single(_actions.CustomRuns).Item);
        Assert.DoesNotContain(_actions.Runs, r => r.Action == RadialAction.Close);
        Assert.Equal(6, _actions.Runs.Count);
        Assert.All(_actions.Runs, r => Assert.Same(ctx, r.Context));
    }

    [Fact]
    public void Build_PerfilDeOutroProcesso_NaoAcrescentaNada()
    {
        var profile = Profile(new CustomMenuItem { Label = "Notas", Kind = CustomMenuItemKind.Launch, Target = "notepad.exe" });
        profile.ProcessName = "notepad";

        var slices = SliceFactory.Build(Context(RadialContextKind.Generic), Deps(profiles: [profile]));

        Assert.Equal(4, slices.Count);
        Assert.DoesNotContain(slices, s => s.Label == "Notas");
    }

    [Fact]
    public void Build_PerfilComTitulo_SoCasaQuandoOTituloContem()
    {
        var profile = Profile(new CustomMenuItem { Label = "Relatórios", Kind = CustomMenuItemKind.OpenPath, Target = @"C:\Docs" });
        profile.TitleContains = "relatório";

        var baseCtx = new ContextInfo(RadialContextKind.Generic, Window, "explorer", 100, 200, ContextInfo.NoPaths, "Relatório final");
        var withTitle = SliceFactory.Build(baseCtx, Deps(profiles: [profile]));
        var withoutTitle = SliceFactory.Build(baseCtx with { WindowTitle = "" }, Deps(profiles: [profile]));

        Assert.Contains(withTitle, s => s.Label == "Relatórios");
        Assert.DoesNotContain(withoutTitle, s => s.Label == "Relatórios");
    }

    [Fact]
    public void Build_FatiaComSubmenu_NaoEhSubstituidaPeloItemDeAcao()
    {
        var item = new CustomMenuItem
        {
            Label = "Meu converter",
            Kind = CustomMenuItemKind.Action,
            Action = RadialAction.Convert,
        };

        var slices = SliceFactory.Build(
            Context(RadialContextKind.FileOrFolder, SourceFile),
            Deps([Option("jpg")], profiles: [Profile(item)]));

        var automatic = slices[1];
        Assert.Equal("Converter", automatic.Label);
        Assert.NotNull(automatic.SubSlices);
        Assert.Equal("Meu converter", slices[^1].Label);

        slices[^1].Execute!();

        Assert.Same(item, Assert.Single(_actions.CustomRuns).Item);
        Assert.Empty(_actions.Runs);
    }

    [Fact]
    public void Build_LimiteDeOito_PreservaAutomaticasEDescartaExtras()
    {
        var first = new CustomMenuItem { Label = "Extra 1", Kind = CustomMenuItemKind.OpenUrl, Target = "https://example.com" };
        var second = new CustomMenuItem { Label = "Extra 2", Kind = CustomMenuItemKind.OpenUrl, Target = "https://example.org" };

        var slices = SliceFactory.Build(Context(RadialContextKind.TitleBar), Deps(profiles: [Profile(first, second)]));

        Assert.Equal(RadialGeometry.MaxSlices, slices.Count);
        Assert.Equal("Fechar", slices[2].Label);   // automáticas intactas
        Assert.Equal("Extra 1", slices[^1].Label);
        Assert.DoesNotContain(slices, s => s.Label == "Extra 2");
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
