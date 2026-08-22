using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Platforms
{
    /// <summary>
    /// 支持的主机平台枚举。不包含任何 NDA 信息。
    /// </summary>
    public enum ConsolePlatform
    {
        None,
        PS5,
        XboxSeriesX,
        XboxSeriesS,
        NintendoSwitch,
        SteamDeck,
        PC
    }

    /// <summary>
    /// 系统 UI 类型。用于调用平台级覆盖界面。
    /// </summary>
    public enum SystemUIType
    {
        Friends,
        Achievements,
        Store,
        Settings,
        Invite,
        Profile,
        Party,
        Messages
    }

    /// <summary>
    /// 手柄按键与轴的状态结构体。
    /// </summary>
    public struct ControllerState
    {
        public bool IsConnected;
        public int PlayerIndex;
        public float LeftStickX;
        public float LeftStickY;
        public float RightStickX;
        public float RightStickY;
        public float LeftTrigger;
        public float RightTrigger;
        public bool ButtonA;
        public bool ButtonB;
        public bool ButtonX;
        public bool ButtonY;
        public bool DPadUp;
        public bool DPadDown;
        public bool DPadLeft;
        public bool DPadRight;
        public bool LeftShoulder;
        public bool RightShoulder;
        public bool LeftStickPress;
        public bool RightStickPress;
        public bool Start;
        public bool Select;
        public bool Touchpad;
        public DateTime Timestamp;
    }

    /// <summary>
    /// GPU 信息结构体。
    /// </summary>
    public struct ConsoleGPUInfo
    {
        public string DeviceName;
        public string ApiVersion;
        public ulong DedicatedVideoMemory;
        public ulong SharedSystemMemory;
        public uint MaxComputeUnits;
        public uint MaxClockFrequencyMHz;
        public bool SupportsRayTracing;
        public bool SupportsVariableRateShading;
        public bool SupportsMeshShaders;
        public string FeatureLevel;
    }

    /// <summary>
    /// 内存信息结构体。
    /// </summary>
    public struct MemoryInfo
    {
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong TotalVideoMemory;
        public ulong AvailableVideoMemory;
    }

    /// <summary>
    /// 存储信息结构体。
    /// </summary>
    public struct StorageInfo
    {
        public ulong TotalSpace;
        public ulong FreeSpace;
        public ulong SaveDataUsed;
        public bool IsSSD;
        public uint ReadSpeedMBps;
        public uint WriteSpeedMBps;
    }

    /// <summary>
    /// 振动/触觉反馈参数。
    /// </summary>
    public struct VibrationParams
    {
        public float LeftMotor;
        public float RightMotor;
        public float LeftTrigger;
        public float RightTrigger;
        public uint DurationMs;
    }

    /// <summary>
    /// 分辨率设置结构体。
    /// </summary>
    public struct Resolution
    {
        public int Width;
        public int Height;
        public int RefreshRate;
        public bool HDR;
    }

    /// <summary>
    /// 成就信息。
    /// </summary>
    public struct AchievementInfo
    {
        public string Id;
        public string Name;
        public string Description;
        public bool IsHidden;
        public float Progress;
        public bool IsUnlocked;
    }

    /// <summary>
    /// 主机 SDK 抽象接口。所有平台特定实现均通过此接口暴露，
    /// 接口本身不包含任何受 NDA 保护的信息。
    /// </summary>
    public interface IConsoleSDK
    {
        ConsolePlatform Platform { get; }

        string GetPlatformName();
        ConsoleGPUInfo GetGPUInfo();
        MemoryInfo GetMemoryInfo();
        StorageInfo GetStorageInfo();

        bool SetResolution(Resolution resolution);
        bool SetFrameRateCap(uint fps);
        uint GetFrameRateCap();

        bool VibrateController(int playerIndex, VibrationParams vibration);
        ControllerState GetControllerState(int playerIndex);
        bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b);

        bool ShowSystemUI(SystemUIType uiType);
        bool HideSystemUI();

        bool SubmitAchievement(string achievementId, float progress = 100f);
        AchievementInfo[] GetAchievements();

        string GetSaveDataPath();
        bool BeginSaveTransaction();
        bool EndSaveTransaction(bool commit);
        bool IsSaveTransactionActive();

        bool IsOnline();
        bool ShowInviteUI();
        bool ShowProfileUI(string userId);
        string GetLocalUserId();
        string GetLocalUserDisplayName();

        void Update();
        void Shutdown();
    }

    /// <summary>
    /// 空实现：用于 PC 或默认无平台 SDK 的场景。
    /// </summary>
    public class NullConsoleSDK : IConsoleSDK
    {
        public ConsolePlatform Platform => ConsolePlatform.PC;

        public string GetPlatformName() => "PC / Null";

        public ConsoleGPUInfo GetGPUInfo()
        {
            return new ConsoleGPUInfo
            {
                DeviceName = "Unknown",
                ApiVersion = "1.0",
                DedicatedVideoMemory = 0,
                SharedSystemMemory = 0,
                MaxComputeUnits = 0,
                MaxClockFrequencyMHz = 0,
                SupportsRayTracing = false,
                SupportsVariableRateShading = false,
                SupportsMeshShaders = false,
                FeatureLevel = "N/A"
            };
        }

        public MemoryInfo GetMemoryInfo()
        {
            return new MemoryInfo
            {
                TotalPhysical = 0,
                AvailablePhysical = 0,
                TotalVirtual = 0,
                AvailableVirtual = 0,
                TotalVideoMemory = 0,
                AvailableVideoMemory = 0
            };
        }

        public StorageInfo GetStorageInfo()
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\");
            return new StorageInfo
            {
                TotalSpace = (ulong)drive.TotalSize,
                FreeSpace = (ulong)drive.AvailableFreeSpace,
                SaveDataUsed = 0,
                IsSSD = false,
                ReadSpeedMBps = 0,
                WriteSpeedMBps = 0
            };
        }

        public bool SetResolution(Resolution resolution) => true;
        public bool SetFrameRateCap(uint fps) => true;
        public uint GetFrameRateCap() => 0;

        public bool VibrateController(int playerIndex, VibrationParams vibration) => false;

        public ControllerState GetControllerState(int playerIndex)
        {
            return new ControllerState { IsConnected = false, PlayerIndex = playerIndex, Timestamp = DateTime.UtcNow };
        }

        public bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b) => false;

        public bool ShowSystemUI(SystemUIType uiType) => false;
        public bool HideSystemUI() => false;

        public bool SubmitAchievement(string achievementId, float progress = 100f) => false;
        public AchievementInfo[] GetAchievements() => Array.Empty<AchievementInfo>();

        public string GetSaveDataPath()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShanyuEngine", "Saves");
            Directory.CreateDirectory(path);
            return path;
        }

        public bool BeginSaveTransaction() => true;
        public bool EndSaveTransaction(bool commit) => true;
        public bool IsSaveTransactionActive() => false;

        public bool IsOnline() => true;
        public bool ShowInviteUI() => false;
        public bool ShowProfileUI(string userId) => false;
        public string GetLocalUserId() => Environment.UserName;
        public string GetLocalUserDisplayName() => Environment.UserName;

        public void Update() { }
        public void Shutdown() { }
    }

#if PS5
    /// <summary>
    /// PS5 SDK Stub。接口实现需在有官方 SDK 授权后填充真实调用。
    /// 当前仅包含与公共抽象接口对齐的骨架代码，不包含任何 NDA 信息。
    /// </summary>
    public class PS5SDKStub : IConsoleSDK
    {
        public ConsolePlatform Platform => ConsolePlatform.PS5;

        public string GetPlatformName() => "PlayStation 5";

        public ConsoleGPUInfo GetGPUInfo()
        {
            // TODO: Fill with real PS5 SDK calls after NDA authorization
            return new ConsoleGPUInfo
            {
                DeviceName = "AMD RDNA 2 (PS5)",
                ApiVersion = "AGC 1.0",
                DedicatedVideoMemory = 16UL * 1024 * 1024 * 1024,
                SharedSystemMemory = 0,
                MaxComputeUnits = 36,
                MaxClockFrequencyMHz = 2233,
                SupportsRayTracing = true,
                SupportsVariableRateShading = true,
                SupportsMeshShaders = true,
                FeatureLevel = "Custom"
            };
        }

        public MemoryInfo GetMemoryInfo()
        {
            // TODO: Use sceKernelGetMainMemorySize etc.
            return new MemoryInfo
            {
                TotalPhysical = 16UL * 1024 * 1024 * 1024,
                AvailablePhysical = 8UL * 1024 * 1024 * 1024,
                TotalVirtual = 0,
                AvailableVirtual = 0,
                TotalVideoMemory = 16UL * 1024 * 1024 * 1024,
                AvailableVideoMemory = 12UL * 1024 * 1024 * 1024
            };
        }

        public StorageInfo GetStorageInfo()
        {
            // TODO: Use sceSaveData etc.
            return new StorageInfo
            {
                TotalSpace = 825UL * 1024 * 1024 * 1024,
                FreeSpace = 400UL * 1024 * 1024 * 1024,
                SaveDataUsed = 0,
                IsSSD = true,
                ReadSpeedMBps = 5500,
                WriteSpeedMBps = 5000
            };
        }

        public bool SetResolution(Resolution resolution)
        {
            // TODO: sceVideoOutSetFlipRate / resolution config
            return true;
        }

        public bool SetFrameRateCap(uint fps)
        {
            // TODO: sceVideoOutSetFlipRate
            return true;
        }

        public uint GetFrameRateCap() => 60;

        public bool VibrateController(int playerIndex, VibrationParams vibration)
        {
            // TODO: scePadSetVibration
            return true;
        }

        public ControllerState GetControllerState(int playerIndex)
        {
            // TODO: scePadReadState
            return new ControllerState { IsConnected = true, PlayerIndex = playerIndex, Timestamp = DateTime.UtcNow };
        }

        public bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b)
        {
            // TODO: scePadSetLightBar
            return true;
        }

        public bool ShowSystemUI(SystemUIType uiType)
        {
            // TODO: sceSystemServiceShowSignInDialog / overlay
            return false;
        }

        public bool HideSystemUI() => false;

        public bool SubmitAchievement(string achievementId, float progress = 100f)
        {
            // TODO: sceNpTrophy
            return false;
        }

        public AchievementInfo[] GetAchievements()
        {
            // TODO: sceNpTrophy
            return Array.Empty<AchievementInfo>();
        }

        public string GetSaveDataPath()
        {
            // TODO: sceSaveDataGetMountPoint
            return "/user/data/";
        }

        public bool BeginSaveTransaction()
        {
            // TODO: sceSaveDataMount
            return true;
        }

        public bool EndSaveTransaction(bool commit)
        {
            // TODO: sceSaveDataUmount
            return true;
        }

        public bool IsSaveTransactionActive() => false;

        public bool IsOnline()
        {
            // TODO: sceNpCheckCallback
            return true;
        }

        public bool ShowInviteUI()
        {
            // TODO: sceInvitationDialogOpen
            return false;
        }

        public bool ShowProfileUI(string userId)
        {
            // TODO: sceProfileDialogOpen
            return false;
        }

        public string GetLocalUserId()
        {
            // TODO: sceUserServiceGetUserName
            return "Player1";
        }

        public string GetLocalUserDisplayName()
        {
            // TODO: sceUserServiceGetUserName
            return "Player1";
        }

        public void Update()
        {
            // TODO: sceSystemServiceUpdateStatus
        }

        public void Shutdown()
        {
            // TODO: sceSystemServiceLoadExec
        }
    }
#endif

#if XBOX
    /// <summary>
    /// Xbox Series X|S SDK Stub。接口实现需在有 GDK 授权后填充真实调用。
    /// 当前仅包含与公共抽象接口对齐的骨架代码，不包含任何 NDA 信息。
    /// </summary>
    public class XboxSDKStub : IConsoleSDK
    {
        private ConsolePlatform _platform;

        public XboxSDKStub(bool isSeriesS = false)
        {
            _platform = isSeriesS ? ConsolePlatform.XboxSeriesS : ConsolePlatform.XboxSeriesX;
        }

        public ConsolePlatform Platform => _platform;

        public string GetPlatformName() => _platform == ConsolePlatform.XboxSeriesX ? "Xbox Series X" : "Xbox Series S";

        public ConsoleGPUInfo GetGPUInfo()
        {
            bool isX = _platform == ConsolePlatform.XboxSeriesX;
            return new ConsoleGPUInfo
            {
                DeviceName = isX ? "AMD RDNA 2 (XSX)" : "AMD RDNA 2 (XSS)",
                ApiVersion = "DX12 GDK",
                DedicatedVideoMemory = isX ? 10UL * 1024 * 1024 * 1024 : 8UL * 1024 * 1024 * 1024,
                SharedSystemMemory = 0,
                MaxComputeUnits = isX ? 52u : 20u,
                MaxClockFrequencyMHz = isX ? 1825u : 1565u,
                SupportsRayTracing = true,
                SupportsVariableRateShading = true,
                SupportsMeshShaders = true,
                FeatureLevel = "12_2"
            };
        }

        public MemoryInfo GetMemoryInfo()
        {
            bool isX = _platform == ConsolePlatform.XboxSeriesX;
            return new MemoryInfo
            {
                TotalPhysical = isX ? 16UL * 1024 * 1024 * 1024 : 10UL * 1024 * 1024 * 1024,
                AvailablePhysical = isX ? 12UL * 1024 * 1024 * 1024 : 8UL * 1024 * 1024 * 1024,
                TotalVirtual = 0,
                AvailableVirtual = 0,
                TotalVideoMemory = isX ? 10UL * 1024 * 1024 * 1024 : 8UL * 1024 * 1024 * 1024,
                AvailableVideoMemory = isX ? 8UL * 1024 * 1024 * 1024 : 6UL * 1024 * 1024 * 1024
            };
        }

        public StorageInfo GetStorageInfo()
        {
            // TODO: XStorage APIs
            return new StorageInfo
            {
                TotalSpace = 1000UL * 1024 * 1024 * 1024,
                FreeSpace = 500UL * 1024 * 1024 * 1024,
                SaveDataUsed = 0,
                IsSSD = true,
                ReadSpeedMBps = 2400,
                WriteSpeedMBps = 1200
            };
        }

        public bool SetResolution(Resolution resolution)
        {
            // TODO: XDisplay APIs
            return true;
        }

        public bool SetFrameRateCap(uint fps)
        {
            // TODO: XDisplay APIs
            return true;
        }

        public uint GetFrameRateCap() => 60;

        public bool VibrateController(int playerIndex, VibrationParams vibration)
        {
            // TODO: XInput / GameInput
            return true;
        }

        public ControllerState GetControllerState(int playerIndex)
        {
            // TODO: XInputGetState
            return new ControllerState { IsConnected = true, PlayerIndex = playerIndex, Timestamp = DateTime.UtcNow };
        }

        public bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b)
        {
            // Not supported on standard Xbox controllers
            return false;
        }

        public bool ShowSystemUI(SystemUIType uiType)
        {
            // TODO: XSystemShow*Dialog
            return false;
        }

        public bool HideSystemUI() => false;

        public bool SubmitAchievement(string achievementId, float progress = 100f)
        {
            // TODO: XAchievements APIs
            return false;
        }

        public AchievementInfo[] GetAchievements()
        {
            // TODO: XAchievements APIs
            return Array.Empty<AchievementInfo>();
        }

        public string GetSaveDataPath()
        {
            // TODO: XStorageGetContainerPath
            return "/savedata/";
        }

        public bool BeginSaveTransaction()
        {
            // TODO: XStorage APIs
            return true;
        }

        public bool EndSaveTransaction(bool commit)
        {
            // TODO: XStorage APIs
            return true;
        }

        public bool IsSaveTransactionActive() => false;

        public bool IsOnline()
        {
            // TODO: XNetwork APIs
            return true;
        }

        public bool ShowInviteUI()
        {
            // TODO: XGameInvite APIs
            return false;
        }

        public bool ShowProfileUI(string userId)
        {
            // TODO: XUser APIs
            return false;
        }

        public string GetLocalUserId()
        {
            // TODO: XUserGetId
            return "XboxUser";
        }

        public string GetLocalUserDisplayName()
        {
            // TODO: XUserGetGamertag
            return "Player1";
        }

        public void Update()
        {
            // TODO: XTaskQueueDispatch
        }

        public void Shutdown()
        {
            // TODO: XGameRuntimeUninitialize
        }
    }
#endif

#if SWITCH
    /// <summary>
    /// Nintendo Switch SDK Stub。接口实现需在有 Nintendo SDK 授权后填充真实调用。
    /// 当前仅包含与公共抽象接口对齐的骨架代码，不包含任何 NDA 信息。
    /// </summary>
    public class SwitchSDKStub : IConsoleSDK
    {
        public ConsolePlatform Platform => ConsolePlatform.NintendoSwitch;

        public string GetPlatformName() => "Nintendo Switch";

        public ConsoleGPUInfo GetGPUInfo()
        {
            return new ConsoleGPUInfo
            {
                DeviceName = "NVIDIA Custom Tegra",
                ApiVersion = "NVN",
                DedicatedVideoMemory = 4UL * 1024 * 1024 * 1024,
                SharedSystemMemory = 0,
                MaxComputeUnits = 0,
                MaxClockFrequencyMHz = 768,
                SupportsRayTracing = false,
                SupportsVariableRateShading = false,
                SupportsMeshShaders = false,
                FeatureLevel = "Custom"
            };
        }

        public MemoryInfo GetMemoryInfo()
        {
            return new MemoryInfo
            {
                TotalPhysical = 4UL * 1024 * 1024 * 1024,
                AvailablePhysical = 2UL * 1024 * 1024 * 1024,
                TotalVirtual = 0,
                AvailableVirtual = 0,
                TotalVideoMemory = 4UL * 1024 * 1024 * 1024,
                AvailableVideoMemory = 2UL * 1024 * 1024 * 1024
            };
        }

        public StorageInfo GetStorageInfo()
        {
            // TODO: nn::fs APIs
            return new StorageInfo
            {
                TotalSpace = 32UL * 1024 * 1024 * 1024,
                FreeSpace = 10UL * 1024 * 1024 * 1024,
                SaveDataUsed = 0,
                IsSSD = false,
                ReadSpeedMBps = 100,
                WriteSpeedMBps = 90
            };
        }

        public bool SetResolution(Resolution resolution)
        {
            // TODO: nn::vi APIs
            return true;
        }

        public bool SetFrameRateCap(uint fps)
        {
            // TODO: nn::oe APIs
            return true;
        }

        public uint GetFrameRateCap() => 30;

        public bool VibrateController(int playerIndex, VibrationParams vibration)
        {
            // TODO: nn::hid::VibrationNode
            return true;
        }

        public ControllerState GetControllerState(int playerIndex)
        {
            // TODO: nn::hid::Npad
            return new ControllerState { IsConnected = true, PlayerIndex = playerIndex, Timestamp = DateTime.UtcNow };
        }

        public bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b)
        {
            // TODO: nn::hid::NpadColor
            return true;
        }

        public bool ShowSystemUI(SystemUIType uiType)
        {
            // TODO: nn::friends / nn::account
            return false;
        }

        public bool HideSystemUI() => false;

        public bool SubmitAchievement(string achievementId, float progress = 100f)
        {
            // Switch does not natively support achievements; this is a stub.
            return false;
        }

        public AchievementInfo[] GetAchievements()
        {
            return Array.Empty<AchievementInfo>();
        }

        public string GetSaveDataPath()
        {
            // TODO: nn::fs::MountSaveData
            return "/save/";
        }

        public bool BeginSaveTransaction()
        {
            // TODO: nn::fs::CreateFile / OpenFile
            return true;
        }

        public bool EndSaveTransaction(bool commit)
        {
            // TODO: nn::fs::Commit
            return true;
        }

        public bool IsSaveTransactionActive() => false;

        public bool IsOnline()
        {
            // TODO: nn::nex / nn::pns
            return true;
        }

        public bool ShowInviteUI()
        {
            // TODO: nn::friends
            return false;
        }

        public bool ShowProfileUI(string userId)
        {
            // TODO: nn::friends
            return false;
        }

        public string GetLocalUserId()
        {
            // TODO: nn::account::GetUserId
            return "SwitchUser";
        }

        public string GetLocalUserDisplayName()
        {
            // TODO: nn::account::GetNickname
            return "Player1";
        }

        public void Update()
        {
            // TODO: nn::oe / nn::hid polling
        }

        public void Shutdown()
        {
            // TODO: nn::oe::FinishStartup
        }
    }
#endif

    /// <summary>
    /// Steam Deck SDK Stub。使用标准 Steamworks API 封装。
    /// </summary>
    public class SteamDeckSDK : IConsoleSDK
    {
        public ConsolePlatform Platform => ConsolePlatform.SteamDeck;

        public string GetPlatformName() => "Steam Deck";

        public ConsoleGPUInfo GetGPUInfo()
        {
            return new ConsoleGPUInfo
            {
                DeviceName = "AMD APU (Steam Deck)",
                ApiVersion = "Vulkan 1.3 / DX12",
                DedicatedVideoMemory = 16UL * 1024 * 1024 * 1024,
                SharedSystemMemory = 0,
                MaxComputeUnits = 8,
                MaxClockFrequencyMHz = 1600,
                SupportsRayTracing = false,
                SupportsVariableRateShading = false,
                SupportsMeshShaders = false,
                FeatureLevel = "12_1"
            };
        }

        public MemoryInfo GetMemoryInfo()
        {
            return new MemoryInfo
            {
                TotalPhysical = 16UL * 1024 * 1024 * 1024,
                AvailablePhysical = 12UL * 1024 * 1024 * 1024,
                TotalVirtual = 0,
                AvailableVirtual = 0,
                TotalVideoMemory = 16UL * 1024 * 1024 * 1024,
                AvailableVideoMemory = 14UL * 1024 * 1024 * 1024
            };
        }

        public StorageInfo GetStorageInfo()
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/");
            return new StorageInfo
            {
                TotalSpace = (ulong)drive.TotalSize,
                FreeSpace = (ulong)drive.AvailableFreeSpace,
                SaveDataUsed = 0,
                IsSSD = true,
                ReadSpeedMBps = 2400,
                WriteSpeedMBps = 1200
            };
        }

        public bool SetResolution(Resolution resolution) => true;
        public bool SetFrameRateCap(uint fps) => true;
        public uint GetFrameRateCap() => 60;

        public bool VibrateController(int playerIndex, VibrationParams vibration)
        {
            // Steam Input haptics
            return true;
        }

        public ControllerState GetControllerState(int playerIndex)
        {
            return new ControllerState { IsConnected = true, PlayerIndex = playerIndex, Timestamp = DateTime.UtcNow };
        }

        public bool SetControllerLightbar(int playerIndex, byte r, byte g, byte b) => true;

        public bool ShowSystemUI(SystemUIType uiType)
        {
            // Steam overlay
            return false;
        }

        public bool HideSystemUI() => false;

        public bool SubmitAchievement(string achievementId, float progress = 100f)
        {
            // SteamUserStats.SetAchievement
            return false;
        }

        public AchievementInfo[] GetAchievements()
        {
            return Array.Empty<AchievementInfo>();
        }

        public string GetSaveDataPath()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ShanyuEngine", "Saves");
            Directory.CreateDirectory(path);
            return path;
        }

        public bool BeginSaveTransaction() => true;
        public bool EndSaveTransaction(bool commit) => true;
        public bool IsSaveTransactionActive() => false;

        public bool IsOnline() => true;
        public bool ShowInviteUI() => false;
        public bool ShowProfileUI(string userId) => false;
        public string GetLocalUserId() => "SteamUser";
        public string GetLocalUserDisplayName() => "SteamPlayer";

        public void Update() { }
        public void Shutdown() { }
    }

    /// <summary>
    /// 主机 SDK 工厂。通过条件编译与运行时检测创建正确的 SDK 实例。
    /// </summary>
    public static class ConsoleSDKFactory
    {
        public static IConsoleSDK CreateSDK(ConsolePlatform? preferredPlatform = null)
        {
            if (preferredPlatform.HasValue)
            {
                switch (preferredPlatform.Value)
                {
                    case ConsolePlatform.PS5:
#if PS5
                        return new PS5SDKStub();
#else
                        throw new PlatformNotSupportedException("PS5 SDK not available. Build with PS5 symbol.");
#endif
                    case ConsolePlatform.XboxSeriesX:
                    case ConsolePlatform.XboxSeriesS:
#if XBOX
                        return new XboxSDKStub(preferredPlatform.Value == ConsolePlatform.XboxSeriesS);
#else
                        throw new PlatformNotSupportedException("Xbox SDK not available. Build with XBOX symbol.");
#endif
                    case ConsolePlatform.NintendoSwitch:
#if SWITCH
                        return new SwitchSDKStub();
#else
                        throw new PlatformNotSupportedException("Switch SDK not available. Build with SWITCH symbol.");
#endif
                    case ConsolePlatform.SteamDeck:
                        return new SteamDeckSDK();
                    case ConsolePlatform.PC:
                    case ConsolePlatform.None:
                    default:
                        return new NullConsoleSDK();
                }
            }

            // Auto-detect by conditional compilation
#if PS5
            return new PS5SDKStub();
#elif XBOX
            return new XboxSDKStub();
#elif SWITCH
            return new SwitchSDKStub();
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && IsSteamDeck())
                return new SteamDeckSDK();
            return new NullConsoleSDK();
#endif
        }

        private static bool IsSteamDeck()
        {
            try
            {
                // Simple heuristic: check for SteamDeck specific environment or device tree
                var path = "/sys/class/dmi/id/product_name";
                if (File.Exists(path))
                {
                    var name = File.ReadAllText(path).Trim();
                    return name.Contains("Jupiter", StringComparison.OrdinalIgnoreCase) || name.Contains("Steam Deck", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        public static ConsolePlatform DetectPlatform()
        {
#if PS5
            return ConsolePlatform.PS5;
#elif XBOX
            return ConsolePlatform.XboxSeriesX;
#elif SWITCH
            return ConsolePlatform.NintendoSwitch;
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && IsSteamDeck())
                return ConsolePlatform.SteamDeck;
            return ConsolePlatform.PC;
#endif
        }
    }
}
