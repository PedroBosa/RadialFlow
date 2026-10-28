// ============================================================================
// RadialFlow - Identidade do aplicativo (lida do assembly: uma só fonte de verdade)
// ============================================================================

using System;
using System.Reflection;

namespace RadialFlow.Infrastructure;

public static class AppInfo
{
    /// <summary>Nome do produto (propriedade Product do projeto).</summary>
    public static string Product { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "RadialFlow";

    /// <summary>Versão exibida, ex.: "1.2.0" (sem sufixo de commit).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"RadialFlow 1.2.0".</summary>
    public static string DisplayName => $"{Product} {Version}";

    private static string ReadVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);   // "1.2.0+abc123" → "1.2.0"
            return plus >= 0 ? informational[..plus] : informational;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
