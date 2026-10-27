using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Lote real pelo motor do Windows (WIC), com arquivos numa pasta temporária.</summary>
public sealed class ConversionServiceBatchTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string WritePng(string name, byte b, byte g, byte r)
    {
        string path = _temp.Combine(name);
        byte[] pixels = [b, g, r, 255];
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        image.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>IProgress síncrono: garante a ordem dos relatórios (Progress&lt;T&gt; posta no contexto).</summary>
    private sealed class CollectingProgress : IProgress<ProgressReport>
    {
        public List<ProgressReport> Reports { get; } = new();

        public void Report(ProgressReport value)
        {
            lock (Reports) Reports.Add(value);
        }
    }

    [Fact]
    public async Task ConvertBatchAsync_ConverteTodosEOProgressoTerminaEm100()
    {
        string first = WritePng("a.png", 0, 0, 255);
        string second = WritePng("b.png", 255, 0, 0);
        string output = _temp.CreateDirectory("Saida");

        Assert.True(ConversionBatch.TryPlan([first, second], "bmp", output, out var plan, out string? error), error);

        var progress = new CollectingProgress();
        var result = await ConversionService.ConvertBatchAsync(plan!.Jobs, plan.TargetFormat, progress);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.All(plan.Jobs, job => Assert.True(File.Exists(job.Destination), job.Destination));
        Assert.All(progress.Reports, report => Assert.InRange(report.Percentage, 0.0, 100.0));
        Assert.Equal(100.0, progress.Reports[^1].Percentage, precision: 6);
        Assert.Contains(progress.Reports, report => report.StatusText.StartsWith("(2/2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertBatchAsync_ArquivoComFalhaNaoImpedeOsSeguintes()
    {
        string broken = _temp.CreateFile("quebrada.png");   // zero bytes: o WIC não decodifica
        string good = WritePng("boa.png", 0, 255, 0);
        string output = _temp.CreateDirectory("Saida");

        var jobs = new[]
        {
            new ConversionJob(broken, Path.Combine(output, "quebrada.bmp")),
            new ConversionJob(good, Path.Combine(output, "boa.bmp")),
        };

        var result = await ConversionService.ConvertBatchAsync(jobs, "bmp", progress: null);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(File.Exists(jobs[0].Destination));
        Assert.True(File.Exists(jobs[1].Destination));
        Assert.False(string.IsNullOrWhiteSpace(result.Items[0].Error));
        Assert.Null(result.Items[1].Error);
    }
}
