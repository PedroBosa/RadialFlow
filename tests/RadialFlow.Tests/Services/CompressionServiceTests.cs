using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CompressionServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // Data no intervalo do ZIP
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1970, 1, 1, 1980, 1, 1)]
    [InlineData(1980, 1, 1, 1980, 1, 1)]
    [InlineData(2026, 10, 27, 2026, 10, 27)]
    [InlineData(2200, 1, 1, 2107, 12, 31)]
    public void DataForaDoIntervalo_EncaixaNosLimites(int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        DateTime clamped = CompressionService.ClampTimestamp(new DateTime(year, month, day));

        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay), clamped.Date);
        Assert.InRange(clamped, new DateTime(1980, 1, 1), new DateTime(2107, 12, 31, 23, 59, 58));
    }

    [Fact]
    public async Task ArquivoComDataFutura_CompactaComADataMaxima()
    {
        string file = _temp.CreateFile("futuro.txt");
        await File.WriteAllTextAsync(file, "conteudo");
        File.SetLastWriteTime(file, new DateTime(2200, 1, 1));
        string zip = _temp.Combine("saida.zip");

        await CompressionService.CompressAsync([file], zip, null);

        using ZipArchive archive = ZipFile.OpenRead(zip);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(new DateTime(2107, 12, 31), entry.LastWriteTime.Date);
    }

    [Fact]
    public async Task ArquivoComum_PreservaADataDeModificacao()
    {
        string file = _temp.CreateFile("comum.txt");
        await File.WriteAllTextAsync(file, "conteudo");
        DateTime written = new(2026, 10, 27, 10, 30, 0);
        File.SetLastWriteTime(file, written);
        string zip = _temp.Combine("saida.zip");

        await CompressionService.CompressAsync([file], zip, null);

        using ZipArchive archive = ZipFile.OpenRead(zip);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(written, entry.LastWriteTime.DateTime);
    }
}
