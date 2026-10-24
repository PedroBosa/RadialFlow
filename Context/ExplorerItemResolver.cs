// ============================================================================
// RadialFlow - Módulo 3 (parte 3): Do item de UI Automation ao caminho real
//
// Por que isso é necessário: o gesto engole o RMB Down, então o Explorer
// NUNCA seleciona o item sob o cursor. Não dá para confiar na seleção do
// Explorer; é preciso achar o item pelo ponto e descobrir o caminho.
//
// Estratégia (melhor esforço):
//   * O Name do item UIA é o nome exibido (pode estar SEM extensão).
//   * Explorer: a pasta atual vem de Shell.Application (LocationURL).
//   * Desktop: pasta do usuário + pasta pública da Área de Trabalho.
//   * Nome exato primeiro; depois "nome.*" (extensões ocultas).
//   * Se o item JÁ está selecionado, devolve a seleção inteira (multisseleção).
//
// Limitações conhecidas: pastas virtuais (Este Computador, Bibliotecas) não
// têm caminho de arquivo e viram fundo; seleção múltipla só no Explorer
// (no Desktop vale o item sob o cursor).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Automation;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.Context;

internal static class ExplorerItemResolver
{
    public static IReadOnlyList<string> Resolve(AutomationElement item, IntPtr rootWindow, string rootClass)
    {
        // O nome vem da UI Automation de OUTRO processo (qualquer app pode registrar uma janela de
        // classe "Progman"/"CabinetWClass"). Só aceitamos um nome simples: "..\..\Windows\x" faria o
        // RadialFlow (administrador) agir sobre um arquivo fora da pasta exibida.
        string name = item.Current.Name ?? "";
        if (!FileNaming.TryNormalizeFileName(name, out _, out _))
            return Array.Empty<string>();

        bool isSelected = item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                          && ((SelectionItemPattern)pattern).Current.IsSelected;

        var folders = new List<string>();

        if (rootClass == "CabinetWClass")
        {
            // Com abas (Win11) várias entradas compartilham o mesmo HWND:
            // testamos todas e ficamos com a que contém o item.
            foreach (var (folder, selection) in QueryExplorerWindows(rootWindow, isSelected))
            {
                if (isSelected && selection.Count > 0 && selection.Exists(p => NameMatches(p, name)))
                    return SafePaths(selection);            // multisseleção inteira

                if (folder is not null) folders.Add(folder);
            }
        }
        else // Progman / WorkerW = Área de Trabalho
        {
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        }

        foreach (var folder in folders)
            if (FindInFolder(folder, name) is { } path)
                return new[] { path };

        return Array.Empty<string>();
    }

    // ------------------------------------------------------------------

    /// <summary>Só caminhos absolutos e canônicos; qualquer item suspeito descarta a seleção inteira.</summary>
    private static IReadOnlyList<string> SafePaths(List<string> selection)
    {
        var result = new List<string>(selection.Count);
        foreach (string p in selection)
        {
            if (!TrustedPath.TryGetSafeFullPath(p, out string full)) return Array.Empty<string>();
            result.Add(full);
        }
        return result;
    }

    private static string? FindInFolder(string folder, string name)
    {
        try
        {
            if (!TrustedPath.TryGetSafeFullPath(folder, out string safeFolder)) return null;

            string exact = Path.Combine(safeFolder, name);
            if (File.Exists(exact) || Directory.Exists(exact)) return exact;

            // Extensões ocultas: "Relatório" -> "Relatório.docx", "App" -> "App.lnk"
            foreach (var entry in Directory.EnumerateFileSystemEntries(safeFolder, name + ".*"))
                return TrustedPath.TryGetSafeFullPath(entry, out string full) ? full : null;
        }
        catch { /* pasta inacessível: tenta a próxima */ }

        return null;
    }

    private static bool NameMatches(string path, string displayName)
    {
        string file = Path.GetFileName(path);
        return file.Equals(displayName, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(file).Equals(displayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumera as "janelas" do Shell (uma por aba) cujo HWND é o da janela raiz.
    /// Late binding (dynamic) evita referência de interop. Loops por índice,
    /// porque foreach em COM dinâmico é frágil.
    /// </summary>
    private static List<(string? Folder, List<string> Selection)> QueryExplorerWindows(IntPtr rootWindow, bool wantSelection)
    {
        var result = new List<(string?, List<string>)>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return result;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    if (w is null || Convert.ToInt64(w.HWND) != rootWindow.ToInt64()) continue;

                    string? folder = null;
#pragma warning disable CS8602 // w é dynamic e foi null-checked acima (w is null → continue)
                    string? url = (string?)w.LocationURL;
#pragma warning restore CS8602
                    if (!string.IsNullOrEmpty(url) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                        folder = uri.LocalPath;

                    var selection = new List<string>();
                    if (wantSelection)
                    {
                        dynamic items = w.Document.SelectedItems();
                        int n = items.Count;
                        for (int k = 0; k < n; k++)
                            selection.Add((string)items.Item(k).Path);
                    }

                    result.Add((folder, selection));
                }
                catch { /* aba/janela inválida: ignora */ }
            }
        }
        catch { /* Shell indisponível */ }

        return result;
    }
}
