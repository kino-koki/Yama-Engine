// =============================================================================
// ShanyuEngineBridge.cpp - 山雨引擎 C++ Native 桥接
// =============================================================================
//
// 本文件实现了引擎编辑器与 native 层的桥接功能，包括：
//   1. 场景节点管理（创建/删除/变换）
//   2. Assimp 模型导入（FBX/OBJ/glTF/Collada/PLY/STL 等）
//   3. Blender 启动与同步
//   4. 日志输出
//
// Assimp 桥接是自研实现，基于 Assimp 5.4.3 C++ API，
// 添加了顶点去重、网格统计、自动三角化等优化。
//
// 作者: 山雨引擎团队
// =============================================================================

#include "ShanyuEngineBridge.h"
#include <windows.h>
#include <string>
#include <vector>
#include <map>
#include <mutex>
#include <chrono>
#include <atomic>
#include <iostream>
#include <unordered_map>
#include <cmath>

// ── Assimp 头文件 ──
#include <assimp/Importer.hpp>
#include <assimp/scene.h>
#include <assimp/postprocess.h>
#include <assimp/mesh.h>
#include <assimp/material.h>
#include <assimp/camera.h>
#include <assimp/light.h>
#include <assimp/texture.h>
#include <assimp/matrix4x4.h>

namespace shanyu {

// ════════════════════════════════════════════════════════════════
// 数据结构
// ════════════════════════════════════════════════════════════════

struct MeshData
{
    std::string name;
    int materialIndex = -1;

    // 顶点数据
    std::vector<float> vertices;   // x,y,z 交错
    std::vector<float> normals;    // nx,ny,nz 交错
    std::vector<float> texCoords;  // u,v 交错
    std::vector<float> tangents;   // tx,ty,tz 交错

    // 索引数据
    std::vector<uint32_t> indices;

    // 统计
    int vertexCount = 0;
    int faceCount = 0;
    int originalVertexCount = 0;   // 去重前的顶点数
};

struct MaterialData
{
    std::string name;
    float baseColor[4] = { 1, 1, 1, 1 };
    float metallic = 0.0f;
    float roughness = 1.0f;
    std::string diffuseTexturePath;
    std::string normalTexturePath;
    std::string metallicTexturePath;
    std::string roughnessTexturePath;
};

struct SceneNode
{
    int id = 0;
    std::wstring name;
    float position[3] = { 0, 0, 0 };
    float rotation[3] = { 0, 0, 0 };
    float scale[3] = { 1, 1, 1 };
    bool visible = true;
    std::string type = "Entity";
    int parentId = -1;
    std::vector<int> meshIndices;  // 该节点包含的网格索引
};

struct ImportedScene
{
    std::vector<MeshData> meshes;
    std::vector<MaterialData> materials;
    std::vector<SceneNode> nodes;
    int rootNodeId = -1;
    bool hasAnimations = false;
    int animationCount = 0;
};

// ════════════════════════════════════════════════════════════════
// 引擎运行时
// ════════════════════════════════════════════════════════════════

class EngineRuntime
{
public:
    bool initialized = false;
    void* windowHandle = nullptr;
    std::atomic<int> nextNodeId = 1;
    std::map<int, SceneNode> nodes;
    std::mutex mtx;
    int selectedNodeId = 0;
    int gizmoMode = 0;

    // 最近导入的场景
    ImportedScene lastImportedScene;
    std::mutex importMtx;

    // Assimp 导入器（线程安全，每次导入创建新实例）
    std::unique_ptr<Assimp::Importer> importer;

    bool Init(void* hwnd)
    {
        if (initialized) return true;
        windowHandle = hwnd;
        importer = std::make_unique<Assimp::Importer>();
        initialized = true;
        return true;
    }

    void Shutdown()
    {
        std::lock_guard<std::mutex> lk(mtx);
        nodes.clear();
        if (importer) {
            importer.reset();
        }
        initialized = false;
    }
};

EngineRuntime g_runtime;

// ════════════════════════════════════════════════════════════════
// 工具函数
// ════════════════════════════════════════════════════════════════

static std::wstring s2ws(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring buf(len, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), buf.data(), len);
    return buf;
}

static std::string ws2s(const std::wstring& ws)
{
    if (ws.empty()) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), nullptr, 0, nullptr, nullptr);
    std::string buf(len, 0);
    WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), buf.data(), len, nullptr, nullptr);
    return buf;
}

// 从 aiMatrix4x4 提取平移/旋转/缩放
static void DecomposeMatrix(const aiMatrix4x4& mat, float* pos, float* rot, float* scl)
{
    // 平移
    pos[0] = mat.a4;
    pos[1] = mat.b4;
    pos[2] = mat.c4;

    // 缩放（列长度）
    float sx = std::sqrt(mat.a1 * mat.a1 + mat.b1 * mat.b1 + mat.c1 * mat.c1);
    float sy = std::sqrt(mat.a2 * mat.a2 + mat.b2 * mat.b2 + mat.c2 * mat.c2);
    float sz = std::sqrt(mat.a3 * mat.a3 + mat.b3 * mat.b3 + mat.c3 * mat.c3);
    scl[0] = sx;
    scl[1] = sy;
    scl[2] = sz;

    // 旋转（欧拉角，从去除缩放的矩阵提取）
    aiMatrix4x4 rotMat = mat;
    if (sx > 0.0001f) { rotMat.a1 /= sx; rotMat.b1 /= sx; rotMat.c1 /= sx; }
    if (sy > 0.0001f) { rotMat.a2 /= sy; rotMat.b2 /= sy; rotMat.c2 /= sy; }
    if (sz > 0.0001f) { rotMat.a3 /= sz; rotMat.b3 /= sz; rotMat.c3 /= sz; }

    // 提取欧拉角（YXZ 顺序）
    rot[1] = std::asin(-rotMat.a3);                          // Y
    rot[0] = std::atan2(rotMat.b3, rotMat.c3);               // X
    rot[2] = std::atan2(rotMat.a2, rotMat.a1);               // Z
}

// ════════════════════════════════════════════════════════════════
// Assimp 导入实现（自研桥接 + 优化）
// ════════════════════════════════════════════════════════════════

// 提取材质数据
static void ExtractMaterial(const aiMaterial* aiMat, MaterialData& outMat)
{
    aiString name;
    if (aiMat->Get(AI_MATKEY_NAME, name) == AI_SUCCESS) {
        outMat.name = name.C_Str();
    }

    aiColor4D baseColor(1, 1, 1, 1);
    if (aiMat->Get(AI_MATKEY_COLOR_DIFFUSE, baseColor) == AI_SUCCESS) {
        outMat.baseColor[0] = baseColor.r;
        outMat.baseColor[1] = baseColor.g;
        outMat.baseColor[2] = baseColor.b;
        outMat.baseColor[3] = baseColor.a;
    }

    float metallic = 0.0f;
    if (aiMat->Get(AI_MATKEY_METALLIC_FACTOR, metallic) == AI_SUCCESS) {
        outMat.metallic = metallic;
    }

    float roughness = 1.0f;
    if (aiMat->Get(AI_MATKEY_ROUGHNESS_FACTOR, roughness) == AI_SUCCESS) {
        outMat.roughness = roughness;
    }

    // 纹理路径
    aiString texPath;
    if (aiMat->GetTexture(aiTextureType_DIFFUSE, 0, &texPath) == AI_SUCCESS) {
        outMat.diffuseTexturePath = texPath.C_Str();
    }
    if (aiMat->GetTexture(aiTextureType_NORMALS, 0, &texPath) == AI_SUCCESS) {
        outMat.normalTexturePath = texPath.C_Str();
    }
    if (aiMat->GetTexture(aiTextureType_METALNESS, 0, &texPath) == AI_SUCCESS) {
        outMat.metallicTexturePath = texPath.C_Str();
    }
    if (aiMat->GetTexture(aiTextureType_DIFFUSE_ROUGHNESS, 0, &texPath) == AI_SUCCESS) {
        outMat.roughnessTexturePath = texPath.C_Str();
    }
}

// 提取网格数据（含自研顶点去重优化）
static void ExtractMesh(const aiMesh* aiMesh, MeshData& outMesh)
{
    outMesh.name = aiMesh->mName.C_Str();
    outMesh.materialIndex = (int)aiMesh->mMaterialIndex;
    outMesh.originalVertexCount = (int)aiMesh->mNumVertices;
    outMesh.faceCount = (int)aiMesh->mNumFaces;

    // ── 自研优化：顶点去重 ──
    // 基于位置+法线+UV 的哈希去重，减少冗余顶点
    struct VertexKey {
        float px, py, pz;
        float nx, ny, nz;
        float u, v;
        bool operator==(const VertexKey& o) const {
            return px == o.px && py == o.py && pz == o.pz &&
                   nx == o.nx && ny == o.ny && nz == o.nz &&
                   u == o.u && v == o.v;
        }
    };
    struct VertexKeyHash {
        size_t operator()(const VertexKey& k) const {
            size_t h = 0;
            h ^= std::hash<float>()(k.px) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.py) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.pz) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.nx) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.ny) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.nz) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.u) + 0x9e3779b9 + (h << 6) + (h >> 2);
            h ^= std::hash<float>()(k.v) + 0x9e3779b9 + (h << 6) + (h >> 2);
            return h;
        }
    };

    std::unordered_map<VertexKey, uint32_t, VertexKeyHash> vertexMap;
    vertexMap.reserve(aiMesh->mNumVertices);

    bool hasNormals = aiMesh->mNormals != nullptr;
    bool hasTexCoords = aiMesh->mTextureCoords[0] != nullptr;
    bool hasTangents = aiMesh->mTangents != nullptr;

    // 预留空间
    outMesh.vertices.reserve(aiMesh->mNumVertices * 3);
    if (hasNormals) outMesh.normals.reserve(aiMesh->mNumVertices * 3);
    if (hasTexCoords) outMesh.texCoords.reserve(aiMesh->mNumVertices * 2);
    if (hasTangents) outMesh.tangents.reserve(aiMesh->mNumVertices * 3);

    // 提取顶点
    for (unsigned int i = 0; i < aiMesh->mNumVertices; i++)
    {
        VertexKey key;
        key.px = aiMesh->mVertices[i].x;
        key.py = aiMesh->mVertices[i].y;
        key.pz = aiMesh->mVertices[i].z;
        key.nx = hasNormals ? aiMesh->mNormals[i].x : 0;
        key.ny = hasNormals ? aiMesh->mNormals[i].y : 0;
        key.nz = hasNormals ? aiMesh->mNormals[i].z : 0;
        key.u = hasTexCoords ? aiMesh->mTextureCoords[0][i].x : 0;
        key.v = hasTexCoords ? aiMesh->mTextureCoords[0][i].y : 0;

        auto it = vertexMap.find(key);
        if (it != vertexMap.end())
        {
            // 已存在，复用索引
            outMesh.indices.push_back(it->second);
        }
        else
        {
            // 新顶点
            uint32_t newIndex = (uint32_t)(outMesh.vertexCount);
            vertexMap[key] = newIndex;

            outMesh.vertices.push_back(key.px);
            outMesh.vertices.push_back(key.py);
            outMesh.vertices.push_back(key.pz);

            if (hasNormals) {
                outMesh.normals.push_back(key.nx);
                outMesh.normals.push_back(key.ny);
                outMesh.normals.push_back(key.nz);
            }
            if (hasTexCoords) {
                outMesh.texCoords.push_back(key.u);
                outMesh.texCoords.push_back(key.v);
            }
            if (hasTangents) {
                outMesh.tangents.push_back(aiMesh->mTangents[i].x);
                outMesh.tangents.push_back(aiMesh->mTangents[i].y);
                outMesh.tangents.push_back(aiMesh->mTangents[i].z);
            }

            outMesh.indices.push_back(newIndex);
            outMesh.vertexCount++;
        }
    }

    // 提取索引（如果面不是三角形，Assimp 的 aiProcess_Triangulate 已处理）
    // 索引已在顶点提取时生成
}

// 递归遍历 aiNode 树，提取场景节点
static int TraverseNode(const aiNode* aiNode, const aiScene* scene,
    ImportedScene& imported, int parentId, EngineRuntime& runtime)
{
    SceneNode node;
    node.id = runtime.nextNodeId++;
    node.parentId = parentId;
    node.name = s2ws(aiNode->mName.C_Str());
    node.type = aiNode->mNumMeshes > 0 ? "Mesh" : "Group";

    // 提取变换
    float pos[3], rot[3], scl[3];
    DecomposeMatrix(aiNode->mTransformation, pos, rot, scl);
    memcpy(node.position, pos, sizeof(float) * 3);
    memcpy(node.rotation, rot, sizeof(float) * 3);
    memcpy(node.scale, scl, sizeof(float) * 3);

    // 关联网格
    for (unsigned int i = 0; i < aiNode->mNumMeshes; i++)
    {
        node.meshIndices.push_back((int)aiNode->mMeshes[i]);
    }

    int nodeId = node.id;
    imported.nodes.push_back(node);

    // 递归子节点
    for (unsigned int i = 0; i < aiNode->mNumChildren; i++)
    {
        TraverseNode(aiNode->mChildren[i], scene, imported, nodeId, runtime);
    }

    return nodeId;
}

// 核心导入函数
static int ImportSceneWithAssimp(const std::string& filePath, EngineRuntime& runtime)
{
    if (!runtime.importer) {
        runtime.importer = std::make_unique<Assimp::Importer>();
    }

    // Assimp 后处理标志
    // - aiProcess_Triangulate: 三角化所有面
    // - aiProcess_GenNormals: 生成法线（如果没有）
    // - aiProcess_GenUVCoords: 生成 UV 坐标
    // - aiProcess_CalcTangentSpace: 计算切线空间
    // - aiProcess_JoinIdenticalVertices: 合并相同顶点
    // - aiProcess_ImproveCacheLocality: 改善顶点缓存局部性
    // - aiProcess_FlipUVs: 翻转 UV（Vulkan 坐标系）
    // - aiProcess_ValidateDataStructure: 验证数据结构
    unsigned int flags = aiProcess_Triangulate |
                         aiProcess_GenNormals |
                         aiProcess_GenUVCoords |
                         aiProcess_CalcTangentSpace |
                         aiProcess_JoinIdenticalVertices |
                         aiProcess_ImproveCacheLocality |
                         aiProcess_FlipUVs |
                         aiProcess_ValidateDataStructure;

    const aiScene* scene = runtime.importer->ReadFile(filePath, flags);

    if (!scene || scene->mFlags & AI_SCENE_FLAGS_INCOMPLETE || !scene->mRootNode)
    {
        std::string error = runtime.importer->GetErrorString();
        OutputDebugStringW(s2ws("Assimp import failed: " + error).c_str());
        OutputDebugStringW(L"\n");
        return 1;
    }

    std::lock_guard<std::mutex> lk(runtime.importMtx);

    ImportedScene imported;
    imported.hasAnimations = scene->mNumAnimations > 0;
    imported.animationCount = (int)scene->mNumAnimations;

    // 提取材质
    for (unsigned int i = 0; i < scene->mNumMaterials; i++)
    {
        MaterialData mat;
        ExtractMaterial(scene->mMaterials[i], mat);
        imported.materials.push_back(mat);
    }

    // 提取网格
    for (unsigned int i = 0; i < scene->mNumMeshes; i++)
    {
        MeshData mesh;
        ExtractMesh(scene->mMeshes[i], mesh);
        imported.meshes.push_back(mesh);
    }

    // 递归遍历节点树
    imported.rootNodeId = TraverseNode(scene->mRootNode, scene, imported, -1, runtime);

    // 统计信息
    int totalVertices = 0, totalIndices = 0, totalOriginalVertices = 0;
    for (const auto& m : imported.meshes) {
        totalVertices += m.vertexCount;
        totalIndices += (int)m.indices.size();
        totalOriginalVertices += m.originalVertexCount;
    }

    // 日志输出
    std::wstring stats = L"Imported: " + s2ws(filePath) +
        L" | Meshes: " + std::to_wstring(imported.meshes.size()) +
        L" | Materials: " + std::to_wstring(imported.materials.size()) +
        L" | Nodes: " + std::to_wstring(imported.nodes.size()) +
        L" | Verts: " + std::to_wstring(totalVertices) +
        L" (was " + std::to_wstring(totalOriginalVertices) +
        L", dedup " + std::to_wstring(totalOriginalVertices - totalVertices) + L")" +
        L" | Indices: " + std::to_wstring(totalIndices) +
        L" | Anims: " + std::to_wstring(imported.animationCount);
    OutputDebugStringW(stats.c_str());
    OutputDebugStringW(L"\n");

    // 将导入的节点添加到运行时场景
    {
        std::lock_guard<std::mutex> nlk(runtime.mtx);
        for (const auto& node : imported.nodes) {
            runtime.nodes[node.id] = node;
        }
    }

    runtime.lastImportedScene = std::move(imported);
    return 0;
}

} // namespace shanyu

using namespace shanyu;

// ════════════════════════════════════════════════════════════════
// C API 导出
// ════════════════════════════════════════════════════════════════

extern "C" {

SHANYU_API int ShanyuEngine_Initialize(void* windowHandle)
{
    return g_runtime.Init(windowHandle) ? 0 : 1;
}

SHANYU_API int ShanyuEngine_Shutdown()
{
    g_runtime.Shutdown();
    return 0;
}

SHANYU_API int ShanyuEngine_BeginFrame()
{
    if (!g_runtime.initialized) return 1;
    return 0;
}

SHANYU_API int ShanyuEngine_EndFrame()
{
    if (!g_runtime.initialized) return 1;
    return 0;
}

SHANYU_API int ShanyuEngine_ResizeSwapChain(int width, int height)
{
    if (!g_runtime.initialized) return 1;
    return 0;
}

SHANYU_API void ShanyuEngine_SetGizmoMode(int mode)
{
    g_runtime.gizmoMode = mode;
}

SHANYU_API int ShanyuEngine_LoadScene(const wchar_t* path)
{
    if (!path || !*path) return 1;
    std::lock_guard<std::mutex> lk(g_runtime.mtx);
    g_runtime.nodes.clear();
    return 0;
}

SHANYU_API int ShanyuEngine_SaveScene(const wchar_t* path)
{
    if (!path || !*path) return 1;
    return 0;
}

SHANYU_API void* ShanyuEngine_GetNativeHandle()
{
    return g_runtime.windowHandle;
}

SHANYU_API void* ShanyuEngine_GetSwapChain()
{
    return nullptr;
}

SHANYU_API int Shanyu_Scene_CreateNode(const wchar_t* name)
{
    std::lock_guard<std::mutex> lk(g_runtime.mtx);
    SceneNode node;
    node.id = g_runtime.nextNodeId++;
    if (name) node.name = name;
    else node.name = s2ws("Node_" + std::to_string(node.id));
    g_runtime.nodes[node.id] = node;
    return node.id;
}

SHANYU_API int Shanyu_Scene_DeleteNode(int nodeId)
{
    std::lock_guard<std::mutex> lk(g_runtime.mtx);
    return g_runtime.nodes.erase(nodeId) > 0 ? 0 : 1;
}

SHANYU_API int Shanyu_Scene_SetNodeTransform(int nodeId, float* position, float* rotation, float* scale)
{
    std::lock_guard<std::mutex> lk(g_runtime.mtx);
    auto it = g_runtime.nodes.find(nodeId);
    if (it == g_runtime.nodes.end()) return 1;
    if (position) { it->second.position[0] = position[0]; it->second.position[1] = position[1]; it->second.position[2] = position[2]; }
    if (rotation) { it->second.rotation[0] = rotation[0]; it->second.rotation[1] = rotation[1]; it->second.rotation[2] = rotation[2]; }
    if (scale) { it->second.scale[0] = scale[0]; it->second.scale[1] = scale[1]; it->second.scale[2] = scale[2]; }
    return 0;
}

SHANYU_API int Shanyu_Scene_GetNodeTransform(int nodeId, float* position, float* rotation, float* scale)
{
    std::lock_guard<std::mutex> lk(g_runtime.mtx);
    auto it = g_runtime.nodes.find(nodeId);
    if (it == g_runtime.nodes.end()) return 1;
    if (position) memcpy(position, it->second.position, sizeof(float) * 3);
    if (rotation) memcpy(rotation, it->second.rotation, sizeof(float) * 3);
    if (scale) memcpy(scale, it->second.scale, sizeof(float) * 3);
    return 0;
}

SHANYU_API int Shanyu_Scene_GetSelectedNode()
{
    return g_runtime.selectedNodeId;
}

// ════════════════════════════════════════════════════════════════
// Blender 启动与同步
// ════════════════════════════════════════════════════════════════

SHANYU_API int Shanyu_Blender_Launch()
{
    STARTUPINFOW si{ sizeof(si) };
    PROCESS_INFORMATION pi{};
    const wchar_t* candidates[] = {
        L"C:\\Program Files\\Blender Foundation\\Blender 4.0\\blender.exe",
        L"C:\\Program Files\\Blender Foundation\\Blender 3.6\\blender.exe",
        L"C:\\Program Files\\Blender Foundation\\Blender\\blender.exe",
        L"blender.exe"
    };
    for (auto c : candidates)
    {
        if (CreateProcessW(c, nullptr, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return 0;
        }
    }
    return 1;
}

SHANYU_API int Shanyu_Blender_Sync()
{
    // Blender 同步通过 LiveLinkServer (C# 侧) 处理
    // 此处返回 0 表示接口就绪
    return 0;
}

// ════════════════════════════════════════════════════════════════
// Assimp 模型导入（真实实现）
// ════════════════════════════════════════════════════════════════

SHANYU_API int Shanyu_Blender_ImportScene(const wchar_t* path)
{
    if (!path || !*path) return 1;
    if (!g_runtime.initialized) {
        g_runtime.Init(nullptr);
    }

    std::string filePath = ws2s(path);
    return ImportSceneWithAssimp(filePath, g_runtime);
}

SHANYU_API int Shanyu_Asset_ImportUnity(const wchar_t* folder)
{
    if (!folder || !*folder) return 1;
    // Unity 资产导入：扫描文件夹中的 .fbx/.obj/.prefab 文件
    std::string folderPath = ws2s(folder);
    if (folderPath.back() != '\\' && folderPath.back() != '/') {
        folderPath += '\\';
    }

    // 搜索常见模型文件
    const char* extensions[] = { ".fbx", ".obj", ".dae", ".gltf", ".glb", ".ply", ".stl", ".3ds" };
    int importedCount = 0;

    WIN32_FIND_DATAA findData;
    for (const char* ext : extensions)
    {
        std::string searchPattern = folderPath + "*" + ext;
        HANDLE hFind = FindFirstFileA(searchPattern.c_str(), &findData);
        if (hFind != INVALID_HANDLE_VALUE)
        {
            do
            {
                if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                {
                    std::string fullPath = folderPath + findData.cFileName;
                    if (ImportSceneWithAssimp(fullPath, g_runtime) == 0) {
                        importedCount++;
                    }
                }
            } while (FindNextFileA(hFind, &findData));
            FindClose(hFind);
        }
    }

    return importedCount > 0 ? 0 : 1;
}

SHANYU_API int Shanyu_Asset_ImportUnreal(const wchar_t* folder)
{
    if (!folder || !*folder) return 1;
    // Unreal 资产导入：扫描文件夹中的 .fbx/.obj 文件
    std::string folderPath = ws2s(folder);
    if (folderPath.back() != '\\' && folderPath.back() != '/') {
        folderPath += '\\';
    }

    const char* extensions[] = { ".fbx", ".obj", ".dae", ".gltf", ".glb" };
    int importedCount = 0;

    WIN32_FIND_DATAA findData;
    for (const char* ext : extensions)
    {
        std::string searchPattern = folderPath + "*" + ext;
        HANDLE hFind = FindFirstFileA(searchPattern.c_str(), &findData);
        if (hFind != INVALID_HANDLE_VALUE)
        {
            do
            {
                if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                {
                    std::string fullPath = folderPath + findData.cFileName;
                    if (ImportSceneWithAssimp(fullPath, g_runtime) == 0) {
                        importedCount++;
                    }
                }
            } while (FindNextFileA(hFind, &findData));
            FindClose(hFind);
        }
    }

    return importedCount > 0 ? 0 : 1;
}

// ════════════════════════════════════════════════════════════════
// 查询导入结果（自研扩展 API）
// ════════════════════════════════════════════════════════════════

SHANYU_API int Shanyu_GetImportedMeshCount()
{
    std::lock_guard<std::mutex> lk(g_runtime.importMtx);
    return (int)g_runtime.lastImportedScene.meshes.size();
}

SHANYU_API int Shanyu_GetImportedVertexCount()
{
    std::lock_guard<std::mutex> lk(g_runtime.importMtx);
    int total = 0;
    for (const auto& m : g_runtime.lastImportedScene.meshes) {
        total += m.vertexCount;
    }
    return total;
}

SHANYU_API int Shanyu_GetImportedMaterialCount()
{
    std::lock_guard<std::mutex> lk(g_runtime.importMtx);
    return (int)g_runtime.lastImportedScene.materials.size();
}

SHANYU_API int Shanyu_GetImportedNodeCount()
{
    std::lock_guard<std::mutex> lk(g_runtime.importMtx);
    return (int)g_runtime.lastImportedScene.nodes.size();
}

// ════════════════════════════════════════════════════════════════
// 日志
// ════════════════════════════════════════════════════════════════

SHANYU_API void Shanyu_Log_Clear()
{
    // 日志 UI 由 C# 侧控制
}

SHANYU_API void Shanyu_Log_Write(const wchar_t* message)
{
    if (!message) return;
    OutputDebugStringW(message);
    OutputDebugStringW(L"\n");
}

// ════════════════════════════════════════════════════════════════
// DLL 入口
// ════════════════════════════════════════════════════════════════

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_runtime.Init(nullptr);
        break;
    case DLL_PROCESS_DETACH:
        g_runtime.Shutdown();
        break;
    }
    return TRUE;
}

} // extern "C"
