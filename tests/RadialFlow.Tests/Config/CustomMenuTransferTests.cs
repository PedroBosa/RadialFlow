using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomMenuTransferTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // Utilitários
    // ------------------------------------------------------------------

    private static CustomMenuProfile Profile(string name = "Meu menu") => new()
    {
        Name = name,
        Priority = 10,
        ProcessName = "notepad",
        TitleContains = "Notas",
        Contexts = [RadialContextKind.FileOrFolder],
    };

    private static CustomMenuItem ActionItem(string label = "Copiar", RadialAction action = RadialAction.CopyPath) => new()
    {
        Label = label,
        Kind = CustomMenuItemKind.Action,
        Action = action,
    };

    private static CustomRoutineStep ActionStep(RadialAction action = RadialAction.CopyPath) => new()
    {
        Kind = CustomRoutineStepKind.Action,
        Action = action,
    };

    private static CustomMenuItem RoutineItem(params CustomRoutineStep[] steps)
    {
        var item = new CustomMenuItem { Label = "Rotina", Kind = CustomMenuItemKind.Routine };
        item.Steps.AddRange(steps);
        return item;
    }

    private static string Export(params CustomMenuProfile[] profiles)
    {
        Assert.True(CustomMenuTransfer.TryExportToJson(profiles, out string json, out string? reason), reason);
        return json;
    }

    private static List<CustomMenuProfile> Import(
        string json,
        IEnumerable<string?>? existingNames = null,
        int existingCount = 0)
    {
        Assert.True(
            CustomMenuTransfer.TryImportFromJson(
                json,
                (existingNames ?? Array.Empty<string?>()).ToList(),
                existingCount,
                out List<CustomMenuProfile> imported,
                out string? reason),
            reason);

        return imported;
    }

    private static void Reject(
        string json,
        string expected,
        IEnumerable<string?>? existingNames = null,
        int existingCount = 0)
    {
        Assert.False(CustomMenuTransfer.TryImportFromJson(
            json,
            (existingNames ?? Array.Empty<string?>()).ToList(),
            existingCount,
            out List<CustomMenuProfile> imported,
            out string? reason));

        Assert.Empty(imported);
        Assert.NotNull(reason);
        Assert.Contains(expected, reason);
    }

    /// <summary>Cria o JSON sem passar pela validação do exportador (para testar arquivos editados à mão).</summary>
    private static string RawBundle(
        IEnumerable<CustomMenuProfile> profiles,
        string? format = CustomMenuBundle.FormatName,
        int? version = CustomMenuBundle.CurrentVersion)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        return JsonSerializer.Serialize(new CustomMenuBundle
        {
            Format = format,
            Version = version,
            Profiles = profiles.ToList(),
        }, options);
    }

    private static string BundleWith(CustomMenuItem item)
    {
        CustomMenuProfile profile = Profile();
        profile.Items.Add(item);
        return RawBundle([profile]);
    }

    // ------------------------------------------------------------------
    // Ida e volta
    // ------------------------------------------------------------------

    [Fact]
    public void ExportarEImportar_PreservaCamposETodosOsTipos()
    {
        CustomMenuProfile source = Profile("Tudo");
        source.Priority = 7;
        source.ProcessName = "Notepad.exe";
        source.Contexts = [RadialContextKind.FileOrFolder, RadialContextKind.TextField];

        source.Items.Add(new CustomMenuItem
        {
            Label = "Ação", Glyph = "\uE8C8", Description = "copia o caminho",
            Kind = CustomMenuItemKind.Action, Action = RadialAction.CopyPath,
        });
        source.Items.Add(new CustomMenuItem { Label = "Abrir", Kind = CustomMenuItemKind.Launch, Target = "notepad.exe" });
        source.Items.Add(new CustomMenuItem { Label = "Site", Kind = CustomMenuItemKind.OpenUrl, Target = "https://example.com" });
        source.Items.Add(new CustomMenuItem { Label = "Arquivo", Kind = CustomMenuItemKind.OpenPath, Target = @"C:\Windows\notepad.exe" });
        source.Items.Add(new CustomMenuItem { Label = "Atalho", Kind = CustomMenuItemKind.Hotkey, Target = "Ctrl+Shift+P" });
        source.Items.Add(new CustomMenuItem { Label = "Texto", Kind = CustomMenuItemKind.Text, Text = "Olá, mundo" });

        var submenu = new CustomMenuItem { Label = "Submenu", Kind = CustomMenuItemKind.Submenu };
        submenu.Options.Add(ActionItem("Opção 1"));
        submenu.Options.Add(new CustomMenuItem { Label = "Opção 2", Kind = CustomMenuItemKind.Text, Text = "ok" });
        source.Items.Add(submenu);

        var routine = RoutineItem(
            ActionStep(RadialAction.CopyPath),
            new CustomRoutineStep { Kind = CustomRoutineStepKind.Delay, DelayMilliseconds = 120 },
            ActionStep(RadialAction.Paste));
        source.Items.Add(routine);

        string json = Export(source);
        Assert.Contains("\"format\": \"RadialFlow.Menus\"", json);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"kind\": \"OpenUrl\"", json);
        Assert.Contains("Olá, mundo", json);

        CustomMenuProfile copy = Assert.Single(Import(json));
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Tudo", copy.Name);
        Assert.Equal(7, copy.Priority);
        Assert.Equal("Notepad", copy.ProcessName);
        Assert.Equal("Notas", copy.TitleContains);
        Assert.Equal(new[] { RadialContextKind.FileOrFolder, RadialContextKind.TextField }, copy.Contexts);
        Assert.Equal(8, copy.Items.Count);

        Assert.Equal(CustomMenuItemKind.Action, copy.Items[0].Kind);
        Assert.Equal(RadialAction.CopyPath, copy.Items[0].Action);
        Assert.Equal("\uE8C8", copy.Items[0].Glyph);
        Assert.Equal("copia o caminho", copy.Items[0].Description);
        Assert.Equal("notepad.exe", copy.Items[1].Target);
        Assert.Equal("https://example.com", copy.Items[2].Target);
        Assert.Equal(@"C:\Windows\notepad.exe", copy.Items[3].Target);
        Assert.Equal("Ctrl+Shift+P", copy.Items[4].Target);
        Assert.Equal("Olá, mundo", copy.Items[5].Text);
        Assert.Equal(new[] { "Opção 1", "Opção 2" }, copy.Items[6].Options.Select(o => o.Label));
        Assert.Equal(3, copy.Items[7].Steps.Count);
        Assert.Equal(120, copy.Items[7].Steps[1].DelayMilliseconds);

        var originalIds = source.Items.Select(i => i.Id)
            .Concat(source.Items[6].Options.Select(o => o.Id))
            .ToHashSet(StringComparer.Ordinal);
        var copyIds = copy.Items.Select(i => i.Id)
            .Concat(copy.Items[6].Options.Select(o => o.Id))
            .ToList();

        Assert.All(copyIds, id => Assert.DoesNotContain(id, originalIds));
    }

    [Fact]
    public void Importar_NomesRepetidosGanhamSufixo()
    {
        string json = Export(Profile("Meu menu"), Profile("Meu menu"));

        List<CustomMenuProfile> imported = Import(json, existingNames: ["Meu menu"]);

        Assert.Equal(new[] { "Meu menu (importado)", "Meu menu (importado 2)" },
            imported.Select(p => p.Name));
        Assert.NotEqual(imported[0].Id, imported[1].Id);
    }

    [Fact]
    public void Importar_NomeNoLimiteComColisao_ContinuaDentroDoLimite()
    {
        string name = new('A', CustomMenuNormalizer.MaxNameLength);

        string imported = Assert.Single(Import(Export(Profile(name)), existingNames: [name])).Name;

        Assert.NotEqual(name, imported);
        Assert.True(imported.Length <= CustomMenuNormalizer.MaxNameLength);
        Assert.EndsWith("(importado)", imported);
    }

    // ------------------------------------------------------------------
    // Validação do arquivo
    // ------------------------------------------------------------------

    [Fact]
    public void Importar_RecusaArquivoVazioJsonInvalidoFormatoOuVersao()
    {
        Reject("", "vazio");
        Reject("{", "JSON");
        Reject("{}", "não é um menu");
        Reject("{\"format\":\"RadialFlow.Menus\",\"version\":1}", "não tem menus");
        Reject(RawBundle(Array.Empty<CustomMenuProfile>()), "não tem menus");
        Reject(RawBundle([Profile()], format: "Outro"), "não é um menu");
        Reject(RawBundle([Profile()], version: 2), "versão 2");
        Reject(RawBundle([Profile()], version: null), "não informa a versão");
    }

    [Fact]
    public void Importar_RecusaAcimaDosLimitesDePerfis()
    {
        CustomMenuProfile[] many = Enumerable
            .Range(0, CustomMenuNormalizer.MaxProfiles + 1)
            .Select(i => Profile($"Menu {i + 1}"))
            .ToArray();

        Reject(RawBundle(many), $"passa de {CustomMenuNormalizer.MaxProfiles} menus");

        Reject(
            RawBundle([Profile("A"), Profile("B")]),
            "Não há espaço",
            existingCount: CustomMenuNormalizer.MaxProfiles - 1);
    }

    [Fact]
    public void Importar_RecusaCamposDeMenuForaDosLimites()
    {
        Reject(
            RawBundle([Profile(new string('A', CustomMenuNormalizer.MaxNameLength + 1))]),
            $"nome passa de {CustomMenuNormalizer.MaxNameLength}");

        CustomMenuProfile longTitle = Profile();
        longTitle.TitleContains = new string('A', CustomMenuNormalizer.MaxDescriptionLength + 1);
        Reject(RawBundle([longTitle]), $"título contém passa de {CustomMenuNormalizer.MaxDescriptionLength}");

        CustomMenuProfile priority = Profile();
        priority.Priority = CustomMenuNormalizer.MaxPriority + 1;
        Reject(RawBundle([priority]), "prioridade");

        CustomMenuProfile manyItems = Profile();
        for (int i = 0; i < CustomMenuNormalizer.MaxItems + 1; i++)
            manyItems.Items.Add(ActionItem($"Item {i + 1}"));

        Reject(RawBundle([manyItems]), $"passa de {CustomMenuNormalizer.MaxItems} itens");
    }

    [Fact]
    public void Importar_RecusaCamposDeItemForaDosLimites()
    {
        CustomMenuProfile emptyLabel = Profile();
        CustomMenuItem empty = ActionItem();
        empty.Label = "   ";
        emptyLabel.Items.Add(empty);
        Reject(RawBundle([emptyLabel]), "rótulo está vazio");

        CustomMenuProfile longLabel = Profile();
        longLabel.Items.Add(ActionItem(new string('A', CustomMenuNormalizer.MaxLabelLength + 1)));
        Reject(RawBundle([longLabel]), $"rótulo passa de {CustomMenuNormalizer.MaxLabelLength}");

        CustomMenuProfile longDescription = Profile();
        CustomMenuItem described = ActionItem();
        described.Description = new string('A', CustomMenuNormalizer.MaxDescriptionLength + 1);
        longDescription.Items.Add(described);
        Reject(RawBundle([longDescription]), $"descrição passa de {CustomMenuNormalizer.MaxDescriptionLength}");

        CustomMenuProfile longTarget = Profile();
        longTarget.Items.Add(new CustomMenuItem
        {
            Label = "Abrir",
            Kind = CustomMenuItemKind.Launch,
            Target = new string('a', CustomMenuNormalizer.MaxTargetLength + 1),
        });
        Reject(RawBundle([longTarget]), $"alvo passa de {CustomMenuNormalizer.MaxTargetLength}");

        CustomMenuProfile longText = Profile();
        longText.Items.Add(new CustomMenuItem
        {
            Label = "Texto",
            Kind = CustomMenuItemKind.Text,
            Text = new string('a', CustomMenuNormalizer.MaxTextLength + 1),
        });
        Reject(RawBundle([longText]), $"texto passa de {CustomMenuNormalizer.MaxTextLength}");
    }

    [Fact]
    public void Importar_RecusaTipoAcaoOuContextoDesconhecidos()
    {
        CustomMenuProfile badKind = Profile();
        CustomMenuItem type = ActionItem();
        type.Kind = (CustomMenuItemKind)99;
        badKind.Items.Add(type);
        Reject(RawBundle([badKind]), "tipo 99");

        CustomMenuProfile badAction = Profile();
        CustomMenuItem action = ActionItem();
        action.Action = (RadialAction)999;
        badAction.Items.Add(action);
        Reject(RawBundle([badAction]), "ação 999");

        CustomMenuProfile badContext = Profile();
        badContext.Contexts = [(RadialContextKind)99];
        Reject(RawBundle([badContext]), "contexto 99");
    }

    [Fact]
    public void Importar_RecusaSubmenuAninhadoRotinaEmSubmenuEOpcoesDemais()
    {
        var nested = new CustomMenuItem { Label = "Ações", Kind = CustomMenuItemKind.Submenu };
        nested.Options.Add(new CustomMenuItem { Label = "Outro", Kind = CustomMenuItemKind.Submenu });
        nested.Options.Add(ActionItem());
        Reject(BundleWith(nested), "primeiro anel");

        var routineInside = new CustomMenuItem { Label = "Ações", Kind = CustomMenuItemKind.Submenu };
        routineInside.Options.Add(RoutineItem(ActionStep()));
        routineInside.Options.Add(ActionItem());
        Reject(BundleWith(routineInside), "primeiro anel");

        var tooMany = new CustomMenuItem { Label = "Grande", Kind = CustomMenuItemKind.Submenu };
        for (int i = 0; i < CustomMenuNormalizer.MaxOptions + 1; i++)
            tooMany.Options.Add(ActionItem($"Opção {i + 1}"));

        Reject(BundleWith(tooMany), $"passa de {CustomMenuNormalizer.MaxOptions} opções");
    }

    [Fact]
    public void Importar_RecusaRotinasInvalidas()
    {
        Reject(BundleWith(RoutineItem(ActionStep(RadialAction.Convert))), "não é permitida");

        CustomMenuItem outdated = RoutineItem(ActionStep());
        outdated.RoutineVersion = 2;
        Reject(BundleWith(outdated), "não é suportada");

        Reject(
            BundleWith(RoutineItem(
                ActionStep(RadialAction.SearchWeb),
                ActionStep(RadialAction.Paste))),
            "último passo");

        Reject(BundleWith(RoutineItem()), "não tem passos");
    }

    [Fact]
    public void Importar_DescartaOpcoesEPassosDoTipoErrado()
    {
        CustomMenuProfile profile = Profile();

        CustomMenuItem action = ActionItem();
        action.Options.Add(ActionItem("Opção"));
        action.Steps.Add(ActionStep());
        profile.Items.Add(action);

        var submenu = new CustomMenuItem { Label = "Submenu", Kind = CustomMenuItemKind.Submenu };
        submenu.Options.Add(ActionItem("Opção 1"));
        submenu.Options.Add(ActionItem("Opção 2"));
        submenu.Steps.Add(ActionStep());
        profile.Items.Add(submenu);

        CustomMenuItem routine = RoutineItem(ActionStep());
        routine.Options.Add(ActionItem("Opção"));
        profile.Items.Add(routine);

        CustomMenuProfile copy = Assert.Single(Import(RawBundle([profile])));
        Assert.Empty(copy.Items[0].Options);
        Assert.Empty(copy.Items[0].Steps);
        Assert.Empty(copy.Items[1].Steps);
        Assert.Equal(2, copy.Items[1].Options.Count);
        Assert.Empty(copy.Items[2].Options);
        Assert.Single(copy.Items[2].Steps);
    }

    [Fact]
    public void Importar_RecusaMenusItensOuPassosNulos()
    {
        CustomMenuProfile nullItem = Profile();
        nullItem.Items.Add(null!);
        Reject(RawBundle([nullItem]), "item de menu vazio");

        var nullStep = new CustomMenuItem { Label = "Rotina", Kind = CustomMenuItemKind.Routine };
        nullStep.Steps.Add(null!);
        Reject(BundleWith(nullStep), "passo de rotina vazio");

        Reject(RawBundle(new List<CustomMenuProfile> { null! }), "menu vazio");
    }

    // ------------------------------------------------------------------
    // Disco
    // ------------------------------------------------------------------

    [Fact]
    public void ExportarEImportarArquivo_UsaDiscoSemBomENaoDeixaTemporario()
    {
        string path = _temp.Combine("menus.json");
        CustomMenuProfile profile = Profile();
        profile.Items.Add(ActionItem());

        Assert.True(CustomMenuTransfer.TryExportToFile(path, [profile], out string? reason), reason);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Empty(Directory.GetFiles(_temp.Root, "*.tmp-*"));

        Assert.True(
            CustomMenuTransfer.TryImportFromFile(
                path, Array.Empty<string?>(), 0, out List<CustomMenuProfile> imported, out reason),
            reason);

        Assert.Equal("Meu menu", Assert.Single(imported).Name);
    }

    [Fact]
    public void Importar_RecusaArquivoInexistenteEVazio()
    {
        Assert.False(CustomMenuTransfer.TryImportFromFile(
            _temp.Combine("nao-existe.json"), Array.Empty<string?>(), 0, out _, out string? reason));
        Assert.Contains("Não foi possível ler", reason);

        string empty = _temp.Combine("vazio.json");
        File.WriteAllText(empty, "");

        Assert.False(CustomMenuTransfer.TryImportFromFile(
            empty, Array.Empty<string?>(), 0, out _, out reason));
        Assert.Contains("vazio", reason);
    }

    [Fact]
    public void Importar_RecusaArquivoAcimaDoLimite()
    {
        string path = _temp.Combine("grande.json");
        File.WriteAllText(path, new string(' ', CustomMenuTransfer.MaxFileCharacters + 1));

        Assert.False(CustomMenuTransfer.TryImportFromFile(
            path, Array.Empty<string?>(), 0, out _, out string? reason));

        Assert.Contains("8 MB", reason);
    }

    [Fact]
    public void Exportar_RecusaRascunhoVazioOuInvalido()
    {
        Assert.False(CustomMenuTransfer.TryExportToJson(
            Array.Empty<CustomMenuProfile>(), out _, out string? reason));
        Assert.Contains("Não há menus", reason);

        CustomMenuProfile emptyLabel = Profile();
        emptyLabel.Items.Add(new CustomMenuItem { Label = "", Kind = CustomMenuItemKind.Action });
        Assert.False(CustomMenuTransfer.TryExportToJson([emptyLabel], out _, out reason));
        Assert.Contains("rótulo está vazio", reason);

        CustomMenuProfile invalidRoutine = Profile();
        invalidRoutine.Items.Add(RoutineItem(ActionStep(RadialAction.Convert)));
        Assert.False(CustomMenuTransfer.TryExportToJson([invalidRoutine], out _, out reason));
        Assert.Contains("não é permitida", reason);
    }
}
