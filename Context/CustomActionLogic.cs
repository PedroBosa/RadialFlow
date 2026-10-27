// ============================================================================
// RadialFlow - Validação dos itens personalizados
// ----------------------------------------------------------------------------
// Funções PURAS (sem efeito): decidem se um item pode rodar e resolvem o alvo.
// A execução fica em ActionRouter.Custom.
//
// Postura de segurança (o app roda elevado; estes itens NÃO):
//  * Launch: só .exe em local protegido (System32/Windows/PATH do sistema ou
//    caminho absoluto com ACL confiável), sem argumentos e sem interpretadores.
//    O TryResolveTarget do UnelevatedLauncher NÃO valida ACL de caminho absoluto,
//    por isso a checagem com TrustedPath.IsTrusted é feita aqui.
//  * OpenUrl: apenas http/https.
//  * OpenPath: pasta ou arquivo existente; executáveis, scripts e atalhos são
//    recusados (a associação do Windows os executaria).
//  * Hotkey: modificadores conhecidos + uma tecla de uma lista curada.
//  * Text: 1..4096 caracteres, colados via clipboard.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.Context;

internal sealed record CustomActionPlan(
    CustomMenuItemKind Kind,
    string Target,
    byte[] Keys,
    string Text);

internal static class CustomActionLogic
{
    private const byte VK_SHIFT   = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU    = 0x12;   // Alt
    private const byte VK_LWIN    = 0x5B;

    /// <summary>
    /// Executáveis capazes de rodar comandos/scripts. Mesmo confiáveis, abrir
    /// esses sem argumentos ainda entrega um shell ou um interpretador.
    /// </summary>
    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "powershell_ise", "pwsh", "wt", "wsl", "bash",
        "cscript", "wscript", "mshta", "rundll32", "regsvr32", "msiexec",
        "wmic", "schtasks", "sc", "reg", "bitsadmin", "certutil",
        "msbuild", "csc", "installutil", "regasm", "regsvcs", "forfiles",
        "explorer"
    };

    /// <summary>Extensões que o Windows executa (ou que apontam para algo executável).</summary>
    private static readonly HashSet<string> BlockedPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msc", ".scr",
        ".cpl", ".reg", ".lnk", ".url", ".jar", ".py", ".pyw", ".pif",
        ".application", ".chm", ".docm", ".xlsm", ".pptm", ".settingcontent-ms",
        ".scf", ".diagcab", ".search-ms", ".library-ms"
    };

    private static readonly Dictionary<string, byte> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Return"] = 0x0D,
        ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Tab"] = 0x09, ["Space"] = 0x20, ["Backspace"] = 0x08,
        ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Ins"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PgUp"] = 0x21, ["PageDown"] = 0x22, ["PgDn"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C, ["PrtSc"] = 0x2C, ["Pause"] = 0x13,
        ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["VolumeMute"] = 0xAD, ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF,
        ["MediaNext"] = 0xB0, ["MediaPrev"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
        ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7, ["BrowserRefresh"] = 0xA8,
        ["BrowserStop"] = 0xA9, ["BrowserSearch"] = 0xAA, ["BrowserFavorites"] = 0xAB,
        ["BrowserHome"] = 0xAC
    };

    /// <summary>
    /// Itens de ação nativa são executados por <see cref="ActionRouter.Run"/>;
    /// aqui só entram os tipos externos (Launch/OpenUrl/OpenPath/Hotkey/Text).
    /// </summary>
    internal static bool TryValidate(CustomMenuItem item, out CustomActionPlan plan, out string? reason)
    {
        plan = null!;
        reason = null;

        if (item is null) { reason = "item nulo"; return false; }
        if (!item.Enabled) { reason = "item desabilitado"; return false; }

        string label = (item.Label ?? "").Trim();
        if (label.Length == 0) { reason = "item sem rótulo"; return false; }

        switch (item.Kind)
        {
            case CustomMenuItemKind.Launch:
                if (!TryResolveLaunch(item.Target, out string executable, out reason)) return false;
                plan = new(CustomMenuItemKind.Launch, executable, Array.Empty<byte>(), "");
                return true;

            case CustomMenuItemKind.OpenUrl:
                string url = (item.Target ?? "").Trim();
                if (url.Length > CustomMenuNormalizer.MaxTargetLength)
                {
                    reason = "URL longa demais";
                    return false;
                }
                if (!UnelevatedLauncher.IsSafeUrl(url))
                {
                    reason = "URL inválida (use http:// ou https://)";
                    return false;
                }
                plan = new(CustomMenuItemKind.OpenUrl, url, Array.Empty<byte>(), "");
                return true;

            case CustomMenuItemKind.OpenPath:
                if (!TryResolveOpenPath(item.Target, out string path, out reason)) return false;
                plan = new(CustomMenuItemKind.OpenPath, path, Array.Empty<byte>(), "");
                return true;

            case CustomMenuItemKind.Hotkey:
                if (!TryParseHotkey(item.Target, out byte[] keys, out reason)) return false;
                plan = new(CustomMenuItemKind.Hotkey, (item.Target ?? "").Trim(), keys, "");
                return true;

            case CustomMenuItemKind.Text:
                string text = item.Text ?? "";
                if (text.Length == 0) { reason = "informe o texto"; return false; }
                if (text.Length > CustomMenuNormalizer.MaxTextLength)
                {
                    reason = $"texto acima de {CustomMenuNormalizer.MaxTextLength} caracteres";
                    return false;
                }
                plan = new(CustomMenuItemKind.Text, "", Array.Empty<byte>(), text);
                return true;

            case CustomMenuItemKind.Action:
                reason = "ações nativas são executadas pelo roteador";
                return false;

            case CustomMenuItemKind.Routine:
                reason = "rotinas são executadas pelo roteador";
                return false;

            default:
                reason = "tipo de item inválido";
                return false;
        }
    }

    internal static bool TryResolveLaunch(string? target, out string executable, out string? reason)
    {
        executable = "";
        reason = null;

        string value = (target ?? "").Trim().Trim('"').Trim();
        if (value.Length == 0) { reason = "informe o executável"; return false; }

        // Nome simples: nunca olha o diretório atual; só System32/Windows/PATH do sistema.
        if (value.IndexOfAny(['\\', '/', ':']) < 0)
        {
            string name = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
            if (IsBlockedExecutable(name))
            {
                reason = $"'{Path.GetFileNameWithoutExtension(name)}' não é permitido por segurança";
                return false;
            }

            if (!TrustedPath.TryResolveSystemTool(name, out executable))
            {
                reason = $"'{name}' não foi encontrado em local protegido (System32, Windows ou PATH do sistema)";
                return false;
            }
            return true;
        }

        if (!TrustedPath.TryGetSafeFullPath(value, out string full))
        {
            reason = "caminho inválido";
            return false;
        }

        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "o executável precisa terminar em .exe";
            return false;
        }

        if (IsBlockedExecutable(full))
        {
            reason = $"'{Path.GetFileNameWithoutExtension(full)}' não é permitido por segurança";
            return false;
        }

        if (!TrustedPath.IsTrusted(full))
        {
            reason = $"'{Path.GetFileName(full)}' não está em local protegido (Program Files, System32...)";
            return false;
        }

        executable = full;
        return true;
    }

    internal static bool TryResolveOpenPath(string? target, out string path, out string? reason)
    {
        path = "";
        reason = null;

        if (!TrustedPath.TryGetSafeFullPath(target, out string full))
        {
            reason = "caminho inválido";
            return false;
        }

        if (Directory.Exists(full))
        {
            path = full;
            return true;
        }

        if (!File.Exists(full))
        {
            reason = "o caminho não existe";
            return false;
        }

        string extension = Path.GetExtension(full);
        if (BlockedPathExtensions.Contains(extension))
        {
            reason = $"abrir '{extension}' por aqui não é permitido";
            return false;
        }

        path = full;
        return true;
    }

    /// <summary>Aceita "Ctrl+Shift+P", "alt+f4", "Ctrl+PageUp"...</summary>
    internal static bool TryParseHotkey(string? combo, out byte[] keys, out string? reason)
    {
        keys = Array.Empty<byte>();
        reason = null;

        string value = (combo ?? "").Trim();
        if (value.Length == 0) { reason = Loc.Get("Hotkey.Empty"); return false; }

        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            reason = Loc.Get("Hotkey.NeedModifierAndKey");
            return false;
        }
        if (parts.Length > 4)
        {
            reason = Loc.Get("Hotkey.TooManyKeys");
            return false;
        }

        var modifiers = new List<byte>(3);
        byte? finalKey = null;

        foreach (string part in parts)
        {
            if (TryParseModifier(part, out byte modifier))
            {
                if (modifiers.Contains(modifier))
                {
                    reason = Loc.Format("Hotkey.RepeatedModifier", part);
                    return false;
                }
                modifiers.Add(modifier);
            }
            else if (finalKey is not null)
            {
                reason = Loc.Get("Hotkey.SingleFinalKey");
                return false;
            }
            else if (!TryParseKey(part, out byte key))
            {
                reason = Loc.Format("Hotkey.UnknownKey", part);
                return false;
            }
            else
            {
                finalKey = key;
            }
        }

        if (finalKey is null)
        {
            reason = Loc.Get("Hotkey.MissingFinalKey");
            return false;
        }

        // Ordem canônica: Ctrl, Alt, Shift, Win. SimulateKeys solta na ordem inversa.
        var ordered = new List<byte>(4);
        foreach (byte canonical in new byte[] { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN })
            if (modifiers.Contains(canonical)) ordered.Add(canonical);
        ordered.Add(finalKey.Value);

        keys = ordered.ToArray();
        return true;
    }

    private static bool IsBlockedExecutable(string pathOrName) =>
        BlockedExecutables.Contains(Path.GetFileNameWithoutExtension(pathOrName));

    private static bool TryParseModifier(string token, out byte vk)
    {
        vk = token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => VK_CONTROL,
            "alt" => VK_MENU,
            "shift" => VK_SHIFT,
            "win" or "windows" or "meta" => VK_LWIN,
            _ => 0
        };
        return vk != 0;
    }

    private static bool TryParseKey(string token, out byte vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            char c = token[0];
            if (c is >= 'a' and <= 'z') { vk = (byte)(0x41 + (c - 'a')); return true; }
            if (c is >= 'A' and <= 'Z') { vk = (byte)(0x41 + (c - 'A')); return true; }
            if (c is >= '0' and <= '9') { vk = (byte)(0x30 + (c - '0')); return true; }
            return false;
        }

        if (token.Length is 2 or 3 &&
            token[0] is 'f' or 'F' &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
            number is >= 1 and <= 24)
        {
            vk = (byte)(0x70 + number - 1);
            return true;
        }

        return NamedKeys.TryGetValue(token, out vk);
    }
}
