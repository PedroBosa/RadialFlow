using System;
using System.Linq;
using System.Text.Json;
using RadialFlow.Config;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Catálogo e persistência dos estilos visuais da roda. Cores/contraste ficam em RadialVisualsTests.</summary>
public sealed class WheelPresetTests
{
    [Fact]
    public void Catalogo_TemQuatroEstilosComChavesUnicas()
    {
        string[] keys = WheelPreset.All.Select(p => p.Key).ToArray();

        Assert.Equal(4, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(WheelPreset.DefaultKey, keys[0]);
        Assert.Equal("Standard", WheelPreset.DefaultKey);
        Assert.All(WheelPreset.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.InRange(p.Scale, 0.8, 1.25);
        });
    }

    [Fact]
    public void Catalogo_TemTamanhosDiferentesEUmAltoContraste()
    {
        Assert.Equal(1.0, WheelPreset.Standard.Scale);
        Assert.True(WheelPreset.Compact.Scale < WheelPreset.Standard.Scale);
        Assert.True(WheelPreset.Large.Scale > WheelPreset.Standard.Scale);

        Assert.False(WheelPreset.Standard.UsesHighContrast);
        Assert.False(WheelPreset.Compact.UsesHighContrast);
        Assert.False(WheelPreset.Large.UsesHighContrast);
        Assert.True(WheelPreset.HighContrast.UsesHighContrast);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("standard")]
    [InlineData("  COMPACT ")]
    [InlineData("Large")]
    [InlineData("HighContrast")]
    public void Resolve_ChaveValida_DevolveOEstilo(string key)
    {
        Assert.Equal(key.Trim(), WheelPreset.Resolve(key).Key, ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Roxo")]
    public void Resolve_ChaveAusenteOuDesconhecida_CaiNoPadrao(string? key)
    {
        Assert.Same(WheelPreset.Standard, WheelPreset.Resolve(key));
        Assert.Equal(WheelPreset.DefaultKey, WheelPreset.Normalize(key));
    }

    [Fact]
    public void Configuracoes_PadraoEArquivoAntigo_UsamOPresetPadrao()
    {
        Assert.Equal(WheelPreset.DefaultKey, new AppSettings().WheelPreset);
        Assert.Equal(WheelPreset.DefaultKey, AppSettings.CreateDefault().WheelPreset);

        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "DeadzonePixels": 20 }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded);
        Assert.Equal(WheelPreset.DefaultKey, loaded!.WheelPreset);
        Assert.Equal(20, loaded.DeadzonePixels);
    }

    [Fact]
    public void Configuracoes_ValorGravadoSobreviveAIdaEVolta()
    {
        string json = JsonSerializer.Serialize(new AppSettings { WheelPreset = "HighContrast" });
        var back = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal("HighContrast", back!.WheelPreset);
    }

    [Theory]
    [InlineData(null, "Standard")]
    [InlineData("", "Standard")]
    [InlineData("  compact ", "Compact")]
    [InlineData("HIGHCONTRAST", "HighContrast")]
    [InlineData("Large", "Large")]
    [InlineData("desconhecido", "Standard")]
    public void Normalizacao_ArquivoComQualquerTexto_NaoInvalidaOResto(string? stored, string expected)
    {
        var loaded = new AppSettings { WheelPreset = stored!, DeadzonePixels = 22 };

        AppSettings normalized = SettingsStore.Normalize(loaded);

        Assert.Equal(expected, normalized.WheelPreset);
        Assert.Equal(22, normalized.DeadzonePixels);
    }
}
