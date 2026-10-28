using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

public sealed class RadialPlacementTests
{
    // Monitor Full HD em (0,0)-(1920,1080), escala 1.
    private static (int X, int Y) Place(int x, int y, double scale = 1.0, int left = 0, int top = 0, int right = 1920, int bottom = 1080) =>
        RadialPlacement.Clamp(x, y, left, top, right, bottom, scale);

    [Theory]
    [InlineData(960, 540)]
    [InlineData(300, 300)]
    [InlineData(1600, 800)]
    public void LongeDasBordas_NaoMudaNada(int x, int y)
    {
        Assert.Equal((x, y), Place(x, y));
    }

    [Fact]
    public void PertoDaBarraDeTarefas_SobeSoOQueFaltaIncluindoADica()
    {
        // Caso do print: gesto na parte de baixo da tela. A roda + etiqueta precisam caber.
        var (x, y) = Place(1000, 1050);

        Assert.Equal(1000, x);
        Assert.Equal(1080 - (int)RadialPlacement.BottomMargin, y);
    }

    [Fact]
    public void CantoSuperiorEsquerdo_DesceEVaiParaADireita()
    {
        var (x, y) = Place(5, 3);

        Assert.Equal((int)RadialPlacement.SideMargin, x);
        Assert.Equal((int)RadialPlacement.TopMargin, y);
    }

    [Fact]
    public void CantoInferiorDireito_SobeEVaiParaAEsquerda()
    {
        var (x, y) = Place(1919, 1079);

        Assert.Equal(1920 - (int)RadialPlacement.SideMargin, x);
        Assert.Equal(1080 - (int)RadialPlacement.BottomMargin, y);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ResultadoSempreCabeNoMonitor_EmQualquerEscala(double scale)
    {
        int width = 2560, height = 1440;
        foreach (int x in new[] { -50, 0, 10, 1280, 2550, 2559, 3000 })
        {
            foreach (int y in new[] { -50, 0, 10, 720, 1430, 1439, 2000 })
            {
                var (cx, cy) = Place(x, y, scale, right: width, bottom: height);

                Assert.True(cx - RadialPlacement.SideMargin * scale >= -1, $"esquerda ({x},{y}) x{scale}");
                Assert.True(cx + RadialPlacement.SideMargin * scale <= width + 1, $"direita ({x},{y}) x{scale}");
                Assert.True(cy - RadialPlacement.TopMargin * scale >= -1, $"topo ({x},{y}) x{scale}");
                Assert.True(cy + RadialPlacement.BottomMargin * scale <= height + 1, $"base ({x},{y}) x{scale}");
            }
        }
    }

    [Fact]
    public void MonitorSecundarioComCoordenadasNegativas_RespeitaOsLimitesDele()
    {
        // Monitor à esquerda do principal: X de -1920 a 0.
        var (x, y) = RadialPlacement.Clamp(-1915, 500, -1920, 0, 0, 1080, 1.0);

        Assert.Equal(-1920 + (int)RadialPlacement.SideMargin, x);
        Assert.Equal(500, y);
    }

    [Fact]
    public void MonitorMenorQueARoda_Centraliza_EmVezDeFicarPresoNumaBorda()
    {
        var (x, y) = RadialPlacement.Clamp(10, 10, 0, 0, 300, 300, 1.0);

        Assert.Equal(150, x);
        Assert.InRange(y, 100, 200);   // centro entre os limites impossíveis
    }

    [Fact]
    public void EscalaInvalida_UsaUm()
    {
        Assert.Equal(Place(5, 3), RadialPlacement.Clamp(5, 3, 0, 0, 1920, 1080, 0));
        Assert.Equal(Place(5, 3), RadialPlacement.Clamp(5, 3, 0, 0, 1920, 1080, -2));
    }
}
