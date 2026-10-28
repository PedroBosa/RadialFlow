// ============================================================================
// RadialFlow - {Binding ..., Converter={StaticResource LocFormat}}
// ----------------------------------------------------------------------------
// Formata um valor com um texto do resx que tem {0}. Usado onde o XAML usa
// StringFormat com um texto que precisa ser traduzido (listas e contadores).
// ============================================================================

using System;
using System.Globalization;
using System.Windows.Data;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI.Markup;

public sealed class LocFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key ? Loc.Format(key, value) : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
