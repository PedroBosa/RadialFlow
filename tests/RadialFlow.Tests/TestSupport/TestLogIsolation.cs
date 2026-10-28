using System;
using System.IO;
using System.Runtime.CompilerServices;
using RadialFlow.Infrastructure;

namespace RadialFlow.Tests.TestSupport;

/// <summary>
/// Os testes passam por código que registra no log (avisos de caminho recusado, falhas simuladas...). Sem isto, tudo ia
/// para o log REAL do usuário em %LocalAppData%\RadialFlow\logs, misturando ruído de teste com o uso de verdade.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void RedirectAppLog() =>
        AppLog.DirectoryOverride = Path.Combine(Path.GetTempPath(), "RadialFlow.Tests-logs", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
