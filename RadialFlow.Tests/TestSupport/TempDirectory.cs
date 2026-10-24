using System;
using System.IO;

namespace RadialFlow.Tests.TestSupport;

/// <summary>
/// Pasta temporária exclusiva do teste (caminho canônico); apagada no Dispose.
/// Cada instância fica direto em %TEMP% (sem pasta-mãe compartilhada): os testes rodam em paralelo
/// e apagar uma pasta-mãe comum corria contra a criação da pasta de outro teste.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Root { get; }

    public TempDirectory()
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"RadialFlow.Tests-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Caminho dentro da pasta (não cria nada).</summary>
    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Cria um arquivo vazio (e as pastas intermediárias) e devolve o caminho.</summary>
    public string CreateFile(params string[] parts)
    {
        string file = Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, []);
        return file;
    }

    /// <summary>Cria uma pasta (e as intermediárias) e devolve o caminho.</summary>
    public string CreateDirectory(params string[] parts)
    {
        string dir = Combine(parts);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { /* arquivo preso por antivírus/indexador: o %TEMP% é limpo pelo sistema */ }
        catch (UnauthorizedAccessException) { }
    }
}
