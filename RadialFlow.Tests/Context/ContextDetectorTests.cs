using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using RadialFlow.Context;
using Xunit;

namespace RadialFlow.Tests;

[Collection("UiWindows")]
public sealed class ContextDetectorTests
{
    [Fact]
    public void WindowTitleOf_LeOTituloDeUmaJanelaReal()
    {
        RunSta(() =>
        {
            var window = new Window { Title = "RadialFlow título de teste", ShowInTaskbar = false };
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert.NotEqual(IntPtr.Zero, hwnd);
                Assert.Equal("RadialFlow título de teste", ContextDetector.WindowTitleOf(hwnd));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowTitleOf_HandleZero_DevolveVazio() =>
        Assert.Equal("", ContextDetector.WindowTitleOf(IntPtr.Zero));

    [Fact]
    public void ContextInfo_SemTitulo_MantemCompatibilidadeComConstrucoesExistentes()
    {
        var ctx = new ContextInfo(RadialContextKind.Generic, (IntPtr)123, "explorer", 10, 20, ContextInfo.NoPaths);

        Assert.Equal("", ctx.WindowTitle);
        Assert.Equal("novo", (ctx with { WindowTitle = "novo" }).WindowTitle);
    }

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
}
