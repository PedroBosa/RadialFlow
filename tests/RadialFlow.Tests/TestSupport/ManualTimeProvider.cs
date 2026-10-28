using System;

namespace RadialFlow.Tests.TestSupport;

/// <summary>Relógio controlado pelo teste (cache de "não encontrado" do ToolLocator).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
