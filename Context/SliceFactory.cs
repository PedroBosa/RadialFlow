// ============================================================================
// RadialFlow - Fatias da roda e submenus por contexto
// ----------------------------------------------------------------------------
// Gera as fatias radiais adaptadas ao contexto sob o cursor.
// Suporta navegação hierárquica (SubSlices):
//  * "Converter": ao manter o mouse sobre a fatia, abre as opções reais de
//    conversão (PNG, JPG, WEBP, PDF, MP4, MP3...) calculadas para o arquivo.
//  * "Apps favoritos": expande os aplicativos cadastrados pelo usuário.
//
// Roda FORA da thread de UI (RadialController usa Task.Run): consulta disco e
// ferramentas instaladas. Nada aqui executa ação: cada fatia só guarda o
// delegate que o ActionRouter executa quando o botão é solto.
//
// Dependências (ações, opções de conversão, favoritos, disco) entram por
// SliceFactoryDependencies: o app usa Default; os testes injetam dublês.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using RadialFlow.Config;
using RadialFlow.Services;
using RadialFlow.UI;

namespace RadialFlow.Context;

/// <summary>Efeitos disparados pelas fatias. Implementação real: <see cref="ActionRouter"/>.</summary>
internal interface ISliceActions
{
    void Run(RadialAction action, ContextInfo context);
    void ConvertTo(string sourceFile, string targetFormat);
    void LaunchFavorite(FavoriteAppEntry app);
}

/// <summary>Encaminha para o <see cref="ActionRouter"/> (só é tocado quando uma fatia é executada).</summary>
internal sealed class ActionRouterSliceActions : ISliceActions
{
    public void Run(RadialAction action, ContextInfo context) => ActionRouter.Run(action, context);
    public void ConvertTo(string sourceFile, string targetFormat) => ActionRouter.RunDirectConversion(sourceFile, targetFormat);
    public void LaunchFavorite(FavoriteAppEntry app) => ActionRouter.LaunchAppDirect(app);
}

/// <summary>O que o <see cref="SliceFactory"/> consulta para montar as fatias.</summary>
/// <param name="Actions">Destino das fatias quando executadas.</param>
/// <param name="GetConversionOptions">Formatos realmente disponíveis para um arquivo.</param>
/// <param name="GetFavoriteApps">Favoritos configurados (pode devolver null).</param>
/// <param name="FileExists">Teste de existência de arquivo (não pasta).</param>
internal sealed record SliceFactoryDependencies(
    ISliceActions Actions,
    Func<string, IReadOnlyList<ConversionOption>> GetConversionOptions,
    Func<IReadOnlyList<FavoriteAppEntry>?> GetFavoriteApps,
    Func<string, bool> FileExists)
{
    public static SliceFactoryDependencies Default { get; } = new(
        new ActionRouterSliceActions(),
        path => ConversionService.GetAvailableOptions(path),
        () => SettingsService.Current.FavoriteApps,
        File.Exists);
}

public static class SliceFactory
{
    /// <summary>Monta as fatias do contexto com as dependências reais do app.</summary>
    public static IReadOnlyList<RadialSlice> Build(ContextInfo ctx) => Build(ctx, SliceFactoryDependencies.Default);

    /// <summary>Monta as fatias do contexto com dependências explícitas (testes).</summary>
    internal static IReadOnlyList<RadialSlice> Build(ContextInfo ctx, SliceFactoryDependencies deps) => ctx.Kind switch
    {
        RadialContextKind.FileOrFolder => FileSlices(ctx, deps),
        RadialContextKind.TitleBar     => TitleBarSlices(ctx, deps.Actions),
        RadialContextKind.TextField    => TextFieldSlices(ctx, deps.Actions),
        _                              => SystemSlices(ctx, deps)   // DesktopBackground e Generic
    };

    // ------------------------------------------------------------------
    // Contexto 1: Arquivo ou pasta
    // ------------------------------------------------------------------
    private static RadialSlice[] FileSlices(ContextInfo c, SliceFactoryDependencies deps)
    {
        var actions = deps.Actions;
        List<RadialSlice>? convertSubSlices = null;

        if (c.Paths.Count > 0 && deps.FileExists(c.Paths[0]))
        {
            string sourceFile = c.Paths[0];
            var options = deps.GetConversionOptions(sourceFile);
            if (options.Count > 0)
            {
                convertSubSlices = new List<RadialSlice>();
                foreach (var opt in options)
                {
                    if (convertSubSlices.Count == RadialGeometry.MaxSlices) break;   // a roda comporta no máximo 8

                    string target = opt.TargetFormat;
                    convertSubSlices.Add(new RadialSlice(
                        Label: target.ToUpperInvariant(),
                        Glyph: GetFormatGlyph(target),
                        Execute: () => actions.ConvertTo(sourceFile, target),
                        Description: opt.DisplayName));
                }
            }
        }

        return new[]
        {
            new RadialSlice("Comprimir",      "\uF012", () => actions.Run(RadialAction.Compress, c), null, "Compactar em .ZIP"),
            new RadialSlice("Converter",      "\uE8AB", () => actions.Run(RadialAction.Convert, c), convertSubSlices, "Formatos de Conversão"),
            new RadialSlice("Copiar caminho", "\uE71B", () => actions.Run(RadialAction.CopyPath, c), null, "Copiar caminho completo"),
            new RadialSlice("Mover para",     "\uE838", () => actions.Run(RadialAction.MoveTo, c), null, "Mover para outra pasta"),
            new RadialSlice("Enviar para",    "\uE724", () => actions.Run(RadialAction.SendTo, c), null, "Anexar ou enviar arquivo"),
            new RadialSlice("Renomear",       "\uE8AC", () => actions.Run(RadialAction.Rename, c), null, "Renomear arquivo"),
        };
    }

    // ------------------------------------------------------------------
    // Contexto 2: Área de Trabalho (fundo vazio) e fallback genérico
    // ------------------------------------------------------------------
    private static RadialSlice[] SystemSlices(ContextInfo c, SliceFactoryDependencies deps)
    {
        var actions = deps.Actions;
        List<RadialSlice>? favoriteSubSlices = null;
        var favorites = deps.GetFavoriteApps();
        if (favorites is { Count: > 0 })
        {
            favoriteSubSlices = new List<RadialSlice>();
            foreach (var app in favorites)
            {
                // Além de 8, o submenu não cabe na roda; a fatia principal abre a lista completa.
                if (favoriteSubSlices.Count == RadialGeometry.MaxSlices) break;
                if (app is null) continue;

                var targetApp = app;
                favoriteSubSlices.Add(new RadialSlice(
                    Label: app.Name,
                    Glyph: string.IsNullOrEmpty(app.Glyph) ? "\uE734" : app.Glyph,
                    Execute: () => actions.LaunchFavorite(targetApp),
                    Description: $"Abrir {app.Name}"));
            }
        }

        return new[]
        {
            new RadialSlice("Apps favoritos",  "\uE734", () => actions.Run(RadialAction.FavoriteApps, c), favoriteSubSlices, "Aplicativos Favoritos"),
            new RadialSlice("Sistema",         "\uE713", () => actions.Run(RadialAction.SystemControls, c), null, "Painel de Controle / Ações"),
            new RadialSlice("Área de transf.", "\uE81C", () => actions.Run(RadialAction.ClipboardHistory, c), null, "Histórico da Área de Transferência"),
            new RadialSlice("Bloquear",        "\uE72E", () => actions.Run(RadialAction.LockScreen, c), null, "Bloquear estação de trabalho"),
        };
    }

    // ------------------------------------------------------------------
    // Contexto 3: Barra de título
    // ------------------------------------------------------------------
    private static RadialSlice[] TitleBarSlices(ContextInfo c, ISliceActions actions) => new[]
    {
        new RadialSlice("Maximizar",     "\uE922", () => actions.Run(RadialAction.Maximize, c), null, "Maximizar / Restaurar"),
        new RadialSlice("Snap dir.",     "\uE72A", () => actions.Run(RadialAction.SnapRight, c), null, "Encaixar à Direita"),
        new RadialSlice("Fechar",        "\uE8BB", () => actions.Run(RadialAction.Close, c), null, "Fechar Janela"),
        new RadialSlice("Transparência", "\uE706", () => actions.Run(RadialAction.Transparency, c), null, "Alternar Transparência"),
        new RadialSlice("Sempre no topo","\uE718", () => actions.Run(RadialAction.AlwaysOnTop, c), null, "Fixar Acima de Todas"),
        new RadialSlice("Minimizar",     "\uE921", () => actions.Run(RadialAction.Minimize, c), null, "Minimizar Janela"),
        new RadialSlice("Snap esq.",     "\uE72B", () => actions.Run(RadialAction.SnapLeft, c), null, "Encaixar à Esquerda"),
    };

    // ------------------------------------------------------------------
    // Contexto 4: Campo de texto editável
    // ------------------------------------------------------------------
    private static RadialSlice[] TextFieldSlices(ContextInfo c, ISliceActions actions) => new[]
    {
        new RadialSlice("Colar",     "\uE77F", () => actions.Run(RadialAction.Paste, c), null, "Colar texto"),
        new RadialSlice("Traduzir",  "\uE774", () => actions.Run(RadialAction.Translate, c), null, "Traduzir seleção no Google"),
        new RadialSlice("Pesquisar", "\uE721", () => actions.Run(RadialAction.SearchWeb, c), null, "Pesquisar texto na Web"),
        new RadialSlice("Formatar",  "\uE8DD", () => actions.Run(RadialAction.Format, c), null, "Aplicar Negrito"),
        new RadialSlice("Limpar",    "\uE894", () => actions.Run(RadialAction.Clear, c), null, "Apagar todo o texto"),
    };

    internal static string GetFormatGlyph(string format) => format.ToLowerInvariant() switch
    {
        "png" or "jpg" or "jpeg" or "webp" or "bmp" or "tiff" or "ico" => "\uEB9F",
        "mp4" or "webm" or "mkv" or "gif" => "\uE714",
        "mp3" or "wav" or "flac" or "aac" or "ogg" => "\uE8D6",
        "pdf" or "docx" or "html" or "md" or "txt" => "\uE8A5",
        _ => "\uE8AB"
    };
}
