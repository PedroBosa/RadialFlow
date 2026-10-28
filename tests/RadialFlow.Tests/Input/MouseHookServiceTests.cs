using System;
using System.Collections.Generic;
using RadialFlow.Input;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Modo atalho do hook do mouse: a roda aberta por RegisterHotKey é uma janela WS_EX_NOACTIVATE sem
/// hit-test, então movimento e cliques só chegam pelo hook. Os eventos são processados direto
/// (HandleEventForTests), sem instalar o hook global nem sintetizar cliques no sistema.
/// </summary>
public sealed class MouseHookServiceTests
{
    private static MouseHookService.POINT At(int x, int y) => new() { X = x, Y = y };

    // ------------------------------------------------------------------
    // Clique esquerdo: escolhe a opção
    // ------------------------------------------------------------------

    [Fact]
    public void ModoAtalho_CliqueEsquerdo_EngoleDownEUp_EExecutaNoSoltar()
    {
        using var hook = new MouseHookService { HotkeyMode = true };
        var clicks = new List<MouseHookService.POINT>();
        hook.HotkeyClicked += clicks.Add;

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONDOWN, At(10, 20)));
        Assert.Empty(clicks);   // a ação só dispara no soltar

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONUP, At(11, 21)));

        MouseHookService.POINT click = Assert.Single(clicks);
        Assert.Equal(11, click.X);
        Assert.Equal(21, click.Y);
    }

    [Fact]
    public void ModoAtalho_UpPendente_ContinuaEngolidoDepoisDoModoTerminar()
    {
        // A roda pode fechar entre o down e o up: o up ainda pertence ao clique que começou nela.
        using var hook = new MouseHookService { HotkeyMode = true };
        bool clicked = false;
        hook.HotkeyClicked += _ => clicked = true;

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONDOWN, At(0, 0)));

        hook.HotkeyMode = false;
        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONUP, At(0, 0)));
        Assert.False(clicked);
    }

    [Fact]
    public void ModoAtalho_Movimento_EntregaPosicao_NaoEngole()
    {
        using var hook = new MouseHookService { HotkeyMode = true };
        var moves = new List<MouseHookService.POINT>();
        hook.PointerMoved += moves.Add;

        Assert.False(hook.HandleEventForTests(MouseHookService.WM_MOUSEMOVE, At(7, 8)));

        MouseHookService.POINT move = Assert.Single(moves);
        Assert.Equal(7, move.X);
        Assert.Equal(8, move.Y);
    }

    // ------------------------------------------------------------------
    // Botão direito: cancela
    // ------------------------------------------------------------------

    [Fact]
    public void ModoAtalho_BotaoDireito_Cancela_EUpTambemEhEngolido()
    {
        using var hook = new MouseHookService { HotkeyMode = true };
        int cancels = 0;
        hook.HotkeyCancelled += () => cancels++;

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_RBUTTONDOWN, At(0, 0)));
        Assert.Equal(1, cancels);

        hook.HotkeyMode = false;   // o cancelamento fecha a roda antes do up
        Assert.True(hook.HandleEventForTests(MouseHookService.WM_RBUTTONUP, At(0, 0)));
        Assert.Equal(1, cancels);  // o up só completa o clique já engolido
    }

    // ------------------------------------------------------------------
    // Fora do modo: comportamento de sempre
    // ------------------------------------------------------------------

    [Fact]
    public void ForaDoModo_CliquesEsquerdosEMovimentoPassam()
    {
        using var hook = new MouseHookService();
        int events = 0;
        hook.HotkeyClicked += _ => events++;
        hook.PointerMoved += _ => events++;

        Assert.False(hook.HandleEventForTests(MouseHookService.WM_LBUTTONDOWN, At(1, 1)));
        Assert.False(hook.HandleEventForTests(MouseHookService.WM_LBUTTONUP, At(1, 1)));
        Assert.False(hook.HandleEventForTests(MouseHookService.WM_MOUSEMOVE, At(2, 2)));
        Assert.Equal(0, events);
    }

    [Fact]
    public void ModoAtalho_ValeComEnabledDesligado()
    {
        // App da blacklist: o gesto está desligado, mas o atalho explícito do usuário continua valendo.
        using var hook = new MouseHookService { Enabled = false, HotkeyMode = true };
        bool clicked = false;
        hook.HotkeyClicked += _ => clicked = true;

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONDOWN, At(0, 0)));
        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONUP, At(0, 0)));
        Assert.True(clicked);

        // Sem a roda aberta, o botão direito volta a passar direto (gesto desligado).
        hook.HotkeyMode = false;
        Assert.False(hook.HandleEventForTests(MouseHookService.WM_RBUTTONDOWN, At(0, 0)));
    }

    [Fact]
    public void Gesto_BotaoDireitoAtivaOGesto_AteSoltar()
    {
        using var hook = new MouseHookService();
        var starts = new List<MouseHookService.POINT>();
        hook.DragStarted += starts.Add;

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_RBUTTONDOWN, At(100, 100)));
        Assert.True(hook.IsGestureActive);

        Assert.False(hook.HandleEventForTests(MouseHookService.WM_MOUSEMOVE, At(103, 104)));   // dentro da zona morta
        Assert.Empty(starts);
        Assert.True(hook.IsGestureActive);

        Assert.False(hook.HandleEventForTests(MouseHookService.WM_MOUSEMOVE, At(130, 100)));   // passou da zona morta
        Assert.Single(starts);
        Assert.True(hook.IsGestureActive);

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_RBUTTONUP, At(130, 100)));
        Assert.False(hook.IsGestureActive);
    }

    // ------------------------------------------------------------------
    // Robustez
    // ------------------------------------------------------------------

    [Fact]
    public void ExcecaoEmHandler_NaoEscapaDoHook()
    {
        // Uma exceção dentro do callback nativo derrubaria o processo; Raise a registra e segue.
        using var hook = new MouseHookService { HotkeyMode = true };
        hook.PointerMoved += _ => throw new InvalidOperationException("falha de teste");
        hook.HotkeyClicked += _ => throw new InvalidOperationException("falha de teste");
        hook.HotkeyCancelled += () => throw new InvalidOperationException("falha de teste");

        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONDOWN, At(0, 0)));
        Assert.True(hook.HandleEventForTests(MouseHookService.WM_LBUTTONUP, At(0, 0)));
        Assert.False(hook.HandleEventForTests(MouseHookService.WM_MOUSEMOVE, At(1, 1)));
        Assert.True(hook.HandleEventForTests(MouseHookService.WM_RBUTTONDOWN, At(1, 1)));
    }
}
