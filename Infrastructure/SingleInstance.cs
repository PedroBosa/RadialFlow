// ============================================================================
// RadialFlow - Instância única
// ----------------------------------------------------------------------------
// Duas cópias instalariam dois hooks de mouse que engolem o mesmo botão direito
// (ex.: "Iniciar com o Windows" + abrir pelo atalho). Só a primeira continua; a
// segunda manda um sinal para a primeira abrir as Configurações e encerra.
//
// Como funciona:
//   * Mutex nomeado criado SEM posse: o que vale é a EXISTÊNCIA do nome (createdNew).
//     Sem posse não há ReleaseMutex em outra thread nem mutex "abandonado" se a
//     primeira cair: o nome some quando o processo morre.
//   * EventWaitHandle nomeado (reset automático) = "ative-se". A primeira espera nele
//     sem prender nenhuma thread (RegisterWaitForSingleObject).
//   * Namespace "Local\": por sessão do Windows (cada usuário logado tem a sua).
//   * Se o sistema recusar o nome (outro processo o criou com permissões restritas),
//     o app segue SEM a proteção e registra o fato: melhor que se recusar a iniciar.
// ============================================================================

using System;
using System.IO;
using System.Threading;

namespace RadialFlow.Infrastructure;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;
    private int _disposed;

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>true se o nome do sistema foi obtido; false = rodando SEM a proteção (nome recusado pelo sistema).</summary>
    public bool IsGuarded => _mutex is not null;

    /// <summary>
    /// Tenta se tornar a instância única.
    /// </summary>
    /// <param name="name">Nome lógico (ex.: "RadialFlow"); vira "Local\{name}.lock" e "Local\{name}.activate".</param>
    /// <returns>A instância (guarde-a até o fim do processo) ou null se OUTRA instância já está rodando.</returns>
    public static SingleInstance? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Mutex? mutex = null;
        EventWaitHandle? activate = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, LockName(name), out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateName(name));
            return new SingleInstance(mutex, activate);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            mutex?.Dispose();
            activate?.Dispose();
            AppLog.Warning("Instance", $"Não foi possível criar a trava de instância única ({ex.GetType().Name}); seguindo sem ela.", ex);
            return new SingleInstance(null, null);
        }
    }

    /// <summary>
    /// Pede à instância que já está rodando para se ativar.
    /// </summary>
    /// <returns>true se o sinal foi entregue; false se não há instância ouvindo ou o sistema recusou.</returns>
    public static bool SignalExisting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivateName(name));
            return existing.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning("Instance", $"Não foi possível sinalizar a instância existente ({ex.GetType().Name}).", ex);
            return false;
        }
    }

    /// <summary>
    /// Passa a chamar <paramref name="onActivate"/> sempre que outra instância sinalizar. Roda em thread do pool
    /// (use o Dispatcher para tocar na UI); exceções do callback são registradas e a escuta continua.
    /// </summary>
    public void StartListening(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_activate is null || _registration is not null) return;

        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            try
            {
                onActivate();
            }
            catch (Exception ex)
            {
                AppLog.Error("Instance", "Falha ao tratar o pedido de outra instância.", ex);
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _registration?.Unregister(null);
        _activate?.Dispose();
        _mutex?.Dispose();   // sem posse: apenas fecha o handle (o nome some com o último handle)
    }

    private static string LockName(string name) => $@"Local\{name}.lock";
    private static string ActivateName(string name) => $@"Local\{name}.activate";
}
