using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Avisos da lista de itens: mesmas regras que o Salvar recusaria, sem tocar disco.</summary>
public sealed class CustomMenuIssuesTests
{
    private static CustomMenuItem Valid() => new()
    {
        Label = "Renomear",
        Kind = CustomMenuItemKind.Action,
        Action = RadialAction.Rename,
    };

    [Fact]
    public void Describe_ItemValido_NaoTemAviso()
    {
        Assert.Null(CustomMenuIssues.Describe(Valid()));
        Assert.Null(CustomMenuIssues.Describe(null));
    }

    [Fact]
    public void Describe_RotuloVazio_Avisa()
    {
        CustomMenuItem item = Valid();
        item.Label = "   ";

        Assert.False(string.IsNullOrEmpty(CustomMenuIssues.Describe(item)));
    }

    [Fact]
    public void Describe_SubmenuComMenosDeDuasOpcoesAtivas_Avisa()
    {
        var item = new CustomMenuItem
        {
            Label = "Mais",
            Kind = CustomMenuItemKind.Submenu,
            Options =
            {
                new CustomMenuItem { Label = "Um", Kind = CustomMenuItemKind.Action, Action = RadialAction.CopyPath },
                new CustomMenuItem { Label = "Dois", Kind = CustomMenuItemKind.Action, Action = RadialAction.Rename, Enabled = false },
            },
        };

        Assert.False(string.IsNullOrEmpty(CustomMenuIssues.Describe(item)));

        item.Options[1].Enabled = true;
        Assert.Null(CustomMenuIssues.Describe(item));
    }

    [Fact]
    public void Describe_UrlSemHttp_Avisa()
    {
        var item = new CustomMenuItem
        {
            Label = "Site",
            Kind = CustomMenuItemKind.OpenUrl,
            Target = "file:///C:/segredo.txt",
        };

        Assert.False(string.IsNullOrEmpty(CustomMenuIssues.Describe(item)));

        item.Target = "https://example.com";
        Assert.Null(CustomMenuIssues.Describe(item));
    }

    [Fact]
    public void Describe_RotinaInvalida_Avisa()
    {
        var item = new CustomMenuItem
        {
            Label = "Rotina",
            Kind = CustomMenuItemKind.Routine,
        };

        Assert.False(string.IsNullOrEmpty(CustomMenuIssues.Describe(item)));

        item.Steps.Add(new CustomRoutineStep { Kind = CustomRoutineStepKind.Action, Action = RadialAction.CopyPath });
        Assert.Null(CustomMenuIssues.Describe(item));
    }

    [Fact]
    public void CountEnabled_IgnoraItensDesabilitados()
    {
        var profile = new CustomMenuProfile
        {
            Name = "Teste",
            Items =
            {
                Valid(),
                new CustomMenuItem { Label = "Vazio", Kind = CustomMenuItemKind.OpenUrl, Target = "" },
                new CustomMenuItem { Label = "Desligado", Kind = CustomMenuItemKind.OpenUrl, Target = "", Enabled = false },
            },
        };

        Assert.Equal(1, CustomMenuIssues.CountEnabled(profile));
        Assert.Equal(0, CustomMenuIssues.CountEnabled(null));
    }
}
