#pragma once

#ifdef SHANYU_EXPORTS
#define SHANYU_API __declspec(dllexport)
#else
#define SHANYU_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* ShanyuHandle;

SHANYU_API int ShanyuEngine_Initialize(void* windowHandle);
SHANYU_API int ShanyuEngine_Shutdown();
SHANYU_API int ShanyuEngine_BeginFrame();
SHANYU_API int ShanyuEngine_EndFrame();
SHANYU_API int ShanyuEngine_ResizeSwapChain(int width, int height);
SHANYU_API void ShanyuEngine_SetGizmoMode(int mode);
SHANYU_API int ShanyuEngine_LoadScene(const wchar_t* path);
SHANYU_API int ShanyuEngine_SaveScene(const wchar_t* path);
SHANYU_API void* ShanyuEngine_GetNativeHandle();
SHANYU_API void* ShanyuEngine_GetSwapChain();

SHANYU_API int Shanyu_Scene_CreateNode(const wchar_t* name);
SHANYU_API int Shanyu_Scene_DeleteNode(int nodeId);
SHANYU_API int Shanyu_Scene_SetNodeTransform(int nodeId, float* position, float* rotation, float* scale);
SHANYU_API int Shanyu_Scene_GetNodeTransform(int nodeId, float* position, float* rotation, float* scale);
SHANYU_API int Shanyu_Scene_GetSelectedNode();

SHANYU_API int Shanyu_Blender_Launch();
SHANYU_API int Shanyu_Blender_Sync();
SHANYU_API int Shanyu_Blender_ImportScene(const wchar_t* path);

SHANYU_API int Shanyu_Asset_ImportUnity(const wchar_t* folder);
SHANYU_API int Shanyu_Asset_ImportUnreal(const wchar_t* folder);

SHANYU_API void Shanyu_Log_Clear();
SHANYU_API void Shanyu_Log_Write(const wchar_t* message);

#ifdef __cplusplus
}
#endif
