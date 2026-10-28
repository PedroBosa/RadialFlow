using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace RadialFlow.Tests.TestSupport;

// Amostras reais (16 x 8, metade vermelha e metade azul) para provar que o codec da Store decodifica de fato.
// O decodificador pode estar registrado no WIC e mesmo assim falhar ao iniciar (0x88982F8B), como nos runners do CI.

/// <summary>Codecs da Microsoft Store: amostras e sonda de decodificação de verdade.</summary>
internal static class StoreImageFixtures
{
    internal const string Webp = "UklGRiYAAABXRUJQVlA4TBkAAAAvD8ABAA8Q87//8x8OxILJ/KU7U4jof+QAAA==";

    internal const string Avif =
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRt" +
        "AAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAoAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNw" +
        "ZQAAAAAAAAAQAAAACAAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAwbWRhdAoFGAy/tgEy" +
        "HxKAAABAAAAAvuQSkZIxbazuyrBUEm+NBnUr6BbUboA=";

    /// <summary>
    /// true se o WIC decodifica a amostra. Extensões sem amostra ficam por conta da enumeração do WicCodecs.
    /// </summary>
    internal static bool CanDecode(string extension)
    {
        string? base64 = extension.ToLowerInvariant() switch
        {
            ".webp" => Webp,
            ".avif" => Avif,
            _ => null
        };
        if (base64 is null) return true;

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or
                                         InvalidOperationException or ArgumentException or IOException)
        {
            return false;
        }
    }
}
