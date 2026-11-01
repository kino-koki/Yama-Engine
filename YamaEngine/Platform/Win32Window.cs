// ==================================================================================================
//  Win32Window.cs — yama引擎 Win32 原生窗口平台层
//
//  命名空间: YamaEngine.Platform
//  依赖:     仅 P/Invoke kernel32 / user32 / dwmapi
//
//  功能:
//    1. RegisterClassEx + CreateWindowEx 创建 Win32 窗口（不依赖 WinUI / SDL）
//    2. 原生消息循环（TranslateMessage / DispatchMessage）
//    3. 窗口样式: WS_OVERLAPPEDWINDOW，可调整大小
//    4. 客户区尺寸获取
//    5. 窗口过程: 处理 WM_CLOSE / WM_SIZE / WM_KEYDOWN / WM_MOUSEMOVE / WM_LBUTTONDOWN 等关键消息
//    6. 提供 Width / Height / Handle / IsOpen 属性
//    7. 事件: OnResize, OnClose, OnKeyDown, OnKeyUp, OnMouseMove, OnMouseDown, OnMouseUp
//    8. 高 DPI 支持（SetProcessDpiAwarenessContext）
//    9. 垂直同步标志
// ==================================================================================================

using System;
using System.Runtime.InteropServices;

namespace YamaEngine.Platform
{
    /// <summary>
    /// Win32 原生窗口。通过 P/Invoke 直接调用 user32 / kernel32 创建窗口，
    /// 不依赖 WinUI、SDL 或任何第三方窗口框架。
    /// </summary>
    public sealed class Win32Window : IDisposable
    {
        // ──────────────────────────────────────────────────────────
        // Win32 常量
        // ──────────────────────────────────────────────────────────

        private const int CS_HREDRAW = 0x0002;
        private const int CS_VREDRAW = 0x0001;
        private const int WM_DESTROY = 0x0002;
        private const int WM_CLOSE = 0x0010;
        private const int WM_SIZE = 0x0005;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_PAINT = 0x000F;
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_DPICHANGED = 0x02E0;
        private const int SIZE_RESTORED = 0;
        private const int SIZE_MINIMIZED = 1;
        private const int SIZE_MAXIMIZED = 2;
        private const int WA_INACTIVE = 0;
        private const int WA_ACTIVE = 1;
        private const int WA_CLICKACTIVE = 2;

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const int SW_SHOW = 5;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_FRAMECHANGED = 0x0020;

        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        // ──────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────

        private IntPtr _hwnd = IntPtr.Zero;
        private WNDPROC? _wndProcDelegate;
        private bool _isOpen;
        private int _clientWidth;
        private int _clientHeight;
        private string _title;
        private uint _style;
        private uint _exStyle;
        private bool _vSync = true;

        // ──────────────────────────────────────────────────────────
        // 属性
        // ──────────────────────────────────────────────────────────

        /// <summary>原生 Win32 窗口句柄 (HWND)</summary>
        public IntPtr Handle => _hwnd;

        /// <summary>窗口客户区宽度</summary>
        public int Width => _clientWidth;

        /// <summary>窗口客户区高度</summary>
        public int Height => _clientHeight;

        /// <summary>窗口是否仍然打开</summary>
        public bool IsOpen => _isOpen;

        /// <summary>窗口标题</summary>
        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                if (_hwnd != IntPtr.Zero)
                    SetWindowText(_hwnd, value);
            }
        }

        /// <summary>垂直同步标志</summary>
        public bool VSync
        {
            get => _vSync;
            set => _vSync = value;
        }

        // ──────────────────────────────────────────────────────────
        // 事件
        // ──────────────────────────────────────────────────────────

        /// <summary>窗口大小改变时触发 (width, height)</summary>
        public event Action<int, int>? OnResize;

        /// <summary>窗口即将关闭时触发（返回 true 阻止关闭，返回 false 允许关闭）</summary>
        public event Func<bool>? OnClose;

        /// <summary>按键按下</summary>
        public event Action<int>? OnKeyDown;

        /// <summary>按键释放</summary>
        public event Action<int>? OnKeyUp;

        /// <summary>鼠标移动 (x, y)</summary>
        public event Action<int, int>? OnMouseMove;

        /// <summary>鼠标按下 (button)</summary>
        public event Action<int>? OnMouseDown;

        /// <summary>鼠标释放 (button)</summary>
        public event Action<int>? OnMouseUp;

        /// <summary>鼠标滚轮 (delta)</summary>
        public event Action<int>? OnMouseWheel;

        // ──────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────

        public Win32Window(string title = "YamaEngine", int width = 1280, int height = 720)
        {
            _title = title;
            _clientWidth = width;
            _clientHeight = height;
            _style = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
            _exStyle = WS_EX_APPWINDOW;
        }

        // ──────────────────────────────────────────────────────────
        // 初始化 / 创建窗口
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 创建 Win32 窗口。在调用 Run() 之前调用此方法。
        /// </summary>
        public void Create()
        {
            // 高 DPI 支持
            SetHighDpiAwareness();

            // 注册窗口类
            _wndProcDelegate = WndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = _wndProcDelegate,
                hInstance = GetModuleHandle(IntPtr.Zero),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = IntPtr.Zero, // 不需要背景刷 — D3D12 全覆盖渲染
                lpszClassName = "YamaEngineWindowClass",
                hIcon = IntPtr.Zero,
                hIconSm = IntPtr.Zero
            };
            ushort atom = RegisterClassEx(ref wc);
            if (atom == 0)
                throw new InvalidOperationException($"Failed to register window class. Error: {Marshal.GetLastWin32Error()}");

            // 调整窗口尺寸使客户区匹配指定尺寸
            int adjustedWidth = _clientWidth;
            int adjustedHeight = _clientHeight;
            AdjustWindowRectExForDpi(ref adjustedWidth, ref adjustedHeight, _style, _exStyle, 96);

            // 创建窗口
            _hwnd = CreateWindowEx(
                _exStyle,
                "YamaEngineWindowClass",
                _title,
                _style,
                CW_USEDEFAULT, CW_USEDEFAULT,
                adjustedWidth, adjustedHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(IntPtr.Zero),
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to create window. Error: {Marshal.GetLastWin32Error()}");

            // 获取真实客户区尺寸
            GetClientRect(_hwnd, out var rect);
            _clientWidth = rect.right - rect.left;
            _clientHeight = rect.bottom - rect.top;

            _isOpen = true;

            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
        }

        // ──────────────────────────────────────────────────────────
        // 消息循环
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 泵出所有待处理的 Win32 消息。每帧调用一次。
        /// 返回 false 表示窗口应该关闭。
        /// </summary>
        public bool PumpMessages()
        {
            if (!_isOpen) return false;

            MSG msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_CLOSE)
                {
                    // 允许外部代码决定是否关闭
                    if (OnClose != null && OnClose.Invoke())
                    {
                        _isOpen = false;
                        return false;
                    }
                    // OnClose 为 null 或返回 false → 默认关闭
                    _isOpen = false;
                    return false;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            return _isOpen;
        }

        // ──────────────────────────────────────────────────────────
        // 窗口过程
        // ──────────────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_SIZE:
                {
                    int w = LOWORD(lParam);
                    int h = HIWORD(lParam);
                    if (w > 0 && h > 0)
                    {
                        _clientWidth = w;
                        _clientHeight = h;
                        OnResize?.Invoke(w, h);
                    }
                    return IntPtr.Zero;
                }

                case WM_CLOSE:
                    // 在 PumpMessages 中处理
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    _isOpen = false;
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    OnKeyDown?.Invoke((int)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case WM_KEYUP:
                case WM_SYSKEYUP:
                    OnKeyUp?.Invoke((int)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                {
                    int x = LOWORD(lParam);
                    int y = HIWORD(lParam);
                    OnMouseMove?.Invoke(x, y);
                    return IntPtr.Zero;
                }

                case WM_LBUTTONDOWN:
                    OnMouseDown?.Invoke(0); // 左键 = 0
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    OnMouseUp?.Invoke(0);
                    return IntPtr.Zero;

                case WM_RBUTTONDOWN:
                    OnMouseDown?.Invoke(1); // 右键 = 1
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    OnMouseUp?.Invoke(1);
                    return IntPtr.Zero;

                case WM_MBUTTONDOWN:
                    OnMouseDown?.Invoke(2); // 中键 = 2
                    return IntPtr.Zero;

                case WM_MBUTTONUP:
                    OnMouseUp?.Invoke(2);
                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                {
                    int delta = (short)HIWORD(wParam);
                    OnMouseWheel?.Invoke(delta);
                    return IntPtr.Zero;
                }

                case WM_DPICHANGED:
                {
                    // DPI 变更时自动调整窗口尺寸
                    var newRect = Marshal.PtrToStructure<RECT>(lParam);
                    SetWindowPos(hwnd, IntPtr.Zero,
                        newRect.left, newRect.top,
                        newRect.right - newRect.left,
                        newRect.bottom - newRect.top,
                        SWP_NOZORDER | SWP_FRAMECHANGED);
                    return IntPtr.Zero;
                }
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // ──────────────────────────────────────────────────────────
        // 工具方法
        // ──────────────────────────────────────────────────────────

        private void SetHighDpiAwareness()
        {
            try
            {
                // Windows 10 1703+ : SetProcessDpiAwarenessContext
                if (SetProcessDpiAwarenessContext(
                        new IntPtr(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)))
                    return;

                // 回退: SetProcessDpiAwareness (Windows 8.1+)
                SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch
            {
                // 低版本 Windows 忽略
            }
        }

        private void AdjustWindowRectExForDpi(ref int width, ref int height, uint style, uint exStyle, uint dpi)
        {
            // 使用 AdjustWindowRectEx 计算包含边框的窗口尺寸
            var rect = new RECT { left = 0, top = 0, right = width, bottom = height };
            AdjustWindowRectEx(ref rect, (int)style, false, (int)exStyle);
            width = rect.right - rect.left;
            height = rect.bottom - rect.top;
        }

        // ──────────────────────────────────────────────────────────
        // 清理
        // ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            _isOpen = false;
        }

        // ──────────────────────────────────────────────────────────
        // 辅助
        // ──────────────────────────────────────────────────────────

        private static int LOWORD(IntPtr value) => (short)(value.ToInt64() & 0xFFFF);
        private static int HIWORD(IntPtr value) => (short)((value.ToInt64() >> 16) & 0xFFFF);

        // ──────────────────────────────────────────────────────────
        // Win32 结构体
        // ──────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WNDPROC lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        private delegate IntPtr WNDPROC(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ──────────────────────────────────────────────────────────
        // P/Invoke — kernel32
        // ──────────────────────────────────────────────────────────

        private const int IDC_ARROW = 32512;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

        // ──────────────────────────────────────────────────────────
        // P/Invoke — user32
        // ──────────────────────────────────────────────────────────

        private const uint PM_REMOVE = 0x0001;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, [MarshalAs(UnmanagedType.LPStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPStr)] string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowText(IntPtr hWnd, [MarshalAs(UnmanagedType.LPStr)] string lpString);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AdjustWindowRectEx(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        // ──────────────────────────────────────────────────────────
        // P/Invoke — High DPI (user32 / shcore)
        // ──────────────────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int awareness);
    }
}
