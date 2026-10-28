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
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal sealed record WheelPreset(
    string Key,
    string LabelKey,
    string DescriptionKey,
    double Scale,
    bool UsesHighContrast)
{
    internal const string DefaultKey = "Standard";

    // Públicas: o ComboBox das Configurações liga por DisplayMemberPath="Label",
    // e o binding do WPF só enxerga propriedades públicas.
    public string Label => Loc.Get(LabelKey);
    public string Description => Loc.Get(DescriptionKey);

    internal static readonly WheelPreset Standard = new(
        DefaultKey, "Wheel.Preset.Standard.Label", "Wheel.Preset.Standard.Description",
        Scale: 1.0, UsesHighContrast: false);

    internal static readonly WheelPreset Compact = new(
        "Compact", "Wheel.Preset.Compact.Label", "Wheel.Preset.Compact.Description",
        Scale: 0.85, UsesHighContrast: false);

    internal static readonly WheelPreset Large = new(
        "Large", "Wheel.Preset.Large.Label", "Wheel.Preset.Large.Description",
        Scale: 1.15, UsesHighContrast: false);

    internal static readonly WheelPreset HighContrast = new(
        "HighContrast", "Wheel.Preset.HighContrast.Label", "Wheel.Preset.HighContrast.Description",
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
