using System.Linq;
using RadialFlow.Config;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomMenuHotkeysTests
{
    private static CustomMenuProfile Profile(
        string name,
        string hotkey,
        bool enabled = true,
        bool withItem = true)
    {
        var profile = new CustomMenuProfile
        {
            Name = name,
            Hotkey = hotkey,
            Enabled = enabled,
        };

        if (withItem)
        {
            profile.Items.Add(new CustomMenuItem
            {
                Label = "Abrir",
                Kind = CustomMenuItemKind.Launch,
                Target = "notepad.exe",
            });
        }

        return profile;
    }

    [Fact]
    public void Resolve_SemPerfis_NaoRegistraNada()
    {
        Assert.Empty(CustomMenuHotkeys.Resolve(null, null));
        Assert.Empty(CustomMenuHotkeys.Resolve([], "Ctrl+Alt+Space"));
    }

    [Fact]
    public void Resolve_MantemSomentePerfisValidosENormalizaOAtalho()
    {
        var valid = Profile("Válido", " ctrl + alt + 1 ");
        var disabled = Profile("Desligado", "Ctrl+Alt+2", enabled: false);
        var empty = Profile("Sem atalho", "");
        var invalid = Profile("Inválido", "Ctrl+Alt");
        var noItems = Profile("Sem itens", "Ctrl+Alt+3", withItem: false);

        var result = CustomMenuHotkeys.Resolve([valid, disabled, empty, invalid, noItems], null);

        var entry = Assert.Single(result);
        Assert.Same(valid, entry.Profile);
        Assert.Equal("Ctrl+Alt+1", entry.Hotkey);
    }

    [Fact]
    public void Resolve_IgnoraPerfilSemItemAtivo()
    {
        CustomMenuProfile profile = Profile("Só desligado", "Ctrl+Alt+1", withItem: false);
        profile.Items.Add(new CustomMenuItem
        {
            Label = "Desligado",
            Enabled = false,
            Kind = CustomMenuItemKind.Launch,
            Target = "notepad.exe",
        });

        Assert.Empty(CustomMenuHotkeys.Resolve([profile], null));
    }

    [Fact]
    public void Resolve_NaoRepeteOAtalhoDaRodaPrincipal()
    {
        var conflict = Profile("Conflito", "Ctrl+Alt+Space");
        var other = Profile("Livre", "Ctrl+Alt+1");

        var result = CustomMenuHotkeys.Resolve([conflict, other], "ctrl+alt+space");

        var entry = Assert.Single(result);
        Assert.Same(other, entry.Profile);
    }

    [Fact]
    public void Resolve_MantemApenasOPrimeiroAtalhoRepetido()
    {
        var first = Profile("Primeiro", "Ctrl+Alt+1");
        var second = Profile("Segundo", "ctrl+alt+1");

        var result = CustomMenuHotkeys.Resolve([first, second], null);

        var entry = Assert.Single(result);
        Assert.Same(first, entry.Profile);
        Assert.Equal("Ctrl+Alt+1", entry.Hotkey);
    }

    [Fact]
    public void Resolve_SemAtalhoDaRoda_NaoDescartaNada()
    {
        var a = Profile("A", "Ctrl+Alt+1");
        var b = Profile("B", "Ctrl+Alt+2");

        var result = CustomMenuHotkeys.Resolve([a, b], null);

        Assert.Equal(new[] { a, b }, result.Select(r => r.Profile));
    }
}
