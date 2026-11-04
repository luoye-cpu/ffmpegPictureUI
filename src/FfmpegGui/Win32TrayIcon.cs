using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FfmpegGui
{
    /// <summary>
    /// Windows 原生托盘图标（Shell_NotifyIcon 直写），2026-11-04 引入。
    ///
    /// ⚠ 为什么不再用 Avalonia 的 <c>TrayIcon</c>（Windows 侧）：维护者实机（Win11 26300）实测
    ///   Avalonia 12.1.4 的 TrayIcon 无论 standalone 还是挂 <c>TrayIcon.Icons</c> 附加集合，
    ///   shell 里都**没有任何图标**（UIA 枚举任务栏+溢出区 0 命中），而其 <c>IsVisible</c> 仍自报
    ///   <c>True</c> ⇒ 旧实现把「自报可见」当「托盘真可用」⇒ 关窗被拦进托盘黑洞（窗口没了、
    ///   托盘没图标、进程残留）。本类以 <see cref="Shell_NotifyIcon"/> 的**真实返回值**为唯一真源：
    ///   <c>NIM_ADD</c> 失败（典型：提权/受限上下文被 shell 拒绝，GetLastError=5）⇒
    ///   <see cref="IsRegistered"/>=false ⇒ 调用方（App）据此把 <c>_trayAvailable</c> 置 false，
    ///   关窗走真退出，不再藏不可见进程。
    ///
    /// 线程模型：自带 STA 消息泵线程 + 隐藏消息窗口；菜单点击/左键经事件抛给调用方
    /// （事件在托盘线程触发，调用方自行 marshal 回 UI 线程）。本类不引用 Avalonia。
    /// Explorer 重启（TaskbarCreated 广播）后自动重挂图标。
    /// </summary>
    internal sealed class Win32TrayIcon : IDisposable
    {
        // ── 事件（在托盘线程触发）──
        /// <summary>左键单击图标（约定：显示主窗口）。</summary>
        public event Action? LeftClicked;
        /// <summary>菜单「显示主窗口」。</summary>
        public event Action? ShowRequested;
        /// <summary>菜单「暂停/恢复队列」（调用方自行判定当前态并切换）。</summary>
        public event Action? PauseResumeRequested;
        /// <summary>菜单「退出」。</summary>
        public event Action? ExitRequested;

        private const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2;
        private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
        private const uint WM_APP_TRAY = 0x8000; // WM_APP
        private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
        private const int WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_QUIT = 0x0012;
        private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
        private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
        private const int MENU_SHOW = 1, MENU_PAUSE = 2, MENU_EXIT = 3;

        private readonly string _tipBase;
        private readonly string _labelShow, _labelPause, _labelExit;
        private IntPtr _hwnd;
        private uint _threadId;
        private Thread? _pump;
        private int _callbackMsg = (int)(WM_APP_TRAY + 0x100);
        private uint _taskbarCreatedMsg;
        private bool _added;          // 当前 shell 里挂着图标（NIM_ADD 成功且未 NIM_DELETE）
        private string _tip = "";
        private IntPtr _hIcon;
        private bool _disposed;

        public Win32TrayIcon(string tipBase, string labelShow, string labelPause, string labelExit)
        {
            _tipBase = tipBase ?? "";
            _labelShow = labelShow; _labelPause = labelPause; _labelExit = labelExit;
        }

        /// <summary>消息窗口与托盘注册是否成功。false ⇒ 事件不会触发。</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>NIM_ADD 失败时的 Win32 错误码（诊断用；0=未尝试）。</summary>
        public int LastAddError { get; private set; }

        /// <summary>
        /// 创建消息窗口并注册托盘图标。必须在进程生命周期内只调一次；
        /// 返回 <see cref="IsRegistered"/> 即 <c>Shell_NotifyIcon(NIM_ADD)</c> 的真实结果。
        /// </summary>
        public bool Start()
        {
            if (_pump != null) throw new InvalidOperationException("Win32TrayIcon already started");
            var ready = new ManualResetEventSlim(false);
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "FfmpegGui.Win32Tray" };
            _pump.SetApartmentState(ApartmentState.STA);
            _pump.Start(ready);
            ready.Wait(5000);
            return IsRegistered;
        }

        /// <summary>图标是否当前挂在 shell 上（NIM_ADD 成功且未被收起）。</summary>
        public bool IsVisibleOnShell => _added;

        /// <summary>显示（NIM_ADD）/ 收起（NIM_DELETE）托盘图标。重复调用幂等。</summary>
        public void SetVisible(bool visible)
        {
            _wantVisible = visible;
            if (!IsRegistered || _disposed) return;
            if (visible && !_added) AddIcon();
            else if (!visible && _added) RemoveIcon();
        }

        /// <summary>更新悬浮提示文本（超长自动截断到 127 字符）。</summary>
        public void SetTooltip(string text)
        {
            _tip = text ?? "";
            if (!_added || _disposed) return;
            var nid = BuildNid();
            nid.uFlags = NIF_TIP;
            bool ok = Shell_NotifyIcon(NIM_MODIFY, ref nid);
            if (!ok) LogCompat($"[tray] win32 tooltip modify failed err={Marshal.GetLastWin32Error()}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_added) RemoveIcon(); } catch { }
            try { if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); } catch { }
            try
            {
                if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        // ── 内部 ──────────────────────────────────────────────

        private void PumpLoop(object? state)
        {
            // 托盘失败一律降级不许崩进程（契约：托盘不可用 ⇒ 关窗真退出，而不是 app 起不来）
            try
            {
                PumpLoopCore(state);
            }
            catch (Exception ex)
            {
                IsRegistered = false;
                LastAddError = -1;
                LogCompat($"[tray] win32 pump crashed {ex.GetType().Name}: {ex.Message}");
            }
            finally { ((ManualResetEventSlim)state!).Set(); }
        }

        private void PumpLoopCore(object? state)
        {
            var ready = (ManualResetEventSlim)state!;
            _threadId = GetCurrentThreadId();
            _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

            var cs = new WNDCLASSW
            {
                // 委托必须驻留在字段上：原生侧只持函数指针，GC 掉委托 = 托盘消息回崩
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(
                    _wndProcRef ??= new WndProcDelegate(WndProc)),
                lpszClassName = "FfmpegGuiTray-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                hInstance = Marshal.GetHINSTANCE(typeof(Win32TrayIcon).Module),
            };
            ushort atom = RegisterClassW(ref cs);
            if (atom == 0)
            {
                LastAddError = Marshal.GetLastWin32Error();
                ready.Set();
                return;
            }
            _hwnd = CreateWindowExW(0, cs.lpszClassName, "", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, cs.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                LastAddError = Marshal.GetLastWin32Error();
                ready.Set();
                return;
            }

            // 图标来源：从本进程 exe 提取（ApplicationIcon 真 ICO，五档位）。
            // 不走 Avalonia 的 PNG→WindowIcon→HICON 链（UiTestHost C20c 实测该链可解出 1×1）。
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
                SHDefExtractIconW(exe, 0, 0, out _hIcon, IntPtr.Zero, 0x20);
            if (_hIcon == IntPtr.Zero)
            {
                LastAddError = 0x2027; // 记一个可辨识的非零值，真因看日志
                LogCompat("[tray] win32 icon extract failed from " + exe);
                ready.Set();
                return;
            }

            IsRegistered = AddIcon();
            ready.Set();

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            // 泵退出：窗口与类随进程回收；图标已在 Dispose 里 NIM_DELETE。
            try { DestroyWindow(_hwnd); } catch { }
        }

        private bool AddIcon()
        {
            var nid = BuildNid();
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            bool ok = Shell_NotifyIcon(NIM_ADD, ref nid);
            LastAddError = ok ? 0 : Marshal.GetLastWin32Error();
            _added = ok;
            return ok;
        }

        private void RemoveIcon()
        {
            var nid = BuildNid();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }

        private NOTIFYICONDATAW BuildNid()
        {
            var tip = string.IsNullOrEmpty(_tip) ? _tipBase : _tip;
            if (tip.Length > 127) tip = tip.Substring(0, 127);
            return new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uCallbackMessage = (uint)_callbackMsg,
                hIcon = _hIcon,
                szTip = tip,
            };
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == (uint)_callbackMsg)
            {
                int m = lParam.ToInt32();
                if (m == WM_LBUTTONUP) SafeRaise(LeftClicked);
                else if (m == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                // Explorer 重启会收走全部图标 ⇒ 重挂（仅当本应可见时）
                if (IsRegistered && !_disposed && !_added && _wantVisible) AddIcon();
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private bool _wantVisible;

        private void ShowMenu()
        {
            // 经典套路：SetForegroundWindow 让菜单在外点时正确收起
            SetForegroundWindow(_hwnd);
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendMenuW(menu, MF_STRING, MENU_SHOW, _labelShow);
                AppendMenuW(menu, MF_STRING, MENU_PAUSE, _labelPause);
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                AppendMenuW(menu, MF_STRING, MENU_EXIT, _labelExit);
                if (!GetCursorPos(out var pt)) pt = new POINT { X = 0, Y = 0 };
                int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                    pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
                switch (cmd)
                {
                    case MENU_SHOW: SafeRaise(ShowRequested); break;
                    case MENU_PAUSE: SafeRaise(PauseResumeRequested); break;
                    case MENU_EXIT: SafeRaise(ExitRequested); break;
                }
                // 防御：TrackPopupMenu 后补一个空消息，吃掉 SetForegroundWindow 的副作用
                PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { DestroyMenu(menu); }
        }

        private static void SafeRaise(Action? h) { try { h?.Invoke(); } catch { } }

        private static void LogCompat(string line)
        {
            // 复用 App.LogTray 的落盘链路（line 已带 [tray] 标签，满足门禁分桶规则）
            try { App.LogTray(line); } catch { }
        }

        // ── Win32 ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX, ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _wndProcRef; // 防 GC 回收委托

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr hIconLarge, IntPtr hIconSmall, uint size);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
