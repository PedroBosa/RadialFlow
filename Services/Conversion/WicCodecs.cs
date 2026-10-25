// ============================================================================
// RadialFlow - Codecs de imagem do Windows (WIC)
// ----------------------------------------------------------------------------
// O Windows lê WebP, HEIC/AVIF e fotos RAW quando as extensões de imagem da
// Microsoft Store estão instaladas. Aqui se descobre o que ESTE PC tem:
//   * leitura: enumeração dos decodificadores registrados (COM, ~2 ms);
//   * HEIC: grava uma imagem 16x16 de teste (o codificador HEIF aparece na
//     lista mesmo quando falta a extensão HEVC, e aí falharia na conversão).
// O JPEG XL fica de fora: o decodificador aparece na lista, mas o WPF não
// consegue usá-lo (verificado com arquivos .jxl reais).
// Resultados em cache por 5 minutos (instalar uma extensão não exige reiniciar).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;

namespace RadialFlow.Services;

internal static class WicCodecs
{
    /// <summary>Contêiner HEIF (CLSID/GUID do formato no WIC).</summary>
    internal static readonly Guid HeifContainer = new("e1e62521-6787-405b-a339-500715b5763f");

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".jxl" };

    private static readonly object Gate = new();
    private static (IReadOnlySet<string> Value, DateTime At)? _readable;
    private static (bool Value, DateTime At)? _heic;

    /// <summary>Extensões (com ponto, minúsculas) que o Windows decodifica neste PC.</summary>
    public static IReadOnlySet<string> Readable
    {
        get
        {
            lock (Gate)
            {
                if (_readable is { } cached && DateTime.UtcNow - cached.At < CacheTtl) return cached.Value;
            }

            var set = new HashSet<string>(ConversionFormats.BuiltInWicReadable, StringComparer.OrdinalIgnoreCase);
            foreach (string ext in EnumerateDecoderExtensions())
                if (!Excluded.Contains(ext)) set.Add(ext);

            lock (Gate) _readable = (set, DateTime.UtcNow);
            return set;
        }
    }

    /// <summary>true se o codificador HEIF realmente grava neste PC.</summary>
    public static bool CanWriteHeic
    {
        get
        {
            lock (Gate)
            {
                if (_heic is { } cached && DateTime.UtcNow - cached.At < CacheTtl) return cached.Value;
            }

            bool works = ProbeEncoder(HeifContainer);
            lock (Gate) _heic = (works, DateTime.UtcNow);
            return works;
        }
    }

    private static bool ProbeEncoder(Guid container)
    {
        try
        {
            BitmapEncoder encoder = BitmapEncoder.Create(container);
            var pixels = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, new byte[16 * 16 * 4], 16 * 4);
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.Length > 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or COMException or InvalidOperationException or
                                         ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Enumeração COM (IWICImagingFactory::CreateComponentEnumerator)
    // ------------------------------------------------------------------

    private static readonly Guid ImagingFactoryClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private const uint WicDecoder = 0x1;
    private const uint EnumerateDefault = 0x0;

    /// <summary>Extensões declaradas por todos os decodificadores registrados (vazio se a consulta falhar).</summary>
    internal static List<string> EnumerateDecoderExtensions()
    {
        var result = new List<string>();
        object? factoryObject = null;
        IEnumUnknown? enumerator = null;

        try
        {
            Type? type = Type.GetTypeFromCLSID(ImagingFactoryClsid, throwOnError: false);
            if (type is null) return result;

            factoryObject = Activator.CreateInstance(type);
            if (factoryObject is not IWICImagingFactory factory) return result;
            if (factory.CreateComponentEnumerator(WicDecoder, EnumerateDefault, out enumerator) < 0 || enumerator is null) return result;

            while (enumerator.Next(1, out object? item, out uint fetched) == 0 && fetched == 1 && item is not null)
            {
                try
                {
                    if (item is IWICBitmapCodecInfo info)
                    {
                        foreach (string ext in ReadString(info.GetFileExtensions).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (ext.Length > 1 && ext[0] == '.') result.Add(ext.ToLowerInvariant());
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or
                                         NotSupportedException or TypeLoadException or ArgumentException)
        {
            AppLog.Warning("Conversion", $"Não foi possível listar os codecs de imagem do Windows: {ex.Message}");
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject)) Marshal.ReleaseComObject(factoryObject);
        }

        return result;
    }

    private delegate int StringGetter(uint bufferLength, IntPtr buffer, out uint actualLength);

    private static string ReadString(StringGetter getter)
    {
        if (getter(0, IntPtr.Zero, out uint length) < 0 || length is 0 or > 4096) return "";

        IntPtr buffer = Marshal.AllocCoTaskMem((int)length * sizeof(char));
        try
        {
            return getter(length, buffer, out _) < 0 ? "" : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    // Só os slots usados têm assinatura real; os demais existem para manter a ordem da vtable (wincodec.h).

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        void CreateStream();
        void CreateColorContext();
        void CreateColorTransformer();
        void CreateBitmap();
        void CreateBitmapFromSource();
        void CreateBitmapFromSourceRect();
        void CreateBitmapFromMemory();
        void CreateBitmapFromHBITMAP();
        void CreateBitmapFromHICON();
        [PreserveSig] int CreateComponentEnumerator(uint componentTypes, uint options, out IEnumUnknown enumerator);
    }

    [ComImport, Guid("00000100-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumUnknown
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.IUnknown)] out object? element, out uint fetched);
    }

    [ComImport, Guid("E87A44C4-B76E-4c47-8B09-298EB12A2714"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapCodecInfo
    {
        // IWICComponentInfo
        void GetComponentType();
        void GetCLSID();
        void GetSigningStatus();
        void GetAuthor();
        void GetVendorGUID();
        void GetVersion();
        void GetSpecVersion();
        void GetFriendlyName();

        // IWICBitmapCodecInfo
        void GetContainerFormat();
        void GetPixelFormats();
        void GetColorManagementVersion();
        void GetDeviceManufacturer();
        void GetDeviceModels();
        void GetMimeTypes();
        [PreserveSig] int GetFileExtensions(uint bufferLength, IntPtr buffer, out uint actualLength);
    }
}
