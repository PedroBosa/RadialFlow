// ============================================================================
// RadialFlow - Consistência dos recursos de idioma
// ----------------------------------------------------------------------------
// Lê os três .resx como texto: paridade de chaves, valores não vazios,
// espaços reservados ({0}) iguais entre idiomas e toda chave citada no XAML
// ou em chamadas literais a Loc.Get/Format realmente existindo no recurso.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace RadialFlow.Tests;

public sealed class ResourceParityTests
{
    private static readonly string[] Languages = ["pt-BR", "en-US", "es-ES"];

    private static readonly Lazy<IReadOnlyDictionary<string, SortedDictionary<string, string>>> Resources =
        new(() => Languages.ToDictionary(language => language, Read));

    private static string RepoFile(params string[] parts) =>
        Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", .. parts]));

    private static string ResourceFile(string language) =>
        language == "pt-BR"
            ? RepoFile("Resources", "Strings.resx")
            : RepoFile("Resources", $"Strings.{language}.resx");

    private static SortedDictionary<string, string> Read(string language)
    {
        XDocument document = XDocument.Load(ResourceFile(language));
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (XElement data in document.Root!.Elements("data"))
        {
            string? name = (string?)data.Attribute("name");
            string? value = (string?)data.Element("value");
            if (name is not null && value is not null) values[name] = value;
        }

        return values;
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)\}").Select(match => match.Value).ToArray();

    private static bool IsOutsideProject(string path)
    {
        string relative = Path.GetRelativePath(RepoFile(), path);
        return relative.StartsWith("RadialFlow.Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(RepoFile(), pattern, SearchOption.AllDirectories)
            .Where(path => !IsOutsideProject(path))
            .ToList();

    private static void AssertNoMissingKeys(IEnumerable<(string Key, string Source)> references)
    {
        SortedSet<string> known = new(Resources.Value["pt-BR"].Keys, StringComparer.Ordinal);
        string[] missing = references
            .Where(reference => !known.Contains(reference.Key))
            .Select(reference => $"{reference.Key} ({reference.Source})")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "chaves sem tradução:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // ------------------------------------------------------------------
    // Arquivos .resx
    // ------------------------------------------------------------------

    [Fact]
    public void TodosOsIdiomas_TemAsMesmasChaves_ComTextoNaoVazio()
    {
        SortedDictionary<string, string> reference = Resources.Value["pt-BR"];
        Assert.NotEmpty(reference);

        foreach (string language in Languages)
        {
            SortedDictionary<string, string> values = Resources.Value[language];
            Assert.Equal(reference.Keys, values.Keys);

            string[] empty = values.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToArray();
            Assert.True(empty.Length == 0, $"{language} com valor vazio: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void EspacosReservados_CoincidemEntreIdiomas()
    {
        SortedDictionary<string, string> reference = Resources.Value["pt-BR"];
        var problems = new List<string>();

        foreach ((string key, string value) in reference)
        {
            string[] expected = Placeholders(value);
            foreach (string language in Languages.Skip(1))
            {
                if (!Resources.Value[language].TryGetValue(key, out string? translated)) continue;

                string[] actual = Placeholders(translated);
                if (!expected.SequenceEqual(actual))
                    problems.Add($"{language} {key}: esperado [{string.Join(", ", expected)}], obtido [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ------------------------------------------------------------------
    // Chaves citadas no código
    // ------------------------------------------------------------------

    [Fact]
    public void ChavesDoXaml_ExistemNoRecurso()
    {
        IReadOnlyList<string> files = SourceFiles("*.xaml");

        var references = files
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\{loc:Loc\s+([A-Za-z0-9_.]+)\s*\}")
                .Select(match => (Key: match.Groups[1].Value, Source: Path.GetFileName(file))))
            .ToList();

        Assert.True(references.Count >= 200, $"apenas {references.Count} usos de loc:Loc encontrados; o teste perderia cobertura");
        AssertNoMissingKeys(references);
    }

    [Fact]
    public void ChavesLiteraisDoCodigo_ExistemNoRecurso()
    {
        var references = SourceFiles("*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"Loc\.(?:Get|Format)\(""([^""]+)""")
                .Select(match => (Key: match.Groups[1].Value, Source: Path.GetFileName(file))))
            .ToList();

        Assert.True(references.Count >= 300, $"apenas {references.Count} chaves literais encontradas; o teste perderia cobertura");
        AssertNoMissingKeys(references);
    }
}
