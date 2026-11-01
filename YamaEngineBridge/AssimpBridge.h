#ifndef SHANYU_ASSIMP_BRIDGE_H
#define SHANYU_ASSIMP_BRIDGE_H

#include <assimp/mesh.h>
#include <assimp/scene.h>
#include <assimp/postprocess.h>
#include <assimp/Importer.hpp>
#include <assimp/Exporter.hpp>
#include <assimp/material.h>
#include <assimp/camera.h>
#include <assimp/light.h>
#include <assimp/texture.h>
#include <assimp/types.h>

// ============================================================
// Assimp Bridge - 跨平台资产导入导出库
// 支持格式: FBX, OBJ, glTF, GLB, Collada, PLY, STL, 3DS, Blend
// ============================================================

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================
// C API 接口
// ============================================================

typedef void* AssimpImporter;
typedef void* AssimpScene;
typedef void* AssimpMesh;
typedef void* AssimpMaterial;
typedef void* AssimpNode;

// 导入选项
typedef struct {
    int calc_tangents;          // 计算切线和副法线
    int join_vertices;           // 合并重复顶点
    int triangulate;            // 三角化所有面
    int gen_normals;            // 生成法线
    int gen_smooth_normals;     // 生成平滑法线
    int split_large_meshes;     // 分割大网格
    int pre_transform_vertices;  // 预变换顶点
    int limit_bone_weights;     // 限制骨骼权重
    int validate_data_structure;// 验证数据结构
    int improve_cache_locality; // 改善缓存局部性
    int remove_redundant_materials; // 移除冗余材质
    int fix_infacing_normals;   // 修正法线方向
    int sort_by_ptype;          // 按图元类型排序
    int find_invalid_data;      // 查找无效数据
    int gen_uv_coords;          // 生成 UV 坐标
    int transform_uv_coords;    // 变换 UV 坐标
    int flip_uvs;               // 翻转 UV
    int flip_winding_order;     // 翻转缠绕顺序
    int split_by_bone_count;    // 按骨骼数量分割
    int debone;                 // 骨骼分离
} ImportOptions;

// 材质属性
typedef struct {
    char name[256];
    float diffuse[4];
    float specular[4];
    float ambient[4];
    float emissive[4];
    float transparent[4];
    float reflective[4];
    float bump[4];
    float shininess;
    float shininess_strength;
    float opacity;
    float reflectivity;
    float bump_multiplier;
    int two_sided;
    int wireframe;
    int use_alpha;
    int blend_mode; // 0=Opaque, 1=Alpha, 2=Add, 3=Mult
    char diffuse_texture[512];
    char specular_texture[512];
    char ambient_texture[512];
    char emissive_texture[512];
    char height_texture[512];
    char normals_texture[512];
    char shininess_texture[512];
    char opacity_texture[512];
    char displacement_texture[512];
    char lightmap_texture[512];
    char reflection_texture[512];
} MaterialData;

// 网格数据
typedef struct {
    char name[256];
    int vertex_count;
    int face_count;
    int material_index;
    
    // 顶点数据
    float* positions;
    float* normals;
    float* tangents;
    float* bitangents;
    float* colors;
    
    // UV 层 (最多 4 层)
    float* uvs[4];
    int uv_channel_count;
    
    // 骨骼数据
    int bone_count;
    void* bone_weights; // BoneWeight*
    
    // 面数据
    int* indices;
    int* face_sizes;
    
    // 边界盒
    float bbox_min[3];
    float bbox_max[3];
    
    // 变形目标
    int anim_mesh_count;
    void* anim_meshes;
} MeshData;

// 节点层级
typedef struct {
    char name[256];
    int mesh_count;
    int* mesh_indices;
    int child_count;
    void** children;
    float transform[16]; // 列主序 4x4 矩阵
} NodeData;

// 场景数据
typedef struct {
    char name[256];
    char source_file[512];
    int mesh_count;
    int material_count;
    int animation_count;
    int light_count;
    int camera_count;
    void** meshes;
    void** materials;
    void** animations;
    void** lights;
    void** cameras;
    void* root_node;
    
    // 统计
    unsigned int total_vertices;
    unsigned int total_faces;
    unsigned int total_bones;
    
    // 层级
    int node_count;
} SceneData;

// 动画数据
typedef struct {
    char name[256];
    double duration;
    double ticks_per_second;
    int channel_count;
    void** channels;
} AnimationData;

typedef struct {
    char node_name[256];
    int position_key_count;
    int rotation_key_count;
    int scale_key_count;
    void* position_keys; // VectorKey*
    void* rotation_keys; // QuaternionKey*
    void* scale_keys;    // VectorKey*
} AnimationChannel;

typedef struct {
    char name[256];
    char type[32]; // light, point, spot, directional, area
    float position[3];
    float direction[3];
    float color[3];
    float intensity;
    float angle;
    float attenuation_constant;
    float attenuation_linear;
    float attenuation_quadratic;
} LightData;

typedef struct {
    char name[256];
    float fov;
    float aspect;
    float near_clip;
    float far_clip;
    float position[3];
    float look_at[3];
    float up[3];
} CameraData;

// ============================================================
// 回调函数类型
// ============================================================

typedef void (*ProgressCallback)(float progress, void* user_data);
typedef void (*LogCallback)(int level, const char* message, void* user_data);

// ============================================================
// 导入器 API
// ============================================================

// 创建/销毁导入器
SHANYU_ASSIMP_API AssimpImporter Assimp_CreateImporter();
SHANYU_ASSIMP_API void Assimp_DestroyImporter(AssimpImporter importer);

// 设置回调
SHANYU_ASSIMP_API void Assimp_SetProgressCallback(AssimpImporter importer, ProgressCallback callback, void* user_data);
SHANYU_ASSIMP_API void Assimp_SetLogCallback(AssimpImporter importer, LogCallback callback, void* user_data);

// 导入
SHANYU_ASSIMP_API int Assimp_ImportFile(AssimpImporter importer, const char* file_path, unsigned int flags);
SHANYU_ASSIMP_API int Assimp_ImportFileFromMemory(AssimpImporter importer, const void* buffer, unsigned int size, unsigned int flags, const char* hint);
SHANYU_ASSIMP_API void Assimp_ReleaseImport(AssimpImporter importer);

// 获取场景
SHANYU_ASSIMP_API const aiScene* Assimp_GetScene(AssimpImporter importer);

// 获取错误信息
SHANYU_ASSIMP_API const char* Assimp_GetErrorString(AssimpImporter importer);

// ============================================================
// 场景 API
// ============================================================

SHANYU_ASSIMP_API SceneData* Assimp_CreateSceneData(const aiScene* scene, const char* source_file);
SHANYU_ASSIMP_API void Assimp_DestroySceneData(SceneData* data);

// 遍历节点
SHANYU_ASSIMP_API NodeData* Assimp_GetRootNode(SceneData* scene);
SHANYU_ASSIMP_API int Assimp_GetChildCount(NodeData* node);
SHANYU_ASSIMP_API NodeData* Assimp_GetChild(NodeData* node, int index);

// ============================================================
// 网格 API
// ============================================================

SHANYU_ASSIMP_API MeshData* Assimp_GetMesh(SceneData* scene, int index);
SHANYU_ASSIMP_API void Assimp_DestroyMeshData(MeshData* mesh);

// 顶点操作
SHANYU_ASSIMP_API void Assimp_ApplyTransform(MeshData* mesh, const float* matrix);
SHANYU_ASSIMP_API void Assimp_ReverseWindingOrder(MeshData* mesh);
SHIMP_API void Assimp_FlipUVs(MeshData* mesh);

// LOD 生成
SHANYU_ASSIMP_API MeshData* Assimp_GenerateLOD(MeshData* mesh, float ratio, int regenerate_normals);

// 网格合并
SHANYU_ASSIMP_API MeshData* Assimp_MergeMeshes(MeshData** meshes, int count);

// 网格分割
SHANYU_ASSIMP_API MeshData** Assimp_SplitMeshByMaterial(MeshData* mesh, int* out_count);

// ============================================================
// 材质 API
// ============================================================

SHANYU_ASSIMP_API MaterialData* Assimp_GetMaterial(SceneData* scene, int index);
SHANYU_ASSIMP_API void Assimp_DestroyMaterialData(MaterialData* mat);

// 材质属性读取
SHANYU_ASSIMP_API int Assimp_GetMaterialTextureCount(MaterialData* mat, int type);
SHANYU_ASSIMP_API int Assimp_GetMaterialTexture(MaterialData* mat, int type, int index, char* path, int* uv_index, float* blend, int* map_mode);

// PBR 属性
SHANYU_ASSIMP_API float Assimp_GetMaterialPropertyF(MaterialData* mat, const char* key, int type, int index);
SHANYU_ASSIMP_API void Assimp_GetMaterialColor(MaterialData* mat, const char* key, int type, float* color);

// ============================================================
// 动画 API
// ============================================================

SHANYU_ASSIMP_API AnimationData* Assimp_GetAnimation(SceneData* scene, int index);
SHANYU_ASSIMP_API void Assimp_DestroyAnimationData(AnimationData* anim);

SHANYU_ASSIMP_API AnimationChannel* Assimp_GetAnimationChannel(AnimationData* anim, int index);
SHANYU_ASSIMP_API void Assimp_DestroyAnimationChannel(AnimationChannel* channel);

// 骨骼操作
SHANYU_ASSIMP_API int Assimp_GetMeshBoneCount(MeshData* mesh);
SHANYU_ASSIMP_API void* Assimp_GetMeshBones(MeshData* mesh);

// ============================================================
// 光照 API
// ============================================================

SHANYU_ASSIMP_API LightData* Assimp_GetLight(SceneData* scene, int index);
SHANYU_ASSIMP_API void Assimp_DestroyLightData(LightData* light);

// ============================================================
// 相机 API
// ============================================================

SHANYU_ASSIMP_API CameraData* Assimp_GetCamera(SceneData* scene, int index);
SHANYU_ASSIMP_API void Assimp_DestroyCameraData(CameraData* camera);

// ============================================================
// 导出 API
// ============================================================

SHANYU_ASSIMP_API int Assimp_ExportScene(const SceneData* scene, const char* file_path, const char* format_id);
SHANYU_ASSIMP_API void* Assimp_ExportSceneToBlob(const SceneData* scene, const char* format_id);
SHANYU_ASSIMP_API void Assimp_FreeBlob(void* blob);

// 获取支持的格式
SHANYU_ASSIMP_API int Assimp_GetExportFormatCount();
SHANYU_ASSIMP_API const char* Assimp_GetExportFormatDescription(int index);
SHANYU_ASSIMP_API const char* Assimp_GetExportFormatExtension(int index);

// ============================================================
// 验证 API
// ============================================================

SHANYU_ASSIMP_API int Assimp_ValidateScene(const SceneData* scene);
SHANYU_ASSIMP_API int Assimp_HasInvalidContents(const SceneData* scene, char* error_buffer, int buffer_size);

// ============================================================
// 工具函数
// ============================================================

SHANYU_ASSIMP_API void Assimp_MakeLeftHanded(float matrix[16]);
SHANYU_ASSIMP_API void Assimp_ConvertToLeftHanded(SceneData* scene);
SHANYU_ASSIMP_API void Assimp_TransformScene(SceneData* scene, const float matrix[16]);

// 版本信息
SHANYU_ASSIMP_API unsigned int Assimp_GetVersion();
SHANYU_ASSIMP_API const char* Assimp_GetBranchName();
SHANYU_ASSIMP_API const char* Assimp_GetCompileFlags();

#ifdef __cplusplus
}
#endif

#endif // SHANYU_ASSIMP_BRIDGE_H
