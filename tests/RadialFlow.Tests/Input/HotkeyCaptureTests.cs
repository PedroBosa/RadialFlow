using System.Windows.Input;
using RadialFlow.Input;
using Xunit;

namespace RadialFlow.Tests;

public sealed class HotkeyCaptureTests
{
    [Theory]
    [InlineData(Key.F5, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+F5")]
    [InlineData(Key.Space, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+Space")]
    [InlineData(Key.F4, ModifierKeys.Alt, "Alt+F4")]
    [InlineData(Key.D1, ModifierKeys.Windows, "Win+1")]
    [InlineData(Key.A, ModifierKeys.Control, "Ctrl+A")]
    [InlineData(Key.PageDown, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+PageDown")]
    public void TryCapture_MontaACombinacaoCanonica(Key key, ModifierKeys modifiers, string expected)
    {
        HotkeyCaptureResult result = HotkeyCapture.TryCapture(key, modifiers, out HotkeyGesture gesture);

        Assert.Equal(HotkeyCaptureResult.Captured, result);
        Assert.Equal(expected, gesture.ToString());
    }

    [Fact]
    public void EscSemModificadores_Cancela()
    {
        Assert.Equal(HotkeyCaptureResult.Cancelled,
            HotkeyCapture.TryCapture(Key.Escape, ModifierKeys.None, out _));
    }

    [Fact]
    public void EscComModificador_EhUmAtalhoValido()
    {
        HotkeyCaptureResult result = HotkeyCapture.TryCapture(
            Key.Escape, ModifierKeys.Control | ModifierKeys.Shift, out HotkeyGesture gesture);

        Assert.Equal(HotkeyCaptureResult.Captured, result);
        Assert.Equal("Ctrl+Shift+Esc", gesture.ToString());
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    public void SomenteModificador_AguardaATeclaFinal(Key key) =>
        Assert.Equal(HotkeyCaptureResult.ModifierOnly,
            HotkeyCapture.TryCapture(key, ModifierKeys.Control | ModifierKeys.Alt, out _));

    [Fact]
    public void SomenteModificador_ValeMesmoSemOutrosModificadores() =>
        Assert.Equal(HotkeyCaptureResult.ModifierOnly,
            HotkeyCapture.TryCapture(Key.LeftCtrl, ModifierKeys.None, out _));

    [Fact]
    public void TeclaSemModificador_EhRecusada() =>
        Assert.Equal(HotkeyCaptureResult.MissingModifier,
            HotkeyCapture.TryCapture(Key.F5, ModifierKeys.None, out _));

    [Fact]
    public void TeclaForaDaTabela_EhRecusada() =>
        Assert.Equal(HotkeyCaptureResult.Unsupported,
            HotkeyCapture.TryCapture(Key.OemPlus, ModifierKeys.Control, out _));

    [Theory]
    [InlineData(Key.LeftCtrl, true)]
    [InlineData(Key.RWin, true)]
    [InlineData(Key.A, false)]
    [InlineData(Key.Escape, false)]
    public void IsModifierKey_ReconheceOsModificadores(Key key, bool expected) =>
        Assert.Equal(expected, HotkeyCapture.IsModifierKey(key));

    [Fact]
    public void DescribeModifiers_UsaAOrdemCanonica()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win",
            HotkeyCapture.DescribeModifiers(
                ModifierKeys.Windows | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control));
        Assert.Equal("", HotkeyCapture.DescribeModifiers(ModifierKeys.None));
    }
}
