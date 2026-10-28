// ============================================================================
// RadialFlow - Idioma da interface
// ----------------------------------------------------------------------------
// Textos ficam em Resources/Strings.resx (português, o padrão) e nas cópias
// .en-US / .es-ES, embutidas como satélites pelo SDK.
//
// A cultura é resolvida UMA vez por execução (App.OnStartup). A escolha feita
// nas Configurações é gravada e passa a valer no próximo início: nada de
// recarregar XAML já construído. Loc.Get/Format não dependem da cultura
// ambiente da thread, o que mantém os testes determinísticos.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;

namespace RadialFlow.Infrastructure;

/// <summary>Idioma oferecido nas Configurações. O nome é sempre o nativo (não se traduz).</summary>
internal sealed record LanguageOption(string Key, string Tag, string NativeName);

internal static class Loc
{
    internal const string DefaultLanguage = "pt-BR";

    private const string ResourceBaseName = "RadialFlow.Resources.Strings";

    private static readonly ResourceManager Strings =
        new(ResourceBaseName, typeof(Loc).Assembly);

    private static CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultLanguage);

    /// <summary>Ordem exibida no seletor de idioma.</summary>
    internal static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("pt-BR", "pt", "Português (Brasil)"),
        new("en-US", "en", "English (United States)"),
        new("es-ES", "es", "Español (España)")
    ];

    internal static CultureInfo Culture => _culture;

    /// <summary>
    /// Chave suportada para o idioma gravado. Aceita também a etiqueta base ("en", "es", "pt") e
    /// qualquer variação regional dela ("en-GB" → "en-US"); desconhecido ou vazio cai no padrão.
    /// </summary>
    internal static string Resolve(string? language)
    {
        string value = (language ?? "").Trim();
        if (value.Length == 0) return DefaultLanguage;

        foreach (LanguageOption option in Languages)
            if (string.Equals(option.Key, value, StringComparison.OrdinalIgnoreCase))
                return option.Key;

        int dash = value.IndexOf('-');
        string tag = dash > 0 ? value[..dash] : value;

        foreach (LanguageOption option in Languages)
            if (string.Equals(option.Tag, tag, StringComparison.OrdinalIgnoreCase))
                return option.Key;

        return DefaultLanguage;
    }

    internal static string Normalize(string? language) => Resolve(language);

    /// <summary>
    /// Troca a cultura usada por <see cref="Get"/> e <see cref="Format"/>. Não mexe na cultura da thread:
    /// quem inicia o app chama <see cref="ApplyToCurrentThread"/> logo em seguida.
    /// </summary>
    internal static void SetLanguage(string? language) =>
        _culture = CultureInfo.GetCultureInfo(Resolve(language));

    /// <summary>
    /// Alinha a cultura de interface da thread e das threads novas (textos padrão do WPF, caixas nativas).
    /// A cultura de formatação (números/datas) continua a do Windows: o idioma da interface não a define.
    /// </summary>
    internal static void ApplyToCurrentThread()
    {
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
    }

    /// <summary>Texto traduzido; chave ausente devolve a própria chave (visível nos testes, nunca vazio).</summary>
    internal static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        return Strings.GetString(key, _culture) ?? key;
    }

    internal static string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    /// <summary>
    /// Chaves realmente presentes no recurso de um idioma (testes e diagnóstico). Idiomas sem satélite
    /// — o padrão — caem no recurso neutro.
    /// </summary>
    internal static IReadOnlyList<string> Keys(string? language)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(Resolve(language));
        ResourceSet? set = Strings.GetResourceSet(culture, createIfNotExists: false, tryParents: false)
                           ?? Strings.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);

        if (set is null) return [];

        var keys = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in set)
            if (entry.Key is string key) keys.Add(key);

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}
