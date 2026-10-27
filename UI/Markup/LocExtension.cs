// ============================================================================
// RadialFlow - {loc:Loc Chave} para textos no XAML
// ----------------------------------------------------------------------------
// Resolve o texto na cultura atual no momento em que a janela é construída.
// Como o idioma só muda ao reiniciar, não há atualização dinâmica a fazer.
// ============================================================================

using System;
using System.Windows.Markup;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI.Markup;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
