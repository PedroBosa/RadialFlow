// ============================================================================
// RadialFlow - Roteador de ações
// ----------------------------------------------------------------------------
// Cada RadialAction é despachada para o handler correto, que usa o
// ContextInfo (RootWindow, Paths, ProcessName).
//
// Regras de threading:
//   * Run() é chamado na thread de UI (por DragCompleted no RadialController).
//   * Ações rápidas (PostMessage, SetWindowPos, keybd_event) rodam inline em
//     Task.Run — não bloqueiam a UI.
//   * Ações que tocam o clipboard usam Dispatcher.Invoke (exige STA).
//   * Ações com diálogo (Rename, MoveTo) abrem a janela via Dispatcher.
//   * Nenhuma ação bloqueia a roda: ela já foi descartada antes de Run().
//
// Segurança: Run() valida o contexto (ActionRouter.Logic.TryValidate) ANTES
// de qualquer efeito: caminhos canônicos via TrustedPath, janela presente.
// Estado por janela (snap/transparência) é tocado por várias threads do pool:
// protegido por _windowStateGate e podado quando a janela deixa de existir.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RadialFlow.Infrastructure;
using RadialFlow.Config;
using RadialFlow.Security;
using RadialFlow.Services;
using RadialFlow.UI;

namespace RadialFlow.Context;

public static partial class ActionRouter
{
    // Dispatcher da thread de UI (operações STA: clipboard, diálogos).
    private static readonly Dispatcher _ui = Application.Current.Dispatcher;

    // Estado por janela, acessado de threads do pool: sempre sob _windowStateGate.
    private static readonly object _windowStateGate = new();

    // Transparência por janela: índice em Logic.OpacityLevels (0 = opaco, 1..3 = translúcido).
    private static readonly Dictionary<IntPtr, int> _transparencyLevels = new();

    // Posição original antes do snap (para restaurar ao clicar de novo).
    private static readonly Dictionary<IntPtr, RECT> _preSnapBounds = new();

    // ------------------------------------------------------------------
    // Entrada principal
    // ------------------------------------------------------------------

    /// <summary>
    /// Ponto de entrada único. Valida o contexto (caminhos via TrustedPath) e dispara a ação em
    /// background (Task.Run) para não bloquear a thread de UI. Ações que precisam da UI fazem
    /// marshal via Dispatcher internamente. Nunca lança exceção.
    /// </summary>
    public static void Run(RadialAction action, ContextInfo ctx)
    {
        if (!Logic.TryValidate(action, ctx, out ContextInfo safeCtx, out string? reason))
        {
            AppLog.Warning("Action", $"{action} recusada: {reason}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchAsync(action, safeCtx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Action", $"Erro em {action}", ex);
            }
        });
    }

    private static Task DispatchAsync(RadialAction action, ContextInfo ctx)
    {
        switch (action)
        {
            // ── Barra de título ──────────────────────────────────────
            case RadialAction.SnapLeft:      SnapWindow(ctx.RootWindow, left: true); break;
            case RadialAction.SnapRight:     SnapWindow(ctx.RootWindow, left: false); break;
            case RadialAction.Maximize:      ToggleMaximize(ctx.RootWindow); break;
            case RadialAction.Minimize:      MinimizeWindow(ctx.RootWindow); break;
            case RadialAction.Close:         CloseWindow(ctx.RootWindow); break;
            case RadialAction.AlwaysOnTop:   ToggleAlwaysOnTop(ctx.RootWindow); break;
            case RadialAction.Transparency:  CycleTransparency(ctx.RootWindow); break;

            // ── Arquivo / pasta ──────────────────────────────────────
            case RadialAction.CopyPath:      CopyPathToClipboard(ctx); break;
            case RadialAction.Rename:        return RenameAsync(ctx);
            case RadialAction.MoveTo:        return MoveToAsync(ctx);
            case RadialAction.SendTo:        SendToFiles(ctx); break;
            case RadialAction.Compress:      return CompressAsync(ctx);
            case RadialAction.Convert:       return ConvertAsync(ctx);

            // ── Campo de texto ───────────────────────────────────────
            case RadialAction.Paste:         PasteText(ctx); break;
            case RadialAction.Clear:         ClearText(ctx); break;
            case RadialAction.Format:        FormatBold(ctx); break;
            case RadialAction.Translate:     return TranslateAsync(ctx);
            case RadialAction.SearchWeb:     return SearchWebAsync(ctx);

            // ── Sistema / Desktop ────────────────────────────────────
            case RadialAction.LockScreen:       LockWorkStation(); break;
            case RadialAction.ClipboardHistory: SimulateKeys(VK_LWIN, VK_V); break;
            case RadialAction.FavoriteApps:     return LaunchFavoriteAppsAsync(ctx);
            case RadialAction.SystemControls:   OpenQuickSettings(); break;

            default:
                AppLog.Warning("Action", $"Ação desconhecida: {action}");
                break;
        }
        return Task.CompletedTask;
    }

    // ==================================================================
    // BARRA DE TÍTULO
    // ==================================================================

    /// <summary>
    /// Encaixa a janela na metade esquerda ou direita da área de trabalho
    /// do monitor atual. Se já estiver encaixada no mesmo lado, restaura.
    /// </summary>
    private static void SnapWindow(IntPtr hwnd, bool left)
    {
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return;

        ref RECT wa = ref mi.rcWork; // área de trabalho (exclui Taskbar)
        int halfW = (wa.Right - wa.Left) / 2;
        int h = wa.Bottom - wa.Top;

        // Verifica se já está encaixado no mesmo lado → restaura.
        if (GetWindowRect(hwnd, out RECT cur))
        {
            bool snappedLeft  = cur.Left == wa.Left && cur.Right == wa.Left + halfW;
            bool snappedRight = cur.Left == wa.Left + halfW && cur.Right == wa.Right;

            if ((left && snappedLeft) || (!left && snappedRight))
            {
                bool hadPrevious;
                RECT prev;
                lock (_windowStateGate) hadPrevious = _preSnapBounds.Remove(hwnd, out prev);

                if (hadPrevious)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetWindowPos(hwnd, IntPtr.Zero, prev.Left, prev.Top,
                        prev.Right - prev.Left, prev.Bottom - prev.Top,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return;
            }
        }

        // Salva posição original (primeira vez).
        if (GetWindowRect(hwnd, out RECT original))
        {
            lock (_windowStateGate)
            {
                PruneDeadWindows();
                _preSnapBounds.TryAdd(hwnd, original);
            }
        }

        // Se maximizada, restaura antes de mover.
        if ((GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) != 0)
            ShowWindow(hwnd, SW_RESTORE);

        int x = left ? wa.Left : wa.Left + halfW;
        SetWindowPos(hwnd, IntPtr.Zero, x, wa.Top, halfW, h, SWP_NOZORDER | SWP_NOACTIVATE);

        AppLog.Debug("Action", $"Snap {(left ? "esquerda" : "direita")}");
    }

    /// <summary>Alterna entre maximizar e restaurar.</summary>
    private static void ToggleMaximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        bool maximized = (GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) != 0;
        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)(maximized ? SC_RESTORE : SC_MAXIMIZE), IntPtr.Zero);
        if (maximized)
        {
            lock (_windowStateGate) _preSnapBounds.Remove(hwnd);
        }
        AppLog.Debug("Action", maximized ? "Restaurar" : "Maximizar");
    }

    private static void MinimizeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
    }

    /// <summary>Fecha via SC_CLOSE (assíncrono: não bloqueia se o app pedir confirmação).</summary>
    private static void CloseWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
    }

    /// <summary>Alterna WS_EX_TOPMOST (Sempre no topo).</summary>
    private static void ToggleAlwaysOnTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        bool isTopmost = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        IntPtr insertAfter = isTopmost ? HWND_NOTOPMOST : HWND_TOPMOST;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        AppLog.Debug("Action", $"AlwaysOnTop: {(isTopmost ? "OFF" : "ON")}");
    }

    /// <summary>
    /// Cicla a opacidade: 100% → 85% → 70% → 55% → 100%.
    /// Aplica WS_EX_LAYERED + SetLayeredWindowAttributes.
    /// Ao voltar a 100%, remove WS_EX_LAYERED para restaurar composição.
    /// </summary>
    private static void CycleTransparency(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        int idx;
        lock (_windowStateGate)
        {
            PruneDeadWindows();
            idx = Logic.NextOpacityIndex(_transparencyLevels.GetValueOrDefault(hwnd));   // ausente = opaco
            if (idx == 0) _transparencyLevels.Remove(hwnd);
            else _transparencyLevels[hwnd] = idx;
        }

        byte alpha = Logic.OpacityLevels[idx];
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (idx == 0)
        {
            // Voltou a 100%: remove WS_EX_LAYERED.
            SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            AppLog.Debug("Action", "Transparência: OFF (100%)");
        }
        else
        {
            if ((ex & WS_EX_LAYERED) == 0)
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);

            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            AppLog.Debug("Action", $"Transparência: {alpha * 100 / 255}%");
        }
    }

    /// <summary>
    /// Remove estado de janelas que já não existem (o HWND pode ser reutilizado por outra janela,
    /// e os dicionários cresceriam para sempre). Chamar com _windowStateGate adquirido.
    /// </summary>
    private static void PruneDeadWindows()
    {
        foreach (IntPtr dead in _preSnapBounds.Keys.Where(h => !IsWindow(h)).ToList())
            _preSnapBounds.Remove(dead);
        foreach (IntPtr dead in _transparencyLevels.Keys.Where(h => !IsWindow(h)).ToList())
            _transparencyLevels.Remove(dead);
    }

    // ==================================================================
    // ARQUIVO / PASTA
    // ==================================================================

    /// <summary>
    /// Copia todos os caminhos (entre aspas) para o clipboard.
    /// Múltiplos caminhos são separados por quebra de linha.
    /// </summary>
    private static void CopyPathToClipboard(ContextInfo ctx)
    {
        if (ctx.Paths.Count == 0) return;

        string text = Logic.FormatPathsForClipboard(ctx.Paths);

        _ui.Invoke(() =>
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { AppLog.Warning("Action", $"CopyPath clipboard: {ex.Message}"); }
        });

        AppLog.Debug("Action", $"CopyPath: {ctx.Paths.Count} caminho(s).");
    }

    /// <summary>
    /// Renomeia o item selecionado. Com um único item, o fallback é o diálogo WPF; com vários,
    /// usa o F2 do Explorer (renomeação nativa em lote) e NUNCA renomeia só o primeiro quando
    /// a seleção não pode ser preparada: avisa e não toca em nada.
    /// </summary>
    private static async Task RenameAsync(ContextInfo ctx)
    {
        if (ctx.Paths.Count == 0) return;

        if (ctx.Paths.Count == 1)
        {
            string path = ctx.Paths[0];
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                AppLog.Warning("Action", $"Rename: não encontrado: {path}");
                return;
            }

            // Tenta via Shell: seleciona o item e injeta F2.
            if (await TryRenameViaShellAsync(ctx.RootWindow, ctx.Paths)) return;

            // Fallback: diálogo WPF simples.
            _ui.Invoke(() => RenameViaDialog(path));
            return;
        }

        if (!Logic.CanRenameTogether(ctx.Paths, out string? reason))
        {
            AppLog.Debug("Action", $"Rename em lote recusado: {reason}");
            _ui.Invoke(() => Dialogs.Message(null, "Renomear",
                reason ?? "Não é possível renomear esta seleção de uma vez.", DialogIcon.Warning));
            return;
        }

        if (await TryRenameViaShellAsync(ctx.RootWindow, ctx.Paths)) return;

        AppLog.Warning("Action", "Rename em lote: o Explorer não aceitou a seleção.");
        _ui.Invoke(() => Dialogs.Message(null, "Renomear",
            "Não foi possível preparar a renomeação na janela do Explorer.\n" +
            "Selecione os arquivos no Explorer e pressione F2. Nada foi renomeado.",
            DialogIcon.Warning));
    }

    /// <summary>
    /// Seleciona os itens no Explorer via Shell.Application e injeta F2. Se algum item não for
    /// encontrado, não seleciona nada (uma seleção incompleta renomearia só parte do lote).
    /// Retorna true se conseguiu; false se falhou.
    /// </summary>
    private static async Task<bool> TryRenameViaShellAsync(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        try
        {
            bool selected = await Task.Run(() =>
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic windows = shell.Windows();
                int count = windows.Count;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic w = windows.Item(i);
                        if (w is null) continue;
                        if (Convert.ToInt64(w.HWND) != hwnd.ToInt64()) continue;

                        dynamic folder = w.Document.Folder;
                        var items = new List<object?>(paths.Count);
                        foreach (string path in paths)
                        {
                            object? item = FindShellItem(folder, Path.GetFileName(path));
                            if (item is null) return false;
                            items.Add(item);
                        }

                        for (int k = 0; k < items.Count; k++)
                        {
                            // O primeiro limpa a seleção anterior; os demais SOMAM (sem SVSI_DESELECTOTHERS).
                            // SVSI_SELECT | SVSI_ENSUREVISIBLE (+ SVSI_DESELECTOTHERS | SVSI_FOCUSED no primeiro).
                            w.Document.SelectItem(items[k], k == 0 ? 0x1D : 0x09);
                        }

                        return true;
                    }
                    catch { /* aba/janela inválida */ }
                }
                return false;
            });

            if (!selected) return false;

            // Tempo para o Explorer processar a seleção.
            await Task.Delay(200);
            WindowFocus.ForceForeground(hwnd);
            await Task.Delay(80);

            // Nunca manda F2 para outro app: se a janela do Explorer não ficou em foco, cai no diálogo.
            if (!WindowFocus.IsForegroundRoot(hwnd))
            {
                AppLog.Debug("Action", "Rename: a janela de origem não ficou em primeiro plano.");
                return false;
            }
            SimulateKeys(VK_F2);

            AppLog.Debug("Action", $"Rename via Shell+F2 ({paths.Count} item(ns)).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Action", $"Rename Shell falhou: {ex.Message}");
            return false;
        }
    }

    private static object? FindShellItem(dynamic folder, string fileName)
    {
        dynamic? item = folder.ParseName(fileName);
        if (item is not null) return item;

        // Extensão oculta: ParseName pode falhar com o nome exibido.
        string bare = Path.GetFileNameWithoutExtension(fileName);
        dynamic items = folder.Items();
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic fi = items.Item(i);
            if (fi is not null && string.Equals((string)fi.Name, bare, StringComparison.OrdinalIgnoreCase))
                return fi;
        }

        return null;
    }

    /// <summary>
    /// Diálogo de renomeação (fallback do F2 do Explorer). Roda na thread de UI.
    /// O nome digitado é validado (<see cref="Logic.TryBuildRenameTarget"/>): sem separadores, "..",
    /// curingas ou nomes reservados, e o resultado fica na MESMA pasta (o app é administrador).
    /// Nome inválido mantém o diálogo aberto com o motivo escrito nele.
    /// </summary>
    private static void RenameViaDialog(string path)
    {
        string oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (oldName.Length == 0) return;

        string? newPath = null;
        int dot = oldName.LastIndexOf('.');

        string? typed = Dialogs.PromptText(null, "Renomear", "Novo nome", oldName,
            validate: text =>
            {
                if (!Logic.TryBuildRenameTarget(path, text, out string target, out string? error)) return error;
                newPath = target;
                return null;
            },
            // Seleciona o nome sem a extensão (como o Explorer faz no F2).
            selectLength: dot > 0 ? dot : -1,
            okText: "Renomear");

        if (typed is null || newPath is null) return;

        // Mesmo nome (comparação exata: só mudar maiúsculas/minúsculas ainda é renomear).
        if (string.Equals(Path.GetFileName(newPath), oldName, StringComparison.Ordinal)) return;

        try
        {
            if (Directory.Exists(path))
                Directory.Move(path, newPath);
            else
                File.Move(path, newPath);
            AppLog.Info("Action", $"Renomeado: {oldName} → {Path.GetFileName(newPath)}");
        }
        catch (Exception ex)
        {
            Dialogs.Message(null, "Não foi possível renomear", ex.Message, DialogIcon.Error);
        }
    }
    /// <summary>
    /// Abre seletor de pasta e move todos os Paths para o destino.
    /// Conflitos geram pergunta ao usuário.
    /// </summary>
    private static Task MoveToAsync(ContextInfo ctx)
    {
        if (ctx.Paths.Count == 0) return Task.CompletedTask;

        // Abre seletor de pasta na thread de UI.
        string? destFolder = _ui.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Mover para..." };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });

        if (string.IsNullOrEmpty(destFolder)) return Task.CompletedTask;

        if (!TrustedPath.TryGetSafeFullPath(destFolder, out string safeDestFolder))
        {
            AppLog.Debug("Action", $"MoveTo: destino inválido '{destFolder}'.");
            return Task.CompletedTask;
        }

        foreach (var src in ctx.Paths)
        {
            try
            {
                var plan = Logic.PlanMove(src, safeDestFolder);
                string name = plan.Name;
                string dest = plan.Destination;

                switch (plan.Decision)
                {
                    case MoveDecision.Invalid:
                    case MoveDecision.AlreadyThere:   // já está lá: nada a fazer
                        continue;

                    case MoveDecision.IntoItself:     // pasta para dentro de si mesma é impossível
                        _ui.Invoke(() => Dialogs.Message(null, "Mover", $"Não é possível mover '{name}' para dentro dele mesmo.", DialogIcon.Warning));
                        continue;
                }

                bool replace = false;
                if (plan.Decision == MoveDecision.Conflict)
                {
                    // 0 = Parar, 1 = Manter os dois, 2 = Substituir (o botão principal é o MENOS destrutivo).
                    int answer = _ui.Invoke(() => Dialogs.Choose(null, "Mover",
                        $"'{name}' já existe na pasta de destino. O que fazer?", DialogIcon.Warning,
                        ["Parar", "Manter os dois", "Substituir"], defaultIndex: 1, cancelIndex: 0));

                    if (answer == 0) break;
                    if (answer == 1) dest = FileNaming.GetUniquePath(dest);
                    else replace = true;
                }

                MoveItemSafely(src, dest, replace);
                AppLog.Info("Action", $"Movido: {src} → {dest}");
            }
            catch (Exception ex)
            {
                AppLog.Warning("Action", $"Falha ao mover '{src}': {ex.Message}");
                _ui.Invoke(() => Dialogs.Message(null, "Não foi possível mover",
                    $"'{Path.GetFileName(src.TrimEnd('\\', '/'))}': {ex.Message}", DialogIcon.Error));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Move sem nunca perder os dois lados: o destino existente só é descartado
    /// DEPOIS de o item novo estar no lugar (e é restaurado se algo falhar).
    /// </summary>
    private static void MoveItemSafely(string src, string dest, bool replace)
    {
        if (File.Exists(src))
        {
            if (Directory.Exists(dest))
                throw new IOException("O destino é uma pasta com o mesmo nome de um arquivo.");

            // File.Move trata outro volume (copia e apaga) e substitui de forma atômica quando possível.
            File.Move(src, dest, overwrite: replace);
            return;
        }

        if (File.Exists(dest))
            throw new IOException("O destino é um arquivo com o mesmo nome de uma pasta.");

        string? backup = null;
        try
        {
            if (replace && Directory.Exists(dest))
            {
                backup = dest + ".radialflow-old-" + Guid.NewGuid().ToString("N");
                Directory.Move(dest, backup);   // mesmo volume: instantâneo
            }

            MoveDirectory(src, dest);

            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception ex) { AppLog.Warning("Action", $"Backup temporário não removido: {ex.Message}"); }
            }
        }
        catch
        {
            // Restaura o destino original se o novo não chegou inteiro.
            if (backup is not null && Directory.Exists(backup))
            {
                try
                {
                    if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                    Directory.Move(backup, dest);
                }
                catch (Exception ex) { AppLog.Warning("Action", $"Falha ao restaurar o destino: {ex.Message}"); }
            }
            throw;
        }
    }

    private static void MoveDirectory(string src, string dest)
    {
        // Directory.Move só funciona dentro do mesmo volume.
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)),
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, dest);
            return;
        }

        try
        {
            CopyDirectory(src, dest);
        }
        catch
        {
            // Cópia incompleta: remove o pedaço (o destino não existia antes deste passo).
            try { Directory.Delete(dest, recursive: true); } catch { }
            throw;
        }

        Directory.Delete(src, recursive: true);   // só apaga a origem depois da cópia completa
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));

        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), overwrite: false);
    }

    /// <summary>
    /// Compacta arquivos/pastas em um .zip ou descompacta caso o item selecionado já seja .zip.
    /// Exibe uma janela de progresso assíncrona sem travar a interface nem a roda.
    /// Destino sempre livre ("nome (1).zip", "pasta (1)"), calculado por <see cref="Logic.TryPlanArchive"/>.
    /// </summary>
    private static Task CompressAsync(ContextInfo ctx)
    {
        if (!Logic.TryPlanArchive(ctx.Paths, out ArchivePlan? plan, out string? error) || plan is null)
        {
            AppLog.Warning("Action", $"Compress recusado: {error}");
            return Task.CompletedTask;
        }

        if (plan.Mode == ArchiveMode.Extract)
        {
            string zipFile = plan.Sources[0];
            RunWithProgress($"Extraindo {Path.GetFileName(zipFile)}...", plan.Output,
                (progress, ct) => CompressionService.DecompressAsync(zipFile, plan.Output, progress, ct),
                "Extração cancelada pelo usuário.", "Falha ao extrair");
        }
        else
        {
            RunWithProgress($"Compactando {plan.Sources.Count} item(ns)...", plan.Output,
                (progress, ct) => CompressionService.CompressAsync(plan.Sources, plan.Output, progress, ct),
                "Compactação cancelada pelo usuário.", "Falha ao compactar");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Abre a janela de seleção de formatos para a seleção inteira e inicia a conversão com progresso.
    /// </summary>
    private static Task ConvertAsync(ContextInfo ctx)
    {
        if (ctx.Paths.Count == 0) return Task.CompletedTask;

        ConversionBatchPlan? plan = _ui.Invoke(() =>
        {
            var convDialog = new ConversionWindow(ctx.Paths);
            return convDialog.ShowDialog() == true ? convDialog.Plan : null;
        });

        if (plan is null) return Task.CompletedTask;

        RunConversionPlan(plan);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Converte diretamente a seleção para o formato solicitado (acionado pela sub-fatia radial).
    /// Não abre diálogos modais: executa imediatamente e mostra a barra de progresso.
    /// Chamado na thread de UI (DragCompleted): todo o trabalho (disco incluso) vai para o pool.
    /// </summary>
    public static void RunDirectConversion(IReadOnlyList<string> sourceFiles, string targetFormat)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!ConversionBatch.TryPlan(sourceFiles, targetFormat, destinationFolder: null,
                        out ConversionBatchPlan? plan, out string? error) || plan is null)
                {
                    AppLog.Warning("Action", $"Conversão recusada: {error}");
                    return;
                }

                RunConversionPlan(plan);
            }
            catch (Exception ex)
            {
                AppLog.Error("Action", "Falha ao iniciar a conversão", ex);
            }
        });
    }

    /// <summary>Executa um plano já validado numa única janela de progresso, com o resumo no final.</summary>
    private static void RunConversionPlan(ConversionBatchPlan plan)
    {
        if (plan.Jobs.Count == 0) return;

        string title = plan.Jobs.Count == 1
            ? $"Convertendo {Path.GetFileName(plan.Jobs[0].Source)}..."
            : $"Convertendo {plan.Jobs.Count} arquivos...";

        ConversionBatchResult? result = null;
        RunWithProgress(title, plan.Jobs[0].Destination,
            async (progress, ct) =>
            {
                result = await ConversionService.ConvertBatchAsync(plan.Jobs, plan.TargetFormat, progress, ct)
                    .ConfigureAwait(false);
            },
            "Conversão cancelada pelo usuário.", "Falha na conversão",
            completion: () => DescribeBatchResult(result, plan));
    }

    private static CompletionMessage DescribeBatchResult(ConversionBatchResult? result, ConversionBatchPlan plan)
    {
        string target = plan.TargetFormat.ToUpperInvariant();
        string skipped = plan.Skipped.Count switch
        {
            0 => "",
            1 => " 1 arquivo já estava no formato.",
            var count => $" {count} arquivos já estavam no formato.",
        };

        if (result is null)
            return new CompletionMessage($"Conversão concluída para .{target}.", false);

        foreach (var item in result.Items)
        {
            if (!item.Success)
                AppLog.Warning("Action", $"Conversão falhou ({Path.GetFileName(item.Source)}): {item.Error}");
        }

        if (result.Failed == 0)
        {
            string text = result.Succeeded == 1
                ? $"Arquivo convertido para .{target}."
                : $"{result.Succeeded} arquivos convertidos para .{target}.";
            return new CompletionMessage(text + skipped, false);
        }

        string summary = result.Succeeded == 0
            ? $"Nenhum arquivo foi convertido para .{target} ({result.Failed} com erro)."
            : $"{result.Succeeded} de {plan.Jobs.Count} arquivos convertidos para .{target}; {result.Failed} com erro.";
        return new CompletionMessage(summary + skipped, true);
    }

    /// <summary>Mensagem final exibida pela janela de progresso; <c>Warning</c> usa as cores de atenção.</summary>
    private readonly record struct CompletionMessage(string Text, bool Warning);

    /// <summary>
    /// Abre a <see cref="ProgressWindow"/> na thread de UI e executa <paramref name="work"/> no pool.
    /// Cancelamento e falhas viram mensagem na janela; nenhuma exceção escapa da tarefa.
    /// <paramref name="completion"/> roda depois do trabalho bem-sucedido (ex.: resumo de um lote).
    /// Chamar fora da thread de UI.
    /// </summary>
    private static void RunWithProgress(string title, string output,
        Func<IProgress<ProgressReport>, System.Threading.CancellationToken, Task> work,
        string cancelledText, string failurePrefix,
        Func<CompletionMessage>? completion = null)
    {
        ProgressWindow progressWin = _ui.Invoke(() =>
        {
            var window = new ProgressWindow(title, output);
            window.Show();
            return window;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await work(progressWin, progressWin.CancellationToken).ConfigureAwait(false);
                if (completion is not null)
                {
                    CompletionMessage message = completion();
                    progressWin.MarkCompleted(message.Text, message.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                progressWin.MarkFailed(cancelledText);
            }
            catch (Exception ex)
            {
                AppLog.Error("Action", $"{failurePrefix}", ex);
                progressWin.MarkFailed($"{failurePrefix}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Inicia um aplicativo favorito diretamente (sub-fatia). Chamado na thread de UI: a resolução
    /// e validação do caminho (disco, ACL) roda no pool. <see cref="LaunchApp"/> não lança exceção.
    /// </summary>
    public static void LaunchAppDirect(FavoriteAppEntry app) => _ = Task.Run(() => LaunchApp(app));

    /// <summary>
    /// Copia os arquivos selecionados para a Área de Transferência como FileDropList,
    /// permitindo colagem direta (Ctrl+V) de anexos no Teams, Slack, WhatsApp, e-mail ou Explorer.
    /// </summary>
    private static void SendToFiles(ContextInfo ctx)
    {
        if (ctx.Paths.Count == 0) return;

        _ui.Invoke(() =>
        {
            try
            {
                var fileList = new System.Collections.Specialized.StringCollection();
                foreach (var path in ctx.Paths)
                    fileList.Add(path);

                Clipboard.SetFileDropList(fileList);
                AppLog.Debug("Action", $"SendTo: {ctx.Paths.Count} arquivo(s) prontos para colar (Ctrl+V).");
            }
            catch (Exception ex)
            {
                AppLog.Warning("Action", $"SendTo falhou: {ex.Message}");
            }
        });
    }

    // ==================================================================
    // CAMPO DE TEXTO
    // ==================================================================

    /// <summary>Simula Ctrl+V na janela em foco.</summary>
    private static void PasteText(ContextInfo ctx)
    {
        // Foca o campo sob a ORIGEM do gesto (o clique foi engolido pelo hook).
        var target = TextTarget.Prepare(ctx, strict: false);
        if (!target.Ready)
        {
            AppLog.Debug("Action", "Paste cancelado: a janela de origem não está em foco.");
            return;
        }

        SimulateKeys(VK_CONTROL, VK_V);
        AppLog.Debug("Action", "Paste");
    }

    /// <summary>
    /// Ctrl+A → Delete (limpa o campo). Ação destrutiva: exige foco CONFIRMADO no
    /// elemento certo e pede confirmação quando o alvo é um documento inteiro.
    /// </summary>
    private static void ClearText(ContextInfo ctx)
    {
        var target = TextTarget.Prepare(ctx, strict: true);
        if (!target.Ready)
        {
            AppLog.Warning("Action", "Clear cancelado: não foi possível focar o campo com segurança.");
            return;
        }

        if (target.IsDocument)
        {
            // Ação destrutiva: o botão padrão (Enter) é CANCELAR.
            bool confirmed = _ui.Invoke(() => Dialogs.Confirm(null, "Limpar documento",
                "Apagar TODO o conteúdo deste documento?\n(Ctrl+Z desfaz na maioria dos aplicativos.)",
                confirmText: "Apagar tudo", cancelText: "Cancelar", icon: DialogIcon.Warning, defaultIsConfirm: false));

            if (!confirmed) return;

            // A caixa de diálogo tirou o foco: restaura antes de enviar as teclas.
            target = TextTarget.Prepare(ctx, strict: true);
            if (!target.Ready) return;
        }

        SimulateKeys(VK_CONTROL, VK_A);
        System.Threading.Thread.Sleep(30);
        SimulateKeys(VK_DELETE);
        AppLog.Debug("Action", "Clear");
    }

    /// <summary>Ctrl+B (negrito). Funciona em Word, Google Docs, etc. Não clica: preserva a seleção.</summary>
    private static void FormatBold(ContextInfo ctx)
    {
        var target = TextTarget.Prepare(ctx, strict: false);
        if (!target.Ready)
        {
            AppLog.Warning("Action", "Format cancelado: a janela de origem não está em foco.");
            return;
        }

        SimulateKeys(VK_CONTROL, VK_B);
        AppLog.Debug("Action", "Format (Bold)");
    }

    /// <summary>
    /// Captura o texto selecionado e abre o Google Tradutor no navegador.
    /// O clipboard original (texto) é preservado.
    /// </summary>
    private static async Task TranslateAsync(ContextInfo ctx)
    {
        string? text = await CaptureSelectedTextAsync(ctx);
        if (string.IsNullOrWhiteSpace(text)) return;

        OpenUrl(Logic.BuildTranslateUrl(text));
        AppLog.Debug("Action", $"Translate: {text.Length} caracteres.");
    }

    /// <summary>
    /// Captura o texto selecionado e abre pesquisa Google no navegador.
    /// O clipboard original (texto) é preservado.
    /// </summary>
    private static async Task SearchWebAsync(ContextInfo ctx)
    {
        string? text = await CaptureSelectedTextAsync(ctx);
        if (string.IsNullOrWhiteSpace(text)) return;

        OpenUrl(Logic.BuildSearchUrl(text));
        AppLog.Debug("Action", $"SearchWeb: {text.Trim().Length} caracteres.");
    }

    // ==================================================================
    // SISTEMA / APPS FAVORITOS
    // ==================================================================

    /// <summary>
    /// Executa o aplicativo favorito configurado. Se houver mais de um,
    /// abre um mini-launcher na tela para o usuário escolher.
    /// </summary>
    private static Task LaunchFavoriteAppsAsync(ContextInfo ctx)
    {
        var apps = SettingsService.Current.FavoriteApps;
        if (apps == null || apps.Count == 0)
        {
            SimulateKeys(VK_LWIN);
            return Task.CompletedTask;
        }

        if (apps.Count == 1)
        {
            LaunchApp(apps[0]);
            return Task.CompletedTask;
        }

        _ui.Invoke(() => ShowFavoriteAppsPicker(ctx, apps));
        return Task.CompletedTask;
    }

    private static void LaunchApp(FavoriteAppEntry app)
    {
        try
        {
            // O RadialFlow é administrador; o favorito NÃO deve herdar isso.
            bool started = UnelevatedLauncher.Start(app.ExecutablePath, app.Arguments);
            if (started) AppLog.Info("Action", $"App favorito iniciado: {app.Name}");
            else AppLog.Warning("Action", $"Não foi possível iniciar '{app.Name}' sem elevação.");
        }
        catch (Exception ex)
        {
            AppLog.Warning("Action", $"Falha ao iniciar '{app.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Mini-launcher de apps favoritos (popup transitório): mesmo tema da roda e das janelas, abre ao lado do
    /// ponto do gesto, sempre dentro da tela, e fecha ao perder o foco ou com Esc.
    /// </summary>
    private static void ShowFavoriteAppsPicker(ContextInfo ctx, IReadOnlyList<FavoriteAppEntry> apps)
    {
        var popup = new Window
        {
            Title = "RadialFlow — Apps favoritos",
            Width = 260,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            UseLayoutRounding = true
        };
        UiTheme.Install(popup);

        var list = new StackPanel();
        var title = new TextBlock
        {
            Text = "APPS FAVORITOS", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 2, 10, 8)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        list.Children.Add(title);

        foreach (var app in apps)
        {
            var targetApp = app;
            var name = new TextBlock { Text = app.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var button = new Button
            {
                Content = name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };
            button.Click += (_, _) =>
            {
                popup.Close();
                LaunchApp(targetApp);
            };
            list.Children.Add(button);
        }

        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(10, 12, 10, 8), Child = list, Margin = new Thickness(14) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.45, Color = System.Windows.Media.Colors.Black };
        popup.Content = card;

        // Posição: perto do gesto, mas sempre inteiro dentro da área útil do monitor DO GESTO (pixels físicos).
        popup.Loaded += (_, _) =>
        {
            double scale = RadialMenuWindow.GetMonitorScale(ctx.OriginX, ctx.OriginY);
            int width = (int)Math.Round(popup.ActualWidth * scale);
            int height = (int)Math.Round(popup.ActualHeight * scale);
            var area = RadialMenuWindow.GetWorkArea(ctx.OriginX, ctx.OriginY);

            var (x, y) = RadialPlacement.ClampRect(ctx.OriginX - width / 2, ctx.OriginY - (int)(40 * scale),
                width, height, area.Left, area.Top, area.Right, area.Bottom);
            RadialMenuWindow.MoveWindowPx(popup, x, y);
        };

        popup.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) popup.Close(); };
        popup.Deactivated += (_, _) => popup.Close();

        popup.Show();
        popup.Activate();
    }
    // ==================================================================
    // HELPERS
    // ==================================================================

    /// <summary>
    /// Captura o texto selecionado na janela alvo via Ctrl+C.
    /// Preserva (e restaura) o conteúdo textual anterior do clipboard.
    /// Conteúdo não-textual (imagens, arquivos) é perdido — tradeoff aceito.
    /// </summary>
    private static async Task<string?> CaptureSelectedTextAsync(ContextInfo ctx)
    {
        // Em terminais Ctrl+C é "interromper processo": nunca enviar.
        if (Logic.IsConsoleHost(ctx.ProcessName))
        {
            AppLog.Warning("Action", "Captura de texto ignorada em terminal.");
            return null;
        }

        // Foca o campo da origem (sem clique: preserva a seleção). Aborta se a janela errada estiver em foco.
        var target = TextTarget.Prepare(ctx, strict: false);
        if (!target.Ready) return null;

        // 1. Salva o texto atual e limpa o clipboard.
        string? previousText = _ui.Invoke(() =>
        {
            try
            {
                string? t = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                Clipboard.Clear();
                return t;
            }
            catch { return null; }
        });

        // 2. Simula Ctrl+C (o foco já foi garantido acima).
        SimulateKeys(VK_CONTROL, VK_C);
        await Task.Delay(150);

        // 3. Lê o texto copiado.
        string? captured = _ui.Invoke(() =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return (string?)null; }
        });

        // 4. Restaura o clipboard original (melhor esforço).
        _ui.Invoke(() =>
        {
            try
            {
                if (previousText is not null) Clipboard.SetText(previousText);
                else Clipboard.Clear();
            }
            catch { /* Falha ao restaurar: vida segue. */ }
        });

        return captured;
    }

    /// <summary>
    /// Simula sequência de teclas: pressiona todas em ordem, solta em
    /// ordem reversa. Para Ctrl+C: SimulateKeys(VK_CONTROL, VK_C).
    /// </summary>
    private static void SimulateKeys(params byte[] vks)
    {
        foreach (byte vk in vks)
            keybd_event(vk, 0, 0, 0);
        for (int i = vks.Length - 1; i >= 0; i--)
            keybd_event(vks[i], 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>Abre URL no navegador padrão.</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            UnelevatedLauncher.Start(url);   // navegador com privilégios do usuário, não de administrador
        }
        catch (Exception ex)
        {
            AppLog.Warning("Action", $"Falha ao abrir URL: {ex.Message}");
        }
    }

    /// <summary>Abre as Configurações rápidas do Windows (Wi-Fi, volume, brilho...), o mesmo que Win+A.</summary>
    private static void OpenQuickSettings()
    {
        SimulateKeys(VK_LWIN, VK_A);
        AppLog.Debug("Action", "Configurações rápidas (Win+A)");
    }

    // ------------------------------------------------------------------
    // Virtual Key Codes
    // ------------------------------------------------------------------

    private const byte VK_CONTROL = 0x11;
    private const byte VK_DELETE  = 0x2E;
    private const byte VK_LWIN    = 0x5B;
    private const byte VK_A       = 0x41;
    private const byte VK_B       = 0x42;
    private const byte VK_C       = 0x43;
    private const byte VK_V       = 0x56;
    private const byte VK_F2      = 0x71;

    // ------------------------------------------------------------------
    // Constantes Win32
    // ------------------------------------------------------------------

    private const int GWL_STYLE   = -16;
    private const int GWL_EXSTYLE = -20;

    private const int WS_MAXIMIZE      = 0x01000000;
    private const int WS_EX_TOPMOST    = 0x00000008;
    private const int WS_EX_LAYERED    = 0x00080000;

    private const uint WM_SYSCOMMAND   = 0x0112;
    private const int SC_CLOSE         = 0xF060;
    private const int SC_MINIMIZE      = 0xF020;
    private const int SC_MAXIMIZE      = 0xF030;
    private const int SC_RESTORE       = 0xF120;

    private const int SW_RESTORE       = 9;

    private const uint SWP_NOMOVE      = 0x0002;
    private const uint SWP_NOSIZE      = 0x0001;
    private const uint SWP_NOZORDER    = 0x0004;
    private const uint SWP_NOACTIVATE  = 0x0010;

    private static readonly IntPtr HWND_TOPMOST    = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST  = new(-2);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const byte LWA_ALPHA                = 0x02;
    private const uint KEYEVENTF_KEYUP          = 0x0002;

    // ------------------------------------------------------------------
    // Structs Win32
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, byte dwFlags);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
