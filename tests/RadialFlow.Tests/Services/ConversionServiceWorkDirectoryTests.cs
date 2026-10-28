using System;
using System.IO;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Limpeza das pastas de trabalho que conversões interrompidas deixam em %TEMP%\RadialFlow.</summary>
public sealed class ConversionServiceWorkDirectoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string CreateWorkDirectory(DateTime lastWriteUtc, bool withContent = false)
    {
        string dir = _temp.CreateDirectory(Guid.NewGuid().ToString("N"));
        if (withContent)
        {
            string outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
            File.WriteAllText(Path.Combine(outDir, "resultado.txt"), "x");
        }
        Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
        return dir;
    }

    [Fact]
    public void Limpeza_RemoveSomentePastasDeTrabalhoAntigas()
    {
        DateTime now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        DateTime cutoff = now - TimeSpan.FromDays(1);

        string stale = CreateWorkDirectory(now - TimeSpan.FromDays(2), withContent: true);
        string fresh = CreateWorkDirectory(now - TimeSpan.FromMinutes(5));
        string oldOther = _temp.CreateDirectory("conversao-antiga");
        Directory.SetLastWriteTimeUtc(oldOther, now - TimeSpan.FromDays(2));
        string oldWithHyphens = _temp.CreateDirectory(Guid.NewGuid().ToString("D"));
        Directory.SetLastWriteTimeUtc(oldWithHyphens, now - TimeSpan.FromDays(2));
        string oldFile = _temp.CreateFile(Guid.NewGuid().ToString("N"));
        File.SetLastWriteTimeUtc(oldFile, now - TimeSpan.FromDays(2));

        int removed = ConversionService.CleanUpStaleWorkDirectories(_temp.Root, cutoff);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(oldOther));
        Assert.True(Directory.Exists(oldWithHyphens));
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public void Limpeza_PastaNoLimiteDeIdade_NaoRemove()
    {
        DateTime cutoff = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        string atCutoff = CreateWorkDirectory(cutoff);

        int removed = ConversionService.CleanUpStaleWorkDirectories(_temp.Root, cutoff);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(atCutoff));
    }

    [Fact]
    public void Limpeza_RaizAusente_NaoFalha()
    {
        int removed = ConversionService.CleanUpStaleWorkDirectories(_temp.Combine("nao-existe"), DateTime.UtcNow);

        Assert.Equal(0, removed);
    }
}
