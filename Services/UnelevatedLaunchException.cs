// ============================================================================
// RadialFlow - Falha ao iniciar um processo SEM elevação
// ----------------------------------------------------------------------------
// Lançada por UnelevatedLauncher.ExecuteAsync ANTES de qualquer código do
// executável rodar. Failure permite à interface (e aos testes) distinguir
// "recusado por segurança" de "falha do sistema".
// ============================================================================

using System;

namespace RadialFlow.Services;

public enum UnelevatedLaunchFailure
{
    /// <summary>Caminho inválido/inseguro, não é .exe, não existe ou não está em local protegido (TrustedPath).</summary>
    UntrustedExecutable,

    /// <summary>Argumentos ou pasta de trabalho inválidos (nulos, caractere NUL, comprimento, pasta inexistente).</summary>
    InvalidArguments,

    /// <summary>O app é administrador e não há shell (Explorer) cujo token não elevado possa ser usado.</summary>
    ShellUnavailable,

    /// <summary>Só existe token elevado disponível (ex.: UAC desligado): nada é executado como administrador.</summary>
    ElevatedToken,

    /// <summary>O Windows recusou a criação do processo, do pipe ou do job.</summary>
    LaunchFailed
}

public sealed class UnelevatedLaunchException(UnelevatedLaunchFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UnelevatedLaunchFailure Failure { get; } = failure;
}
