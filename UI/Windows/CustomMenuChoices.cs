// ============================================================================
// RadialFlow - Escolhas compartilhadas do editor de menus
// ----------------------------------------------------------------------------
// Usadas pelo editor de itens e pelo diálogo de opções do submenu. Os rótulos
// são resolvidos a cada acesso, no idioma atual.
// ============================================================================

using System.Linq;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal static class CustomMenuChoices
{
    private static readonly RadialAction[] ActionValues =
    [
        RadialAction.Convert,
        RadialAction.Compress,
        RadialAction.CopyPath,
        RadialAction.MoveTo,
        RadialAction.SendTo,
        RadialAction.Rename,
        RadialAction.FavoriteApps,
        RadialAction.SystemControls,
        RadialAction.ClipboardHistory,
        RadialAction.LockScreen,
        RadialAction.SnapLeft,
        RadialAction.SnapRight,
        RadialAction.Maximize,
        RadialAction.Minimize,
        RadialAction.Close,
        RadialAction.AlwaysOnTop,
        RadialAction.Transparency,
        RadialAction.Paste,
        RadialAction.Translate,
        RadialAction.Clear,
        RadialAction.SearchWeb,
        RadialAction.Format,
    ];

    internal static (RadialAction Action, string Label)[] Actions =>
        ActionValues.Select(a => (a, Loc.Get($"Choices.Action.{a}"))).ToArray();

    private static readonly CustomMenuItemKind[] KindValues =
    [
        CustomMenuItemKind.Action,
        CustomMenuItemKind.Launch,
        CustomMenuItemKind.OpenUrl,
        CustomMenuItemKind.OpenPath,
        CustomMenuItemKind.Hotkey,
        CustomMenuItemKind.Text,
        CustomMenuItemKind.Submenu,
        CustomMenuItemKind.Routine,
    ];

    internal static (CustomMenuItemKind Kind, string Label)[] Kinds =>
        KindValues.Select(k => (k, Loc.Get($"Choices.Kind.{k}"))).ToArray();

    /// <summary>Ações oferecidas no editor de rotinas: as mesmas que a execução aceita.</summary>
    internal static (RadialAction Action, string Label)[] RoutineActions =>
        Actions.Where(a => CustomRoutineLogic.IsAllowedAction(a.Action)).ToArray();

    internal static string ActionLabel(RadialAction action)
    {
        foreach (var (candidate, label) in Actions)
            if (candidate == action) return label;

        return action.ToString();
    }

    /// <summary>
    /// Ícones locais da Segoe Fluent Icons (com fallback Segoe MDL2 Assets). Nada é baixado nem instalado:
    /// o conjunto é fixo e curado para os menus.
    /// </summary>
    private static readonly (string Glyph, string Key)[] GlyphEntries =
    [
        ("\uE734", "Glyph.Apps"),
        ("\uE70F", "Glyph.Edit"),
        ("\uE8A5", "Glyph.Document"),
        ("\uEB9F", "Glyph.Image"),
        ("\uE714", "Glyph.Video"),
        ("\uE8D6", "Glyph.Audio"),
        ("\uF012", "Glyph.ZipFile"),
        ("\uE8AB", "Glyph.Convert"),
        ("\uE8AC", "Glyph.Rename"),
        ("\uE838", "Glyph.Move"),
        ("\uE724", "Glyph.Send"),
        ("\uE71B", "Glyph.Copy"),
        ("\uE8B7", "Glyph.Folder"),
        ("\uE774", "Glyph.Translate"),
        ("\uE721", "Glyph.Search"),
        ("\uE77F", "Glyph.Paste"),
        ("\uE72E", "Glyph.Lock"),
        ("\uE713", "Glyph.Settings"),
        ("\uE8BB", "Glyph.Close"),
        ("\uE922", "Glyph.Maximize"),
        ("\uE921", "Glyph.Minimize"),
        ("\uE718", "Glyph.Pin"),
        ("\uE706", "Glyph.Transparency"),
        ("\uE8DD", "Glyph.Bold"),
        ("\uE894", "Glyph.Clear"),
        ("\uE8EF", "Glyph.Calculator"),
        ("\uE756", "Glyph.Terminal"),
        ("\uE80F", "Glyph.Home"),
        ("\uE8F4", "Glyph.NewFolder"),
        ("\uE7C3", "Glyph.Page"),
        ("\uE8E5", "Glyph.OpenFile"),
        ("\uE74E", "Glyph.Save"),
        ("\uE749", "Glyph.Print"),
        ("\uE74D", "Glyph.Delete"),
        ("\uE8C6", "Glyph.Cut"),
        ("\uE72C", "Glyph.Refresh"),
        ("\uE896", "Glyph.Download"),
        ("\uE768", "Glyph.Play"),
        ("\uE769", "Glyph.Pause"),
        ("\uE71A", "Glyph.Stop"),
        ("\uE7BA", "Glyph.Warning"),
        ("\uE946", "Glyph.Info"),
        ("\uE930", "Glyph.Done"),
        ("\uE785", "Glyph.Unlock"),
        ("\uE8B3", "Glyph.SelectAll"),
        ("\uE787", "Glyph.Calendar"),
        ("\uE890", "Glyph.Preview"),
    ];

    internal static (string Glyph, string Label)[] Glyphs =>
        GlyphEntries.Select(e => (e.Glyph, Loc.Get(e.Key))).ToArray();
}
