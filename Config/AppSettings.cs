// ============================================================================
// RadialFlow - Configurações e inicialização com o Windows
// ----------------------------------------------------------------------------
// Gerencia a serialização em JSON (%LocalAppData%\RadialFlow\settings.json)
// e a inicialização automática com o Windows via TAREFA AGENDADA.
//
// Por que Tarefa Agendada e não HKCU\...\Run: o app exige administrador
// (app.manifest) e o Windows NÃO inicia programas elevados pela chave Run
// (exigiria UAC no logon). Uma tarefa com "privilégios mais altos" inicia
// elevada, sem prompt.
//
// Usings implícitos estão desligados no .csproj, portanto todos os imports
// são explícitos e sem ambiguidades.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using RadialFlow.Infrastructure;
using RadialFlow.Input;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.Config;

/// <summary>
/// Representa um aplicativo favorito cadastrado pelo usuário.
/// </summary>
public sealed class FavoriteAppEntry
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Glyph { get; set; } = "\uE734"; // Ícone padrão (Estrela / App)

    public FavoriteAppEntry() { }

    public FavoriteAppEntry(string name, string path, string arguments = "", string glyph = "\uE734")
    {
        Name = name;
        ExecutablePath = path;
        Arguments = arguments;
        Glyph = glyph;
    }
}

/// <summary>
/// Modelo raiz de todas as configurações do RadialFlow.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Se true, o aplicativo é iniciado junto com o Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Se true, o hook é desativado quando um app estiver em tela cheia exclusiva (jogos).</summary>
    public bool BlockExclusiveFullscreen { get; set; } = true;

    /// <summary>Distância em pixels para disparar o arrasto da roda (padrão: 15 px).</summary>
    public int DeadzonePixels { get; set; } = 15;

    /// <summary>
    /// Tempo (ms) com o cursor sobre uma fatia com opções, ou no centro de um submenu, até o submenu abrir ou fechar.
    /// Antes era fixo em 260 ms. Limites: <see cref="RadialFlow.UI.RadialMenuWindow.MinDwellMilliseconds"/> a
    /// <see cref="RadialFlow.UI.RadialMenuWindow.MaxDwellMilliseconds"/>.
    /// </summary>
    public int DwellMilliseconds { get; set; } = RadialFlow.UI.RadialMenuWindow.DefaultDwellMilliseconds;

    /// <summary>
    /// Cor de destaque da roda: "Blue" (padrão), "Violet", "Teal", "Green", "Orange", "Pink" ou "Windows"
    /// (cor de destaque do sistema). Guardada como texto de propósito: um valor desconhecido no arquivo cai no
    /// padrão em vez de invalidar o settings.json inteiro.
    /// </summary>
    public string Accent { get; set; } = "Blue";

    /// <summary>
    /// Estilo visual da roda: "Standard" (padrão), "Compact", "Large" ou "HighContrast". Guardado como texto:
    /// um valor desconhecido no arquivo cai no padrão sem invalidar o resto.
    /// </summary>
    public string WheelPreset { get; set; } = RadialFlow.UI.WheelPreset.DefaultKey;

    /// <summary>Lista de processos em blacklist (jogos, apps 3D, CAD).</summary>
    public List<ForegroundBlacklist.BlacklistEntry> Blacklist { get; set; } = new();

    /// <summary>Lista de aplicativos favoritos configurados pelo usuário.</summary>
    public List<FavoriteAppEntry> FavoriteApps { get; set; } = new();

    /// <summary>Menus personalizados que somam itens à roda conforme o contexto.</summary>
    public List<CustomMenuProfile> CustomProfiles { get; set; } = new();

    /// <summary>Cria uma instância com valores padrão de fábrica.</summary>
    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            BlockExclusiveFullscreen = true,
            DeadzonePixels = 15,
            Blacklist = new List<ForegroundBlacklist.BlacklistEntry>(ForegroundBlacklist.DefaultEntries),
            FavoriteApps = new List<FavoriteAppEntry>
            {
                new("Bloco de Notas", "notepad.exe", "", "\uE70F"),
                new("Calculadora", "calc.exe", "", "\uE8EF"),
                new("Terminal / PowerShell", "powershell.exe", "", "\uE756"),
                new("Navegador Web", "explorer.exe", "https://www.google.com", "\uE774")
            }
        };

        return settings;
    }
}

/// <summary>
/// Serviço de leitura, gravação e aplicação de configurações no sistema.
/// </summary>
public static class SettingsService
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryAppName = "RadialFlow";
    private const string StartupTaskName = "RadialFlow";

    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadialFlow");

    private static AppSettings? _current;
    private static bool? _startupApplied;
    private static string? _startupAppliedPath;

    /// <summary>Configurações ativas no momento.</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>
    /// Só para testes: define as configurações ativas SEM ler nem gravar o arquivo e SEM mexer no Agendador de
    /// Tarefas (Load/Save fazem as duas coisas). Passar null volta ao comportamento normal.
    /// </summary>
    internal static void OverrideCurrentForTests(AppSettings? settings) => _current = settings;

    /// <summary>
    /// Só para testes: substitui a gravação em disco (e o Agendador) por um dublê. Se o dublê lançar,
    /// <see cref="TrySave"/> devolve false como faria uma falha real de disco.
    /// </summary>
    internal static Action<AppSettings>? SaveOverrideForTests { get; set; }

    private static readonly SettingsStore Store = new(ConfigDirectory);

    /// <summary>
    /// Carrega as configurações do disco (%LocalAppData%\RadialFlow\settings.json).
    /// Nunca apaga nem sobrescreve o arquivo do usuário: se estiver ilegível, usa a cópia anterior (.bak) ou,
    /// no pior caso, os padrões — e o arquivo ruim é GUARDADO ao lado (ver <see cref="SettingsStore"/>).
    /// </summary>
    public static AppSettings Load()
    {
        SettingsLoadResult result = Store.Load();
        _current = result.Settings;

        switch (result.Source)
        {
            case SettingsSource.DefaultsNoFile:
                // Primeira execução: cria o arquivo com os padrões.
                Save(result.Settings);
                break;

            case SettingsSource.Backup:
            case SettingsSource.DefaultsAfterCorruption:
                // Já houve perda de dados que o usuário merece saber; o arquivo novo é criado no próximo Save.
                AppLog.Warning("Settings", result.QuarantinedTo is null
                    ? "Configurações restauradas de uma cópia anterior."
                    : $"Configurações restauradas. O arquivo com problema foi guardado em '{result.QuarantinedTo}'.");
                break;
        }

        return result.Settings;
    }

    /// <summary>
    /// Salva as configurações no arquivo JSON (gravação atômica) e ajusta a inicialização automática se necessário.
    /// Falha de disco é registrada e NÃO apaga o arquivo anterior. Quem precisa reagir à falha usa
    /// <see cref="TrySave"/>; este método existe para chamadas que não têm como mostrar o resultado (carga inicial).
    /// </summary>
    public static void Save(AppSettings settings) => TrySave(settings, out _);

    /// <summary>
    /// Salva as configurações e devolve false (com o motivo) quando o disco não aceitou.
    /// A ordem importa: o arquivo é gravado ANTES de publicar <see cref="Current"/>; em falha, Current continua
    /// sendo o que está no disco e a inicialização automática não é tocada.
    /// </summary>
    internal static bool TrySave(AppSettings settings, out string? error)
    {
        error = null;

        if (SaveOverrideForTests is { } saveForTests)
        {
            try
            {
                saveForTests(settings);
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings", "Erro ao salvar configurações (dublê de teste).", ex);
                error = SaveFailureMessage(ex);
                return false;
            }

            _current = settings;
            return true;
        }

        try
        {
            Store.Save(settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings", "Erro ao salvar configurações (o arquivo anterior foi preservado).", ex);
            error = SaveFailureMessage(ex);
            return false;
        }

        _current = settings;

        // Daqui em diante o JSON já está salvo: falha do agendador é registrada, mas não desfaz o sucesso.
        try
        {
            string? exePath = Environment.ProcessPath;
            if (NeedsStartupUpdate(settings.StartWithWindows, _startupApplied, _startupAppliedPath, exePath))
            {
                SetStartWithWindows(settings.StartWithWindows);
                _startupApplied = settings.StartWithWindows;
                _startupAppliedPath = settings.StartWithWindows ? exePath : null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Settings", $"Falha ao atualizar a inicialização automática: {ex.Message}");
        }

        AppLog.Info("Settings", "Configurações salvas com sucesso.");
        return true;
    }

    private static string SaveFailureMessage(Exception ex) =>
        $"Não foi possível salvar as configurações. O arquivo de configurações não foi alterado.\n\nDetalhe: {ex.Message}";

    /// <summary>
    /// Decide se o Agendador de Tarefas precisa ser tocado neste Save. Puro de propósito: os testes cobrem a
    /// decisão sem chamar o schtasks. O primeiro Save sempre aplica (e, com false, limpa a entrada legada);
    /// depois só na mudança de estado ou de pasta do executável.
    /// </summary>
    internal static bool NeedsStartupUpdate(bool enabled, bool? applied, string? appliedPath, string? currentPath)
    {
        if (applied is null) return true;
        if (applied.Value != enabled) return true;
        return enabled && !string.Equals(appliedPath, currentPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifica se a tarefa de inicialização existe no Agendador de Tarefas.</summary>
    public static bool IsStartupRegistered() => RunSchTasks(new[] { "/Query", "/TN", StartupTaskName }) == 0;

    /// <summary>
    /// Ativa ou desativa a inicialização automática com o Windows
    /// (Tarefa Agendada no logon, com privilégios mais altos).
    /// </summary>
    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            RemoveLegacyRunKey();

            if (!enable)
            {
                RunSchTasks(new[] { "/Delete", "/TN", StartupTaskName, "/F" });   // erro se não existir: ignorado
                AppLog.Info("Settings", "Inicialização automática removida.");
                return;
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // A tarefa guarda o caminho do exe: o primeiro Save de cada sessão a recria para acompanhar mudanças de pasta.
            int code = CreateStartupTask(BuildTaskXml(exePath));
            if (code == 0) AppLog.Info("Settings", "Inicialização automática ativada (Agendador de Tarefas).");
            else AppLog.Warning("Settings", $"schtasks /Create falhou (código {code}).");
        }
        catch (Exception ex)
        {
            AppLog.Warning("Settings", $"Erro ao alterar inicialização automática: {ex.Message}");
        }
    }

    /// <summary>
    /// Registra a tarefa a partir do XML sem dar a um processo comum a chance de trocá-lo.
    /// </summary>
    /// <remarks>
    /// O schtasks roda ELEVADO e cria uma tarefa com "privilégios mais altos": se um processo sem
    /// elevação trocasse o XML entre a gravação e a leitura (antes ficava solto em %TEMP%), ganharia
    /// uma tarefa de administrador. Agora: pasta própria com ACL só para SYSTEM/Administradores;
    /// arquivo criado com CreateNew; reaberto somente-leitura negando escrita/exclusão a todos
    /// (a pasta também não pode ser renomeada com o arquivo aberto) e conferido byte a byte antes
    /// de o schtasks ler.
    /// </remarks>
    /// <returns>Código de saída do schtasks; -1 se não foi possível preparar ou executar.</returns>
    private static int CreateStartupTask(string xml)
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"radialflow-task-{Guid.NewGuid():N}");
        try
        {
            CreatePrivateDirectory(workDir);
            string xmlPath = Path.Combine(workDir, "task.xml");

            // schtasks /XML exige UTF-16 (com BOM).
            byte[] content = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)];

            using (var writer = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                writer.Write(content);

            using var locked = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!HasExactContent(locked, content))
            {
                AppLog.Warning("Settings", "O XML da tarefa foi alterado antes do registro; cancelado.");
                return -1;
            }

            return RunSchTasks(new[] { "/Create", "/TN", StartupTaskName, "/XML", xmlPath, "/F" });
        }
        catch (Exception ex)
        {
            AppLog.Warning("Settings", $"Falha ao preparar a tarefa agendada: {ex.Message}");
            return -1;
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { /* temp: ignora */ }
        }
    }

    /// <summary>Cria uma pasta cuja ACL (sem herança) só concede acesso a SYSTEM e Administradores.</summary>
    private static void CreatePrivateDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var principals = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        // Sem elevação (ex.: depuração) o próprio usuário precisa gravar; e aí não há escalada a evitar.
        if (!UnelevatedLauncher.IsElevated())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) principals.Add(identity.User);
        }

        foreach (var sid in principals)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).Create(security);
    }

    private static bool HasExactContent(FileStream stream, byte[] expected)
    {
        if (stream.Length != expected.Length) return false;

        var actual = new byte[expected.Length];
        stream.ReadExactly(actual);
        stream.Position = 0;
        return actual.AsSpan().SequenceEqual(expected);
    }

    private static string BuildTaskXml(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(identity.Name) ?? "";
        string command = SecurityElement.Escape(exePath) ?? "";
        string workDir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "") ?? "";

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Inicia o RadialFlow ao entrar no Windows.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{command}</Command>
      <WorkingDirectory>{workDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }

    /// <summary>
    /// Executa schtasks.exe resolvido por <see cref="TrustedPath.TryResolveSystemTool"/> (System32 protegido;
    /// nunca o diretório atual). Saídas drenadas e prazo de 15 s.
    /// </summary>
    /// <returns>Código de saída; -1 se o schtasks não for confiável, não iniciar ou estourar o prazo.</returns>
    private static int RunSchTasks(string[] args)
    {
        try
        {
            if (!TrustedPath.TryResolveSystemTool("schtasks.exe", out string schtasks))
            {
                AppLog.Warning("Settings", "schtasks.exe não encontrado em local protegido.");
                return -1;
            }

            var psi = new ProcessStartInfo(schtasks)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return -1;

            // Drena as saídas para não travar caso o buffer encha.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(true); } catch { }
                return -1;
            }
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Remove a entrada antiga em HKCU\...\Run (migração da versão anterior).</summary>
    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, true);
            key?.DeleteValue(RegistryAppName, false);
        }
        catch { /* sem permissão ou chave ausente: ignora */ }
    }
}
