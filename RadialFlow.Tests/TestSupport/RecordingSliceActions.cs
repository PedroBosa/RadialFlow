using System.Collections.Generic;
using RadialFlow.Config;
using RadialFlow.Context;

namespace RadialFlow.Tests.TestSupport;

/// <summary>Dublê de <see cref="ISliceActions"/>: registra o que cada fatia dispararia, sem efeito colateral.</summary>
internal sealed class RecordingSliceActions : ISliceActions
{
    public List<(RadialAction Action, ContextInfo Context)> Runs { get; } = new();
    public List<(IReadOnlyList<string> Sources, string Format)> Conversions { get; } = new();
    public List<FavoriteAppEntry> Launches { get; } = new();
    public List<(CustomMenuItem Item, ContextInfo Context)> CustomRuns { get; } = new();

    public int TotalCalls => Runs.Count + Conversions.Count + Launches.Count + CustomRuns.Count;

    public void Run(RadialAction action, ContextInfo context) => Runs.Add((action, context));
    public void ConvertTo(IReadOnlyList<string> sourceFiles, string targetFormat) => Conversions.Add((sourceFiles, targetFormat));
    public void LaunchFavorite(FavoriteAppEntry app) => Launches.Add(app);
    public void RunCustom(CustomMenuItem item, ContextInfo context) => CustomRuns.Add((item, context));
}
