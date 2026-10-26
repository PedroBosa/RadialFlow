using System;
using System.IO;
using System.Linq;
using RadialFlow.Config;
using RadialFlow.Input;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private SettingsStore Create() => new(_temp.Combine("cfg"));

    private static AppSettings Custom(int deadzone = 22, string accent = "Teal") => new()
    {
        DeadzonePixels = deadzone,
        DwellMilliseconds = 900,
        Accent = accent,
        StartWithWindows = true,
        Blacklist = [new ForegroundBlacklist.BlacklistEntry("meujogo", "Meu Jogo")],
        FavoriteApps = [new FavoriteAppEntry("Calc", "calc.exe")]
    };

    // ------------------------------------------------------------------
    // Ida e volta
    // ------------------------------------------------------------------

    [Fact]
    public void SemArquivo_UsaPadroesENaoCriaNada()
    {
        SettingsStore store = Create();

        var result = store.Load();

        Assert.Equal(SettingsSource.DefaultsNoFile, result.Source);
        Assert.False(File.Exists(store.FilePath));   // quem decide criar é o Save
        Assert.Equal(AppSettings.CreateDefault().DeadzonePixels, result.Settings.DeadzonePixels);
    }

    [Fact]
    public void SaveELoad_PreservaTudo()
    {
        SettingsStore store = Create();
        store.Save(Custom());

        var loaded = store.Load();

        Assert.Equal(SettingsSource.File, loaded.Source);
        Assert.Equal(22, loaded.Settings.DeadzonePixels);
        Assert.Equal(900, loaded.Settings.DwellMilliseconds);
        Assert.Equal("Teal", loaded.Settings.Accent);
        Assert.True(loaded.Settings.StartWithWindows);
        Assert.Equal("meujogo", Assert.Single(loaded.Settings.Blacklist).ProcessName);
        Assert.Equal("Calc", Assert.Single(loaded.Settings.FavoriteApps).Name);
    }

    [Fact]
    public void Save_GravaEmUtf8SemBomECriaAPasta()
    {
        SettingsStore store = Create();
        var settings = Custom();
        settings.FavoriteApps = [new FavoriteAppEntry("Calculadora científica ção", "calc.exe")];

        store.Save(settings);

        byte[] bytes = File.ReadAllBytes(store.FilePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "não deveria ter BOM");
        Assert.Equal("Calculadora científica ção", store.Load().Settings.FavoriteApps.Single().Name);
    }

    // ------------------------------------------------------------------
    // Atomicidade e cópia de segurança
    // ------------------------------------------------------------------

    [Fact]
    public void Save_NaoDeixaArquivoTemporario()
    {
        SettingsStore store = Create();
        store.Save(Custom());
        store.Save(Custom(deadzone: 30));

        string[] files = Directory.GetFiles(_temp.Combine("cfg")).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

        Assert.Equal(["settings.json", "settings.json.bak"], files);
    }

    [Fact]
    public void Save_GuardaAVersaoAnteriorComoBak()
    {
        SettingsStore store = Create();
        store.Save(Custom(deadzone: 20));
        store.Save(Custom(deadzone: 31));

        Assert.Equal(31, store.Load().Settings.DeadzonePixels);

        // O .bak tem a gravação anterior.
        var backup = new SettingsStore(_temp.Combine("bakcheck"));
        Directory.CreateDirectory(_temp.Combine("bakcheck"));
        File.Copy(store.BackupPath, backup.FilePath);
        Assert.Equal(20, backup.Load().Settings.DeadzonePixels);
    }

    [Fact]
    public void Save_FalhaNoMeio_PreservaOArquivoAnteriorELimpaOTemporario()
    {
        SettingsStore store = Create();
        store.Save(Custom(deadzone: 20));

        // O .tmp fica TRAVADO por outro processo: a gravação nova não consegue nem começar.
        string temp = store.FilePath + ".tmp";
        using (new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(Custom(deadzone: 35)));
        }

        Assert.Equal(20, store.Load().Settings.DeadzonePixels);   // o arquivo ativo continua inteiro
    }

    // ------------------------------------------------------------------
    // Corrupção: nada do usuário é apagado
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{ \"DeadzonePixels\": 20, ")]            // cortado no meio (queda de energia)
    [InlineData("isto não é json")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"DeadzonePixels\": \"muito\" }")]     // tipo errado
    public void ArquivoIlegivel_UsaPadroes_E_GuardaOOriginalSemApagar(string content)
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, content);

        var result = store.Load();

        Assert.Equal(SettingsSource.DefaultsAfterCorruption, result.Source);
        Assert.NotNull(result.QuarantinedTo);
        Assert.True(File.Exists(result.QuarantinedTo), "o arquivo ruim precisa ser guardado");
        Assert.Equal(content, File.ReadAllText(result.QuarantinedTo!));   // intacto, byte a byte
        Assert.Contains("corrompido", Path.GetFileName(result.QuarantinedTo));
        Assert.False(File.Exists(store.FilePath));                        // o nome ativo ficou livre para o próximo Save
    }

    [Fact]
    public void ArquivoIlegivel_ComBackupBom_RestauraDoBackup()
    {
        SettingsStore store = Create();
        store.Save(Custom(deadzone: 20));
        store.Save(Custom(deadzone: 31));      // agora o .bak tem 20
        File.WriteAllText(store.FilePath, "{ cortado");

        var result = store.Load();

        Assert.Equal(SettingsSource.Backup, result.Source);
        Assert.Equal(20, result.Settings.DeadzonePixels);
        Assert.Single(Directory.GetFiles(_temp.Combine("cfg"), "settings.corrompido-*.json"));
    }

    [Fact]
    public void DepoisDeCorrupcao_OProximoSaveVoltaAoNormal()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, "lixo");
        store.Load();

        store.Save(Custom(deadzone: 27));

        Assert.Equal(SettingsSource.File, store.Load().Source);
        Assert.Equal(27, store.Load().Settings.DeadzonePixels);
    }

    [Fact]
    public void QuarentenaRepetida_NaoSobrescreveGuardadosAnteriores()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));

        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(store.FilePath, $"lixo {i}");
            store.Load();
        }

        string[] kept = Directory.GetFiles(_temp.Combine("cfg"), "settings.corrompido-*.json");
        Assert.Equal(3, kept.Length);
        Assert.Equal(["lixo 0", "lixo 1", "lixo 2"], kept.Select(File.ReadAllText).OrderBy(s => s).ToArray());
    }

    // ------------------------------------------------------------------
    // Tolerância e normalização
    // ------------------------------------------------------------------

    [Fact]
    public void JsonComComentariosEVirgulaSobrando_EAceito()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """
            {
              // editado à mão
              "DeadzonePixels": 18,
              "Accent": "Pink",
            }
            """);

        var result = store.Load();

        Assert.Equal(SettingsSource.File, result.Source);
        Assert.Equal(18, result.Settings.DeadzonePixels);
        Assert.Equal("Pink", result.Settings.Accent);
    }

    [Fact]
    public void ChavesEmOutraCaixa_SaoAceitas()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """{ "deadzonepixels": 25, "ACCENT": "Green" }""");

        var loaded = store.Load().Settings;

        Assert.Equal(25, loaded.DeadzonePixels);
        Assert.Equal("Green", loaded.Accent);
    }

    [Fact]
    public void CamposAusentes_ViramPadroesSemDescartarOResto()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """{ "DeadzonePixels": 19 }""");

        var loaded = store.Load().Settings;

        Assert.Equal(19, loaded.DeadzonePixels);
        Assert.Equal(650, loaded.DwellMilliseconds);
        Assert.Equal("Blue", loaded.Accent);
        Assert.NotNull(loaded.Blacklist);
        Assert.NotNull(loaded.FavoriteApps);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(4, 15)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    [InlineData(8, 8)]
    [InlineData(15, 15)]
    [InlineData(40, 40)]
    [InlineData(41, 40)]
    [InlineData(100, 40)]
    public void Normalize_SensibilidadeForaDaFaixa_EAjustada(int stored, int expected)
    {
        var settings = new AppSettings { DeadzonePixels = stored };

        Assert.Equal(expected, SettingsStore.Normalize(settings).DeadzonePixels);
    }

    [Theory]
    [InlineData(-5, 200)]
    [InlineData(10, 200)]
    [InlineData(5000, 2000)]
    [InlineData(750, 750)]
    public void Normalize_TempoFicaNosLimites(int stored, int expected)
    {
        Assert.Equal(expected, SettingsStore.Normalize(new AppSettings { DwellMilliseconds = stored }).DwellMilliseconds);
    }

    [Fact]
    public void Normalize_RemoveEntradasNulasOuSemNome_MantendoAsValidas()
    {
        var settings = new AppSettings
        {
            Blacklist = [new ForegroundBlacklist.BlacklistEntry("ok", "Ok"), null!, new ForegroundBlacklist.BlacklistEntry("  ", "vazio")],
            FavoriteApps = [new FavoriteAppEntry("A", "a.exe"), null!]
        };

        SettingsStore.Normalize(settings);

        Assert.Equal("ok", Assert.Single(settings.Blacklist).ProcessName);
        Assert.Equal("A", Assert.Single(settings.FavoriteApps).Name);
    }

    [Fact]
    public void Normalize_Favoritos_RemoveEspacosENaoPerdeCaminhosDistintos()
    {
        var settings = new AppSettings
        {
            FavoriteApps =
            [
                new FavoriteAppEntry("  Calc  ", @"  C:\Tools\calc.exe  ", "  --x  ", "  \uE8EF  "),
                new FavoriteAppEntry("Cópia", @"c:\tools\CALC.EXE", "--x"),
                new FavoriteAppEntry("Mesmo caminho, outro argumento", @"C:\Tools\calc.exe", " --y "),
                new FavoriteAppEntry("Sem caminho", "   ")
            ]
        };

        SettingsStore.Normalize(settings);

        Assert.Equal(2, settings.FavoriteApps.Count);
        FavoriteAppEntry first = settings.FavoriteApps[0];
        Assert.Equal("Calc", first.Name);
        Assert.Equal(@"C:\Tools\calc.exe", first.ExecutablePath);
        Assert.Equal("--x", first.Arguments);
        Assert.Equal("\uE8EF", first.Glyph);
        Assert.Equal("Mesmo caminho, outro argumento", settings.FavoriteApps[1].Name);
        Assert.Equal(@"C:\Tools\calc.exe", settings.FavoriteApps[1].ExecutablePath);
        Assert.Equal("--y", settings.FavoriteApps[1].Arguments);
    }

    [Fact]
    public void Normalize_Blacklist_RemoveEspacosExeEDuplicatas()
    {
        var settings = new AppSettings
        {
            Blacklist =
            [
                new ForegroundBlacklist.BlacklistEntry("  Notepad.EXE  ", "  Bloco  "),
                new ForegroundBlacklist.BlacklistEntry("notepad", "Duplicata"),
                new ForegroundBlacklist.BlacklistEntry("  .exe  ", "Sem nome"),
                new ForegroundBlacklist.BlacklistEntry("  Jogo  ", "  Meu jogo  ")
            ]
        };

        SettingsStore.Normalize(settings);

        Assert.Equal(["Notepad", "Jogo"], settings.Blacklist.Select(e => e.ProcessName).ToArray());
        Assert.Equal(["Bloco", "Meu jogo"], settings.Blacklist.Select(e => e.Description).ToArray());
    }

    [Fact]
    public void Normalize_DuasVezes_NaoMudaNada()
    {
        var settings = new AppSettings
        {
            DeadzonePixels = 100,
            Blacklist =
            [
                new ForegroundBlacklist.BlacklistEntry(" Jogo.EXE ", " Meu jogo "),
                new ForegroundBlacklist.BlacklistEntry("jogo", "dup"),
                new ForegroundBlacklist.BlacklistEntry(" ", "vazio")
            ],
            FavoriteApps =
            [
                new FavoriteAppEntry(" Calc ", @" C:\calc.exe ", " --x ", " \uE8EF "),
                new FavoriteAppEntry("Cópia", @"c:\CALC.EXE", "--x"),
                new FavoriteAppEntry("Sem caminho", " ")
            ]
        };

        SettingsStore.Normalize(settings);
        int deadzone = settings.DeadzonePixels;
        string[] blacklist = settings.Blacklist.Select(e => $"{e.ProcessName}|{e.Description}").ToArray();
        string[] favorites = settings.FavoriteApps
            .Select(a => $"{a.Name}|{a.ExecutablePath}|{a.Arguments}|{a.Glyph}").ToArray();

        SettingsStore.Normalize(settings);

        Assert.Equal(deadzone, settings.DeadzonePixels);
        Assert.Equal(blacklist, settings.Blacklist.Select(e => $"{e.ProcessName}|{e.Description}").ToArray());
        Assert.Equal(favorites, settings.FavoriteApps
            .Select(a => $"{a.Name}|{a.ExecutablePath}|{a.Arguments}|{a.Glyph}").ToArray());
    }

    [Fact]
    public void Load_ComDadosSujos_AplicaANormalizacao()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """
            {
              "DeadzonePixels": 100,
              "Blacklist": [
                { "ProcessName": "  Notepad.EXE ", "Description": "  Bloco " },
                { "ProcessName": "notepad", "Description": "duplicada" }
              ],
              "FavoriteApps": [
                { "Name": " Calc ", "ExecutablePath": " C:\\calc.exe ", "Arguments": " " },
                { "Name": "Cópia", "ExecutablePath": "c:\\CALC.EXE" },
                { "Name": "Sem caminho", "ExecutablePath": "  " }
              ]
            }
            """);

        var loaded = store.Load().Settings;

        Assert.Equal(40, loaded.DeadzonePixels);
        Assert.Equal("Notepad", Assert.Single(loaded.Blacklist).ProcessName);
        Assert.Equal("Bloco", loaded.Blacklist[0].Description);
        Assert.Equal("Calc", Assert.Single(loaded.FavoriteApps).Name);
        Assert.Equal(@"C:\calc.exe", loaded.FavoriteApps[0].ExecutablePath);
    }

    [Fact]
    public void ListasNulasNoJson_ViramListasVazias()
    {
        SettingsStore store = Create();
        Directory.CreateDirectory(_temp.Combine("cfg"));
        File.WriteAllText(store.FilePath, """{ "Blacklist": null, "FavoriteApps": null, "Accent": null }""");

        var loaded = store.Load().Settings;

        Assert.Empty(loaded.Blacklist);
        Assert.Empty(loaded.FavoriteApps);
        Assert.Equal("Blue", loaded.Accent);
    }
}
