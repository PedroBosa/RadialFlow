// ============================================================================
// RadialFlow - Modelos de contexto
// ============================================================================

using System;
using System.Collections.Generic;

namespace RadialFlow.Context;

public enum RadialContextKind
{
    /// <summary>Sem contexto específico (barra de tarefas, apps desconhecidos, falha de detecção).</summary>
    Generic,

    /// <summary>Sobre um arquivo/pasta no Explorer ou na Área de Trabalho.</summary>
    FileOrFolder,

    /// <summary>Fundo vazio da Área de Trabalho (ou de uma pasta do Explorer).</summary>
    DesktopBackground,

    /// <summary>Barra de título de qualquer janela.</summary>
    TitleBar,

    /// <summary>Campo de texto editável (Edit, RichEdit, navegador, Word...).</summary>
    TextField
}

/// <summary>Resultado da detecção, medido no ponto ONDE O RMB FOI PRESSIONADO.</summary>
/// <param name="RootWindow">HWND da janela de nível superior sob a origem.</param>
/// <param name="ProcessName">Nome do processo dono da janela (sem .exe).</param>
/// <param name="Paths">Caminhos resolvidos (apenas em FileOrFolder; pode ter vários).</param>
/// <param name="WindowTitle">Título da janela de origem; vazio se indisponível. Transitório, nunca persistido.</param>
/// <param name="WindowClass">Classe Win32 da janela de origem (ex.: "CabinetWClass"). Transitória, nunca persistida.</param>
public sealed record ContextInfo(
    RadialContextKind Kind,
    IntPtr RootWindow,
    string ProcessName,
    int OriginX,
    int OriginY,
    IReadOnlyList<string> Paths,
    string WindowTitle = "",
    string WindowClass = "")
{
    public static readonly IReadOnlyList<string> NoPaths = Array.Empty<string>();
}

/// <summary>Todas as ações que as fatias podem disparar (implementação: Módulo de ações).</summary>
public enum RadialAction
{
    // Arquivo / pasta
    Convert, Compress, CopyPath, MoveTo, SendTo, Rename,
    // Área de trabalho / genérico
    FavoriteApps, SystemControls, ClipboardHistory, LockScreen,
    // Barra de título
    SnapLeft, SnapRight, Maximize, Minimize, Close, AlwaysOnTop, Transparency,
    // Campo de texto
    Paste, Translate, Clear, SearchWeb, Format
}
