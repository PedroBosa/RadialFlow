using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

public sealed class CustomMenuResolverTests
{
    private static readonly IntPtr Window = (IntPtr)0x1234;

    private static ContextInfo Context(
        RadialContextKind kind = RadialContextKind.FileOrFolder,
        string process = "explorer",
        string title = "") =>
        new(kind, Window, process, 10, 20, ContextInfo.NoPaths, title);

    private static CustomMenuProfile Profile(
        string id,
        int priority = 0,
        string? name = null,
        string process = "",
        string title = "",
        bool enabled = true) => new()
        {
            Id = id,
            Name = name ?? id,
            Priority = priority,
            ProcessName = process,
            TitleContains = title,
            Enabled = enabled
        };

    private static CustomMenuItem Item(
        string id,
        string label = "Item",
        CustomMenuItemKind kind = CustomMenuItemKind.Action,
        RadialAction action = RadialAction.Close,
        bool enabled = true) => new()
        {
            Id = id,
            Label = label,
            Kind = kind,
            Action = action,
            Enabled = enabled
        };

    private static Action<CustomMenuItem> Recorder(List<CustomMenuItem> executed) =>
        item => executed.Add(item);

    private static RadialSlice Slice(
        string label,
        RadialAction? action = null,
        IReadOnlyList<RadialSlice>? subSlices = null) =>
        new(label, "\uE000", null, subSlices, label, action?.ToString());

    // ------------------------------------------------------------------
    // Match: filtros
    // ------------------------------------------------------------------

    [Fact]
    public void Match_SemPerfis_DevolveVazio()
    {
        Assert.Empty(CustomMenuResolver.Match(Context(), null));
        Assert.Empty(CustomMenuResolver.Match(Context(), Array.Empty<CustomMenuProfile>()));
    }

    [Fact]
    public void Match_PerfilGlobal_ValeEmQualquerContexto()
    {
        CustomMenuProfile profile = Profile("p1");
        profile.Items = [Item("i1")];

        var matched = CustomMenuResolver.Match(
            Context(RadialContextKind.TextField, "notepad", "qualquer título"), [profile]);

        Assert.Equal("i1", Assert.Single(matched).Id);
    }

    [Fact]
    public void Match_FiltraPorContexto()
    {
        CustomMenuProfile profile = Profile("p1");
        profile.Contexts = [RadialContextKind.TitleBar];
        profile.Items = [Item("i1")];

        Assert.Empty(CustomMenuResolver.Match(Context(RadialContextKind.TextField), [profile]));
        Assert.Single(CustomMenuResolver.Match(Context(RadialContextKind.TitleBar), [profile]));
    }

    [Fact]
    public void Match_FiltraPorProcesso_SemDiferenciarCaixaNemSufixoExe()
    {
        CustomMenuProfile profile = Profile("p1", process: "notepad.exe");
        profile.Items = [Item("i1")];

        Assert.Single(CustomMenuResolver.Match(Context(process: "NotePad"), [profile]));
        Assert.Empty(CustomMenuResolver.Match(Context(process: "explorer"), [profile]));
    }

    [Fact]
    public void Match_FiltraPorTitulo_SemDiferenciarCaixa()
    {
        CustomMenuProfile profile = Profile("p1", title: "documentos");
        profile.Items = [Item("i1")];

        Assert.Single(CustomMenuResolver.Match(Context(title: "Meus DOCUMENTOS - Bloco"), [profile]));
        Assert.Empty(CustomMenuResolver.Match(Context(title: "Planilha"), [profile]));
    }

    [Fact]
    public void Match_IgnoraPerfilEItemDesabilitados()
    {
        CustomMenuProfile disabledProfile = Profile("p1", enabled: false);
        disabledProfile.Items = [Item("i1")];
        Assert.Empty(CustomMenuResolver.Match(Context(), [disabledProfile]));

        CustomMenuProfile enabledProfile = Profile("p2");
        enabledProfile.Items = [Item("i2", enabled: false)];
        Assert.Empty(CustomMenuResolver.Match(Context(), [enabledProfile]));
    }

    // ------------------------------------------------------------------
    // Match: ordem e composição
    // ------------------------------------------------------------------

    [Fact]
    public void Match_OrdenaPorPrioridadeNomeEId()
    {
        CustomMenuProfile lowB = Profile("p1", priority: 0, name: "B");
        lowB.Items = [Item("i1", "b")];
        CustomMenuProfile lowA = Profile("p2", priority: 0, name: "A");
        lowA.Items = [Item("i2", "a")];
        CustomMenuProfile highZ = Profile("p3", priority: 10, name: "Z");
        highZ.Items = [Item("i3", "z")];
        CustomMenuProfile highA = Profile("p4", priority: 10, name: "A");
        highA.Items = [Item("i4", "a2")];

        var matched = CustomMenuResolver.Match(Context(), [lowB, lowA, highZ, highA]);

        Assert.Equal(["a2", "z", "a", "b"], matched.Select(i => i.Label).ToArray());
    }

    [Fact]
    public void Match_EmpateDeNomeDesempataPorId()
    {
        CustomMenuProfile second = Profile("p2", name: "A");
        second.Items = [Item("i2", "segundo")];
        CustomMenuProfile first = Profile("p1", name: "A");
        first.Items = [Item("i1", "primeiro")];

        var matched = CustomMenuResolver.Match(Context(), [second, first]);

        Assert.Equal(["primeiro", "segundo"], matched.Select(i => i.Label).ToArray());
    }

    [Fact]
    public void Match_SomaPerfilGlobalEJustoDoApp()
    {
        CustomMenuProfile global = Profile("p1", priority: 0, name: "Global");
        global.Items = [Item("i1", "global")];
        CustomMenuProfile notepad = Profile("p2", priority: 10, name: "Bloco", process: "notepad");
        notepad.Items = [Item("i2", "notepad")];

        var matched = CustomMenuResolver.Match(Context(process: "notepad"), [global, notepad]);

        Assert.Equal(["notepad", "global"], matched.Select(i => i.Label).ToArray());
    }

    [Fact]
    public void Match_RemoveItensComMesmoId()
    {
        CustomMenuProfile first = Profile("p1", name: "A");
        first.Items = [Item("mesmo", "primeiro")];
        CustomMenuProfile second = Profile("p2", name: "B");
        second.Items = [Item("mesmo", "segundo")];

        var matched = CustomMenuResolver.Match(Context(), [first, second]);

        Assert.Equal("primeiro", Assert.Single(matched).Label);
    }

    // ------------------------------------------------------------------
    // Merge
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_SemItens_DevolveAMesmaListaAutomatica()
    {
        RadialSlice[] automatic = [Slice("A"), Slice("B")];

        var merged = CustomMenuResolver.Merge(automatic, Array.Empty<CustomMenuItem>(), Recorder([]));

        Assert.Same(automatic, merged);
    }

    [Fact]
    public void Merge_SubstituiAcaoNaMesmaPosicao()
    {
        RadialSlice[] automatic =
        [
            Slice("Fechar", RadialAction.Close),
            Slice("Minimizar", RadialAction.Minimize)
        ];
        CustomMenuItem item = Item("i1", "Fechar sem medo", action: RadialAction.Close);
        var executed = new List<CustomMenuItem>();

        var merged = CustomMenuResolver.Merge(automatic, [item], Recorder(executed));

        Assert.Equal(2, merged.Count);
        Assert.Equal("Fechar sem medo", merged[0].Label);
        Assert.Equal("Minimizar", merged[1].Label);
        merged[0].Execute!();
        Assert.Same(item, Assert.Single(executed));
    }

    [Fact]
    public void Merge_NaoSubstituiFatiaComSubmenu()
    {
        RadialSlice[] automatic = [Slice("Converter", RadialAction.Convert, [Slice("PNG")])];
        CustomMenuItem item = Item("i1", "Meu conversor", action: RadialAction.Convert);

        var merged = CustomMenuResolver.Merge(automatic, [item], Recorder([]));

        Assert.Equal(2, merged.Count);
        Assert.Equal("Converter", merged[0].Label);
        Assert.NotNull(merged[0].SubSlices);
        Assert.Equal("Meu conversor", merged[1].Label);
    }

    [Fact]
    public void Merge_AcrescentaExtrasNoFim_ComGlifoEDescricaoPadrao()
    {
        RadialSlice[] automatic = [Slice("Fechar", RadialAction.Close)];
        var item = new CustomMenuItem
        {
            Id = "i1",
            Label = "Calc",
            Glyph = "",
            Description = "",
            Kind = CustomMenuItemKind.Launch,
            Target = "calc.exe"
        };

        var merged = CustomMenuResolver.Merge(automatic, [item], Recorder([]));

        Assert.Equal(2, merged.Count);
        RadialSlice added = merged[1];
        Assert.Equal("Calc", added.Label);
        Assert.Equal("\uE734", added.Glyph);
        Assert.Equal("Calc", added.Description);
        Assert.Null(added.ActionKey);
    }

    [Fact]
    public void Merge_SubstituiCadaAcaoAutomaticaUmaVez()
    {
        RadialSlice[] automatic =
        [
            Slice("Fechar", RadialAction.Close),
            Slice("Minimizar", RadialAction.Minimize)
        ];
        CustomMenuItem first = Item("i1", "Fechar 1", action: RadialAction.Close);
        CustomMenuItem second = Item("i2", "Fechar 2", action: RadialAction.Close);

        var merged = CustomMenuResolver.Merge(automatic, [first, second], Recorder([]));

        Assert.Equal(["Fechar 1", "Minimizar", "Fechar 2"], merged.Select(s => s.Label).ToArray());
    }

    [Fact]
    public void Merge_RespeitaOLimiteDeOitoFatias()
    {
        RadialSlice[] automatic =
        [
            Slice("A"), Slice("B"), Slice("C"), Slice("D"),
            Slice("E"), Slice("F"), Slice("G"), Slice("H")
        ];
        CustomMenuItem item = Item("i1", "Extra", kind: CustomMenuItemKind.OpenUrl);

        var full = CustomMenuResolver.Merge(automatic, [item], Recorder([]));
        Assert.Equal(8, full.Count);
        Assert.DoesNotContain(full, s => s.Label == "Extra");

        var oneFree = CustomMenuResolver.Merge(automatic[..7], [item, Item("i2", "Sobrando", kind: CustomMenuItemKind.Text)], Recorder([]));
        Assert.Equal(8, oneFree.Count);
        Assert.Equal("Extra", oneFree[^1].Label);
        Assert.DoesNotContain(oneFree, s => s.Label == "Sobrando");
    }
}
