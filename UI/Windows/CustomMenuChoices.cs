// ============================================================================
// RadialFlow - Escolhas compartilhadas do editor de menus
// ----------------------------------------------------------------------------
// Usadas pelo editor de itens e pelo diálogo de opções do submenu.
// ============================================================================

using System.Linq;
using RadialFlow.Config;
using RadialFlow.Context;

namespace RadialFlow.UI;

internal static class CustomMenuChoices
{
    internal static readonly (RadialAction Action, string Label)[] Actions =
    [
        (RadialAction.Convert, "Converter"),
        (RadialAction.Compress, "Comprimir"),
        (RadialAction.CopyPath, "Copiar caminho"),
        (RadialAction.MoveTo, "Mover para"),
        (RadialAction.SendTo, "Enviar para"),
        (RadialAction.Rename, "Renomear"),
        (RadialAction.FavoriteApps, "Apps favoritos"),
        (RadialAction.SystemControls, "Ajustes rápidos"),
        (RadialAction.ClipboardHistory, "Área de transferência"),
        (RadialAction.LockScreen, "Bloquear"),
        (RadialAction.SnapLeft, "Snap à esquerda"),
        (RadialAction.SnapRight, "Snap à direita"),
        (RadialAction.Maximize, "Maximizar"),
        (RadialAction.Minimize, "Minimizar"),
        (RadialAction.Close, "Fechar"),
        (RadialAction.AlwaysOnTop, "Sempre no topo"),
        (RadialAction.Transparency, "Transparência"),
        (RadialAction.Paste, "Colar"),
        (RadialAction.Translate, "Traduzir"),
        (RadialAction.Clear, "Limpar"),
        (RadialAction.SearchWeb, "Pesquisar na web"),
        (RadialAction.Format, "Formatar (negrito)"),
    ];

    internal static readonly (CustomMenuItemKind Kind, string Label)[] Kinds =
    [
        (CustomMenuItemKind.Action, "Ação do RadialFlow"),
        (CustomMenuItemKind.Launch, "Abrir executável"),
        (CustomMenuItemKind.OpenUrl, "Abrir site (URL)"),
        (CustomMenuItemKind.OpenPath, "Abrir arquivo ou pasta"),
        (CustomMenuItemKind.Hotkey, "Enviar atalho"),
        (CustomMenuItemKind.Text, "Colar texto"),
        (CustomMenuItemKind.Submenu, "Submenu (segundo anel)"),
        (CustomMenuItemKind.Routine, "Rotina de ações"),
    ];

    /// <summary>Ações oferecidas no editor de rotinas: as mesmas que a execução aceita.</summary>
    internal static readonly (RadialAction Action, string Label)[] RoutineActions =
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
    internal static readonly (string Glyph, string Label)[] Glyphs =
    [
        ("\uE734", "Aplicativos"),
        ("\uE70F", "Editar"),
        ("\uE8A5", "Documento"),
        ("\uEB9F", "Imagem"),
        ("\uE714", "Vídeo"),
        ("\uE8D6", "Áudio"),
        ("\uF012", "Arquivo zip"),
        ("\uE8AB", "Converter"),
        ("\uE8AC", "Renomear"),
        ("\uE838", "Mover"),
        ("\uE724", "Enviar"),
        ("\uE71B", "Copiar"),
        ("\uE8B7", "Pasta"),
        ("\uE774", "Traduzir"),
        ("\uE721", "Pesquisar"),
        ("\uE77F", "Colar"),
        ("\uE72E", "Bloquear"),
        ("\uE713", "Ajustes"),
        ("\uE8BB", "Fechar"),
        ("\uE922", "Maximizar"),
        ("\uE921", "Minimizar"),
        ("\uE718", "Fixar"),
        ("\uE706", "Transparência"),
        ("\uE8DD", "Negrito"),
        ("\uE894", "Limpar"),
        ("\uE8EF", "Calculadora"),
        ("\uE756", "Terminal"),
        ("\uE80F", "Início"),
        ("\uE8F4", "Nova pasta"),
        ("\uE7C3", "Página"),
        ("\uE8E5", "Abrir arquivo"),
        ("\uE74E", "Salvar"),
        ("\uE749", "Imprimir"),
        ("\uE74D", "Excluir"),
        ("\uE8C6", "Recortar"),
        ("\uE72C", "Atualizar"),
        ("\uE896", "Baixar"),
        ("\uE768", "Reproduzir"),
        ("\uE769", "Pausar"),
        ("\uE71A", "Parar"),
        ("\uE7BA", "Aviso"),
        ("\uE946", "Informações"),
        ("\uE930", "Concluído"),
        ("\uE785", "Desbloquear"),
        ("\uE8B3", "Selecionar tudo"),
        ("\uE787", "Calendário"),
        ("\uE890", "Visualizar"),
    ];
}
