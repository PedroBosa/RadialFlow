// ============================================================================
// RadialFlow - Presets visuais da roda
// ----------------------------------------------------------------------------
// Quatro estilos locais: tamanho (Compacto/Amplo) e legibilidade (Alto
// contraste). A cor de destaque continua valendo em todos; o preset não
// introduz CSS, fontes nem imagens externas.
//
// O preset é gravado como texto de propósito: um valor desconhecido no
// settings.json cai no padrão em vez de invalidar o arquivo inteiro.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RadialFlow.UI;

internal sealed record WheelPreset(
    string Key,
    string Label,
    string Description,
    double Scale,
    bool UsesHighContrast)
{
    internal const string DefaultKey = "Standard";

    internal static readonly WheelPreset Standard = new(
        DefaultKey, "Padrão",
        "Equilibrado, com o fundo desfocado do Windows.",
        Scale: 1.0, UsesHighContrast: false);

    internal static readonly WheelPreset Compact = new(
        "Compact", "Compacto",
        "Roda menor, para telas pequenas ou gestos curtos.",
        Scale: 0.85, UsesHighContrast: false);

    internal static readonly WheelPreset Large = new(
        "Large", "Amplo",
        "Roda maior, com fatias e rótulos mais fáceis de acertar.",
        Scale: 1.15, UsesHighContrast: false);

    internal static readonly WheelPreset HighContrast = new(
        "HighContrast", "Alto contraste",
        "Superfícies opacas e contornos fortes, sem fundo desfocado.",
        Scale: 1.0, UsesHighContrast: true);

    internal static readonly IReadOnlyList<WheelPreset> All =
        [Standard, Compact, Large, HighContrast];

    /// <summary>Preset para a chave gravada; nulo, vazio ou desconhecido = padrão.</summary>
    internal static WheelPreset Resolve(string? key)
    {
        foreach (WheelPreset preset in All)
        {
            if (string.Equals(preset.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return Standard;
    }

    internal static string Normalize(string? key) => Resolve(key).Key;
}
