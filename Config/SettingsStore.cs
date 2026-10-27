// ============================================================================
// RadialFlow - Persistência das configurações (JSON) à prova de queda
// ----------------------------------------------------------------------------
// Problemas da versão anterior:
//   * File.WriteAllText trunca o arquivo ANTES de escrever: uma queda de energia ou
//     um "kill" no meio deixava um settings.json vazio/cortado;
//   * ao falhar a leitura, o Load gravava os PADRÕES por cima do arquivo: as listas
//     do usuário (blacklist, favoritos) eram apagadas sem aviso.
//
// Agora:
//   * SAVE atômico: grava em "settings.json.tmp" (com Flush no disco) e troca com
//     File.Replace, que mantém o arquivo anterior como "settings.json.bak". O arquivo
//     ativo nunca fica pela metade.
//   * LOAD tolerante: se o arquivo principal estiver ilegível, tenta o .bak; se ambos
//     falharem, o arquivo ruim é MOVIDO para "settings.corrompido-AAAAMMDD-HHMMSS.json"
//     (nada é apagado) e só então vêm os padrões.
//   * Campos ausentes ou com valores absurdos são corrigidos sem descartar o resto.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadialFlow.Infrastructure;
using RadialFlow.Input;

namespace RadialFlow.Config;

/// <summary>De onde veio o que foi carregado.</summary>
internal enum SettingsSource
{
    /// <summary>settings.json lido normalmente.</summary>
    File,

    /// <summary>settings.json estava ilegível; usou a cópia settings.json.bak.</summary>
    Backup,

    /// <summary>Não havia arquivo: padrões de fábrica.</summary>
    DefaultsNoFile,

    /// <summary>Havia arquivo, mas ilegível (e o .bak também): padrões; o arquivo ruim foi guardado.</summary>
    DefaultsAfterCorruption
}

internal readonly record struct SettingsLoadResult(AppSettings Settings, SettingsSource Source, string? QuarantinedTo);

internal sealed class SettingsStore(string directory, string fileName = "settings.json")
{
    private const int DefaultDeadzonePixels = 15;
    private const int MinDeadzonePixels = 8;
    private const int MaxDeadzonePixels = 40;

    // Tetos de sanidade para favoriteApps vindos de um settings.json editado à mão.
    internal const int MaxFavorites = 100;
    internal const int MaxFavoriteNameLength = 80;
    internal const int MaxFavoriteArgumentsLength = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string FilePath { get; } = Path.Combine(directory, fileName);
    public string BackupPath => FilePath + ".bak";
    private string TempPath => FilePath + ".tmp";

    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    /// <summary>Lê as configurações. Nunca lança; nunca apaga nem sobrescreve o arquivo do usuário.</summary>
    public SettingsLoadResult Load()
    {
        bool mainExists = File.Exists(FilePath);

        if (mainExists && TryRead(FilePath, out AppSettings? main))
            return new SettingsLoadResult(Normalize(main), SettingsSource.File, null);

        if (File.Exists(BackupPath) && TryRead(BackupPath, out AppSettings? backup))
        {
            AppLog.Warning("Settings", $"'{Path.GetFileName(FilePath)}' estava ilegível; usando a cópia anterior ({Path.GetFileName(BackupPath)}).");
            string? moved = mainExists ? Quarantine() : null;
            return new SettingsLoadResult(Normalize(backup), SettingsSource.Backup, moved);
        }

        if (!mainExists)
            return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsSource.DefaultsNoFile, null);

        string? quarantined = Quarantine();
        AppLog.Error("Settings", $"Configurações ilegíveis; usando os padrões. O arquivo original foi guardado em '{quarantined}'.");
        return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsSource.DefaultsAfterCorruption, quarantined);
    }

    private static bool TryRead(string path, out AppSettings settings)
    {
        settings = null!;
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;   // arquivo vazio/truncado

            AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (parsed is null) return false;

            settings = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Warning("Settings", $"Não foi possível ler '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Corrige o que faltar ou estiver fora do intervalo, preservando todo o resto.</summary>
    internal static AppSettings Normalize(AppSettings loaded)
    {
        loaded.Blacklist ??= new List<ForegroundBlacklist.BlacklistEntry>();
        loaded.FavoriteApps ??= new List<FavoriteAppEntry>();
        loaded.CustomProfiles ??= new List<CustomMenuProfile>();
        loaded.Accent ??= "Blue";
        loaded.WheelPreset = UI.WheelPreset.Normalize(loaded.WheelPreset);
        loaded.Language = Loc.Normalize(loaded.Language);

        string rawHotkey = loaded.Hotkey ?? "";
        loaded.Hotkey = HotkeyGesture.Normalize(rawHotkey);
        if (loaded.Hotkey.Length == 0 && rawHotkey.Trim().Length > 0)
            AppLog.Warning("Settings", "O atalho global gravado no arquivo não é válido; ele foi desativado.");

        CustomMenuNormalizer.Normalize(loaded.CustomProfiles);

        loaded.Blacklist = NormalizeBlacklist(loaded.Blacklist);
        loaded.FavoriteApps = NormalizeFavorites(loaded.FavoriteApps);

        // A UI trabalha em 8–40 px; abaixo de 5 o valor é absurdo e volta ao padrão.
        loaded.DeadzonePixels = loaded.DeadzonePixels switch
        {
            < 5 => DefaultDeadzonePixels,
            < MinDeadzonePixels => MinDeadzonePixels,
            > MaxDeadzonePixels => MaxDeadzonePixels,
            _ => loaded.DeadzonePixels
        };
        loaded.DwellMilliseconds = Math.Clamp(loaded.DwellMilliseconds,
            UI.RadialMenuWindow.MinDwellMilliseconds, UI.RadialMenuWindow.MaxDwellMilliseconds);
        return loaded;
    }

    /// <summary>Remove espaços, linhas sem caminho e duplicatas exatas (caminho + argumentos).</summary>
    private static List<FavoriteAppEntry> NormalizeFavorites(List<FavoriteAppEntry> favorites)
    {
        var normalized = new List<FavoriteAppEntry>(Math.Min(favorites.Count, MaxFavorites));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FavoriteAppEntry favorite in favorites)
        {
            if (favorite is null) continue;
            if (normalized.Count >= MaxFavorites) break;

            favorite.Name = (favorite.Name ?? "").Trim();
            favorite.ExecutablePath = (favorite.ExecutablePath ?? "").Trim();
            favorite.Arguments = (favorite.Arguments ?? "").Trim();
            favorite.Glyph = (favorite.Glyph ?? "").Trim();

            if (favorite.ExecutablePath.Length == 0) continue;
            if (favorite.Arguments.Length > MaxFavoriteArgumentsLength) continue;
            if (!seen.Add(favorite.ExecutablePath + "\0" + favorite.Arguments)) continue;

            favorite.Name = Truncate(favorite.Name, MaxFavoriteNameLength);
            normalized.Add(favorite);
        }

        int removed = favorites.Count - normalized.Count;
        if (removed > 0)
            AppLog.Debug("Settings", $"{removed} favorito(s) ignorado(s) na leitura: vazio, duplicado, argumentos longos demais ou acima de {MaxFavorites}.");

        return normalized;
    }

    /// <summary>Corta sem partir um par surrogate (emoji/ideograma fora do BMP).</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value
        : value[max - 1] is >= '\uD800' and <= '\uDBFF' ? value[..(max - 1)]
        : value[..max];

    /// <summary>Normaliza o nome (trim e sem .exe) e remove vazios/duplicatas, preservando o primeiro.</summary>
    private static List<ForegroundBlacklist.BlacklistEntry> NormalizeBlacklist(
        List<ForegroundBlacklist.BlacklistEntry> blacklist)
    {
        var normalized = new List<ForegroundBlacklist.BlacklistEntry>(blacklist.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ForegroundBlacklist.BlacklistEntry entry in blacklist)
        {
            if (entry is null) continue;

            string processName = CustomMenuNormalizer.NormalizeProcessName(entry.ProcessName);
            if (processName.Length == 0) continue;
            if (!seen.Add(processName)) continue;

            normalized.Add(new(processName, (entry.Description ?? "").Trim()));
        }

        int removed = blacklist.Count - normalized.Count;
        if (removed > 0)
            AppLog.Debug("Settings", $"{removed} entrada(s) de blacklist ignorada(s) na leitura: vazia ou duplicada.");

        return normalized;
    }

    /// <summary>Move o arquivo ruim para um nome único (nada é apagado). Devolve o novo caminho ou null se falhar.</summary>
    private string? Quarantine()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}.corrompido-{stamp}{Path.GetExtension(fileName)}");
            int n = 1;
            while (File.Exists(target))
                target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}.corrompido-{stamp}-{n++}{Path.GetExtension(fileName)}");

            File.Move(FilePath, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Settings", $"Não foi possível guardar o arquivo corrompido: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Gravação atômica
    // ------------------------------------------------------------------

    /// <summary>
    /// Grava as configurações sem nunca deixar o arquivo ativo pela metade.
    /// </summary>
    /// <exception cref="IOException">Disco cheio, sem permissão, etc. O arquivo anterior permanece intacto.</exception>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        try
        {
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);   // os bytes estão no disco ANTES da troca
            }

            if (File.Exists(FilePath))
                File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);   // troca atômica; guarda o anterior como .bak
            else
                File.Move(TempPath, FilePath);
        }
        catch
        {
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch (IOException) { /* lixo inofensivo */ }
            throw;
        }
    }
}
