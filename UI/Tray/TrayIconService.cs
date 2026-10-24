// ============================================================================
// RadialFlow - Módulo 6: Ícone na Bandeja do Sistema (NotifyIcon)
// ----------------------------------------------------------------------------
// Implementa o NotifyIcon nativo usando a API Win32 Shell_NotifyIcon.
//
// Recursos:
//  * Totalmente autônomo: gera ícone de donut (ciano ativo / cinza pausado)
//    em memória via GDI (CreateIconIndirect), sem depender de arquivos .ico
//    externos.
//  * Menu de contexto nativo (TrackPopupMenuEx):
//      - "RadialFlow" (título)
//      - "Pausar" / "Retomar"
//      - "Configurações..."
//      - "Sair"
//  * Clique duplo ou clique simples esquerdo: abre a janela de Configurações.
//  * Gerencia o estado de pausa do RadialController e atualiza o visual.
// ============================================================================

using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RadialFlow.Infrastructure;
using RadialFlow.Config;

namespace RadialFlow.UI;

public sealed class TrayIconService : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 101;
    private const uint TRAY_ICON_ID = 1;

    // IDs dos comandos do menu de contexto
    private const int CMD_TITLE = 1000;
    private const int CMD_PAUSE = 1001;
    private const int CMD_SETTINGS = 1002;
    private const int CMD_EXIT = 1003;
    private const int CMD_LOGS = 1004;

    // Mensagem registrada "TaskbarCreated" (0 = indisponível).
    private uint _taskbarCreatedMessage;

    private readonly RadialController _controller;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _iconActive = IntPtr.Zero;
    private IntPtr _iconPaused = IntPtr.Zero;
    // false = ícone compartilhado do sistema (LoadIcon): NUNCA passar para DestroyIcon.
    private bool _iconActiveOwned;
    private bool _iconPausedOwned;
    private bool _isDisposed;

    private SettingsWindow? _settingsWindow;

    public TrayIconService(RadialController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// Inicializa a janela receptora de mensagens e adiciona o ícone na barra de tarefas.
    /// Deve ser chamado a partir da thread de UI do WPF.
    /// </summary>
    public void Start()
    {
        if (_hwnd != IntPtr.Zero) return;

        // Cria uma janela oculta para receber as mensagens do Shell_NotifyIcon
        var parameters = new HwndSourceParameters("RadialFlowTrayHook")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        _hwnd = _hwndSource.Handle;

        // O Explorer avisa (broadcast "TaskbarCreated") quando a barra é recriada: crash, reinício, troca de tema.
        // Sem tratar, o ícone some para sempre. O app é ADMINISTRADOR e o Explorer não: o UIPI bloquearia a
        // mensagem por padrão, então ela precisa ser liberada para esta janela.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage != 0 && !ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero))
            AppLog.Warning("Tray", $"Não foi possível liberar TaskbarCreated no UIPI (erro {Marshal.GetLastWin32Error()}); o ícone pode não voltar se o Explorer reiniciar.");

        // Cria os ícones em memória (ciano para ativo, cinza para pausado)
        (_iconActive, _iconActiveOwned) = CreateIconOrFallback(active: true);
        (_iconPaused, _iconPausedOwned) = CreateIconOrFallback(active: false);

        AddTrayIcon();
    }

    /// <summary>Ícone donut próprio (precisa de DestroyIcon) ou, se falhar, o ícone compartilhado do sistema.</summary>
    private static (IntPtr Handle, bool Owned) CreateIconOrFallback(bool active)
    {
        IntPtr icon = CreateTrayIcon(active);
        return icon != IntPtr.Zero
            ? (icon, true)
            : (LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION), false);
    }

    private void AddTrayIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _controller.IsPaused ? _iconPaused : _iconActive,
            szTip = _controller.IsPaused ? "RadialFlow (Pausado)" : "RadialFlow"
        };

        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    /// <summary>
    /// Atualiza o ícone e tooltip da bandeja conforme o estado de pausa.
    /// </summary>
    public void UpdateState()
    {
        if (_hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID,
            uFlags = NIF_ICON | NIF_TIP,
            hIcon = _controller.IsPaused ? _iconPaused : _iconActive,
            szTip = _controller.IsPaused ? "RadialFlow (Pausado)" : "RadialFlow"
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Exceção num hook de HwndSource sobe direto para o Dispatcher: registra e segue.
        try
        {
            HandleMessage(msg, wParam, lParam, ref handled);
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray", $"Falha ao tratar a mensagem 0x{msg:X4}", ex);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void HandleMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            // A barra de tarefas foi recriada: todos os ícones de bandeja se perderam. Adiciona de novo
            // (com ícones renovados: o DPI e o tema da barra podem ter mudado junto).
            AppLog.Info("Tray", "Barra de tarefas recriada (Explorer reiniciou); recriando o ícone.");
            RefreshIcons();
            AddTrayIcon();
            handled = true;
            return;
        }

        // Troca de tema claro/escuro: o anel precisa trocar de cor, senão some na barra nova.
        if (msg == WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            AppLog.Debug("Tray", "Tema do Windows mudou; renovando o ícone da bandeja.");
            RefreshIcons();
            return;   // não marca como tratada: outros hooks da janela também podem querer a mensagem
        }

        if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            switch (eventId)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    OpenSettings();
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == WM_COMMAND)
        {
            int commandId = wParam.ToInt32() & 0xFFFF;
            switch (commandId)
            {
                case CMD_PAUSE:
                    TogglePause();
                    handled = true;
                    break;

                case CMD_SETTINGS:
                    OpenSettings();
                    handled = true;
                    break;

                case CMD_LOGS:
                    OpenLogsFolder();
                    handled = true;
                    break;

                case CMD_EXIT:
                    ExitApplication();
                    handled = true;
                    break;
            }
        }
    }

    private void ShowContextMenu()
    {
        GetCursorPos(out POINT pt);

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            // Título (desabilitado, apenas visual)
            AppendMenu(hMenu, MF_STRING | MF_GRAYED, (nuint)CMD_TITLE, "RadialFlow");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);

            // Item de pausar / retomar com checkmark se pausado
            string pauseText = _controller.IsPaused ? "Retomar" : "Pausar";
            uint pauseFlags = MF_STRING | (_controller.IsPaused ? MF_CHECKED : MF_UNCHECKED);
            AppendMenu(hMenu, pauseFlags, (nuint)CMD_PAUSE, pauseText);

            // Item de configurações
            AppendMenu(hMenu, MF_STRING, (nuint)CMD_SETTINGS, "Configurações...");
            AppendMenu(hMenu, MF_STRING, (nuint)CMD_LOGS, "Abrir pasta de logs");

            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);

            // Item de saída
            AppendMenu(hMenu, MF_STRING, (nuint)CMD_EXIT, "Sair");

            // Padrão MS KB73831: foco na janela antes de abrir menu de tray
            SetForegroundWindow(_hwnd);
            TrackPopupMenuEx(hMenu, TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private void TogglePause()
    {
        _controller.IsPaused = !_controller.IsPaused;
        UpdateState();
    }

    /// <summary>Abre a pasta dos logs no Explorer (sem elevação). Cria a pasta se ainda não existir.</summary>
    internal static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            if (!Services.UnelevatedLauncher.ShowInExplorer(AppLog.DirectoryPath))
                AppLog.Warning("Tray", "Não foi possível abrir a pasta de logs no Explorer.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Tray", "Não foi possível preparar a pasta de logs.", ex);
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;

            _settingsWindow.Activate();
            _settingsWindow.Focus();
            return;
        }

        _settingsWindow = new SettingsWindow(_controller, this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static void ExitApplication()
    {
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TRAY_ICON_ID
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _hwnd = IntPtr.Zero;
        }

        _hwndSource?.Dispose();
        _hwndSource = null;

        if (_iconActive != IntPtr.Zero)
        {
            if (_iconActiveOwned) DestroyIcon(_iconActive);
            _iconActive = IntPtr.Zero;
        }

        if (_iconPaused != IntPtr.Zero)
        {
            if (_iconPausedOwned) DestroyIcon(_iconPaused);
            _iconPaused = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------
    // Ícone da bandeja: o mesmo desenho do executável (AppIconRenderer)
    // ------------------------------------------------------------------

    /// <summary>
    /// Cria o HICON da bandeja no tamanho que o sistema usa para ícones pequenos NESTE DPI (16 px a 100%, 20 a 125%,
    /// 24 a 150%, 32 a 200%...), em vez de um 32 px fixo que o sistema esticava. A cor do anel segue o tema da
    /// barra de tarefas (claro ou escuro). Chamar na thread de UI (renderização WPF).
    /// </summary>
    /// <returns>O HICON (o chamador o destrói com DestroyIcon) ou IntPtr.Zero se falhar.</returns>
    private static IntPtr CreateTrayIcon(bool active)
    {
        try
        {
            int size = GetSmallIconSize();
            bool lightBar = RadialTheme.ReadTaskbarIsLight();

            BitmapSource bitmap = AppIconRenderer.Render(size, IconVariant.Glyph, paused: !active, lightBackground: lightBar);
            return CreateHicon(AppIconRenderer.GetPixels(bitmap), size);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Tray", "Falha ao criar o ícone da bandeja.", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>Tamanho do ícone pequeno do sistema no DPI atual; 16 se o sistema não informar.</summary>
    internal static int GetSmallIconSize()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        return size is >= 8 and <= 256 ? size : 16;
    }

    /// <summary>
    /// HICON de 32 bpp a partir de pixels BGRA (alfa NÃO pré-multiplicado, de cima para baixo), como num .ico.
    /// </summary>
    internal static IntPtr CreateHicon(byte[] bgra, int size)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length < size * size * 4) throw new ArgumentException("Pixels insuficientes para o tamanho informado.", nameof(bgra));

        // A máscara AND não recorta nada (o alfa vem dos pixels), mas precisa existir; linhas alinhadas em 16 bits.
        byte[] mask = new byte[(size + 15) / 16 * 2 * size];

        IntPtr color = IntPtr.Zero, maskBitmap = IntPtr.Zero;
        GCHandle colorPin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        GCHandle maskPin = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            color = CreateBitmap(size, size, 1, 32, colorPin.AddrOfPinnedObject());
            maskBitmap = CreateBitmap(size, size, 1, 1, maskPin.AddrOfPinnedObject());
            if (color == IntPtr.Zero || maskBitmap == IntPtr.Zero) return IntPtr.Zero;

            var info = new ICONINFO { fIcon = true, xHotspot = 0, yHotspot = 0, hbmMask = maskBitmap, hbmColor = color };
            return CreateIconIndirect(ref info);   // IntPtr.Zero se falhar; o chamador usa o fallback
        }
        finally
        {
            // O Windows copia os bitmaps para dentro do ícone: os originais são nossos para liberar.
            if (color != IntPtr.Zero) DeleteObject(color);
            if (maskBitmap != IntPtr.Zero) DeleteObject(maskBitmap);
            maskPin.Free();
            colorPin.Free();
        }
    }

    /// <summary>
    /// Recria os dois ícones (ativo e pausado) e troca o da bandeja. Os antigos só são destruídos DEPOIS de o Shell
    /// passar a usar os novos (destruir um HICON em uso o deixaria em branco).
    /// </summary>
    private void RefreshIcons()
    {
        (IntPtr oldActive, bool activeOwned) = (_iconActive, _iconActiveOwned);
        (IntPtr oldPaused, bool pausedOwned) = (_iconPaused, _iconPausedOwned);

        (_iconActive, _iconActiveOwned) = CreateIconOrFallback(active: true);
        (_iconPaused, _iconPausedOwned) = CreateIconOrFallback(active: false);
        UpdateState();

        if (activeOwned && oldActive != IntPtr.Zero) DestroyIcon(oldActive);
        if (pausedOwned && oldPaused != IntPtr.Zero) DestroyIcon(oldPaused);
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    // ------------------------------------------------------------------
    // P/Invoke & Constantes Win32
    // ------------------------------------------------------------------

    private const uint MSGFLT_ALLOW = 1;   // ChangeWindowMessageFilterEx: libera a mensagem para esta janela

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr filterStruct);

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int WM_NULL = 0x0000;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
