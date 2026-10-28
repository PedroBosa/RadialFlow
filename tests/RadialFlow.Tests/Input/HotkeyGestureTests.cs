using RadialFlow.Input;
using Xunit;

namespace RadialFlow.Tests;

public sealed class HotkeyGestureTests
{
    // ------------------------------------------------------------------
    // Forma canônica
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ctrl+f5", "Ctrl+F5")]
    [InlineData("CONTROL+ALT+SPACE", "Ctrl+Alt+Space")]
    [InlineData("shift+ctrl+a", "Ctrl+Shift+A")]
    [InlineData("  Ctrl + Shift + F12  ", "Ctrl+Shift+F12")]
    [InlineData("win+ctrl+1", "Ctrl+Win+1")]
    [InlineData("meta+shift+enter", "Shift+Win+Enter")]
    [InlineData("alt+return", "Alt+Enter")]
    [InlineData("alt+escape", "Alt+Esc")]
    [InlineData("ctrl+spacebar", "Ctrl+Space")]
    [InlineData("ctrl+pgdn", "Ctrl+PageDown")]
    [InlineData("ctrl+arrowleft", "Ctrl+Left")]
    [InlineData("ctrl+ins", "Ctrl+Insert")]
    [InlineData("ctrl+del", "Ctrl+Delete")]
    [InlineData("shift+alt+ctrl+win+a", "Ctrl+Alt+Shift+Win+A")]
    public void Normalize_DevolveAFormaCanonica(string input, string expected) =>
        Assert.Equal(expected, HotkeyGesture.Normalize(input));

    [Fact]
    public void Normalize_EIdempotente()
    {
        string once = HotkeyGesture.Normalize("shift+control+alt+win+f24");
        Assert.Equal("Ctrl+Alt+Shift+Win+F24", once);
        Assert.Equal(once, HotkeyGesture.Normalize(once));
    }

    // ------------------------------------------------------------------
    // Entradas recusadas
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("F5")]
    [InlineData("A")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl+Shift")]                 // sem tecla final
    [InlineData("Ctrl+A+B")]                   // duas teclas finais
    [InlineData("Ctrl+Ctrl+A")]                // modificador repetido
    [InlineData("Ctrl+Shift+Alt+Win+A+B")]     // duas teclas finais
    [InlineData("Ctrl+F25")]                   // fora de F1–F24
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+é")]                     // tecla não suportada
    [InlineData("Ctrl+OemPlus")]
    [InlineData("Hyper+A")]
    public void Normalize_RecusaEntradasInvalidas(string? input) =>
        Assert.Equal("", HotkeyGesture.Normalize(input));

    [Fact]
    public void Normalize_RecusaTextoAcimaDoLimite()
    {
        string longText = "Ctrl+" + new string('A', HotkeyGesture.MaxTextLength);

        Assert.Equal("", HotkeyGesture.Normalize(longText));
        Assert.False(HotkeyGesture.TryParse(longText, out _));
    }

    [Fact]
    public void Normalize_AceitaOsQuatroModificadores()
    {
        string value = "Ctrl+Alt+Shift+Win+F24";
        Assert.True(value.Length <= HotkeyGesture.MaxTextLength);
        Assert.Equal(value, HotkeyGesture.Normalize(value));
    }

    // ------------------------------------------------------------------
    // Parse e valores para o Win32
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_PreencheModificadoresEteclaVirtual()
    {
        Assert.True(HotkeyGesture.TryParse("alt+ctrl+f5", out HotkeyGesture gesture));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal((uint)HotkeyModifiers.Control | (uint)HotkeyModifiers.Alt, gesture.Win32Modifiers);
        Assert.Equal(0x74u, gesture.VirtualKey);   // VK_F5
    }

    [Theory]
    [InlineData("Ctrl+1", 0x31u)]
    [InlineData("Ctrl+9", 0x39u)]
    [InlineData("Ctrl+A", 0x41u)]
    [InlineData("Ctrl+Z", 0x5Au)]
    [InlineData("Ctrl+F1", 0x70u)]
    [InlineData("Ctrl+F24", 0x87u)]
    [InlineData("Ctrl+Space", 0x20u)]
    [InlineData("Ctrl+PageUp", 0x21u)]
    [InlineData("Ctrl+PageDown", 0x22u)]
    [InlineData("Ctrl+End", 0x23u)]
    [InlineData("Ctrl+Home", 0x24u)]
    [InlineData("Ctrl+Left", 0x25u)]
    [InlineData("Ctrl+Up", 0x26u)]
    [InlineData("Ctrl+Right", 0x27u)]
    [InlineData("Ctrl+Down", 0x28u)]
    [InlineData("Ctrl+Backspace", 0x08u)]
    [InlineData("Ctrl+Tab", 0x09u)]
    [InlineData("Ctrl+Enter", 0x0Du)]
    [InlineData("Ctrl+Esc", 0x1Bu)]
    [InlineData("Ctrl+Insert", 0x2Du)]
    [InlineData("Ctrl+Delete", 0x2Eu)]
    public void VirtualKey_ConformeTabelaWin32(string text, uint expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.Equal(expected, gesture.VirtualKey);
    }

    [Fact]
    public void TryCreate_ExigePeloMenosUmModificador()
    {
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.None, 0x41, out _));
        Assert.True(HotkeyGesture.TryCreate(HotkeyModifiers.Shift, 0x41, out HotkeyGesture gesture));
        Assert.Equal("Shift+A", gesture.ToString());
    }

    [Fact]
    public void TryCreate_RecusaModificadorDesconhecidoEteclaForaDaTabela()
    {
        Assert.False(HotkeyGesture.TryCreate((HotkeyModifiers)0x10, 0x41, out _));
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Control, 0, out _));
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Control, 0xBA, out _));   // OEM+
    }

    [Fact]
    public void ToString_DeUmGestoPadraoNaoFinitoFalhaAlto()
    {
        Assert.Throws<System.ArgumentException>(() => HotkeyGesture.Format(default));
    }
}
