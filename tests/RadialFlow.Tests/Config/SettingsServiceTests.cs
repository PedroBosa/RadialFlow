using System;
using System.IO;
using RadialFlow.Config;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Resultado da gravação e decisão da inicialização automática. Nada aqui toca o arquivo real do usuário:
/// a gravação é interceptada por SaveOverrideForTests e a decisão de inicialização é uma função pura
/// (o schtasks nunca é chamado).
/// </summary>
[Collection("UiWindows")]
public sealed class SettingsServiceTests : IDisposable
{
    public SettingsServiceTests()
    {
        SettingsService.OverrideCurrentForTests(AppSettings.CreateDefault());
        SettingsService.SaveOverrideForTests = null;
    }

    public void Dispose()
    {
        SettingsService.SaveOverrideForTests = null;
        SettingsService.OverrideCurrentForTests(null);
    }

    [Fact]
    public void TrySave_FalhaDeDisco_MantemCurrentEExplicaOMotivo()
    {
        AppSettings original = SettingsService.Current;
        var candidate = AppSettings.CreateDefault();
        candidate.DeadzonePixels = 33;

        SettingsService.SaveOverrideForTests = _ => throw new IOException("disco cheio");

        Assert.False(SettingsService.TrySave(candidate, out string? error));

        Assert.NotNull(error);
        Assert.Contains("disco cheio", error);
        Assert.Same(original, SettingsService.Current);
        Assert.Equal(15, SettingsService.Current.DeadzonePixels);
    }

    [Fact]
    public void TrySave_Sucesso_PublicaCurrentSoDepoisDaGravacao()
    {
        var candidate = AppSettings.CreateDefault();
        candidate.DeadzonePixels = 33;

        AppSettings? duringSave = null;
        SettingsService.SaveOverrideForTests = _ => duringSave = SettingsService.Current;

        Assert.True(SettingsService.TrySave(candidate, out string? error));

        Assert.Null(error);
        Assert.NotSame(candidate, duringSave);   // Current ainda era o anterior durante a gravação
        Assert.Same(candidate, SettingsService.Current);
    }

    [Fact]
    public void Save_NaoLancaQuandoODiscoFalha()
    {
        AppSettings original = SettingsService.Current;
        SettingsService.SaveOverrideForTests = _ => throw new UnauthorizedAccessException("sem permissão");

        SettingsService.Save(AppSettings.CreateDefault());

        Assert.Same(original, SettingsService.Current);
    }

    [Theory]
    [InlineData(false, null, null, null, true)]                                            // primeiro Save desligado: limpa a entrada legada
    [InlineData(true, null, null, @"C:\Apps\RadialFlow.exe", true)]                        // primeiro Save ligado
    [InlineData(false, false, null, @"C:\Apps\RadialFlow.exe", false)]                     // já desligado, nada mudou
    [InlineData(true, true, @"C:\Apps\RadialFlow.exe", @"C:\Apps\RadialFlow.exe", false)]  // ligado na mesma pasta
    [InlineData(true, true, @"C:\Apps\RadialFlow.exe", @"D:\Apps\RadialFlow.exe", true)]   // mudou de pasta
    [InlineData(false, true, @"C:\Apps\RadialFlow.exe", @"C:\Apps\RadialFlow.exe", true)]  // desligou
    [InlineData(true, false, null, @"C:\Apps\RadialFlow.exe", true)]                       // ligou
    [InlineData(true, true, @"C:\APPS\RadialFlow.EXE", @"c:\apps\radialflow.exe", false)]  // caminho sem diferenciar caixa
    public void NeedsStartupUpdate_SoNaPrimeiraVezOuQuandoMuda(
        bool enabled, bool? applied, string? appliedPath, string? currentPath, bool expected) =>
        Assert.Equal(expected,
            SettingsService.NeedsStartupUpdate(enabled, applied, appliedPath, currentPath));
}
