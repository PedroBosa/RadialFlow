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
using RadialFlow.Infrastructure;
using RadialFlow.Services;
using RadialFlow.UI;

namespace RadialFlow.Context;

/// <summary>Efeitos disparados pelas fatias. Implementação real: <see cref="ActionRouter"/>.</summary>
internal interface ISliceActions
{
    void Run(RadialAction action, ContextInfo context);
    void ConvertTo(IReadOnlyList<string> sourceFiles, string targetFormat);
    void LaunchFavorite(FavoriteAppEntry app);
    void RunCustom(CustomMenuItem item, ContextInfo context);
}

/// <summary>Encaminha para o <see cref="ActionRouter"/> (só é tocado quando uma fatia é executada).</summary>
internal sealed class ActionRouterSliceActions : ISliceActions
{
    public void Run(RadialAction action, ContextInfo context) => ActionRouter.Run(action, context);
    public void ConvertTo(IReadOnlyList<string> sourceFiles, string targetFormat) => ActionRouter.RunDirectConversion(sourceFiles, targetFormat);
    public void LaunchFavorite(FavoriteAppEntry app) => ActionRouter.LaunchAppDirect(app);
    public void RunCustom(CustomMenuItem item, ContextInfo context) => ActionRouter.RunCustom(item, context);
}

/// <summary>O que o <see cref="SliceFactory"/> consulta para montar as fatias.</summary>
/// <param name="Actions">Destino das fatias quando executadas.</param>
/// <param name="GetConversionOptions">Formatos realmente disponíveis para um arquivo.</param>
/// <param name="GetFavoriteApps">Favoritos configurados (pode devolver null).</param>
/// <param name="FileExists">Teste de existência de arquivo (não pasta).</param>
/// <param name="GetCustomProfiles">Perfis de menus personalizados; null desliga a personalização.</param>
internal sealed record SliceFactoryDependencies(
    ISliceActions Actions,
    Func<string, IReadOnlyList<ConversionOption>> GetConversionOptions,
    Func<IReadOnlyList<FavoriteAppEntry>?> GetFavoriteApps,
    Func<string, bool> FileExists,
    Func<IReadOnlyList<CustomMenuProfile>?>? GetCustomProfiles = null)
{
    public static SliceFactoryDependencies Default { get; } = new(
        new ActionRouterSliceActions(),
        path => ConversionService.GetAvailableOptions(path),
        () => SettingsService.Current.FavoriteApps,
        File.Exists,
        () => SettingsService.Current.CustomProfiles);
}

public static class SliceFactory
{
    /// <summary>Monta as fatias do contexto com as dependências reais do app.</summary>
    public static IReadOnlyList<RadialSlice> Build(ContextInfo ctx) => Build(ctx, SliceFactoryDependencies.Default);

    /// <summary>Monta as fatias do contexto com dependências explícitas (testes).</summary>
    internal static IReadOnlyList<RadialSlice> Build(ContextInfo ctx, SliceFactoryDependencies deps)
    {
        IReadOnlyList<RadialSlice> automatic = ctx.Kind switch
        {
            RadialContextKind.FileOrFolder => FileSlices(ctx, deps),
            RadialContextKind.TitleBar     => TitleBarSlices(ctx, deps.Actions),
            RadialContextKind.TextField    => TextFieldSlices(ctx, deps.Actions),
            _                              => SystemSlices(ctx, deps)   // DesktopBackground e Generic
        };

        var custom = CustomMenuResolver.Match(ctx, deps.GetCustomProfiles?.Invoke());
        return CustomMenuResolver.Merge(automatic, custom, item => deps.Actions.RunCustom(item, ctx));
    }

    // ------------------------------------------------------------------
    // Contexto 1: Arquivo ou pasta
    // ------------------------------------------------------------------
    private static RadialSlice[] FileSlices(ContextInfo c, SliceFactoryDependencies deps)
    {
        var actions = deps.Actions;
        List<RadialSlice>? convertSubSlices = null;
        IReadOnlyList<string> paths = c.Paths;

        bool allFiles = paths.Count > 0;
        foreach (string path in paths)
        {
            if (!deps.FileExists(path))
            {
                allFiles = false;
                break;
            }
        }

        if (allFiles)
        {
            var options = ConversionBatch.GetCommonOptions(paths, deps.GetConversionOptions);
            if (options.Count > 0)
            {
                var formatSlices = new List<RadialSlice>(options.Count);
                foreach (var opt in options)
                {
                    string target = opt.TargetFormat;
                    formatSlices.Add(new RadialSlice(
                        Label: target.ToUpperInvariant(),
                        Glyph: GetFormatGlyph(target),
                        Execute: () => actions.ConvertTo(paths, target),
                        Description: opt.DisplayName));
                }

                // Mais de 8 formatos viram páginas ligadas por "Mais…": nenhuma opção é descartada.
                convertSubSlices = RadialSlicePaging.Paginate(formatSlices);
            }
        }

        var slices = new List<RadialSlice>
        {
            new RadialSlice(Loc.Get("Wheel.Slice.Compress"), "\uF012", () => actions.Run(RadialAction.Compress, c), null,
                Loc.Get("Wheel.Desc.Compress"), nameof(RadialAction.Compress)),
        };

        // Converter só aparece quando existe ao menos uma conversão que funciona para TODOS os itens
        // (pasta, formato desconhecido ou ferramenta ausente: a fatia some em vez de levar a um beco sem saída).
        if (convertSubSlices is { Count: > 0 })
            slices.Add(new RadialSlice(Loc.Get("Wheel.Slice.Convert"), "\uE8AB", () => actions.Run(RadialAction.Convert, c),
                convertSubSlices, Loc.Get("Wheel.Desc.Convert"), nameof(RadialAction.Convert)));

        slices.Add(new RadialSlice(Loc.Get("Wheel.Slice.CopyPath"), "\uE71B", () => actions.Run(RadialAction.CopyPath, c), null,
            Loc.Get("Wheel.Desc.CopyPath"), nameof(RadialAction.CopyPath)));
        slices.Add(new RadialSlice(Loc.Get("Wheel.Slice.MoveTo"), "\uE838", () => actions.Run(RadialAction.MoveTo, c), null,
            Loc.Get("Wheel.Desc.MoveTo"), nameof(RadialAction.MoveTo)));
        slices.Add(new RadialSlice(Loc.Get("Wheel.Slice.SendTo"), "\uE724", () => actions.Run(RadialAction.SendTo, c), null,
            Loc.Get("Wheel.Desc.SendTo"), nameof(RadialAction.SendTo)));
        bool multiple = paths.Count > 1;
        slices.Add(new RadialSlice(Loc.Get("Wheel.Slice.Rename"), "\uE8AC",
            () => actions.Run(RadialAction.Rename, c), null,
            Loc.Get(multiple ? "Wheel.Desc.RenameSelection" : "Wheel.Desc.Rename"), nameof(RadialAction.Rename)));
        return slices.ToArray();
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
                    Description: Loc.Format("Wheel.Desc.OpenApp", app.Name)));
            }
        }

        return new[]
        {
            new RadialSlice(Loc.Get("Wheel.Slice.FavoriteApps"), "\uE734", () => actions.Run(RadialAction.FavoriteApps, c),
                favoriteSubSlices, Loc.Get("Wheel.Desc.FavoriteApps"), nameof(RadialAction.FavoriteApps)),
            new RadialSlice(Loc.Get("Wheel.Slice.SystemControls"), "\uE713", () => actions.Run(RadialAction.SystemControls, c), null,
                Loc.Get("Wheel.Desc.SystemControls"), nameof(RadialAction.SystemControls)),
            new RadialSlice(Loc.Get("Wheel.Slice.ClipboardHistory"), "\uE81C", () => actions.Run(RadialAction.ClipboardHistory, c), null,
                Loc.Get("Wheel.Desc.ClipboardHistory"), nameof(RadialAction.ClipboardHistory)),
            new RadialSlice(Loc.Get("Wheel.Slice.LockScreen"), "\uE72E", () => actions.Run(RadialAction.LockScreen, c), null,
                Loc.Get("Wheel.Desc.LockScreen"), nameof(RadialAction.LockScreen)),
        };
    }

    // ------------------------------------------------------------------
    // Contexto 3: Barra de título
    // ------------------------------------------------------------------
    private static RadialSlice[] TitleBarSlices(ContextInfo c, ISliceActions actions) => new[]
    {
        new RadialSlice(Loc.Get("Wheel.Slice.Maximize"), "\uE922", () => actions.Run(RadialAction.Maximize, c), null,
            Loc.Get("Wheel.Desc.Maximize"), nameof(RadialAction.Maximize)),
        new RadialSlice(Loc.Get("Wheel.Slice.SnapRight"), "\uE72A", () => actions.Run(RadialAction.SnapRight, c), null,
            Loc.Get("Wheel.Desc.SnapRight"), nameof(RadialAction.SnapRight)),
        new RadialSlice(Loc.Get("Wheel.Slice.Close"), "\uE8BB", () => actions.Run(RadialAction.Close, c), null,
            Loc.Get("Wheel.Desc.Close"), nameof(RadialAction.Close)),
        new RadialSlice(Loc.Get("Wheel.Slice.Transparency"), "\uE706", () => actions.Run(RadialAction.Transparency, c), null,
            Loc.Get("Wheel.Desc.Transparency"), nameof(RadialAction.Transparency)),
        new RadialSlice(Loc.Get("Wheel.Slice.AlwaysOnTop"), "\uE718", () => actions.Run(RadialAction.AlwaysOnTop, c), null,
            Loc.Get("Wheel.Desc.AlwaysOnTop"), nameof(RadialAction.AlwaysOnTop)),
        new RadialSlice(Loc.Get("Wheel.Slice.Minimize"), "\uE921", () => actions.Run(RadialAction.Minimize, c), null,
            Loc.Get("Wheel.Desc.Minimize"), nameof(RadialAction.Minimize)),
        new RadialSlice(Loc.Get("Wheel.Slice.SnapLeft"), "\uE72B", () => actions.Run(RadialAction.SnapLeft, c), null,
            Loc.Get("Wheel.Desc.SnapLeft"), nameof(RadialAction.SnapLeft)),
    };

    // ------------------------------------------------------------------
    // Contexto 4: Campo de texto editável
    // ------------------------------------------------------------------
    private static RadialSlice[] TextFieldSlices(ContextInfo c, ISliceActions actions) => new[]
    {
        new RadialSlice(Loc.Get("Wheel.Slice.Paste"), "\uE77F", () => actions.Run(RadialAction.Paste, c), null,
            Loc.Get("Wheel.Desc.Paste"), nameof(RadialAction.Paste)),
        new RadialSlice(Loc.Get("Wheel.Slice.Translate"), "\uE774", () => actions.Run(RadialAction.Translate, c), null,
            Loc.Get("Wheel.Desc.Translate"), nameof(RadialAction.Translate)),
        new RadialSlice(Loc.Get("Wheel.Slice.SearchWeb"), "\uE721", () => actions.Run(RadialAction.SearchWeb, c), null,
            Loc.Get("Wheel.Desc.SearchWeb"), nameof(RadialAction.SearchWeb)),
        new RadialSlice(Loc.Get("Wheel.Slice.Format"), "\uE8DD", () => actions.Run(RadialAction.Format, c), null,
            Loc.Get("Wheel.Desc.Format"), nameof(RadialAction.Format)),
        new RadialSlice(Loc.Get("Wheel.Slice.Clear"), "\uE894", () => actions.Run(RadialAction.Clear, c), null,
            Loc.Get("Wheel.Desc.Clear"), nameof(RadialAction.Clear)),
    };

    internal static string GetFormatGlyph(string format) => format.ToLowerInvariant() switch
    {
        "png" or "jpg" or "jpeg" or "webp" or "bmp" or "tiff" or "ico" or "heic" => "\uEB9F",
        "mp4" or "webm" or "mkv" or "mov" or "gif" => "\uE714",
        "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" or "opus" or "aiff" => "\uE8D6",
        "pdf" or "docx" or "odt" or "rtf" or "html" or "md" or "txt" or "epub" or
        "xlsx" or "ods" or "csv" or "pptx" or "odp" => "\uE8A5",
        _ => "\uE8AB"
    };
}
