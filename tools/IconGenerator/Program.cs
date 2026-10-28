using System;
using System.IO;
using RadialFlow.UI;

namespace RadialFlow.Tools;

internal static class Program
{
    // WPF só renderiza em thread STA.
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine("src", "RadialFlow", "Assets", "RadialFlow.ico");

        try
        {
            byte[] ico = AppIconRenderer.BuildIco(AppIconRenderer.IcoSizes);

            string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllBytes(output, ico);
            Console.WriteLine($"{Path.GetFullPath(output)}  ({ico.Length:N0} bytes, {AppIconRenderer.IcoSizes.Length} tamanhos: {string.Join(", ", AppIconRenderer.IcoSizes)})");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Não foi possível gravar '{output}': {ex.Message}");
            return 1;
        }
    }
}
