using System;
using System.IO;
using RadialFlow.Services;
using Xunit;

namespace RadialFlow.Tests.TestSupport;

// Testes que dependem da máquina (codecs da Store, permissão para links, elevação). Sem a condição, aparecem como
// IGNORADOS, com o motivo, em vez de "aprovados" sem ter verificado nada.

/// <summary>Só roda se o Windows decodifica <c>extension</c> (extensões de imagem da Microsoft Store).</summary>
public sealed class RequiresImageDecoderFactAttribute : FactAttribute
{
    public RequiresImageDecoderFactAttribute(string extension)
    {
        if (!WicCodecs.Readable.Contains(extension))
            Skip = $"O Windows deste PC não lê {extension} (falta a extensão de imagem da Microsoft Store).";
        else if (!StoreImageFixtures.CanDecode(extension))
            Skip = $"O Windows lista {extension}, mas o decodificador não inicia neste PC (extensão da Store sem funcionar).";
    }
}

/// <summary>Só roda se o codificador HEIF do Windows grava (extensões HEIF + HEVC).</summary>
public sealed class RequiresHeicEncoderFactAttribute : FactAttribute
{
    public RequiresHeicEncoderFactAttribute()
    {
        if (!WicCodecs.CanWriteHeic)
            Skip = "O Windows deste PC não grava HEIC (faltam as extensões HEIF e HEVC da Microsoft Store).";
    }
}

/// <summary>Só roda como administrador (o contraste elevado x sem elevação só existe assim).</summary>
public sealed class RequiresElevationFactAttribute : FactAttribute
{
    public RequiresElevationFactAttribute()
    {
        if (!UnelevatedLauncher.IsElevated())
            Skip = "Precisa rodar como administrador.";
        else if (!UnelevatedLauncher.CanLaunchUnelevated())
            Skip = "A sessão só tem token de administrador (UAC desligado): não há como abrir o processo filho sem elevação.";
    }
}

/// <summary>
/// Só roda quando a sessão consegue abrir processos sem elevação. Runner só com token de administrador
/// (UAC desligado) não consegue: o RadialFlow recusa executar ferramentas como administrador.
/// </summary>
public sealed class RequiresUnelevatedLaunchFactAttribute : FactAttribute
{
    public RequiresUnelevatedLaunchFactAttribute()
    {
        if (!UnelevatedLauncher.CanLaunchUnelevated())
            Skip = "A sessão só tem token de administrador (UAC desligado): o RadialFlow não executa ferramentas como administrador.";
    }
}

/// <summary>Idem, para teorias.</summary>
public sealed class RequiresUnelevatedLaunchTheoryAttribute : TheoryAttribute
{
    public RequiresUnelevatedLaunchTheoryAttribute()
    {
        if (!UnelevatedLauncher.CanLaunchUnelevated())
            Skip = "A sessão só tem token de administrador (UAC desligado): o RadialFlow não executa ferramentas como administrador.";
    }
}

/// <summary>Só roda quando ESTE processo não pode instalar conversores (fora de uma pasta protegida ou sem administrador).</summary>
public sealed class RequiresConverterInstallBlockedFactAttribute : FactAttribute
{
    public RequiresConverterInstallBlockedFactAttribute()
    {
        if (ConverterInstaller.CheckCanInstall() is null)
            Skip = "Os testes estão rodando de uma pasta protegida, como administrador: aqui a instalação seria permitida.";
    }
}

/// <summary>Só roda se este Windows deixa criar links simbólicos (administrador ou modo de desenvolvedor).</summary>
public sealed class RequiresSymlinksFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> CanCreate = new(Probe);

    public RequiresSymlinksFactAttribute()
    {
        if (!CanCreate.Value)
            Skip = "Este Windows não permite criar links simbólicos (precisa de administrador ou do modo de desenvolvedor).";
    }

    private static bool Probe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RadialFlow.Tests-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "alvo.txt");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
