// ==================================================================================================
//  SDL2Window.cs — 山雨引擎 SDL2 跨平台窗口系统
//
//  命名空间: ShanyuEngine.Platform
//  依赖:     SDL2 (P/Invoke), System.Runtime.InteropServices
//
//  功能:
//    1. SDL2 P/Invoke 声明（SDL_Init / SDL_CreateWindow / SDL_GetWindowSize / SDL_PollEvent 等）
//    2. SDL2Window：真正的跨平台窗口实现（Windows/Linux/Android）
//    3. OpenGL ES 3.2 / Vulkan surface 创建
//    4. 触摸输入（Android 多指触摸事件）
//    5. 手柄输入（SDL_GameControllerAPI）
//    6. 窗口事件：RESIZE / CLOSE / FOCUS_GAINED / FOCUS_LOST / MOUSE_MOVE / KEY_DOWN / TOUCH
//    7. 高 DPI 支持
//    8. 全屏切换
//    9. 多窗口支持
//   10. SDL2InputManager：跨平台输入管理（键盘/鼠标/手柄/触摸统一API）
//   11. 手柄死区校准 & 触摸手势识别（Tap/Swipe/Pinch/LongPress）
//   12. WindowPlatform 枚举 & PlatformDetect 运行时平台检测
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Platform
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: WindowPlatform 枚举 — 支持的平台列表
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 窗口平台枚举，描述引擎可运行的目标平台。
    /// </summary>
    public enum WindowPlatform
    {
        Windows,
        Linux,
        Android,
        HarmonyOS,
        macOS,
        iOS,
        Web
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: PlatformDetect — 运行时平台检测
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 运行时平台检测器。通过环境变量、操作系统特征和 SDL 平台信息
    /// 自动检测当前运行平台。
    /// </summary>
    unsafe public static class PlatformDetect
    {
        private static WindowPlatform? _cachedPlatform;

        /// <summary>当前运行平台（首次调用后缓存）。</summary>
        public static WindowPlatform CurrentPlatform
        {
            get
            {
                if (_cachedPlatform.HasValue)
                    return _cachedPlatform.Value;

                _cachedPlatform = Detect();
                return _cachedPlatform.Value;
            }
        }

        private static WindowPlatform Detect()
        {
            var platform = Environment.OSVersion.Platform;

            // .NET 运行时平台检测
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowPlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // 检测 Android（通过环境变量或系统属性）
                string? androidRoot = Environment.GetEnvironmentVariable("ANDROID_ROOT");
                if (!string.IsNullOrEmpty(androidRoot))
                    return WindowPlatform.Android;
                return WindowPlatform.Linux;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // 检测 iOS（通常 .NET 不直接在 iOS 上以桌面方式运行）
                string? iosSim = Environment.GetEnvironmentVariable("SIMULATOR_HOST_HOME");
                if (!string.IsNullOrEmpty(iosSim))
                    return WindowPlatform.iOS;
                return WindowPlatform.macOS;
            }

            // 检测 HarmonyOS
            string? harmonyVersion = Environment.GetEnvironmentVariable("HARMONYOS_VERSION");
            if (!string.IsNullOrEmpty(harmonyVersion))
                return WindowPlatform.HarmonyOS;

            // 检测 WebAssembly
            if (RuntimeInformation.OSArchitecture == Architecture.Wasm)
                return WindowPlatform.Web;

            return WindowPlatform.Windows; // 默认回退
        }

        /// <summary>是否为移动平台。</summary>
        public static bool IsMobile => CurrentPlatform is WindowPlatform.Android or WindowPlatform.iOS;
        /// <summary>是否为桌面平台。</summary>
        public static bool IsDesktop => CurrentPlatform is WindowPlatform.Windows or WindowPlatform.Linux or WindowPlatform.macOS;
        /// <summary>是否为嵌入式/鸿蒙平台。</summary>
        public static bool IsEmbedded => CurrentPlatform == WindowPlatform.HarmonyOS;
        /// <summary>是否为 Web 平台。</summary>
        public static bool IsWeb => CurrentPlatform == WindowPlatform.Web;

        /// <summary>重置缓存（用于测试或平台切换场景）。</summary>
        public static void ResetCache() => _cachedPlatform = null;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: SDL2 P/Invoke 声明
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SDL2 原生函数 P/Invoke 绑定。
    /// 覆盖窗口管理、事件处理、输入设备、视频显示等核心 API。
    /// </summary>
    unsafe internal static class SDL2Native
    {
        private const string SDL2Lib =
#if WINDOWS
            "SDL2.dll";
#elif LINUX
            "libSDL2-2.0.so.0";
#elif ANDROID
            "libSDL2.so";
#elif MACOS
            "libSDL2-2.0.0.dylib";
#else
            "SDL2";
#endif

        // ─────────────────────────── 初始化与退出 ───────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_Init(uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_Quit();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_InitSubSystem(uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_WasInit(uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern string SDL_GetError();

        // ─────────────────────────── 窗口管理 ───────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_CreateWindow(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            int x, int y, int w, int h, uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetWindowTitle(IntPtr window,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GetWindowSize(IntPtr window, out int w, out int h);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetWindowSize(IntPtr window, int w, int h);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetWindowPosition(IntPtr window, int x, int y);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GetWindowPosition(IntPtr window, out int x, out int y);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GetWindowDrawableSize(IntPtr window, out int w, out int h);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_ShowWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_HideWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_SetWindowFullscreen(IntPtr window, uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetWindowFlags(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetWindowSurface(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_MinimizeWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_MaximizeWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_RestoreWindow(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetWindowBordered(IntPtr window, int bordered);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetWindowResizable(IntPtr window, int resizable);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetWindowDisplayIndex(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetDisplayDPI(int displayIndex, out float ddpi, out float hdpi, out float vdpi);

        // ─────────────────────────── Vulkan 支持 ────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_Vulkan_CreateSurface(IntPtr window, IntPtr instance, out ulong surface);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_Vulkan_GetDrawableSize(IntPtr window, out int w, out int h);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_Vulkan_GetInstanceExtensions(IntPtr window, out uint count, [Out] IntPtr[] names);

        // ─────────────────────────── OpenGL 支持 ────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GL_CreateContext(IntPtr window);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GL_MakeCurrent(IntPtr window, IntPtr context);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GL_DeleteContext(IntPtr context);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GL_SetAttribute(int attr, int value);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GL_GetAttribute(int attr, out int value);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GL_SetSwapInterval(int interval);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GL_SwapWindow(IntPtr window);

        // ─────────────────────────── 事件处理 ───────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_PollEvent(out SDLEvent e);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_WaitEvent(out SDLEvent e);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_WaitEventTimeout(out SDLEvent e, int timeoutMs);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_PushEvent(ref SDLEvent e);

        // ─────────────────────────── 键盘 / 鼠标 ────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint8_t* SDL_GetKeyboardState(out int numkeys);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetMouseState(out int x, out int y);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetRelativeMouseState(out int x, out int y);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_SetRelativeMouseMode(int enabled);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_WarpMouseInWindow(IntPtr window, int x, int y);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_CaptureMouse(int enabled);

        // ─────────────────────────── 触摸输入 ───────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int16_t SDL_GetTouchFingerId(SDLTouchFinger* finger);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float SDL_GetTouchFingerX(SDLTouchFinger* finger);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float SDL_GetTouchFingerY(SDLTouchFinger* finger);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float SDL_GetTouchFingerPressure(SDLTouchFinger* finger);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumTouchDevices();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumTouchFingers(long touchId);

        // ─────────────────────────── 手柄输入 ───────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_InitSubSystem(uint flags);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_NumJoysticks();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_IsGameController(int joystickIndex);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GameControllerOpen(int joystickIndex);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GameControllerClose(IntPtr gamecontroller);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPUTF8Str)]
        public static extern string SDL_GameControllerName(IntPtr gamecontroller);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GameControllerGetAxis(IntPtr gamecontroller, int axis);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte SDL_GameControllerGetButton(IntPtr gamecontroller, int button);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GameControllerRumble(IntPtr gamecontroller, ushort lowFreqRumble, ushort highFreqRumble, uint durationMs);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_GameControllerSetAxisDeadzone(IntPtr gamecontroller, short deadzone);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GameControllerAddMappingsFromFile(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        // ─────────────────────────── 平台信息 ───────────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern string SDL_GetPlatform();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumVideoDisplays();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumDisplayModes(int displayIndex);

        // ─────────────────────────── 延迟 & 工具 ───────────────────────────
        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetTicks();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_Delay(uint ms);

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SDL_GetPerformanceCounter();

        [DllImport(SDL2Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SDL_GetPerformanceFrequency();

        // ─────────────────────────── 常量 ───────────────────────────────────
        public const uint INIT_VIDEO       = 0x00000020;
        public const uint INIT_AUDIO       = 0x00000010;
        public const uint INIT_GAMECONTROLLER = 0x00002000;
        public const uint INIT_HAPTIC      = 0x00001000;
        public const uint INIT_EVENTS      = 0x00000004;
        public const uint INIT_JOYSTICK    = 0x00000200;
        public const uint INIT_TIMER       = 0x00000001;
        public const uint INIT_EVERYTHING  = 0x0000FFFF;

        public const uint WINDOW_SHOWN              = 0x00000004;
        public const uint WINDOW_HIDDEN             = 0x00000008;
        public const uint WINDOW_BORDERLESS         = 0x00000001;
        public const uint WINDOW_RESIZABLE          = 0x00000020;
        public const uint WINDOW_MINIMIZED          = 0x00000040;
        public const uint WINDOW_MAXIMIZED          = 0x00000080;
        public const uint WINDOW_FULLSCREEN         = 0x00001000;
        public const uint WINDOW_FULLSCREEN_DESKTOP = 0x00001000 | 0x00001000;
        public const uint WINDOW_OPENGL             = 0x00000002;
        public const uint WINDOW_VULKAN             = 0x10000000;
        public const uint WINDOW_ALLOW_HIGHDPI       = 0x00002000;
        public const uint WINDOW_INPUT_FOCUS        = 0x00000200;
        public const uint WINDOW_MOUSE_FOCUS        = 0x00000400;
        public const uint WINDOW_FOREIGN            = 0x00000800;

        public const int GL_CONTEXT_MAJOR_VERSION   = 0x0001;
        public const int GL_CONTEXT_MINOR_VERSION   = 0x0002;
        public const int GL_CONTEXT_PROFILE_MASK    = 0x0003;
        public const int GL_CONTEXT_PROFILE_CORE    = 0x0001;
        public const int GL_CONTEXT_PROFILE_ES      = 0x0004;

        public const int FALSE = 0;
        public const int TRUE  = 1;

        // ─────────────────────────── 事件类型 ───────────────────────────────
        public const uint QUIT                    = 0x100;
        public const uint WINDOWEVENT              = 0x200;
        public const uint KEYDOWN                  = 0x300;
        public const uint KEYUP                    = 0x301;
        public const uint TEXTINPUT                = 0x303;
        public const uint MOUSEMOTION              = 0x400;
        public const uint MOUSEBUTTONDOWN          = 0x401;
        public const uint MOUSEBUTTONUP            = 0x402;
        public const uint MOUSEWHEEL               = 0x403;
        public const uint FINGERDOWN               = 0x700;
        public const uint FINGERUP                 = 0x701;
        public const uint FINGERMOTION             = 0x702;
        public const uint CONTROLLERAXISMOTION      = 0x650;
        public const uint CONTROLLERBUTTONDOWN      = 0x651;
        public const uint CONTROLLERBUTTONUP        = 0x652;
        public const uint CONTROLLERDEVICEADDED     = 0x660;
        public const uint CONTROLLERDEVICEREMOVED   = 0x661;
        public const uint DROPFILE                 = 0x1000;

        // 窗口事件子类型
        public const byte WINDOWEVENT_SHOWN         = 1;
        public const byte WINDOWEVENT_HIDDEN        = 2;
        public const byte WINDOWEVENT_EXPOSED        = 3;
        public const byte WINDOWEVENT_MOVED          = 4;
        public const byte WINDOWEVENT_RESIZED        = 5;
        public const byte WINDOWEVENT_SIZE_CHANGED   = 6;
        public const byte WINDOWEVENT_MINIMIZED     = 7;
        public const byte WINDOWEVENT_MAXIMIZED     = 8;
        public const byte WINDOWEVENT_RESTORED      = 9;
        public const byte WINDOWEVENT_ENTER          = 10;
        public const byte WINDOWEVENT_LEAVE          = 11;
        public const byte WINDOWEVENT_FOCUS_GAINED   = 12;
        public const byte WINDOWEVENT_FOCUS_LOST     = 13;
        public const byte WINDOWEVENT_CLOSE          = 14;
        public const byte WINDOWEVENT_TAKE_FOCUS    = 15;

        // 键盘修饰键
        public const uint KMOD_NONE   = 0x0000;
        public const uint KMOD_LSHIFT = 0x0001;
        public const uint KMOD_RSHIFT = 0x0002;
        public const uint KMOD_LCTRL  = 0x0040;
        public const uint KMOD_RCTRL  = 0x0080;
        public const uint KMOD_LALT   = 0x0100;
        public const uint KMOD_RALT   = 0x0200;
        public const uint KMOD_LGUI   = 0x0400;
        public const uint KMOD_RGUI   = 0x0800;

        // 手柄轴
        public const int CONTROLLER_AXIS_LEFTX        = 0;
        public const int CONTROLLER_AXIS_LEFTY        = 1;
        public const int CONTROLLER_AXIS_RIGHTX       = 2;
        public const int CONTROLLER_AXIS_RIGHTY       = 3;
        public const int CONTROLLER_AXIS_TRIGGERLEFT  = 4;
        public const int CONTROLLER_AXIS_TRIGGERRIGHT = 5;

        // 手柄按钮
        public const int CONTROLLER_BUTTON_A             = 0;
        public const int CONTROLLER_BUTTON_B             = 1;
        public const int CONTROLLER_BUTTON_X             = 2;
        public const int CONTROLLER_BUTTON_Y             = 3;
        public const int CONTROLLER_BUTTON_BACK          = 4;
        public const int CONTROLLER_BUTTON_GUIDE         = 5;
        public const int CONTROLLER_BUTTON_START         = 6;
        public const int CONTROLLER_BUTTON_LEFTSTICK     = 7;
        public const int CONTROLLER_BUTTON_RIGHTSTICK    = 8;
        public const int CONTROLLER_BUTTON_LEFTSHOULDER  = 9;
        public const int CONTROLLER_BUTTON_RIGHTSHOULDER = 10;
        public const int CONTROLLER_BUTTON_DPAD_UP       = 11;
        public const int CONTROLLER_BUTTON_DPAD_DOWN     = 12;
        public const int CONTROLLER_BUTTON_DPAD_LEFT     = 13;
        public const int CONTROLLER_BUTTON_DPAD_RIGHT    = 14;

        // 鼠标按钮
        public const byte BUTTON_LEFT   = 1;
        public const byte BUTTON_MIDDLE = 2;
        public const byte BUTTON_RIGHT  = 3;

        // 窗口位置常量
        public const int WINDOWPOS_CENTERED = 0x2FFF0000;
        public const int WINDOWPOS_UNDEFINED = 0x1FFF0000;

        // ─────────────────────────── 全屏切换标志 ───────────────────────────
        public const uint FULLSCREEN_OFF           = 0x00000000;
        public const uint FULLSCREEN_WINDOWED       = 0x00000001;
        public const uint FULLSCREEN_FULLSCREEN     = WINDOW_FULLSCREEN;
        public const uint FULLSCREEN_FULLSCREEN_DESKTOP = 0x00001001;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: SDL2 事件结构体定义
    // ════════════════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SDLEvent
    {
        public uint Type;
        public uint Timestamp;
        private fixed byte _padding[48]; // 足够容纳最大的 SDL 事件联合体
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLWindowEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public byte Event;
        public byte Padding1, Padding2, Padding3;
        public int Data1;
        public int Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLKeyboardEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public byte State;
        public byte Repeat;
        public byte Padding2, Padding3;
        public uint KeyCode;
        public uint Scancode;
        public uint Mod;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLMouseMotionEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public uint Which;
        public uint State;
        public int X;
        public int Y;
        public int RelX;
        public int RelY;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLMouseButtonEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public uint Which;
        public byte Button;
        public byte State;
        public byte Clicks;
        public byte Padding;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLMouseWheelEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public uint Which;
        public int X;
        public int Y;
        public uint Direction;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLTouchFingerEvent
    {
        public uint Type;
        public uint Timestamp;
        public long TouchID;
        public long FingerID;
        public float X;
        public float Y;
        public float DX;
        public float DY;
        public float Pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLControllerAxisEvent
    {
        public uint Type;
        public uint Timestamp;
        public int Which;
        public byte Axis;
        public byte Padding1, Padding2, Padding3;
        public short Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLControllerButtonEvent
    {
        public uint Type;
        public uint Timestamp;
        public int Which;
        public byte Button;
        public byte State;
        public byte Padding1, Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLControllerDeviceEvent
    {
        public uint Type;
        public uint Timestamp;
        public int Which;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLTextInputEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public fixed byte Text[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLDropEvent
    {
        public uint Type;
        public uint Timestamp;
        public uint WindowID;
        public IntPtr File;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct SDLTouchFinger
    {
        public long Id;
        public float X;
        public float Y;
        public float Pressure;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 窗口事件参数类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>窗口调整大小事件参数。</summary>
    unsafe public sealed class WindowResizeEventArgs : EventArgs
    {
        public int Width { get; }
        public int Height { get; }

        public WindowResizeEventArgs(int w, int h) { Width = w; Height = h; }
    }

    /// <summary>键盘事件参数。</summary>
    unsafe public sealed class KeyEventArgs : EventArgs
    {
        public uint KeyCode { get; }
        public uint Scancode { get; }
        public bool IsRepeat { get; }
        public bool IsDown { get; }
        public uint Modifiers { get; }

        public KeyEventArgs(uint keyCode, uint scancode, bool repeat, bool down, uint mod)
        {
            KeyCode = keyCode; Scancode = scancode; IsRepeat = repeat; IsDown = down; Modifiers = mod;
        }
    }

    /// <summary>鼠标移动事件参数。</summary>
    unsafe public sealed class MouseMoveEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }
        public int DeltaX { get; }
        public int DeltaY { get; }

        public MouseMoveEventArgs(int x, int y, int dx, int dy) { X = x; Y = y; DeltaX = dx; DeltaY = dy; }
    }

    /// <summary>鼠标按钮事件参数。</summary>
    unsafe public sealed class MouseButtonEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }
        public byte Button { get; }
        public bool IsDown { get; }
        public byte Clicks { get; }

        public MouseButtonEventArgs(int x, int y, byte button, bool down, byte clicks)
        {
            X = x; Y = y; Button = button; IsDown = down; Clicks = clicks;
        }
    }

    /// <summary>鼠标滚轮事件参数。</summary>
    unsafe public sealed class MouseWheelEventArgs : EventArgs
    {
        public int ScrollX { get; }
        public int ScrollY { get; }

        public MouseWheelEventArgs(int x, int y) { ScrollX = x; ScrollY = y; }
    }

    /// <summary>触摸手指事件参数。</summary>
    unsafe public sealed class TouchEventArgs : EventArgs
    {
        public long FingerId { get; }
        public float X { get; }       // 归一化 0.0 ~ 1.0
        public float Y { get; }
        public float DX { get; }
        public float DY { get; }
        public float Pressure { get; } // 0.0 ~ 1.0

        public TouchEventArgs(long fid, float x, float y, float dx, float dy, float pressure)
        {
            FingerId = fid; X = x; Y = y; DX = dx; DY = dy; Pressure = pressure;
        }
    }

    /// <summary>文件拖放事件参数。</summary>
    unsafe public sealed class DropFileEventArgs : EventArgs
    {
        public string FilePath { get; }

        public DropFileEventArgs(string path) { FilePath = path; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 触摸手势识别器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 触摸手势类型。
    /// </summary>
    public enum TouchGestureType
    {
        None,
        Tap,
        DoubleTap,
        LongPress,
        SwipeUp,
        SwipeDown,
        SwipeLeft,
        SwipeRight,
        PinchIn,
        PinchOut
    }

    /// <summary>
    /// 触摸手势事件参数。
    /// </summary>
    unsafe public sealed class TouchGestureEventArgs : EventArgs
    {
        public TouchGestureType Gesture { get; }
        public float X { get; }
        public float Y { get; }
        public float Distance { get; }   // 滑动距离 / 捏合缩放因子
        public float Velocity { get; }  // 滑动速度

        public TouchGestureEventArgs(TouchGestureType g, float x, float y, float dist, float vel)
        {
            Gesture = g; X = x; Y = y; Distance = dist; Velocity = vel;
        }
    }

    /// <summary>
    /// 触摸手势识别器。支持 Tap / DoubleTap / LongPress / Swipe(四方向) / Pinch(捏合)。
    /// </summary>
    unsafe public sealed class TouchGestureRecognizer
    {
        // ────────────── 配置 ──────────────
        private const float TapMaxDistance = 20.0f;        // Tap 判定最大移动距离（像素）
        private const float TapMaxTime = 0.3f;             // Tap 最大按下时间（秒）
        private const float DoubleTapInterval = 0.3f;     // 双击最大间隔
        private const float LongPressTime = 0.5f;          // 长按触发时间
        private const float SwipeMinDistance = 50.0f;      // 滑动最小距离
        private const float SwipeMaxTime = 0.5f;           // 滑动最大时间
        private const float SwipeMinVelocity = 100.0f;    // 滑动最小速度

        // ────────────── 追踪数据 ──────────────
        private readonly Dictionary<long, TouchFingerTracker> _activeFingers = new();
        private TouchFingerTracker? _lastTap;
        private float _lastTapTime;
        private float _windowWidth;
        private float _windowHeight;

        /// <summary>手势识别事件。</summary>
        public event EventHandler<TouchGestureEventArgs>? OnGesture;

        /// <summary>更新窗口尺寸（用于坐标归一化）。</summary>
        public void SetWindowSize(int w, int h) { _windowWidth = w; _windowHeight = h; }

        /// <summary>传入新的触摸事件。</summary>
        public void OnTouchEvent(TouchEventArgs e, float currentTime)
        {
            float px = e.X * _windowWidth;
            float py = e.Y * _windowHeight;

            if (e.Pressure > 0.0f)
            {
                // 手指按下 / 移动
                if (!_activeFingers.ContainsKey(e.FingerId))
                {
                    var tracker = new TouchFingerTracker(e.FingerId, px, py, currentTime);
                    _activeFingers[e.FingerId] = tracker;
                }
                else
                {
                    var tracker = _activeFingers[e.FingerId];
                    tracker.Update(px, py, currentTime);
                }
            }
            else
            {
                // 手指抬起
                if (_activeFingers.TryGetValue(e.FingerId, out var tracker))
                {
                    _activeFingers.Remove(e.FingerId);
                    float duration = currentTime - tracker.StartTime;
                    float dist = tracker.DistanceTraveled;

                    // ── 单指手势判定 ──
                    if (_activeFingers.Count == 0 && dist < TapMaxDistance)
                    {
                        if (duration < TapMaxTime)
                        {
                            // Tap 或 DoubleTap
                            if (_lastTap != null && (currentTime - _lastTapTime) < DoubleTapInterval)
                            {
                                EmitGesture(TouchGestureType.DoubleTap, tracker.EndX, tracker.EndY, 0, 0);
                                _lastTap = null;
                            }
                            else
                            {
                                EmitGesture(TouchGestureType.Tap, tracker.EndX, tracker.EndY, 0, 0);
                                _lastTap = tracker;
                                _lastTapTime = currentTime;
                            }
                        }
                        else if (duration >= LongPressTime)
                        {
                            EmitGesture(TouchGestureType.LongPress, tracker.EndX, tracker.EndY, 0, 0);
                        }
                    }
                    else if (_activeFingers.Count == 0 && duration < SwipeMaxTime && dist >= SwipeMinDistance)
                    {
                        // 滑动判定
                        float dx = tracker.EndX - tracker.StartX;
                        float dy = tracker.EndY - tracker.StartY;
                        float velocity = dist / duration;
                        if (velocity >= SwipeMinVelocity)
                        {
                            if (Math.Abs(dx) > Math.Abs(dy))
                                EmitGesture(dx > 0 ? TouchGestureType.SwipeRight : TouchGestureType.SwipeLeft,
                                    tracker.EndX, tracker.EndY, dist, velocity);
                            else
                                EmitGesture(dy > 0 ? TouchGestureType.SwipeDown : TouchGestureType.SwipeUp,
                                    tracker.EndX, tracker.EndY, dist, velocity);
                        }
                    }

                    // ── 双指捏合判定 ──
                    if (_activeFingers.Count == 0 && tracker.HasPinchData)
                    {
                        float pinchDist = tracker.PinchStartDistance - tracker.PinchEndDistance;
                        if (Math.Abs(pinchDist) > TapMaxDistance)
                        {
                            EmitGesture(
                                pinchDist > 0 ? TouchGestureType.PinchIn : TouchGestureType.PinchOut,
                                tracker.EndX, tracker.EndY,
                                Math.Abs(pinchDist), 0);
                        }
                    }
                }
            }

            // 实时追踪捏合
            if (_activeFingers.Count == 2)
            {
                var fingers = new List<TouchFingerTracker>(_activeFingers.Values);
                if (fingers.Count == 2)
                {
                    float dx = fingers[0].CurrentX - fingers[1].CurrentX;
                    float dy = fingers[0].CurrentY - fingers[1].CurrentY;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    foreach (var f in fingers)
                        f.MarkPinch(dist);
                }
            }
        }

        private void EmitGesture(TouchGestureType type, float x, float y, float dist, float vel)
        {
            OnGesture?.Invoke(this, new TouchGestureEventArgs(type, x, y, dist, vel));
        }

        /// <summary>重置识别器状态。</summary>
        public void Reset()
        {
            _activeFingers.Clear();
            _lastTap = null;
            _lastTapTime = 0;
        }

        // ────────────── 手指追踪器 ──────────────
        unsafe private sealed class TouchFingerTracker
        {
            public readonly long FingerId;
            public readonly float StartX, StartY;
            public float EndX, EndY;
            public float CurrentX, CurrentY;
            public readonly float StartTime;
            public float DistanceTraveled;
            public float MaxDistanceFromStart;
            public bool HasPinchData;
            public float PinchStartDistance;
            public float PinchEndDistance;

            public TouchFingerTracker(long id, float x, float y, float t)
            {
                FingerId = id;
                StartX = EndX = CurrentX = x;
                StartY = EndY = CurrentY = y;
                StartTime = t;
            }

            public void Update(float x, float y, float t)
            {
                float dx = x - CurrentX;
                float dy = y - CurrentY;
                DistanceTraveled += (float)Math.Sqrt(dx * dx + dy * dy);

                float dsx = x - StartX;
                float dsy = y - StartY;
                MaxDistanceFromStart = Math.Max(MaxDistanceFromStart, (float)Math.Sqrt(dsx * dsx + dsy * dsy));

                CurrentX = EndX = x;
                CurrentY = EndY = y;
            }

            public void MarkPinch(float distance)
            {
                if (!HasPinchData)
                {
                    HasPinchData = true;
                    PinchStartDistance = distance;
                }
                PinchEndDistance = distance;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: SDL2InputManager — 跨平台输入管理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 跨平台输入管理器。统一键盘/鼠标/手柄/触摸的输入 API，
    /// 包含手柄死区校准和触摸手势识别。
    /// </summary>
    unsafe public sealed class SDL2InputManager : IDisposable
    {
        // ────────────── 手柄状态 ──────────────
        unsafe private sealed class GamepadState
        {
            public IntPtr Handle;
            public string Name;
            public short[] Axes;
            public byte[] Buttons;
            public float[] AxisDeadzone;

            public GamepadState(IntPtr handle, string name, int axisCount)
            {
                Handle = handle;
                Name = name;
                Axes = new short[axisCount];
                Buttons = new byte[32]; // 标准16按钮，预留32
                AxisDeadzone = new float[axisCount];
                for (int i = 0; i < axisCount; i++)
                    AxisDeadzone[i] = 0.1f; // 默认 10% 死区
            }
        }

        private readonly Dictionary<int, GamepadState> _gamepads = new();
        private readonly TouchGestureRecognizer _gestureRecognizer = new();
        private bool _disposed;

        // ────────────── 键盘状态 ──────────────
        private byte[] _keyboardState = Array.Empty<byte>();
        private int _keyboardStateCount;
        private byte[] _prevKeyboardState = Array.Empty<byte>();

        // ────────────── 鼠标状态 ──────────────
        private int _mouseX, _mouseY;
        private int _mouseRelX, _mouseRelY;
        private bool _mouseButtonsDown;

        // ────────────── 触摸状态 ──────────────
        private readonly Dictionary<long, TouchEventArgs> _activeTouches = new();

        /// <summary>手势识别事件。</summary>
        public event EventHandler<TouchGestureEventArgs>? OnTouchGesture;

        /// <summary>初始化输入子系统（手柄热插拔监听）。</summary>
        public void Initialize()
        {
            // 初始化手柄子系统
            SDL2Native.SDL_InitSubSystem(SDL2Native.INIT_GAMECONTROLLER);
            // 尝试加载额外手柄映射
            try
            {
                string mappingFile = System.IO.Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
                if (System.IO.File.Exists(mappingFile))
                    SDL2Native.SDL_GameControllerAddMappingsFromFile(mappingFile);
            }
            catch { /* 忽略映射文件加载失败 */ }

            // 打开已连接的手柄
            int numJoysticks = SDL2Native.SDL_NumJoysticks();
            for (int i = 0; i < numJoysticks; i++)
            {
                if (SDL2Native.SDL_IsGameController(i) != SDL2Native.FALSE)
                    OpenGamepad(i);
            }

            // 获取初始键盘状态
            unsafe
            {
                var ptr = SDL2Native.SDL_GetKeyboardState(out _keyboardStateCount);
                _keyboardState = new byte[_keyboardStateCount];
                _prevKeyboardState = new byte[_keyboardStateCount];
                Marshal.Copy((IntPtr)ptr, _keyboardState, 0, _keyboardStateCount);
            }
        }

        private void OpenGamepad(int index)
        {
            IntPtr handle = SDL2Native.SDL_GameControllerOpen(index);
            if (handle != IntPtr.Zero)
            {
                string name = SDL2Native.SDL_GameControllerName(handle) ?? $"Gamepad_{index}";
                // 标准6轴: 左摇杆XY, 右摇杆XY, 左扳机, 右扳机
                _gamepads[index] = new GamepadState(handle, name, 6);
            }
        }

        /// <summary>每帧更新输入状态。从 SDL2 事件队列中提取输入变化。</summary>
        public void Update(float currentTime)
        {
            // 保存上一帧键盘状态
            if (_keyboardState.Length > 0)
                Array.Copy(_keyboardState, _prevKeyboardState, _keyboardState.Length);

            // 更新键盘
            unsafe
            {
                var ptr = SDL2Native.SDL_GetKeyboardState(out int count);
                if (count > 0 && _keyboardState.Length == count)
                    Marshal.Copy((IntPtr)ptr, _keyboardState, 0, count);
            }

            // 更新鼠标
            SDL2Native.SDL_GetMouseState(out _mouseX, out _mouseY);
            SDL2Native.SDL_GetRelativeMouseState(out _mouseRelX, out _mouseRelY);

            // 更新手柄轴/按钮
            foreach (var pair in _gamepads)
            {
                var gp = pair.Value;
                gp.Axes[0] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_LEFTX),
                    gp.AxisDeadzone[0]);
                gp.Axes[1] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_LEFTY),
                    gp.AxisDeadzone[1]);
                gp.Axes[2] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_RIGHTX),
                    gp.AxisDeadzone[2]);
                gp.Axes[3] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_RIGHTY),
                    gp.AxisDeadzone[3]);
                gp.Axes[4] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_TRIGGERLEFT),
                    gp.AxisDeadzone[4]);
                gp.Axes[5] = ApplyDeadzone(
                    SDL2Native.SDL_GameControllerGetAxis(gp.Handle, SDL2Native.CONTROLLER_AXIS_TRIGGERRIGHT),
                    gp.AxisDeadzone[5]);

                for (int b = 0; b < 15; b++)
                    gp.Buttons[b] = SDL2Native.SDL_GameControllerGetButton(gp.Handle, b);
            }

            // 分发触摸手势
            foreach (var touch in _activeTouches.Values)
                _gestureRecognizer.OnTouchEvent(touch, currentTime);

            _activeTouches.Clear();
        }

        /// <summary>处理触摸事件（由 SDL2Window 的事件循环调用）。</summary>
        public void HandleTouchEvent(TouchEventArgs e)
        {
            if (e.Pressure > 0.0f)
                _activeTouches[e.FingerId] = e;
        }

        private static short ApplyDeadzone(short rawValue, float deadzone)
        {
            // 将 rawValue (short, -32768~32767) 归一化到 [-1, 1]，应用死区后还原
            float normalized = rawValue / 32767.0f;
            if (Math.Abs(normalized) < deadzone)
                return 0;
            // 重新映射死区外范围到 [deadzone, 1.0] -> [0.0, 1.0]
            float sign = Math.Sign(normalized);
            float adjusted = (Math.Abs(normalized) - deadzone) / (1.0f - deadzone);
            return (short)(sign * adjusted * 32767.0f);
        }

        // ────────────── 键盘查询 API ──────────────
        /// <summary>获取指定键码的键盘状态。</summary>
        public bool IsKeyDown(int scancode)
        {
            if (scancode < 0 || scancode >= _keyboardState.Length) return false;
            return _keyboardState[scancode] != 0;
        }

        /// <summary>指定键是否在当前帧刚被按下。</summary>
        public bool IsKeyPressed(int scancode)
        {
            if (scancode < 0 || scancode >= _keyboardState.Length) return false;
            return _keyboardState[scancode] != 0 && _prevKeyboardState[scancode] == 0;
        }

        /// <summary>指定键是否在当前帧刚被释放。</summary>
        public bool IsKeyReleased(int scancode)
        {
            if (scancode < 0 || scancode >= _keyboardState.Length) return false;
            return _keyboardState[scancode] == 0 && _prevKeyboardState[scancode] != 0;
        }

        // ────────────── 鼠标查询 API ──────────────
        /// <summary>获取鼠标绝对坐标。</summary>
        public (int X, int Y) GetMousePosition() => (_mouseX, _mouseY);

        /// <summary>获取鼠标相对移动量。</summary>
        public (int DeltaX, int DeltaY) GetMouseDelta() => (_mouseRelX, _mouseRelY);

        // ────────────── 手柄查询 API ──────────────
        /// <summary>获取已连接手柄数量。</summary>
        public int GamepadCount => _gamepads.Count;

        /// <summary>获取指定手柄的名称。</summary>
        public string GetGamepadName(int index) =>
            _gamepads.TryGetValue(index, out var gp) ? gp.Name : "Unknown";

        /// <summary>获取手柄轴值（已应用死区校准），返回归一化值 [-1, 1]。</summary>
        public float GetGamepadAxis(int gamepadIndex, int axis)
        {
            if (!_gamepads.TryGetValue(gamepadIndex, out var gp)) return 0;
            if (axis < 0 || axis >= gp.Axes.Length) return 0;
            return gp.Axes[axis] / 32767.0f;
        }

        /// <summary>获取手柄按钮状态。</summary>
        public bool GetGamepadButton(int gamepadIndex, int button)
        {
            if (!_gamepads.TryGetValue(gamepadIndex, out var gp)) return false;
            if (button < 0 || button >= gp.Buttons.Length) return false;
            return gp.Buttons[button] != 0;
        }

        /// <summary>设置手柄轴死区（0.0 ~ 1.0）。</summary>
        public void SetGamepadAxisDeadzone(int gamepadIndex, int axis, float deadzone)
        {
            if (!_gamepads.TryGetValue(gamepadIndex, out var gp)) return;
            if (axis < 0 || axis >= gp.AxisDeadzone.Length) return;
            gp.AxisDeadzone[axis] = Math.Clamp(deadzone, 0.0f, 0.9f);
        }

        /// <summary>手柄震动反馈。</summary>
        public bool GamepadRumble(int gamepadIndex, ushort lowFreq, ushort highFreq, uint durationMs)
        {
            if (!_gamepads.TryGetValue(gamepadIndex, out var gp)) return false;
            return SDL2Native.SDL_GameControllerRumble(gp.Handle, lowFreq, highFreq, durationMs) == 0;
        }

        // ────────────── 触摸查询 API ──────────────
        /// <summary>获取当前活动的触摸手指数量。</summary>
        public int ActiveTouchCount => _activeTouches.Count;

        /// <summary>处理手柄热插拔事件（由 SDL2Window 调用）。</summary>
        internal void HandleControllerAdded(int which) => OpenGamepad(which);

        /// <summary>处理手柄移除事件。</summary>
        internal void HandleControllerRemoved(int which)
        {
            if (_gamepads.TryGetValue(which, out var gp))
            {
                SDL2Native.SDL_GameControllerClose(gp.Handle);
                _gamepads.Remove(which);
            }
        }

        // ────────────── 生命周期 ──────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pair in _gamepads)
                SDL2Native.SDL_GameControllerClose(pair.Value.Handle);
            _gamepads.Clear();
            _gestureRecognizer.Reset();
            SDL2Native.SDL_QuitSubSystem(SDL2Native.INIT_GAMECONTROLLER);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: SDL2Window — 跨平台窗口实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SDL2 跨平台窗口。通过 SDL2 创建和管理窗口，
    /// 支持 Windows / Linux / Android 等平台，替代 Win32 专属方案。
    /// 支持 OpenGL ES 3.2 上下文创建、Vulkan surface 创建、
    /// 多指触摸、手柄输入、高 DPI、全屏切换和多窗口。
    /// </summary>
    unsafe public sealed class SDL2Window : IDisposable
    {
        // ──────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;

        // ──────────────────────────────────────────────────────────
        // 私有状态
        // ──────────────────────────────────────────────────────────
        private IntPtr _windowHandle = IntPtr.Zero;
        private IntPtr _glContext = IntPtr.Zero;
        private string _title = "ShanyuEngine";
        private int _width;
        private int _height;
        private int _drawableWidth;
        private int _drawableHeight;
        private bool _isOpen;
        private bool _isFullScreen;
        private bool _isFocused;
        private bool _vSync;
        private float _dpiScale = 1.0f;
        private uint _windowFlags;
        private bool _disposed;

        private readonly SDL2InputManager _inputManager;

        /// <summary>多窗口支持：所有活跃的 SDL2 窗口实例。</summary>
        private static readonly List<SDL2Window> _allWindows = new();

        // ──────────────────────────────────────────────────────────
        // 属性
        // ──────────────────────────────────────────────────────────
        /// <summary>SDL2 原生窗口句柄。</summary>
        public IntPtr Handle => _windowHandle;

        /// <summary>OpenGL 上下文句柄。</summary>
        public IntPtr GLContext => _glContext;

        /// <summary>窗口宽度（逻辑像素）。</summary>
        public int Width => _width;

        /// <summary>窗口高度（逻辑像素）。</summary>
        public int Height => _height;

        /// <summary>可绘制区域宽度（考虑高 DPI 缩放后的物理像素）。</summary>
        public int DrawableWidth => _drawableWidth;

        /// <summary>可绘制区域高度（考虑高 DPI 缩放后的物理像素）。</summary>
        public int DrawableHeight => _drawableHeight;

        /// <summary>窗口是否打开。</summary>
        public bool IsOpen => _isOpen;

        /// <summary>是否全屏。</summary>
        public bool IsFullScreen => _isFullScreen;

        /// <summary>是否拥有焦点。</summary>
        public bool IsFocused => _isFocused;

        /// <summary>DPI 缩放因子。</summary>
        public float DpiScale => _dpiScale;

        /// <summary>窗口标题。</summary>
        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                if (_windowHandle != IntPtr.Zero)
                    SDL2Native.SDL_SetWindowTitle(_windowHandle, value);
            }
        }

        /// <summary>垂直同步标志。</summary>
        public bool VSync
        {
            get => _vSync;
            set
            {
                _vSync = value;
                if (_glContext != IntPtr.Zero)
                    SDL2Native.SDL_GL_SetSwapInterval(value ? 1 : 0);
            }
        }

        /// <summary>输入管理器。</summary>
        public SDL2InputManager Input => _inputManager;

        // ──────────────────────────────────────────────────────────
        // 事件
        // ──────────────────────────────────────────────────────────
        public event EventHandler<WindowResizeEventArgs>? OnResize;
        public event EventHandler? OnClose;
        public event EventHandler? OnFocusGained;
        public event EventHandler? OnFocusLost;
        public event EventHandler<KeyEventArgs>? OnKeyDown;
        public event EventHandler<KeyEventArgs>? OnKeyUp;
        public event EventHandler<MouseMoveEventArgs>? OnMouseMove;
        public event EventHandler<MouseButtonEventArgs>? OnMouseDown;
        public event EventHandler<MouseButtonEventArgs>? OnMouseUp;
        public event EventHandler<MouseWheelEventArgs>? OnMouseWheel;
        public event EventHandler<TouchEventArgs>? OnTouchDown;
        public event EventHandler<TouchEventArgs>? OnTouchUp;
        public event EventHandler<TouchEventArgs>? OnTouchMove;
        public event EventHandler<DropFileEventArgs>? OnDropFile;

        // ──────────────────────────────────────────────────────────
        // 构造函数
        // ──────────────────────────────────────────────────────────

        /// <summary>创建 SDL2 跨平台窗口。</summary>
        public SDL2Window() : this(DefaultWidth, DefaultHeight, "ShanyuEngine") { }

        /// <summary>创建 SDL2 跨平台窗口，指定尺寸和标题。</summary>
        public SDL2Window(int width, int height, string title)
        {
            _width = width;
            _height = height;
            _title = title;
            _inputManager = new SDL2InputManager();
            _allWindows.Add(this);
        }

        // ──────────────────────────────────────────────────────────
        // 窗口生命周期
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化并打开窗口。
        /// </summary>
        /// <param name="useVulkan">true 创建 Vulkan surface，false 创建 OpenGL 上下文。</param>
        /// <param name="fullScreen">是否以全屏模式启动。</param>
        /// <param name="highDpi">是否启用高 DPI 支持。</param>
        public void Create(bool useVulkan = false, bool fullScreen = false, bool highDpi = true)
        {
            if (_isOpen) return;

            // 初始化 SDL2 视频子系统
            if (SDL2Native.SDL_Init(SDL2Native.INIT_VIDEO) != 0)
            {
                string err = SDL2Native.SDL_GetError();
                throw new InvalidOperationException($"SDL_Init 失败: {err}");
            }

            // 构建窗口标志
            uint flags = SDL2Native.WINDOW_SHOWN | SDL2Native.WINDOW_RESIZABLE;

            if (useVulkan)
                flags |= SDL2Native.WINDOW_VULKAN;
            else
                flags |= SDL2Native.WINDOW_OPENGL;

            if (highDpi)
                flags |= SDL2Native.WINDOW_ALLOW_HIGHDPI;

            if (fullScreen)
                flags |= SDL2Native.WINDOW_FULLSCREEN_DESKTOP;

            _windowFlags = flags;

            // 创建窗口
            _windowHandle = SDL2Native.SDL_CreateWindow(
                _title,
                SDL2Native.WINDOWPOS_CENTERED,
                SDL2Native.WINDOWPOS_CENTERED,
                _width,
                _height,
                flags);

            if (_windowHandle == IntPtr.Zero)
            {
                string err = SDL2Native.SDL_GetError();
                throw new InvalidOperationException($"SDL_CreateWindow 失败: {err}");
            }

            // 如果使用 OpenGL，创建上下文（OpenGL ES 3.2 配置）
            if (!useVulkan)
            {
                SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_MAJOR_VERSION, 3);
                SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_MINOR_VERSION, 2);
                SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_PROFILE_MASK, SDL2Native.GL_CONTEXT_PROFILE_ES);

                _glContext = SDL2Native.SDL_GL_CreateContext(_windowHandle);
                if (_glContext == IntPtr.Zero)
                {
                    // OpenGL ES 3.2 回退到 OpenGL 3.3 Core Profile
                    SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_MAJOR_VERSION, 3);
                    SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_MINOR_VERSION, 3);
                    SDL2Native.SDL_GL_SetAttribute(SDL2Native.GL_CONTEXT_PROFILE_MASK, SDL2Native.GL_CONTEXT_PROFILE_CORE);

                    _glContext = SDL2Native.SDL_GL_CreateContext(_windowHandle);
                    if (_glContext == IntPtr.Zero)
                        throw new InvalidOperationException($"SDL_GL_CreateContext 失败: {SDL2Native.SDL_GetError()}");
                }
            }

            // 获取实际窗口尺寸（高 DPI 下可能与请求尺寸不同）
            SDL2Native.SDL_GetWindowSize(_windowHandle, out _width, out _height);
            SDL2Native.SDL_GetWindowDrawableSize(_windowHandle, out _drawableWidth, out _drawableHeight);

            // 计算 DPI 缩放
            if (highDpi && _width > 0)
                _dpiScale = (float)_drawableWidth / _width;

            // 查询显示器 DPI
            int displayIndex = SDL2Native.SDL_GetWindowDisplayIndex(_windowHandle);
            if (displayIndex >= 0)
                SDL2Native.SDL_GetDisplayDPI(displayIndex, out _, out float hdpi, out _);

            _isFullScreen = fullScreen;
            _isOpen = true;

            // 初始化输入
            _inputManager.Initialize();
            _inputManager.SetWindowSize(_drawableWidth, _drawableHeight);
        }

        /// <summary>销毁窗口。</summary>
        public void Destroy()
        {
            if (!_isOpen) return;
            Dispose();
        }

        // ──────────────────────────────────────────────────────────
        // Vulkan Surface 创建
        // ──────────────────────────────────────────────────────────

        /// <summary>创建 Vulkan surface（需要 Vulkan 实例指针）。</summary>
        /// <param name="vulkanInstance">Vulkan VkInstance 原生指针。</param>
        /// <param name="surface">返回的 VkSurfaceKHR 句柄。</param>
        /// <returns>是否成功。</returns>
        public bool CreateVulkanSurface(IntPtr vulkanInstance, out ulong surface)
        {
            surface = 0;
            if (_windowHandle == IntPtr.Zero || vulkanInstance == IntPtr.Zero)
                return false;
            int result = SDL2Native.SDL_Vulkan_CreateSurface(_windowHandle, vulkanInstance, out surface);
            return result == 0;
        }

        /// <summary>获取 Vulkan 所需的实例扩展列表。</summary>
        /// <returns>扩展名称列表。</returns>
        public string[] GetVulkanInstanceExtensions()
        {
            if (_windowHandle == IntPtr.Zero)
                return Array.Empty<string>();

            uint count = 0;
            SDL2Native.SDL_Vulkan_GetInstanceExtensions(_windowHandle, out count, null!);
            if (count == 0) return Array.Empty<string>();

            var names = new IntPtr[count];
            SDL2Native.SDL_Vulkan_GetInstanceExtensions(_windowHandle, out _, names);

            var result = new string[count];
            for (int i = 0; i < (int)count; i++)
                result[i] = Marshal.PtrToStringUTF8(names[i]) ?? "";

            return result;
        }

        // ──────────────────────────────────────────────────────────
        // 窗口操作
        // ──────────────────────────────────────────────────────────

        /// <summary>切换全屏模式。</summary>
        public void ToggleFullScreen()
        {
            if (_windowHandle == IntPtr.Zero) return;
            _isFullScreen = !_isFullScreen;
            uint flag = _isFullScreen
                ? SDL2Native.FULLSCREEN_FULLSCREEN_DESKTOP
                : SDL2Native.FULLSCREEN_OFF;
            SDL2Native.SDL_SetWindowFullscreen(_windowHandle, flag);
        }

        /// <summary>设置窗口尺寸。</summary>
        public void SetSize(int width, int height)
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_SetWindowSize(_windowHandle, width, height);
        }

        /// <summary>设置窗口位置。</summary>
        public void SetPosition(int x, int y)
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_SetWindowPosition(_windowHandle, x, y);
        }

        /// <summary>最小化窗口。</summary>
        public void Minimize()
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_MinimizeWindow(_windowHandle);
        }

        /// <summary>最大化窗口。</summary>
        public void Maximize()
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_MaximizeWindow(_windowHandle);
        }

        /// <summary>恢复窗口。</summary>
        public void Restore()
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_RestoreWindow(_windowHandle);
        }

        /// <summary>显示窗口。</summary>
        public void Show()
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_ShowWindow(_windowHandle);
        }

        /// <summary>隐藏窗口。</summary>
        public void Hide()
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_HideWindow(_windowHandle);
        }

        /// <summary>设置是否显示窗口边框。</summary>
        public void SetBordered(bool bordered)
        {
            if (_windowHandle == IntPtr.Zero) return;
            SDL2Native.SDL_SetWindowBordered(_windowHandle, bordered ? 1 : 0);
        }

        /// <summary>交换缓冲区（OpenGL 模式）。</summary>
        public void SwapBuffers()
        {
            if (_windowHandle != IntPtr.Zero && _glContext != IntPtr.Zero)
                SDL2Native.SDL_GL_SwapWindow(_windowHandle);
        }

        // ──────────────────────────────────────────────────────────
        // 事件循环
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 消息泵：处理所有待处理的 SDL2 事件。
        /// 返回 false 表示窗口应该关闭（收到 QUIT 事件）。
        /// </summary>
        public bool PumpEvents()
        {
            if (!_isOpen) return false;

            float currentTime = SDL2Native.SDL_GetTicks() / 1000.0f;

            while (SDL2Native.SDL_PollEvent(out var sdlEvent) != 0)
            {
                if (!ProcessSDLEvent(sdlEvent, currentTime))
                    return false;
            }

            _inputManager.Update(currentTime);
            return true;
        }

        private unsafe bool ProcessSDLEvent(SDLEvent e, float currentTime)
        {
            switch (e.Type)
            {
                case SDL2Native.QUIT:
                    _isOpen = false;
                    OnClose?.Invoke(this, EventArgs.Empty);
                    return false;

                case SDL2Native.WINDOWEVENT:
                    ProcessWindowEvent(e, currentTime);
                    break;

                case SDL2Native.KEYDOWN:
                case SDL2Native.KEYUP:
                    // 从联合体中提取键盘事件字段
                    var keyEvent = *(SDLKeyboardEvent*)&e;
                    bool isDown = keyEvent.State != 0;
                    var keyArgs = new KeyEventArgs(
                        keyEvent.KeyCode, keyEvent.Scancode,
                        keyEvent.Repeat != 0, isDown, keyEvent.Mod);
                    if (isDown)
                        OnKeyDown?.Invoke(this, keyArgs);
                    else
                        OnKeyUp?.Invoke(this, keyArgs);
                    break;

                case SDL2Native.TEXTINPUT:
                    // 文字输入事件（IME）
                    break;

                case SDL2Native.MOUSEMOTION:
                    var motion = *(SDLMouseMotionEvent*)&e;
                    OnMouseMove?.Invoke(this, new MouseMoveEventArgs(motion.X, motion.Y, motion.RelX, motion.RelY));
                    break;

                case SDL2Native.MOUSEBUTTONDOWN:
                case SDL2Native.MOUSEBUTTONUP:
                    var btn = *(SDLMouseButtonEvent*)&e;
                    var btnArgs = new MouseButtonEventArgs(
                        btn.X, btn.Y, btn.Button, btn.State != 0, btn.Clicks);
                    if (btn.State != 0)
                        OnMouseDown?.Invoke(this, btnArgs);
                    else
                        OnMouseUp?.Invoke(this, btnArgs);
                    break;

                case SDL2Native.MOUSEWHEEL:
                    var wheel = *(SDLMouseWheelEvent*)&e;
                    OnMouseWheel?.Invoke(this, new MouseWheelEventArgs(wheel.X, wheel.Y));
                    break;

                case SDL2Native.FINGERDOWN:
                case SDL2Native.FINGERUP:
                case SDL2Native.FINGERMOTION:
                    var touch = *(SDLTouchFingerEvent*)&e;
                    var touchArgs = new TouchEventArgs(
                        touch.FingerID, touch.X, touch.Y, touch.DX, touch.DY, touch.Pressure);
                    if (e.Type == SDL2Native.FINGERDOWN)
                        OnTouchDown?.Invoke(this, touchArgs);
                    else if (e.Type == SDL2Native.FINGERUP)
                        OnTouchUp?.Invoke(this, touchArgs);
                    else
                        OnTouchMove?.Invoke(this, touchArgs);
                    _inputManager.HandleTouchEvent(touchArgs);
                    break;

                case SDL2Native.CONTROLLERAXISMOTION:
                    // 手柄轴变化由 InputManager.Update() 统一处理
                    break;

                case SDL2Native.CONTROLLERBUTTONDOWN:
                case SDL2Native.CONTROLLERBUTTONUP:
                    // 手柄按钮变化由 InputManager.Update() 统一处理
                    break;

                case SDL2Native.CONTROLLERDEVICEADDED:
                    var added = *(SDLControllerDeviceEvent*)&e;
                    _inputManager.HandleControllerAdded(added.Which);
                    break;

                case SDL2Native.CONTROLLERDEVICEREMOVED:
                    var removed = *(SDLControllerDeviceEvent*)&e;
                    _inputManager.HandleControllerRemoved(removed.Which);
                    break;

                case SDL2Native.DROPFILE:
                    var drop = *(SDLDropEvent*)&e;
                    string? filePath = Marshal.PtrToStringUTF8(drop.File);
                    if (filePath != null)
                        OnDropFile?.Invoke(this, new DropFileEventArgs(filePath));
                    break;
            }

            return true;
        }

        private unsafe void ProcessWindowEvent(SDLEvent e, float currentTime)
        {
            byte windowEvent = *(byte*)((IntPtr)(&e) + 12); // offset 12 = Event field

            switch (windowEvent)
            {
                case SDL2Native.WINDOWEVENT_RESIZED:
                case SDL2Native.WINDOWEVENT_SIZE_CHANGED:
                    SDL2Native.SDL_GetWindowSize(_windowHandle, out int newW, out int newH);
                    SDL2Native.SDL_GetWindowDrawableSize(_windowHandle, out _drawableWidth, out _drawableHeight);
                    _width = newW;
                    _height = newH;
                    if (_width > 0)
                        _dpiScale = (float)_drawableWidth / _width;
                    _inputManager.SetWindowSize(_drawableWidth, _drawableHeight);
                    OnResize?.Invoke(this, new WindowResizeEventArgs(_drawableWidth, _drawableHeight));
                    break;

                case SDL2Native.WINDOWEVENT_CLOSE:
                    _isOpen = false;
                    OnClose?.Invoke(this, EventArgs.Empty);
                    break;

                case SDL2Native.WINDOWEVENT_FOCUS_GAINED:
                    _isFocused = true;
                    OnFocusGained?.Invoke(this, EventArgs.Empty);
                    break;

                case SDL2Native.WINDOWEVENT_FOCUS_LOST:
                    _isFocused = false;
                    OnFocusLost?.Invoke(this, EventArgs.Empty);
                    break;

                case SDL2Native.WINDOWEVENT_MINIMIZED:
                    // 移动平台通常对应进入后台
                    break;

                case SDL2Native.WINDOWEVENT_RESTORED:
                    // 移动平台通常对应回到前台
                    break;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 静态方法
        // ──────────────────────────────────────────────────────────

        /// <summary>获取所有活跃窗口。</summary>
        public static IReadOnlyList<SDL2Window> GetAllWindows() => _allWindows.AsReadOnly();

        /// <summary>关闭所有窗口。</summary>
        public static void CloseAllWindows()
        {
            foreach (var w in _allWindows.ToArray())
                w.Dispose();
            _allWindows.Clear();
        }

        // ──────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _inputManager.Dispose();

            if (_glContext != IntPtr.Zero)
            {
                SDL2Native.SDL_GL_DeleteContext(_glContext);
                _glContext = IntPtr.Zero;
            }

            if (_windowHandle != IntPtr.Zero)
            {
                SDL2Native.SDL_DestroyWindow(_windowHandle);
                _windowHandle = IntPtr.Zero;
            }

            _allWindows.Remove(this);
            _isOpen = false;
        }
    }
}
