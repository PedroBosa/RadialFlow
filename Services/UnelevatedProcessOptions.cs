// ============================================================================
// RadialFlow - Opções de UnelevatedLauncher.ExecuteAsync
// ============================================================================

using System;
using System.Collections.Generic;

namespace RadialFlow.Services;

/// <summary>Como executar um processo sem elevação e como acompanhar a saída dele.</summary>
public sealed record UnelevatedProcessOptions
{
    /// <summary>Argumentos, um por item. O launcher faz o quoting (regras de CommandLineToArgvW).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Pasta de trabalho (absoluta e existente). Nulo = a do RadialFlow.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Uma chamada por linha de stdout, em tempo real, na thread de leitura dedicada (UTF-8; \n, \r e \r\n
    /// terminam a linha; linhas enormes são divididas). Não chamar a UI direto: usar Dispatcher.InvokeAsync.
    /// Exceções são registradas e não interrompem a leitura. Nunca é chamada depois de ExecuteAsync retornar.
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Igual a <see cref="OnOutputLine"/>, para stderr (thread distinta: as duas podem rodar ao mesmo tempo).</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>Recebe o PID logo após a criação, antes de o processo começar a rodar.</summary>
    public Action<int>? OnStarted { get; init; }
}
