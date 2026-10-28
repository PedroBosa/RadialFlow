// ============================================================================
// RadialFlow - Transformações de texto
// ----------------------------------------------------------------------------
// Funções PURAS: recebem a seleção capturada e devolvem o texto transformado.
// Nada aqui lê clipboard, envia teclas ou toca em janela; a captura e a
// colagem ficam em ActionRouter.Custom.
//
// Caixa alta/baixa usam as variantes invariantes para não depender do idioma
// do sistema (o menu pode estar em pt-BR com o Windows em outro idioma).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RadialFlow.Config;

namespace RadialFlow.Context;

internal static class TextTransforms
{
    internal static string Apply(string text, CustomTextTransform transform) => transform switch
    {
        CustomTextTransform.Upper => text.ToUpperInvariant(),
        CustomTextTransform.Lower => text.ToLowerInvariant(),
        CustomTextTransform.TitleCase => TitleCase(text),
        CustomTextTransform.SentenceCase => SentenceCase(text),
        CustomTextTransform.Trim => text.Trim(),
        CustomTextTransform.RemoveLineBreaks => JoinLines(text),
        _ => text
    };

    private static string TitleCase(string text) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    /// <summary>Minúsculas, com a primeira letra e as letras após . ! ? e quebras em maiúscula.</summary>
    private static string SentenceCase(string text)
    {
        string lower = text.ToLowerInvariant();
        var result = new StringBuilder(lower.Length);
        bool capitalize = true;

        foreach (char c in lower)
        {
            if (capitalize && char.IsLetter(c))
            {
                result.Append(char.ToUpperInvariant(c));
                capitalize = false;
                continue;
            }

            result.Append(c);
            if (c is '.' or '!' or '?' or '\n' or '\r') capitalize = true;
        }

        return result.ToString();
    }

    /// <summary>Junta as linhas não vazias com um espaço, aparando cada uma.</summary>
    private static string JoinLines(string text)
    {
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries);

        var parts = new List<string>(lines.Length);
        foreach (string line in lines)
            if (line.Length > 0) parts.Add(line);

        return string.Join(" ", parts);
    }
}
