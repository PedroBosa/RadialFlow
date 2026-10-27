using System;
using System.IO;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomActionLogicTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CustomMenuItem Item(
        CustomMenuItemKind kind,
        string target = "",
        string text = "",
        string label = "Item",
        bool enabled = true) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = label,
            Kind = kind,
            Target = target,
            Text = text,
            Enabled = enabled
        };

    private static string Rejection(CustomMenuItem item)
    {
        Assert.False(CustomActionLogic.TryValidate(item, out _, out string? reason), "o item deveria ser recusado");
        Assert.False(string.IsNullOrWhiteSpace(reason), "a recusa precisa explicar o motivo");
        return reason!;
    }

    // ------------------------------------------------------------------
    // Launch
    // ------------------------------------------------------------------

    [Fact]
    public void Launch_AceitaFerramentaDoSistema()
    {
        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.Launch, "notepad"), out CustomActionPlan plan, out _));

        Assert.Equal(CustomMenuItemKind.Launch, plan.Kind);
        Assert.True(plan.Target.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase), plan.Target);
        Assert.True(File.Exists(plan.Target));
        Assert.Empty(plan.Keys);
    }

    [Fact]
    public void Launch_AceitaCaminhoAbsolutoConfiado()
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string notepad = Path.Combine(system32, "notepad.exe");
        Assert.True(File.Exists(notepad), $"pré-condição: '{notepad}' precisa existir");

        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.Launch, notepad), out CustomActionPlan plan, out _));

        Assert.Equal(notepad, plan.Target, ignoreCase: true);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell")]
    [InlineData("powershell_ise.exe")]
    [InlineData("wt")]
    [InlineData("wsl.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    public void Launch_RecusaInterpretadores(string target)
    {
        string reason = Rejection(Item(CustomMenuItemKind.Launch, target));

        Assert.Contains("segurança", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_RecusaExeForaDeLocalProtegido()
    {
        string executable = _temp.Combine("app.exe");
        File.WriteAllBytes(executable, [0x4D, 0x5A]);

        string reason = Rejection(Item(CustomMenuItemKind.Launch, executable));

        Assert.Contains("local protegido", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_RecusaInexistente()
    {
        string reason = Rejection(Item(CustomMenuItemKind.Launch, "radialflow-nao-existe.exe"));

        Assert.Contains("não foi encontrado", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_RecusaExtensaoQueNaoSejaExe()
    {
        string script = _temp.Combine("script.bat");
        File.WriteAllText(script, "echo oi");

        string reason = Rejection(Item(CustomMenuItemKind.Launch, script));

        Assert.Contains(".exe", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_RecusaVazio()
    {
        Assert.Equal("informe o executável", Rejection(Item(CustomMenuItemKind.Launch)));
    }

    // ------------------------------------------------------------------
    // OpenUrl
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/a?b=c")]
    public void OpenUrl_AceitaHttpEHttps(string url)
    {
        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.OpenUrl, url), out CustomActionPlan plan, out _));

        Assert.Equal(url, plan.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("https://example.com/a\"b")]
    public void OpenUrl_RecusaEsquemasOuCaracteresInvalidos(string url)
    {
        Assert.False(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.OpenUrl, url), out _, out string? reason));
        Assert.Contains("URL inválida", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // OpenPath
    // ------------------------------------------------------------------

    [Fact]
    public void OpenPath_AceitaPastaEArquivoComum()
    {
        string folder = _temp.Combine("pasta");
        Directory.CreateDirectory(folder);
        string text = _temp.Combine("nota.txt");
        File.WriteAllText(text, "oi");

        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.OpenPath, folder), out CustomActionPlan folderPlan, out _));
        Assert.Equal(Path.GetFullPath(folder), folderPlan.Target, ignoreCase: true);

        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.OpenPath, text), out CustomActionPlan filePlan, out _));
        Assert.Equal(Path.GetFullPath(text), filePlan.Target, ignoreCase: true);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".lnk")]
    [InlineData(".url")]
    [InlineData(".msi")]
    [InlineData(".chm")]
    [InlineData(".docm")]
    [InlineData(".xlsm")]
    [InlineData(".pptm")]
    [InlineData(".settingcontent-ms")]
    [InlineData(".scf")]
    [InlineData(".diagcab")]
    [InlineData(".search-ms")]
    [InlineData(".library-ms")]
    public void OpenPath_RecusaExecutaveisEScripts(string extension)
    {
        string file = _temp.Combine("arquivo" + extension);
        File.WriteAllText(file, "x");

        string reason = Rejection(Item(CustomMenuItemKind.OpenPath, file));

        Assert.Contains("não é permitido", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenPath_RecusaInexistente()
    {
        string reason = Rejection(Item(CustomMenuItemKind.OpenPath, _temp.Combine("nao-existe.txt")));

        Assert.Contains("não existe", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Hotkey
    // ------------------------------------------------------------------

    [Fact]
    public void Hotkey_OrdenaModificadoresCanonicamente()
    {
        Assert.True(CustomActionLogic.TryParseHotkey("Shift+Ctrl+P", out byte[] keys, out _));

        Assert.Equal(new byte[] { 0x11, 0x10, 0x50 }, keys);
    }

    [Fact]
    public void Hotkey_AceitaAltF4()
    {
        Assert.True(CustomActionLogic.TryParseHotkey("alt+f4", out byte[] keys, out _));

        Assert.Equal(new byte[] { 0x12, 0x73 }, keys);
    }

    [Fact]
    public void Hotkey_AceitaTeclaNomeada()
    {
        Assert.True(CustomActionLogic.TryParseHotkey("Ctrl+PageUp", out byte[] keys, out _));

        Assert.Equal(new byte[] { 0x11, 0x21 }, keys);
    }

    [Fact]
    public void Hotkey_AceitaQuatroTeclas()
    {
        Assert.True(CustomActionLogic.TryParseHotkey("Ctrl+Alt+Shift+P", out byte[] keys, out _));

        Assert.Equal(new byte[] { 0x11, 0x12, 0x10, 0x50 }, keys);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+Ctrl+P")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Alt+Shift+Win+P")]
    [InlineData("F25")]
    [InlineData("Ctrl+")]
    public void Hotkey_RecusaFormatosInvalidos(string combo)
    {
        Assert.False(CustomActionLogic.TryParseHotkey(combo, out _, out string? reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // ------------------------------------------------------------------
    // Text e validações gerais
    // ------------------------------------------------------------------

    [Fact]
    public void Text_AceitaAte4096Caracteres()
    {
        string text = new('a', 4096);

        Assert.True(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.Text, text: text), out CustomActionPlan plan, out _));

        Assert.Equal(4096, plan.Text.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Text_RecusaTamanhosForaDoLimite(int length)
    {
        Assert.False(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.Text, text: new string('a', length)), out _, out _));
    }

    [Fact]
    public void ItemDesabilitado_EhRecusado()
    {
        Assert.Equal("item desabilitado",
            Rejection(Item(CustomMenuItemKind.Launch, "notepad", enabled: false)));
    }

    [Fact]
    public void ItemSemRotulo_EhRecusado()
    {
        Assert.Equal("item sem rótulo",
            Rejection(Item(CustomMenuItemKind.Launch, "notepad", label: "  ")));
    }

    [Fact]
    public void Action_NaoEhValidadaAqui()
    {
        Assert.False(CustomActionLogic.TryValidate(Item(CustomMenuItemKind.Action), out _, out string? reason));
        Assert.Contains("roteador", reason, StringComparison.OrdinalIgnoreCase);
    }
}
