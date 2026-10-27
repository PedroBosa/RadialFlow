using System;
using System.IO;
using System.Linq;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomMenuStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private SettingsStore Create() => new(_temp.Combine("cfg"));

    private static CustomMenuProfile Profile(string name = "Meu menu") => new()
    {
        Id = "perfil-1",
        Name = name,
        Priority = 10,
        ProcessName = "notepad",
        TitleContains = "Notas",
        Contexts = [RadialContextKind.FileOrFolder],
        Items =
        [
            new CustomMenuItem
            {
                Id = "item-1",
                Label = "Calc",
                Glyph = "\uE8EF",
                Description = "Abrir calculadora",
                Kind = CustomMenuItemKind.Launch,
                Target = "calc.exe"
            },
            new CustomMenuItem
            {
                Id = "item-2",
                Label = "Fechar",
                Kind = CustomMenuItemKind.Action,
                Action = RadialAction.Close
            }
        ]
    };

    private static AppSettings SettingsWith(params CustomMenuProfile[] profiles) => new()
    {
        CustomProfiles = profiles.ToList()
    };

    // ------------------------------------------------------------------
    // Ida e volta
    // ------------------------------------------------------------------

    [Fact]
    public void SaveELoad_PreservaPerfisEItens()
    {
        SettingsStore store = Create();
        store.Save(SettingsWith(Profile()));

        var loaded = store.Load();

        Assert.Equal(SettingsSource.File, loaded.Source);
        CustomMenuProfile profile = Assert.Single(loaded.Settings.CustomProfiles);
        Assert.Equal("perfil-1", profile.Id);
        Assert.Equal("Meu menu", profile.Name);
        Assert.Equal(10, profile.Priority);
        Assert.Equal("notepad", profile.ProcessName);
        Assert.Equal("Notas", profile.TitleContains);
        Assert.Equal(RadialContextKind.FileOrFolder, Assert.Single(profile.Contexts));

        Assert.Equal(2, profile.Items.Count);
        Assert.Equal("Calc", profile.Items[0].Label);
        Assert.Equal(CustomMenuItemKind.Launch, profile.Items[0].Kind);
        Assert.Equal("calc.exe", profile.Items[0].Target);
        Assert.Equal("\uE8EF", profile.Items[0].Glyph);
        Assert.Equal(RadialAction.Close, profile.Items[1].Action);
    }

    [Fact]
    public void JsonSemCustomProfiles_ViraListaVazia()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """{ "DeadzonePixels": 19 }""");

        var loaded = store.Load().Settings;

        Assert.NotNull(loaded.CustomProfiles);
        Assert.Empty(loaded.CustomProfiles);
    }

    [Fact]
    public void CreateDefault_NaoTrazPerfisPersonalizados()
    {
        Assert.Empty(AppSettings.CreateDefault().CustomProfiles);
    }

    // ------------------------------------------------------------------
    // Normalização
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_RemovePerfisEItensNulos()
    {
        CustomMenuProfile profile = Profile();
        profile.Items.Add(null!);
        var settings = SettingsWith(profile, null!);

        SettingsStore.Normalize(settings);

        CustomMenuProfile only = Assert.Single(settings.CustomProfiles);
        Assert.Equal(2, only.Items.Count);
    }

    [Fact]
    public void Normalize_DescartaTipoEAcaoInvalidos()
    {
        CustomMenuProfile profile = Profile();
        profile.Items.Add(new CustomMenuItem { Label = "Tipo ruim", Kind = (CustomMenuItemKind)999 });
        profile.Items.Add(new CustomMenuItem { Label = "Ação ruim", Kind = CustomMenuItemKind.Action, Action = (RadialAction)999 });

        var settings = SettingsWith(profile);
        SettingsStore.Normalize(settings);

        CustomMenuProfile normalized = Assert.Single(settings.CustomProfiles);
        Assert.Equal(["Calc", "Fechar"], normalized.Items.Select(i => i.Label).ToArray());
    }

    [Fact]
    public void Normalize_NormalizaProcessoPrioridadeETamanhos()
    {
        var profile = new CustomMenuProfile
        {
            Name = new string('N', 80),
            ProcessName = "  Bloco.EXE ",
            TitleContains = new string('T', 300),
            Priority = 900,
            Items =
            [
                new CustomMenuItem
                {
                    Label = new string('L', 40),
                    Description = new string('D', 300),
                    Target = new string('A', 3000),
                    Text = new string('X', 5000),
                    Glyph = "  \uE8EF  "
                }
            ]
        };

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Equal("Bloco", profile.ProcessName);
        Assert.Equal(100, profile.Priority);
        Assert.Equal(60, profile.Name.Length);
        Assert.Equal(120, profile.TitleContains.Length);

        CustomMenuItem item = Assert.Single(profile.Items);
        Assert.Equal(24, item.Label.Length);
        Assert.Equal(120, item.Description.Length);
        Assert.Equal(2048, item.Target.Length);
        Assert.Equal(4096, item.Text.Length);
        Assert.Equal("\uE8EF", item.Glyph);
    }

    [Fact]
    public void Normalize_LimitaPerfisEItens()
    {
        CustomMenuProfile[] profiles = Enumerable.Range(0, 25)
            .Select(n => new CustomMenuProfile
            {
                Name = $"Perfil {n}",
                Items = Enumerable.Range(0, 12)
                    .Select(i => new CustomMenuItem { Label = $"Item {i}", Kind = CustomMenuItemKind.Text, Text = "x" })
                    .ToList()
            })
            .ToArray();

        var settings = SettingsWith(profiles);
        SettingsStore.Normalize(settings);

        Assert.Equal(20, settings.CustomProfiles.Count);
        Assert.All(settings.CustomProfiles, p => Assert.Equal(8, p.Items.Count));
    }

    [Fact]
    public void Normalize_ContextosInvalidosEDuplicados_SaoCorrigidos()
    {
        CustomMenuProfile profile = Profile();
        profile.Contexts =
        [
            (RadialContextKind)123,
            RadialContextKind.FileOrFolder,
            RadialContextKind.FileOrFolder,
            RadialContextKind.TextField
        ];

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Equal(
            [RadialContextKind.FileOrFolder, RadialContextKind.TextField],
            profile.Contexts.ToArray());
    }

    [Fact]
    public void Normalize_IdsVazios_SaoGerados()
    {
        CustomMenuProfile profile = Profile();
        profile.Id = "  ";
        profile.Items[0].Id = "";

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
        Assert.False(string.IsNullOrWhiteSpace(profile.Items[0].Id));
        Assert.NotEqual(profile.Items[0].Id, profile.Items[1].Id);
    }

    [Fact]
    public void Normalize_IdsRepetidosEntrePerfis_SaoRegeneradosEIdempotentes()
    {
        CustomMenuProfile first = Profile("Primeiro");
        first.Items = [new CustomMenuItem { Id = "mesmo", Label = "Um" }];
        CustomMenuProfile second = Profile("Segundo");
        second.Items =
        [
            new CustomMenuItem { Id = "mesmo", Label = "Dois" },
            new CustomMenuItem { Id = "outro", Label = "Três" }
        ];

        var settings = SettingsWith(first, second);
        SettingsStore.Normalize(settings);

        Assert.Equal("mesmo", first.Items[0].Id);
        Assert.NotEqual("mesmo", second.Items[0].Id);
        Assert.Equal("outro", second.Items[1].Id);
        Assert.Equal(3, settings.CustomProfiles
            .SelectMany(p => p.Items)
            .Select(i => i.Id)
            .Distinct(StringComparer.Ordinal)
            .Count());
        Assert.Equal(["Um", "Dois", "Três"], settings.CustomProfiles
            .SelectMany(p => p.Items)
            .Select(i => i.Label)
            .ToArray());

        string[] ids = settings.CustomProfiles.SelectMany(p => p.Items).Select(i => i.Id).ToArray();
        SettingsStore.Normalize(settings);

        Assert.Equal(ids, settings.CustomProfiles.SelectMany(p => p.Items).Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Normalize_NaoRegeneraIdsDasOpcoesDeSubmenu()
    {
        var submenu = new CustomMenuItem
        {
            Id = "sub",
            Label = "Sub",
            Kind = CustomMenuItemKind.Submenu,
            Options =
            [
                new CustomMenuItem { Id = "mesmo", Label = "A", Kind = CustomMenuItemKind.Text, Text = "x" },
                new CustomMenuItem { Id = "mesmo", Label = "B", Kind = CustomMenuItemKind.Text, Text = "y" }
            ]
        };
        CustomMenuProfile profile = Profile();
        profile.Items = [submenu];

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Equal(["mesmo", "mesmo"], profile.Items[0].Options.Select(o => o.Id).ToArray());
    }

    // ------------------------------------------------------------------
    // Submenus
    // ------------------------------------------------------------------

    [Fact]
    public void SaveELoad_PreservaSubmenuEOpcoes()
    {
        var profile = new CustomMenuProfile
        {
            Name = "Ferramentas",
            Items =
            [
                new CustomMenuItem
                {
                    Label = "Navegador",
                    Kind = CustomMenuItemKind.Submenu,
                    Options =
                    [
                        new CustomMenuItem { Label = "Chrome", Kind = CustomMenuItemKind.Launch, Target = "chrome.exe" },
                        new CustomMenuItem { Label = "Edge", Kind = CustomMenuItemKind.Launch, Target = "msedge.exe" }
                    ]
                }
            ]
        };

        SettingsStore store = Create();
        store.Save(SettingsWith(profile));
        var loaded = store.Load();

        CustomMenuItem submenu = Assert.Single(Assert.Single(loaded.Settings.CustomProfiles).Items);
        Assert.Equal(CustomMenuItemKind.Submenu, submenu.Kind);
        Assert.Equal(["Chrome", "Edge"], submenu.Options.Select(o => o.Label).ToArray());
        Assert.Equal("chrome.exe", submenu.Options[0].Target);
    }

    [Fact]
    public void Normalize_Submenu_LimitaOpcoesEDescartaOpcoesInvalidas()
    {
        var submenu = new CustomMenuItem
        {
            Label = "Muitas",
            Kind = CustomMenuItemKind.Submenu,
            Options = [null!]
        };
        submenu.Options.Add(new CustomMenuItem { Label = "Aninhado", Kind = CustomMenuItemKind.Submenu });
        submenu.Options.Add(new CustomMenuItem { Label = "" });
        for (int i = 0; i < 10; i++)
            submenu.Options.Add(new CustomMenuItem { Label = $"Opção {i}", Kind = CustomMenuItemKind.Text, Text = "x" });

        CustomMenuProfile profile = Profile();
        profile.Items = [submenu];
        SettingsStore.Normalize(SettingsWith(profile));

        CustomMenuItem normalized = Assert.Single(profile.Items);
        Assert.Equal(CustomMenuNormalizer.MaxOptions, normalized.Options.Count);
        Assert.DoesNotContain(normalized.Options, o => o.Kind == CustomMenuItemKind.Submenu);
        Assert.Equal("Opção 0", normalized.Options[0].Label);
    }

    [Fact]
    public void Normalize_OpcoesEmItemComum_SaoDescartadas()
    {
        var item = new CustomMenuItem
        {
            Label = "Texto",
            Kind = CustomMenuItemKind.Text,
            Text = "oi",
            Options = [new CustomMenuItem { Label = "Sobrando", Kind = CustomMenuItemKind.Text, Text = "x" }]
        };
        CustomMenuProfile profile = Profile();
        profile.Items = [item];

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Empty(Assert.Single(profile.Items).Options);
    }

    [Fact]
    public void ActiveOptions_IgnoraDesabilitadasELimitaAoMaximo()
    {
        var submenu = new CustomMenuItem { Label = "Sub", Kind = CustomMenuItemKind.Submenu };
        submenu.Options.Add(new CustomMenuItem { Label = "Desligada", Enabled = false });
        for (int i = 0; i < 10; i++)
            submenu.Options.Add(new CustomMenuItem { Label = $"Ativa {i}" });

        var active = CustomMenuNormalizer.ActiveOptions(submenu);

        Assert.Equal(CustomMenuNormalizer.MaxOptions, active.Count);
        Assert.DoesNotContain(active, o => o.Label == "Desligada");
    }

    // ------------------------------------------------------------------
    // Rotinas
    // ------------------------------------------------------------------

    private static CustomRoutineStep Action(RadialAction action) => new()
    {
        Kind = CustomRoutineStepKind.Action,
        Action = action,
    };

    private static CustomRoutineStep Delay(int milliseconds) => new()
    {
        Kind = CustomRoutineStepKind.Delay,
        DelayMilliseconds = milliseconds,
    };

    [Fact]
    public void SaveELoad_PreservaRotinaEPassos()
    {
        var profile = new CustomMenuProfile
        {
            Name = "Rotinas",
            Items =
            [
                new CustomMenuItem
                {
                    Label = "Copiar e colar",
                    Kind = CustomMenuItemKind.Routine,
                    Steps =
                    [
                        Action(RadialAction.CopyPath),
                        Delay(250),
                        Action(RadialAction.Paste),
                    ]
                }
            ]
        };

        SettingsStore store = Create();
        store.Save(SettingsWith(profile));
        var loaded = store.Load();

        CustomMenuItem routine = Assert.Single(Assert.Single(loaded.Settings.CustomProfiles).Items);
        Assert.Equal(CustomMenuItemKind.Routine, routine.Kind);
        Assert.Equal(CustomMenuItem.RoutineSchemaVersion, routine.RoutineVersion);
        Assert.Collection(routine.Steps,
            step =>
            {
                Assert.Equal(CustomRoutineStepKind.Action, step.Kind);
                Assert.Equal(RadialAction.CopyPath, step.Action);
            },
            step =>
            {
                Assert.Equal(CustomRoutineStepKind.Delay, step.Kind);
                Assert.Equal(250, step.DelayMilliseconds);
            },
            step =>
            {
                Assert.Equal(CustomRoutineStepKind.Action, step.Kind);
                Assert.Equal(RadialAction.Paste, step.Action);
            });
    }

    [Fact]
    public void Normalize_DescartaRotinasInvalidas()
    {
        CustomMenuProfile profile = Profile();
        profile.Items =
        [
            new CustomMenuItem { Label = "Vazia", Kind = CustomMenuItemKind.Routine },
            new CustomMenuItem { Label = "Só pausas", Kind = CustomMenuItemKind.Routine, Steps = [Delay(100)] },
            new CustomMenuItem { Label = "Proibida", Kind = CustomMenuItemKind.Routine, Steps = [Action(RadialAction.Convert)] },
            new CustomMenuItem
            {
                Label = "Versão nova",
                Kind = CustomMenuItemKind.Routine,
                Steps = [Action(RadialAction.CopyPath)],
                RoutineVersion = CustomMenuItem.RoutineSchemaVersion + 1
            },
            new CustomMenuItem
            {
                Label = "Passo vazio",
                Kind = CustomMenuItemKind.Routine,
                Steps = [null!, Action(RadialAction.CopyPath)]
            },
            new CustomMenuItem { Label = "Válida", Kind = CustomMenuItemKind.Routine, Steps = [Action(RadialAction.CopyPath)] }
        ];

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Equal("Válida", Assert.Single(profile.Items).Label);
    }

    [Fact]
    public void Normalize_RotinaDentroDeSubmenu_EDescartada()
    {
        var submenu = new CustomMenuItem
        {
            Label = "Sub",
            Kind = CustomMenuItemKind.Submenu,
            Options =
            [
                new CustomMenuItem { Label = "Copiar", Kind = CustomMenuItemKind.Action, Action = RadialAction.CopyPath },
                new CustomMenuItem
                {
                    Label = "Rotina aninhada",
                    Kind = CustomMenuItemKind.Routine,
                    Steps = [Action(RadialAction.CopyPath)]
                },
            ]
        };
        CustomMenuProfile profile = Profile();
        profile.Items = [submenu];

        SettingsStore.Normalize(SettingsWith(profile));

        CustomMenuItem normalized = Assert.Single(profile.Items);
        Assert.Equal("Copiar", Assert.Single(normalized.Options).Label);
    }

    [Fact]
    public void Normalize_PassosEmItemComum_SaoDescartados()
    {
        var item = new CustomMenuItem
        {
            Label = "Texto",
            Kind = CustomMenuItemKind.Text,
            Text = "oi",
            Steps = [Action(RadialAction.CopyPath)],
        };
        CustomMenuProfile profile = Profile();
        profile.Items = [item];

        SettingsStore.Normalize(SettingsWith(profile));

        Assert.Empty(Assert.Single(profile.Items).Steps);
    }

    [Fact]
    public void JsonComPerfilMalformado_NaoDerrubaOResto()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """
            {
              "CustomProfiles": [
                {
                  "Id": "p1",
                  "Name": "Válido",
                  "ProcessName": "notepad.exe",
                  "Contexts": [1, 99, 1],
                  "Items": [
                    { "Label": "Ok", "Kind": 1, "Target": "calc.exe" },
                    { "Label": "", "Kind": 0, "Action": 0 },
                    { "Label": "Tipo ruim", "Kind": 99, "Action": 0 },
                    null
                  ]
                },
                null
              ]
            }
            """);

        var result = store.Load();

        Assert.Equal(SettingsSource.File, result.Source);
        CustomMenuProfile profile = Assert.Single(result.Settings.CustomProfiles);
        Assert.Equal("notepad", profile.ProcessName);
        Assert.Equal([RadialContextKind.FileOrFolder], profile.Contexts);
        CustomMenuItem item = Assert.Single(profile.Items);
        Assert.Equal("Ok", item.Label);
        Assert.Equal(CustomMenuItemKind.Launch, item.Kind);
    }
}
