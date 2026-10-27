using System;
using System.Runtime.InteropServices;
using System.Threading;
using RadialFlow.Input;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Registro real no Windows: cada teste usa uma combinação rara (Ctrl+Alt+Shift+F2x)
/// e libera tudo no Dispose. A thread precisa ser STA por causa da janela receptora.
/// </summary>
public sealed class GlobalHotkeyServiceTests
{
    private const int WM_HOTKEY = 0x0312;

    private const string ComboA = "Ctrl+Alt+Shift+F24";
    private const string ComboB = "Ctrl+Alt+Shift+F23";
    private const string ComboC = "Ctrl+Alt+Shift+F22";

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Falha na thread de UI do teste.", error);
    }

    private static void SendHotkey(GlobalHotkeyService service, int id = 1) =>
        SendMessage(service.Handle, WM_HOTKEY, new IntPtr(id), IntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------
    // Registro e remoção
    // ------------------------------------------------------------------

    [Fact]
    public void TrySet_RegistraACombinacaoNoSistema() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();

        HotkeyRegistrationError error = service.TrySet("ctrl+alt+shift+f24", out string normalized);

        Assert.Equal(HotkeyRegistrationError.None, error);
        Assert.Equal(ComboA, normalized);
        Assert.Equal(ComboA, service.Hotkey);
        Assert.True(service.IsRegistered);
        Assert.Equal(HotkeyRegistrationError.None, service.LastError);
        Assert.NotEqual(IntPtr.Zero, service.Handle);
    });

    [Fact]
    public void TrySet_VazioRemoveORegistro() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);

        HotkeyRegistrationError error = service.TrySet("   ", out string normalized);

        Assert.Equal(HotkeyRegistrationError.None, error);
        Assert.Equal("", normalized);
        Assert.Equal("", service.Hotkey);
        Assert.False(service.IsRegistered);
    });

    [Fact]
    public void TrySet_TextoInvalidoNaoMexeNoRegistroAnterior() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);

        HotkeyRegistrationError error = service.TrySet("F5", out string normalized);

        Assert.Equal(HotkeyRegistrationError.Invalid, error);
        Assert.Equal("", normalized);
        Assert.Equal(ComboA, service.Hotkey);
        Assert.True(service.IsRegistered);
        Assert.Equal(HotkeyRegistrationError.Invalid, service.LastError);
    });

    [Fact]
    public void TrySet_MesmoValorDuasVezesContinuaRegistrado() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);

        Assert.Equal(HotkeyRegistrationError.None, service.TrySet(ComboA, out string normalized));
        Assert.Equal(ComboA, normalized);
        Assert.True(service.IsRegistered);
    });

    [Fact]
    public void TrySet_TrocaLiberaACombinacaoAntiga() => RunSta(() =>
    {
        using var first = new GlobalHotkeyService();
        first.TrySet(ComboA, out _);

        Assert.Equal(HotkeyRegistrationError.None, first.TrySet(ComboB, out _));
        Assert.Equal(ComboB, first.Hotkey);
        Assert.True(first.IsRegistered);

        using var second = new GlobalHotkeyService();
        Assert.Equal(HotkeyRegistrationError.None, second.TrySet(ComboA, out _));
    });

    [Fact]
    public void Dispose_LiberaACombinacao() => RunSta(() =>
    {
        var first = new GlobalHotkeyService();
        first.TrySet(ComboA, out _);
        first.Dispose();

        Assert.False(first.IsRegistered);

        using var second = new GlobalHotkeyService();
        Assert.Equal(HotkeyRegistrationError.None, second.TrySet(ComboA, out _));
    });

    // ------------------------------------------------------------------
    // Conflito com outro programa (erro 1409)
    // ------------------------------------------------------------------

    [Fact]
    public void CombinacaoJaUsada_DevolveAlreadyInUse() => RunSta(() =>
    {
        using var first = new GlobalHotkeyService();
        using var second = new GlobalHotkeyService();
        first.TrySet(ComboA, out _);

        HotkeyRegistrationError error = second.TrySet(ComboA, out string normalized);

        Assert.Equal(HotkeyRegistrationError.AlreadyInUse, error);
        Assert.Equal(ComboA, normalized);
        Assert.False(second.IsRegistered);
        Assert.Equal("", second.Hotkey);   // a troca falhou: o desejo anterior (vazio) é restaurado
        Assert.True(first.IsRegistered);
    });

    [Fact]
    public void TrocaQueFalhaRestauraOAtalhoAnterior() => RunSta(() =>
    {
        using var ownerOfA = new GlobalHotkeyService();
        using var service = new GlobalHotkeyService();
        ownerOfA.TrySet(ComboA, out _);
        service.TrySet(ComboB, out _);

        HotkeyRegistrationError error = service.TrySet(ComboA, out _);

        Assert.Equal(HotkeyRegistrationError.AlreadyInUse, error);
        Assert.Equal(ComboB, service.Hotkey);
        Assert.True(service.IsRegistered);

        // A restauração é real: outro serviço não consegue tomar ComboB.
        using var probe = new GlobalHotkeyService();
        Assert.Equal(HotkeyRegistrationError.AlreadyInUse, probe.TrySet(ComboB, out _));
    });

    [Fact]
    public void SemAtalhoAnterior_TrocaQueFalhaNaoDeixaNadaRegistrado() => RunSta(() =>
    {
        using var ownerOfA = new GlobalHotkeyService();
        using var service = new GlobalHotkeyService();
        ownerOfA.TrySet(ComboA, out _);

        HotkeyRegistrationError error = service.TrySet(ComboA, out _);

        Assert.Equal(HotkeyRegistrationError.AlreadyInUse, error);
        Assert.Equal("", service.Hotkey);
        Assert.False(service.IsRegistered);
    });

    // ------------------------------------------------------------------
    // Pausa (ícone da bandeja)
    // ------------------------------------------------------------------

    [Fact]
    public void SetEnabled_DesligaEReligiaMantendoACombinacao() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);

        Assert.Equal(HotkeyRegistrationError.None, service.SetEnabled(false));
        Assert.False(service.IsEnabled);
        Assert.False(service.IsRegistered);
        Assert.Equal(ComboA, service.Hotkey);

        Assert.Equal(HotkeyRegistrationError.None, service.SetEnabled(true));
        Assert.True(service.IsRegistered);
    });

    [Fact]
    public void SetEnabled_CombinacaoTomadaPorOutroPrograma_ReportaOConflito() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        using var intruder = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);
        service.SetEnabled(false);
        intruder.TrySet(ComboA, out _);

        HotkeyRegistrationError error = service.SetEnabled(true);

        Assert.Equal(HotkeyRegistrationError.AlreadyInUse, error);
        Assert.False(service.IsRegistered);
        Assert.Equal(ComboA, service.Hotkey);

        intruder.SetEnabled(false);
        Assert.Equal(HotkeyRegistrationError.None, service.SetEnabled(true));
        Assert.True(service.IsRegistered);
    });

    [Fact]
    public void TrySetDuranteAPausa_ValidaSemSegurarACombinacao() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.SetEnabled(false);

        Assert.Equal(HotkeyRegistrationError.None, service.TrySet(ComboC, out _));
        Assert.Equal(ComboC, service.Hotkey);
        Assert.False(service.IsRegistered);

        Assert.Equal(HotkeyRegistrationError.None, service.SetEnabled(true));
        Assert.True(service.IsRegistered);
    });

    // ------------------------------------------------------------------
    // WM_HOTKEY
    // ------------------------------------------------------------------

    [Fact]
    public void WM_HOTKEY_DisparaPressed() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);   // cria a janela receptora
        int count = 0;
        service.Pressed += () => count++;

        SendHotkey(service);

        Assert.Equal(1, count);
    });

    [Fact]
    public void WM_HOTKEY_ComIdDesconhecidoNaoDispara() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);
        int count = 0;
        service.Pressed += () => count++;

        SendHotkey(service, id: 99);

        Assert.Equal(0, count);
    });

    [Fact]
    public void WM_HOTKEY_FalhaDoHandlerNaoEscapa() => RunSta(() =>
    {
        using var service = new GlobalHotkeyService();
        service.TrySet(ComboA, out _);
        bool reachedNextHandler = false;
        service.Pressed += () => throw new InvalidOperationException("falha proposital");
        service.Pressed += () => reachedNextHandler = true;

        SendHotkey(service);   // não pode lançar

        Assert.False(reachedNextHandler);   // multicast para no primeiro handler que lança
    });
}
