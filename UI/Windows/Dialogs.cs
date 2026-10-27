// ============================================================================
// RadialFlow - Diálogos no estilo do app
// ----------------------------------------------------------------------------
// Substitui as MessageBox brancas do Windows e os diálogos montados à mão em
// várias partes do código (tamanhos e cores diferentes em cada um).
//   * Choose  : mensagem com 1..n botões (retorna o índice clicado)
//   * Message / Confirm : atalhos
//   * PromptText / PromptPair : campos de texto com validação INLINE (a
//     mensagem de erro aparece no próprio diálogo, que continua aberto)
// Chamar sempre na thread de UI.
// ============================================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal enum DialogIcon { None, Info, Question, Warning, Error }

internal static class Dialogs
{
    private const double DialogWidth = 460;

    /// <summary>Só para testes: chamado com a janela logo antes de ShowDialog bloquear (permite inspecionar, interagir e fechar).</summary>
    internal static Action<Window>? BeforeShow { get; set; }

    /// <summary>
    /// Mostra uma mensagem e devolve o índice do botão clicado.
    /// </summary>
    /// <param name="buttons">Rótulos, da esquerda para a direita; o botão padrão (Enter) é o principal.</param>
    /// <param name="defaultIndex">Botão destacado e acionado por Enter.</param>
    /// <param name="cancelIndex">Botão acionado por Esc e pelo X da janela; -1 = o último, se houver mais de um.</param>
    public static int Choose(Window? owner, string title, string message, DialogIcon icon,
        string[] buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Length == 0) throw new ArgumentException("Pelo menos um botão.", nameof(buttons));

        if (cancelIndex < 0) cancelIndex = buttons.Length - 1;
        defaultIndex = Math.Clamp(defaultIndex, 0, buttons.Length - 1);

        Window window = CreateWindow(owner, title, out ContentControl body);
        int result = cancelIndex;

        var content = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        content.Children.Add(BuildHeader(window, icon, message));

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 96, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = i == defaultIndex, IsCancel = i == cancelIndex };
            if (i == defaultIndex) button.Style = (Style)window.FindResource("PrimaryButton");
            button.Click += (_, _) => { result = index; window.DialogResult = true; };
            row.Children.Add(button);
        }

        content.Children.Add(row);
        body.Content = content;
        BeforeShow?.Invoke(window);
        window.ShowDialog();
        return result;
    }

    public static void Message(Window? owner, string title, string message, DialogIcon icon = DialogIcon.Info) =>
        Choose(owner, title, message, icon, [Loc.Get("Common.OK")], 0, 0);

    /// <summary>true se o usuário confirmou. Por segurança o foco pode ficar no botão de cancelar.</summary>
    public static bool Confirm(Window? owner, string title, string message, string? confirmText = null,
        string? cancelText = null, DialogIcon icon = DialogIcon.Question, bool defaultIsConfirm = true)
    {
        string confirm = confirmText ?? Loc.Get("Common.Yes");
        string cancel = cancelText ?? Loc.Get("Common.No");
        return Choose(owner, title, message, icon, [cancel, confirm], defaultIsConfirm ? 1 : 0, 0) == 1;
    }

    /// <summary>
    /// Pede um texto. <paramref name="validate"/> devolve a mensagem de erro (ou null se válido); o erro aparece
    /// dentro do diálogo e ele continua aberto.
    /// </summary>
    /// <param name="selectLength">Quantos caracteres do início ficam selecionados (-1 = tudo). Renomear seleciona só o nome, sem a extensão.</param>
    /// <returns>O texto confirmado, ou null se o usuário cancelou.</returns>
    public static string? PromptText(Window? owner, string title, string label, string initial,
        Func<string, string?>? validate = null, int selectLength = -1, string? okText = null)
    {
        (string First, string Second)? result = Prompt(owner, title, label, initial, selectLength, null, null, validate, null,
            okText ?? Loc.Get("Common.OK"));
        return result?.First;
    }

    /// <summary>Dois campos (ex.: processo e descrição). Devolve null se cancelado.</summary>
    public static (string First, string Second)? PromptPair(Window? owner, string title,
        string firstLabel, string firstInitial, string secondLabel, string secondInitial,
        Func<string, string, string?>? validate = null, string? okText = null) =>
        Prompt(owner, title, firstLabel, firstInitial, -1, secondLabel, secondInitial, null, validate,
            okText ?? Loc.Get("Common.Add"));

    private static (string First, string Second)? Prompt(Window? owner, string title, string firstLabel, string firstInitial,
        int selectLength, string? secondLabel, string? secondInitial,
        Func<string, string?>? validateOne, Func<string, string, string?>? validateTwo, string okText)
    {
        Window window = CreateWindow(owner, title, out ContentControl body);
        (string, string)? result = null;

        var content = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };

        var first = AddField(window, content, firstLabel, firstInitial);
        TextBox? second = secondLabel is null ? null : AddField(window, content, secondLabel, secondInitial ?? "");

        var error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerOnGlassBrush");
        content.Children.Add(error);

        var ok = new Button { Content = okText, MinWidth = 104, IsDefault = true, Style = (Style)window.FindResource("PrimaryButton") };
        var cancel = new Button { Content = Loc.Get("Common.Cancel"), MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            string a = first.Text, b = second?.Text ?? "";
            string? problem = validateTwo is not null ? validateTwo(a, b) : validateOne?.Invoke(a);
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                first.Focus();
                return;
            }

            result = (a, b);
            window.DialogResult = true;
        };

        // Digitar de novo apaga o aviso antigo.
        first.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;

        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0),
            Children = { ok, cancel }
        });

        body.Content = content;
        window.ContentRendered += (_, _) =>
        {
            first.Focus();
            if (selectLength >= 0) first.Select(0, Math.Min(selectLength, first.Text.Length));
            else first.SelectAll();
        };

        BeforeShow?.Invoke(window);
        window.ShowDialog();
        return result;
    }

    // ------------------------------------------------------------------
    // Montagem
    // ------------------------------------------------------------------

    private static TextBox AddField(Window window, Panel parent, string label, string initial)
    {
        var caption = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 14, 0, 6) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        parent.Children.Add(caption);

        var box = new TextBox { Text = initial };
        parent.Children.Add(box);
        return box;
    }

    private static UIElement BuildHeader(Window window, DialogIcon icon, string message)
    {
        var text = new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        if (icon == DialogIcon.None) return text;

        (string glyph, string background, string foreground) = icon switch
        {
            DialogIcon.Warning  => ("\uE7BA", "WarningBrush", "WarningInkBrush"),
            DialogIcon.Error    => ("\uEA39", "DangerBrush", "SurfaceBrush"),
            DialogIcon.Question => ("\uE897", "AccentBrush", "AccentInkBrush"),
            _                   => ("\uE946", "AccentBrush", "AccentInkBrush")
        };

        var disc = new Ellipse { Width = 40, Height = 40 };
        disc.SetResourceReference(Shape.FillProperty, background);
        var mark = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, foreground);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.Children.Add(disc);
        badge.Children.Add(mark);

        Grid.SetColumn(text, 1);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        return grid;
    }

    private static Window CreateWindow(Window? owner, string title, out ContentControl body)
    {
        bool hasOwner = owner is { IsLoaded: true };

        var window = new Window
        {
            Title = title,
            Width = DialogWidth,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            // Sem janela-mãe (ações vindas da roda, sobre outros aplicativos): fica por cima e no centro da tela.
            Topmost = !hasOwner,
            Owner = hasOwner ? owner : null,
            WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            UseLayoutRounding = true
        };

        window.Icon = AppIconRenderer.WindowIcon;
        UiTheme.Install(window);
        UiTheme.StyleTitleBar(window);
        window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");

        // Conteúdo nativo (título e moldura) fica fora do vidro: só a área do cliente recebe o desfoque.
        var imageHost = new Border { IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(imageHost, BitmapScalingMode.HighQuality);
        var tintHost = new Border { IsHitTestVisible = false };
        tintHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        body = new ContentControl();

        var root = new Grid();
        root.Children.Add(imageHost);
        root.Children.Add(tintHost);
        root.Children.Add(body);
        window.Content = root;

        GlassSurface.Attach(window, imageHost, tintHost);
        Motion.AttachEntrance(window, root, Motion.Enabled);
        return window;
    }
}
