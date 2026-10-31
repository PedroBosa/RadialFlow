// ============================================================================
// RadialFlow - Badges de aviso das listas de menus
// ----------------------------------------------------------------------------
// Devolvem texto ("" = sem aviso) para o XAML esconder o badge via DataTrigger.
// ============================================================================

using System;
using System.Globalization;
using System.Windows.Data;
using RadialFlow.Config;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI.Markup;

/// <summary>Motivo do aviso de um item, para o tooltip e o glifo da lista de itens.</summary>
public sealed class MenuItemIssueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        CustomMenuIssues.Describe(value as CustomMenuItem) ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resumo dos itens com aviso de um perfil, para o rail de menus.</summary>
public sealed class ProfileIssueSummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = CustomMenuIssues.CountEnabled(value as CustomMenuProfile);
        if (count == 0) return "";

        return count == 1
            ? Loc.Format("Menus.RailIssuesOne", count)
            : Loc.Format("Menus.RailIssuesMany", count);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
