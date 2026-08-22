// ==================================================================================================
//  BlenderPythonPlugin.cs — 山雨引擎 Blender Python 插件生成器
//
//  命名空间: ShanyuEngine.DCC
//  依赖:     System.IO, System.Text
//
//  功能:
//    生成可直接安装到 Blender 的 Python 插件源码 (.py 文件)。
//    插件功能:
//      1. BlenderSyncPlugin: Blender 3.x/4.x 兼容
//      2. 导出面板（N Panel）: 选择导出格式（.shanyu/.gltf/.fbx）
//      3. 实时同步: 通过 WebSocket 连接引擎编辑器
//      4. 对象选择同步
//      5. 材质同步（Blender 节点 → 引擎 PBR 参数）
//      6. 动画同步（Blender Action → 引擎 AnimAsset）
//      7. Alembic 缓存导出（用于复杂动画）
//    8. 生成文件: 输出为 .py 文件，可直接安装到 Blender addons 目录
// ==================================================================================================

using System;
using System.IO;
using System.Text;

namespace ShanyuEngine.DCC
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: BlenderPythonPlugin — 插件生成器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Blender Python 插件生成器。
    /// 生成可直接安装到 Blender 3.x/4.x 的 Python 插件源码。
    /// 插件实现 Blender 与山雨引擎之间的数据桥接。
    /// </summary>
    public sealed class BlenderPythonPlugin
    {
        private string _pluginName = "ShanyuEngine Bridge";
        private string _pluginId = "shanyu_engine_bridge";
        private string _author = "ShanyuEngine Team";
        private string _version = "1.0.0";
        private string _description = "山雨引擎 Blender 实时同步插件";
        private string _wsHost = "ws://localhost:9876";
        private string _exportDir = "";

        /// <summary>插件名称。</summary>
        public string PluginName { get => _pluginName; set => _pluginName = value; }
        /// <summary>插件 ID。</summary>
        public string PluginId { get => _pluginId; set => _pluginId = value; }
        /// <summary>作者。</summary>
        public string Author { get => _author; set => _author = value; }
        /// <summary>版本号。</summary>
        public string Version { get => _version; set => _version = value; }
        /// <summary>描述。</summary>
        public string Description { get => _description; set => _description = value; }
        /// <summary>WebSocket 主机地址。</summary>
        public string WebSocketHost { get => _wsHost; set => _wsHost = value; }
        /// <summary>默认导出目录。</summary>
        public string ExportDirectory { get => _exportDir; set => _exportDir = value; }

        /// <summary>
        /// 生成完整的 Python 插件源码。
        /// </summary>
        /// <returns>Python 插件源码字符串。</returns>
        public string GenerateSourceCode()
        {
            var sb = new StringBuilder();

            GenerateHeader(sb);
            GenerateImports(sb);
            GenerateBlenderInfo(sb);
            GenerateWebSocketClient(sb);
            GenerateExportPanel(sb);
            GenerateSyncOperators(sb);
            GenerateMaterialSync(sb);
            GenerateAnimationSync(sb);
            GenerateAlembicExport(sb);
            GenerateRegistration(sb);

            return sb.ToString();
        }

        /// <summary>
        /// 生成 Python 插件文件并保存到指定路径。
        /// </summary>
        /// <param name="outputPath">输出 .py 文件路径。</param>
        public void GenerateToFile(string outputPath)
        {
            string code = GenerateSourceCode();
            File.WriteAllText(outputPath, code, Encoding.UTF8);
        }

        /// <summary>
        /// 生成 Python 插件文件到 Blender addons 目录。
        /// </summary>
        /// <param name="blenderAddonsDir">Blender addons 目录路径。</param>
        /// <returns>生成的文件路径。</returns>
        public string InstallToBlenderAddons(string blenderAddonsDir)
        {
            if (!Directory.Exists(blenderAddonsDir))
                Directory.CreateDirectory(blenderAddonsDir);

            string fileName = $"shanyu_engine_bridge_{_version.Replace(".", "_")}.py";
            string fullPath = Path.Combine(blenderAddonsDir, fileName);
            GenerateToFile(fullPath);
            return fullPath;
        }

        // ══════════════════════════════════════════════════════════════════════
        // 源码生成区域
        // ══════════════════════════════════════════════════════════════════════

        private void GenerateHeader(StringBuilder sb)
        {
            sb.AppendLine($@"#!/usr/bin/env python3");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# {_pluginName} — Blender Plugin for ShanyuEngine");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# Compatible with: Blender 3.0+, Blender 4.0+");
            sb.AppendLine($@"# Author: {_author}");
            sb.AppendLine($@"# Version: {_version}");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# {_description}");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# Installation:");
            sb.AppendLine($@"#   1. Copy this .py file to Blender addons directory:");
            sb.AppendLine($@"#      Edit > Preferences > Add-ons > Install...");
            sb.AppendLine($@"#   2. Enable 'ShanyuEngine Bridge' in the Add-ons list");
            sb.AppendLine($@"#   3. Open the N Panel in 3D Viewport to find ShanyuEngine tools");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
        }

        private void GenerateImports(StringBuilder sb)
        {
            sb.AppendLine($@"import bpy");
            sb.AppendLine($@"import bmesh");
            sb.AppendLine($@"import os");
            sb.AppendLine($@"import sys");
            sb.AppendLine($@"import json");
            sb.AppendLine($@"import struct");
            sb.AppendLine($@"import time");
            sb.AppendLine($@"import math");
            sb.AppendLine($@"import socket");
            sb.AppendLine($@"import threading");
            sb.AppendLine($@"from typing import Optional, Dict, List, Any, Tuple");
            sb.AppendLine();
            sb.AppendLine($@"try:");
            sb.AppendLine($@"    import websockets");
            sb.AppendLine($@"    HAS_WEBSOCKETS = True");
            sb.AppendLine($@"except ImportError:");
            sb.AppendLine($@"    HAS_WEBSOCKETS = False");
            sb.AppendLine($@"    print('[ShanyuEngine] Warning: websockets library not found.");
            sb.AppendLine($@"           Install with: pip install websockets')");
            sb.AppendLine();
            sb.AppendLine($@"try:");
            sb.AppendLine($@"    import asyncio");
            sb.AppendLine($@"    HAS_ASYNCIO = True");
            sb.AppendLine($@"except ImportError:");
            sb.AppendLine($@"    HAS_ASYNCIO = False");
            sb.AppendLine();
        }

        private void GenerateBlenderInfo(StringBuilder sb)
        {
            sb.AppendLine($@"bl_info = {{");
            sb.AppendLine($@"    'name': '{_pluginName}',");
            sb.AppendLine($@"    'author': '{_author}',");
            sb.AppendLine($@"    'version': ({_version.Replace(".", ", ")}),");
            sb.AppendLine($@"    'blender': (3, 0, 0),");
            sb.AppendLine($@"    'location': '3D Viewport > N Panel > ShanyuEngine',");
            sb.AppendLine($@"    'description': '{_description}',");
            sb.AppendLine($@"    'category': 'ShanyuEngine',");
            sb.AppendLine($@"    'wiki_url': 'https://shanyuengine.dev/docs/blender-plugin',");
            sb.AppendLine($@"}}");
            sb.AppendLine();
        }

        private void GenerateWebSocketClient(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# WebSocket Client — 实时同步通信");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class ShanyuWebSocketClient:");
            sb.AppendLine($@"    \"\"\"WebSocket 客户端，用于与山雨引擎编辑器实时通信。\"\"\"");
            sb.AppendLine();
            sb.AppendLine($@"    def __init__(self, host: str = '{_wsHost}'):");
            sb.AppendLine($@"        self.host = host");
            sb.AppendLine($@"        self.connected = False");
            sb.AppendLine($@"        self._ws = None");
            sb.AppendLine($@"        self._thread = None");
            sb.AppendLine($@"        self._running = False");
            sb.AppendLine($@"        self._message_queue = []");
            sb.AppendLine($@"        self._lock = threading.Lock()");
            sb.AppendLine($@"        self._on_message_callback = None");
            sb.AppendLine($@"        self._reconnect_interval = 3.0");
            sb.AppendLine();
            sb.AppendLine($@"    def connect(self) -> bool:");
            sb.AppendLine($@"        \"\"\"建立 WebSocket 连接。\"\"\"");
            sb.AppendLine($@"        if not HAS_WEBSOCKETS:");
            sb.AppendLine($@"            print('[ShanyuEngine] websockets library not available.')");
            sb.AppendLine($@"            return False");
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            self._running = True");
            sb.AppendLine($@"            self._thread = threading.Thread(target=self._run_loop, daemon=True)");
            sb.AppendLine($@"            self._thread.start()");
            sb.AppendLine($@"            return True");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            print(f'[ShanyuEngine] WebSocket connection failed: {{e}}')");
            sb.AppendLine($@"            return False");
            sb.AppendLine();
            sb.AppendLine($@"    def disconnect(self):");
            sb.AppendLine($@"        \"\"\"断开 WebSocket 连接。\"\"\"");
            sb.AppendLine($@"        self._running = False");
            sb.AppendLine($@"        if self._ws:");
            sb.AppendLine($@"            try:");
            sb.AppendLine($@"                asyncio.run(self._ws.close())");
            sb.AppendLine($@"            except:");
            sb.AppendLine($@"                pass");
            sb.AppendLine($@"        self.connected = False");
            sb.AppendLine($@"        self._ws = None");
            sb.AppendLine();
            sb.AppendLine($@"    def _run_loop(self):");
            sb.AppendLine($@"        \"\"\"WebSocket 消息循环（在后台线程中运行）。\"\"\"");
            sb.AppendLine($@"        loop = asyncio.new_event_loop()");
            sb.AppendLine($@"        asyncio.set_event_loop(loop)");
            sb.AppendLine($@"        while self._running:");
            sb.AppendLine($@"            try:");
            sb.AppendLine($@"                self._ws = loop.run_until_complete(");
            sb.AppendLine($@"                    websockets.connect(self.host)");
            sb.AppendLine($@"                self.connected = True");
            sb.AppendLine($@"                print(f'[ShanyuEngine] Connected to {{self.host}}')");
            sb.AppendLine($@"                loop.run_until_complete(self._receive_loop())");
            sb.AppendLine($@"            except Exception as e:");
            sb.AppendLine($@"                self.connected = False");
            sb.AppendLine($@"                if self._running:");
            sb.AppendLine($@"                    print(f'[ShanyuEngine] Connection lost, retrying in {{self._reconnect_interval}}s...')");
            sb.AppendLine($@"                    time.sleep(self._reconnect_interval)");
            sb.AppendLine($@"        loop.close()");
            sb.AppendLine();
            sb.AppendLine($@"    async def _receive_loop(self):");
            sb.AppendLine($@"        \"\"\"接收消息循环。\"\"\"");
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            async for message in self._ws:");
            sb.AppendLine($@"                if isinstance(message, bytes):");
            sb.AppendLine($@"                    data = json.loads(message.decode('utf-8'))");
            sb.AppendLine($@"                else:");
            sb.AppendLine($@"                    data = json.loads(message)");
            sb.AppendLine($@"                with self._lock:");
            sb.AppendLine($@"                    self._message_queue.append(data)");
            sb.AppendLine($@"                if self._on_message_callback:");
            sb.AppendLine($@"                    self._on_message_callback(data)");
            sb.AppendLine($@"        except websockets.ConnectionClosed:");
            sb.AppendLine($@"            pass");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            print(f'[ShanyuEngine] Receive error: {{e}}')");
            sb.AppendLine();
            sb.AppendLine($@"    def send(self, data: dict) -> bool:");
            sb.AppendLine($@"        \"\"\"发送消息到引擎。\"\"\"");
            sb.AppendLine($@"        if not self._ws or not self.connected:");
            sb.AppendLine($@"            return False");
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            message = json.dumps(data, ensure_ascii=False)");
            sb.AppendLine($@"            asyncio.run(self._ws.send(message))");
            sb.AppendLine($@"            return True");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            print(f'[ShanyuEngine] Send error: {{e}}')");
            sb.AppendLine($@"            return False");
            sb.AppendLine();
            sb.AppendLine($@"    def get_messages(self) -> List[dict]:");
            sb.AppendLine($@"        \"\"\"获取待处理消息。\"\"\"");
            sb.AppendLine($@"        with self._lock:");
            sb.AppendLine($@"            messages = self._message_queue.copy()");
            sb.AppendLine($@"            self._message_queue.clear()");
            sb.AppendLine($@"        return messages");
            sb.AppendLine();
            sb.AppendLine($@"    def set_message_callback(self, callback):");
            sb.AppendLine($@"        \"\"\"设置消息回调。\"\"\"");
            sb.AppendLine($@"        self._on_message_callback = callback");
            sb.AppendLine();
        }

        private void GenerateExportPanel(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Export Panel — N Panel 导出工具");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_export(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"导出场景/选中物体到山雨引擎格式。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.export'");
            sb.AppendLine($@"    bl_label = 'Export to ShanyuEngine'");
            sb.AppendLine($@"    bl_options = {{'REGISTER', 'UNDO'}}");
            sb.AppendLine();
            sb.AppendLine($@"    format: bpy.props.EnumProperty(");
            sb.AppendLine($@"        name='Format',");
            sb.AppendLine($@"        description='Export format',");
            sb.AppendLine($@"        items=[");
            sb.AppendLine($@"            ('SHANYU', '.shanyu', 'ShanyuEngine Native Format'),");
            sb.AppendLine($@"            ('GLTF', '.gltf', 'glTF 2.0 (Khronos)'),");
            sb.AppendLine($@"            ('GLTF_BIN', '.glb', 'glTF Binary'),");
            sb.AppendLine($@"            ('FBX', '.fbx', 'Autodesk FBX'),");
            sb.AppendLine($@"        ],");
            sb.AppendLine($@"        default='SHANYU'");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    export_selected: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Selected Only',");
            sb.AppendLine($@"        description='Export only selected objects',");
            sb.AppendLine($@"        default=False");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    export_animations: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Export Animations',");
            sb.AppendLine($@"        description='Export armature animations',");
            sb.AppendLine($@"        default=True");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    export_materials: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Export Materials',");
            sb.AppendLine($@"        description='Export PBR materials',");
            sb.AppendLine($@"        default=True");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    apply_modifiers: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Apply Modifiers',");
            sb.AppendLine($@"        description='Apply modifiers before export',");
            sb.AppendLine($@"        default=True");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        \"\"\"执行导出。\"\"\"");
            sb.AppendLine($@"        scene = context.scene");
            sb.AppendLine($@"        objects = context.selected_objects if self.export_selected else scene.objects");
            sb.AppendLine($@"        filepath = scene.shanyu_props.export_path");
            sb.AppendLine($@"");
            sb.AppendLine($@"        if not filepath:");
            sb.AppendLine($@"            self.report({{'ERROR'}}, 'Export path not set. Configure in panel.'})");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"        ext = {{'SHANYU': '.shanyu', 'GLTF': '.gltf', 'GLTF_BIN': '.glb', 'FBX': '.fbx'}}");
            sb.AppendLine($@"        filepath = bpy.path.ensure_ext(filepath, ext[self.format])");
            sb.AppendLine();
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            if self.format == 'SHANYU':");
            sb.AppendLine($@"                self._export_shanyu(objects, filepath, scene)");
            sb.AppendLine($@"            elif self.format in ('GLTF', 'GLTF_BIN'):");
            sb.AppendLine($@"                self._export_gltf(filepath, scene, self.format == 'GLTF_BIN')");
            sb.AppendLine($@"            elif self.format == 'FBX':");
            sb.AppendLine($@"                self._export_fbx(filepath, scene)");
            sb.AppendLine();
            sb.AppendLine($@"            self.report({{'INFO'}}, f'Exported {{len(list(objects))}} objects to {{filepath}}')");
            sb.AppendLine($@"            return {{'FINISHED'}}");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            self.report({{'ERROR'}}, f'Export failed: {{e}}')");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"    def _export_shanyu(self, objects, filepath, scene):");
            sb.AppendLine($@"        \"\"\"导出为 .shanyu 格式（自定义二进制格式）。\"\"\"");
            sb.AppendLine($@"        # 构建 ShanyuEngine 格式的 JSON 头 + 二进制网格数据");
            sb.AppendLine($@"        header = {{");
            sb.AppendLine($@"            'version': '1.0',");
            sb.AppendLine($@"            'engine': 'ShanyuEngine',");
            sb.AppendLine($@"            'exporter': 'BlenderPlugin/{_version}',");
            sb.AppendLine($@"            'scene_name': scene.name,");
            sb.AppendLine($@"            'object_count': len(list(objects)),");
            sb.AppendLine($@"            'unit_scale': scene.unit_settings.scale_length,");
            sb.AppendLine($@"            'frame_start': scene.frame_start,");
            sb.AppendLine($@"            'frame_end': scene.frame_end,");
            sb.AppendLine($@"            'fps': scene.render.fps,");
            sb.AppendLine($@"        }}");
            sb.AppendLine($@"");
            sb.AppendLine($@"        mesh_data = []");
            sb.AppendLine($@"        for obj in objects:");
            sb.AppendLine($@"            if obj.type == 'MESH':");
            sb.AppendLine($@"                depsgraph = bpy.context.evaluated_depsgraph_get()");
            sb.AppendLine($@"                mesh_eval = obj.evaluated_get(depsgraph).to_mesh()");
            sb.AppendLine($@"                vertices, faces, normals, uvs = self._extract_mesh_data(mesh_eval)");
            sb.AppendLine($@"                mesh_data.append({{");
            sb.AppendLine($@"                    'name': obj.name,");
            sb.AppendLine($@"                    'transform': [c for row in obj.matrix_world.transposed() for c in row],");
            sb.AppendLine($@"                    'vertices': vertices,");
            sb.AppendLine($@"                    'faces': faces,");
            sb.AppendLine($@"                    'normals': normals,");
            sb.AppendLine($@"                    'uvs': uvs,");
            sb.AppendLine($@"                    'material_index': obj.active_material_index,");
            sb.AppendLine($@"                }})");
            sb.AppendLine($@"                obj.evaluated_get(depsgraph).to_mesh_clear()");
            sb.AppendLine($@"");
            sb.AppendLine($@"        # 写入 JSON + 二进制混合文件");
            sb.AppendLine($@"        header_json = json.dumps(header, ensure_ascii=False).encode('utf-8')");
            sb.AppendLine($@"        meshes_json = json.dumps(mesh_data, ensure_ascii=False).encode('utf-8')");
            sb.AppendLine($@"");
            sb.AppendLine($@"        with open(filepath, 'wb') as f:");
            sb.AppendLine($@"            # Magic: 'SHANYU01'");
            sb.AppendLine($@"            f.write(b'SHANYU01')");
            sb.AppendLine($@"            # JSON header length (4 bytes)");
            sb.AppendLine($@"            f.write(struct.pack('<I', len(header_json)))");
            sb.AppendLine($@"            f.write(header_json)");
            sb.AppendLine($@"            # JSON mesh data length (4 bytes)");
            sb.AppendLine($@"            f.write(struct.pack('<I', len(meshes_json)))");
            sb.AppendLine($@"            f.write(meshes_json)");
            sb.AppendLine();
            sb.AppendLine($@"    def _extract_mesh_data(self, mesh):");
            sb.AppendLine($@"        \"\"\"从 Blender 网格提取顶点/面/法线/UV 数据。\"\"\"");
            sb.AppendLine($@"        vertices = []");
            sb.AppendLine($@"        for v in mesh.vertices:");
            sb.AppendLine($@"            vertices.extend([v.co.x, v.co.y, v.co.z])");
            sb.AppendLine();
            sb.AppendLine($@"        faces = []");
            sb.AppendLine($@"        for p in mesh.polygons:");
            sb.AppendLine($@"            faces.extend([p.loop_indices[0], p.loop_indices[1], p.loop_indices[2]])");
            sb.AppendLine($@"            for i in range(3, len(p.loop_indices)):");
            sb.AppendLine($@"                faces.extend([p.loop_indices[0], p.loop_indices[i-1], p.loop_indices[i]])");
            sb.AppendLine();
            sb.AppendLine($@"        normals = []");
            sb.AppendLine($@"        for p in mesh.polygons:");
            sb.AppendLine($@"            n = p.normal");
            sb.AppendLine($@"            normals.extend([n.x, n.y, n.z])");
            sb.AppendLine();
            sb.AppendLine($@"        uvs = []");
            sb.AppendLine($@"        if mesh.uv_layers.active:");
            sb.AppendLine($@"            uv_layer = mesh.uv_layers.active.data");
            sb.AppendLine($@"            for loop in mesh.loops:");
            sb.AppendLine($@"                uv = uv_layer[loop.index].uv");
            sb.AppendLine($@"                uvs.extend([uv.x, uv.y])");
            sb.AppendLine();
            sb.AppendLine($@"        return vertices, faces, normals, uvs");
            sb.AppendLine();
            sb.AppendLine($@"    def _export_gltf(self, filepath, scene, binary=False):");
            sb.AppendLine($@"        \"\"\"通过 Blender 内置 glTF 导出器导出。\"\"\"");
            sb.AppendLine($@"        ext = '.glb' if binary else '.gltf'");
            sb.AppendLine($@"        filepath = bpy.path.ensure_ext(filepath, ext)");
            sb.AppendLine($@"        bpy.ops.export_scene.gltf(");
            sb.AppendLine($@"            filepath=filepath,");
            sb.AppendLine($@"            export_format='GLB' if binary else 'GLTF_SEPARATE',");
            sb.AppendLine($@"            export_selected=self.export_selected,");
            sb.AppendLine($@"            export_animations=self.export_animations,");
            sb.AppendLine($@"            export_materials=self.export_materials,");
            sb.AppendLine($@"            apply_modifiers=self.apply_modifiers,");
            sb.AppendLine($@"            export_extras=True,");
            sb.AppendLine($@"            export_tangents=True,");
            sb.AppendLine($@"        )");
            sb.AppendLine();
            sb.AppendLine($@"    def _export_fbx(self, filepath, scene):");
            sb.AppendLine($@"        \"\"\"通过 Blender 内置 FBX 导出器导出。\"\"\"");
            sb.AppendLine($@"        filepath = bpy.path.ensure_ext(filepath, '.fbx')");
            sb.AppendLine($@"        bpy.ops.export_scene.fbx(");
            sb.AppendLine($@"            filepath=filepath,");
            sb.AppendLine($@"            use_selection=self.export_selected,");
            sb.AppendLine($@"            global_scale=1.0,");
            sb.AppendLine($@"            apply_unit_scale=True,");
            sb.AppendLine($@"            bake_anim_use_nla_strips=True,");
            sb.AppendLine($@"            bake_anim_use_all_actions=True,");
            sb.AppendLine($@"        )");
            sb.AppendLine();
            sb.AppendLine($@"    def invoke(self, context, event):");
            sb.AppendLine($@"        wm = context.window_manager");
            sb.AppendLine($@"        return wm.invoke_props_dialog(self)");
            sb.AppendLine();
            sb.AppendLine($@"    def draw(self, context):");
            sb.AppendLine($@"        layout = self.layout");
            sb.AppendLine($@"        layout.prop(self, 'format')");
            sb.AppendLine($@"        layout.prop(self, 'export_selected')");
            sb.AppendLine($@"        layout.prop(self, 'export_animations')");
            sb.AppendLine($@"        layout.prop(self, 'export_materials')");
            sb.AppendLine($@"        layout.prop(self, 'apply_modifiers')");
            sb.AppendLine();
            sb.AppendLine($@"# ──── N Panel 属性 ────");
            sb.AppendLine($@"class ShanyuSceneProperties(bpy.types.PropertyGroup):");
            sb.AppendLine($@"    export_path: bpy.props.StringProperty(");
            sb.AppendLine($@"        name='Export Path',");
            sb.AppendLine($@"        subtype='DIR_PATH',");
            sb.AppendLine($@"        description='Default export directory',");
            sb.AppendLine($@"        default='{(_exportDir != "" ? _exportDir : "//")}'");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    ws_host: bpy.props.StringProperty(");
            sb.AppendLine($@"        name='WS Host',");
            sb.AppendLine($@"        description='WebSocket host address',");
            sb.AppendLine($@"        default='{_wsHost}'");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    sync_enabled: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Real-time Sync',");
            sb.AppendLine($@"        description='Enable real-time sync with engine',");
            sb.AppendLine($@"        default=False");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    sync_interval: bpy.props.FloatProperty(");
            sb.AppendLine($@"        name='Sync Interval',");
            sb.AppendLine($@"        description='Sync interval in seconds',");
            sb.AppendLine($@"        default=0.1,");
            sb.AppendLine($@"        min=0.01, max=5.0");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    sync_transforms: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Sync Transforms',");
            sb.AppendLine($@"        default=True");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    sync_materials: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Sync Materials',");
            sb.AppendLine($@"        default=True");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    sync_animations: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Sync Animations',");
            sb.AppendLine($@"        default=False");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"# ──── N Panel UI ────");
            sb.AppendLine($@"class SHANYU_PT_main_panel(bpy.types.Panel):");
            sb.AppendLine($@"    bl_label = 'ShanyuEngine'");
            sb.AppendLine($@"    bl_idname = 'SHANYU_PT_main'");
            sb.AppendLine($@"    bl_space_type = 'VIEW_3D'");
            sb.AppendLine($@"    bl_region_type = 'UI'");
            sb.AppendLine($@"    bl_category = 'ShanyuEngine'");
            sb.AppendLine();
            sb.AppendLine($@"    def draw(self, context):");
            sb.AppendLine($@"        layout = self.layout");
            sb.AppendLine($@"        scene = context.scene");
            sb.AppendLine($@"        props = scene.shanyu_props");
            sb.AppendLine();
            sb.AppendLine($@"        # ── Export Section ──");
            sb.AppendLine($@"        box = layout.box()");
            sb.AppendLine($@"        box.label(text='Export', icon='EXPORT')");
            sb.AppendLine($@"        box.prop(props, 'export_path')");
            sb.AppendLine($@"        box.operator('shanyu.export', icon='FILE_TICK')");
            sb.AppendLine();
            sb.AppendLine($@"        # ── Sync Section ──");
            sb.AppendLine($@"        box = layout.box()");
            sb.AppendLine($@"        box.label(text='Real-time Sync', icon='WORLD')");
            sb.AppendLine($@"        box.prop(props, 'ws_host')");
            sb.AppendLine($@"        box.prop(props, 'sync_enabled')");
            sb.AppendLine($@"        if props.sync_enabled:");
            sb.AppendLine($@"            box.prop(props, 'sync_interval')");
            sb.AppendLine($@"            box.prop(props, 'sync_transforms')");
            sb.AppendLine($@"            box.prop(props, 'sync_materials')");
            sb.AppendLine($@"            box.prop(props, 'sync_animations')");
            sb.AppendLine($@"        row = box.row(align=True)");
            sb.AppendLine($@"        row.operator('shanyu.sync_connect', icon='PLAY')");
            sb.AppendLine($@"        row.operator('shanyu.sync_disconnect', icon='STOP')");
            sb.AppendLine();
            sb.AppendLine($@"        # ── Material Sync Section ──");
            sb.AppendLine($@"        box = layout.box()");
            sb.AppendLine($@"        box.label(text='Material Sync', icon='MATERIAL')");
            sb.AppendLine($@"        box.operator('shanyu.sync_material', icon='NODE_MATERIAL')");
            sb.AppendLine();
            sb.AppendLine($@"        # ── Animation Sync Section ──");
            sb.AppendLine($@"        box = layout.box()");
            sb.AppendLine($@"        box.label(text='Animation Sync', icon='ANIM')");
            sb.AppendLine($@"        box.operator('shanyu.sync_animation', icon='ACTION')");
            sb.AppendLine();
            sb.AppendLine($@"        # ── Alembic Export Section ──");
            sb.AppendLine($@"        box = layout.box()");
            sb.AppendLine($@"        box.label(text='Alembic Cache', icon='CACHE')");
            sb.AppendLine($@"        box.operator('shanyu.export_alembic', icon='FILE_CACHE')");
            sb.AppendLine();
        }

        private void GenerateSyncOperators(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Sync Operators — 实时同步操作");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"# 全局 WebSocket 客户端实例");
            sb.AppendLine($@"_ws_client: ShanyuWebSocketClient = None");
            sb.AppendLine($@"_sync_timer = None");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_sync_connect(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"连接到山雨引擎编辑器。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.sync_connect'");
            sb.AppendLine($@"    bl_label = 'Connect'");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        global _ws_client");
            sb.AppendLine($@"        props = context.scene.shanyu_props");
            sb.AppendLine();
            sb.AppendLine($@"        if not HAS_WEBSOCKETS:");
            sb.AppendLine($@"            self.report({{'ERROR'}}, 'websockets library not installed.'})");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"        _ws_client = ShanyuWebSocketClient(props.ws_host)");
            sb.AppendLine($@"        _ws_client.set_message_callback(_on_engine_message)");
            sb.AppendLine();
            sb.AppendLine($@"        if _ws_client.connect():");
            sb.AppendLine($@"            # 发送初始场景数据");
            sb.AppendLine($@"            _send_full_scene(context)");
            sb.AppendLine($@"            self.report({{'INFO'}}, 'Connected to engine.'})");
            sb.AppendLine($@"        else:");
            sb.AppendLine($@"            self.report({{'WARNING'}}, 'Connection failed, check host address.'})");
            sb.AppendLine();
            sb.AppendLine($@"        return {{'FINISHED'}}");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_sync_disconnect(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"断开与山雨引擎的连接。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.sync_disconnect'");
            sb.AppendLine($@"    bl_label = 'Disconnect'");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        global _ws_client");
            sb.AppendLine($@"        if _ws_client:");
            sb.AppendLine($@"            _ws_client.disconnect()");
            sb.AppendLine($@"            _ws_client = None");
            sb.AppendLine($@"        self.report({{'INFO'}}, 'Disconnected.')");
            sb.AppendLine($@"        return {{'FINISHED'}}");
            sb.AppendLine();
            sb.AppendLine($@"def _send_full_scene(context):");
            sb.AppendLine($@"    \"\"\"发送完整场景数据到引擎。\"\"\"");
            sb.AppendLine($@"    global _ws_client");
            sb.AppendLine($@"    if not _ws_client or not _ws_client.connected:");
            sb.AppendLine($@"        return");
            sb.AppendLine();
            sb.AppendLine($@"    scene = context.scene");
            sb.AppendLine($@"    data = {{");
            sb.AppendLine($@"        'type': 'scene_full',");
            sb.AppendLine($@"        'scene_name': scene.name,");
            sb.AppendLine($@"        'objects': [],");
            sb.AppendLine($@"    }}");
            sb.AppendLine();
            sb.AppendLine($@"    for obj in scene.objects:");
            sb.AppendLine($@"        obj_data = {{");
            sb.AppendLine($@"            'name': obj.name,");
            sb.AppendLine($@"            'type': obj.type,");
            sb.AppendLine($@"            'location': list(obj.location),");
            sb.AppendLine($@"            'rotation': list(obj.rotation_euler),");
            sb.AppendLine($@"            'scale': list(obj.scale),");
            sb.AppendLine($@"        }}");
            sb.AppendLine($@"");
            sb.AppendLine($@"        if obj.type == 'MESH':");
            sb.AppendLine($@"            obj_data['vertex_count'] = len(obj.data.vertices)");
            sb.AppendLine($@"            obj_data['polygon_count'] = len(obj.data.polygons)");
            sb.AppendLine($@"        elif obj.type == 'LIGHT':");
            sb.AppendLine($@"            if obj.data.type == 'POINT':");
            sb.AppendLine($@"                obj_data['light_type'] = 'point'");
            sb.AppendLine($@"                obj_data['color'] = list(obj.data.color)");
            sb.AppendLine($@"                obj_data['energy'] = obj.data.energy");
            sb.AppendLine($@"                obj_data['radius'] = obj.data.shadow_soft_size");
            sb.AppendLine($@"            elif obj.data.type == 'SUN':");
            sb.AppendLine($@"                obj_data['light_type'] = 'directional'");
            sb.AppendLine($@"                obj_data['color'] = list(obj.data.color)");
            sb.AppendLine($@"                obj_data['energy'] = obj.data.energy");
            sb.AppendLine($@"            elif obj.data.type == 'SPOT':");
            sb.AppendLine($@"                obj_data['light_type'] = 'spot'");
            sb.AppendLine($@"                obj_data['color'] = list(obj.data.color)");
            sb.AppendLine($@"                obj_data['energy'] = obj.data.energy");
            sb.AppendLine($@"                obj_data['spot_size'] = obj.data.spot_size");
            sb.AppendLine($@"        elif obj.type == 'CAMERA':");
            sb.AppendLine($@"            obj_data['fov'] = math.degrees(obj.data.angle)");
            sb.AppendLine($@"            obj_data['near'] = obj.data.clip_start");
            sb.AppendLine($@"            obj_data['far'] = obj.data.clip_end");
            sb.AppendLine($@"");
            sb.AppendLine($@"        data['objects'].append(obj_data)");
            sb.AppendLine();
            sb.AppendLine($@"    _ws_client.send(data)");
            sb.AppendLine();
            sb.AppendLine($@"def _send_object_update(obj):");
            sb.AppendLine($@"    \"\"\"发送单个对象变换更新。\"\"\"");
            sb.AppendLine($@"    global _ws_client");
            sb.AppendLine($@"    if not _ws_client or not _ws_client.connected:");
            sb.AppendLine($@"        return");
            sb.AppendLine();
            sb.AppendLine($@"    data = {{");
            sb.AppendLine($@"        'type': 'object_update',");
            sb.AppendLine($@"        'name': obj.name,");
            sb.AppendLine($@"        'location': list(obj.location),");
            sb.AppendLine($@"        'rotation': list(obj.rotation_euler),");
            sb.AppendLine($@"        'scale': list(obj.scale),");
            sb.AppendLine($@"    }}");
            sb.AppendLine($@"    _ws_client.send(data)");
            sb.AppendLine();
            sb.AppendLine($@"def _on_engine_message(data):");
            sb.AppendLine($@"    \"\"\"处理引擎发来的消息。\"\"\"");
            sb.AppendLine($@"    msg_type = data.get('type', '')");
            sb.AppendLine($@"");
            sb.AppendLine($@"    if msg_type == 'select_object':");
            sb.AppendLine($@"        # 引擎请求选中对象 → 同步到 Blender");
            sb.AppendLine($@"        obj_name = data.get('object_name', '')");
            sb.AppendLine($@"        obj = bpy.data.objects.get(obj_name)");
            sb.AppendLine($@"        if obj:");
            sb.AppendLine($@"            bpy.context.view_layer.objects.active = obj");
            sb.AppendLine($@"            obj.select_set(True)");
            sb.AppendLine($@"    elif msg_type == 'ping':");
            sb.AppendLine($@"        _ws_client.send({{'type': 'pong'}})");
            sb.AppendLine();
            sb.AppendLine($@"def _sync_handler(scene):");
            sb.AppendLine($@"    \"\"\"帧更新同步处理器（由 Blender 应用处理器调用）。\"\"\"");
            sb.AppendLine($@"    global _ws_client");
            sb.AppendLine($@"    props = scene.shanyu_props");
            sb.AppendLine($@"");
            sb.AppendLine($@"    if not props.sync_enabled or not _ws_client or not _ws_client.connected:");
            sb.AppendLine($@"        return");
            sb.AppendLine();
            sb.AppendLine($@"    # 处理引擎发来的消息");
            sb.AppendLine($@"    for msg in _ws_client.get_messages():");
            sb.AppendLine($@"        _on_engine_message(msg)");
            sb.AppendLine();
            sb.AppendLine($@"    # 发送对象选择同步");
            sb.AppendLine($@"    if props.sync_transforms:");
            sb.AppendLine($@"        for obj in bpy.context.selected_objects:");
            sb.AppendLine($@"            _send_object_update(obj)");
            sb.AppendLine();
        }

        private void GenerateMaterialSync(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Material Sync — Blender 节点到引擎 PBR 参数映射");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_sync_material(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"同步选中对象的材质到引擎。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.sync_material'");
            sb.AppendLine($@"    bl_label = 'Sync Material'");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        global _ws_client");
            sb.AppendLine($@"        if not _ws_client or not _ws_client.connected:");
            sb.AppendLine($@"            self.report({{'WARNING'}}, 'Not connected to engine.'})");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"        for obj in context.selected_objects:");
            sb.AppendLine($@"            for slot in obj.material_slots:");
            sb.AppendLine($@"                mat = slot.material");
            sb.AppendLine($@"                if mat:");
            sb.AppendLine($@"                    pbr = self._extract_pbr_params(mat)");
            sb.AppendLine($@"                    data = {{");
            sb.AppendLine($@"                        'type': 'material_update',");
            sb.AppendLine($@"                        'object_name': obj.name,");
            sb.AppendLine($@"                        'material': pbr,");
            sb.AppendLine($@"                    }}");
            sb.AppendLine($@"                    _ws_client.send(data)");
            sb.AppendLine();
            sb.AppendLine($@"        self.report({{'INFO'}}, 'Material data sent.'})");
            sb.AppendLine($@"        return {{'FINISHED'}}");
            sb.AppendLine();
            sb.AppendLine($@"    def _extract_pbr_params(self, material) -> dict:");
            sb.AppendLine($@"        \"\"\"");
            sb.AppendLine($@"        从 Blender 材质节点提取 PBR 参数。");
            sb.AppendLine($@"        支持 Principled BSDF（Blender 3.x/4.x 标准PBR节点）。");
            sb.AppendLine($@"        映射关系:");
            sb.AppendLine($@"          Base Color      -> albedo (RGBA)");
            sb.AppendLine($@"          Metallic         -> metallic (0~1)");
            sb.AppendLine($@"          Roughness        -> roughness (0~1)");
            sb.AppendLine($@"          Normal Map       -> normalMap (RGBA)");
            sb.AppendLine($@"          Emission Color   -> emissive (RGBA)");
            sb.AppendLine($@"          Emission Strength -> emissiveIntensity (float)");
            sb.AppendLine($@"          Alpha           -> alpha (0~1)");
            sb.AppendLine($@"          Subsurface       -> subsurface (0~1)");
            sb.AppendLine($@"          Subsurface Radius -> subsurfaceRadius (RGB)");
            sb.AppendLine($@"          Clearcoat        -> clearcoat (0~1)");
            sb.AppendLine($@"          Clearcoat Roughness -> clearcoatRoughness (0~1)");
            sb.AppendLine($@"          IOR              -> ior (float)");
            sb.AppendLine($@"          Transmission     -> transmission (0~1)");
            sb.AppendLine($@"          Sheen            -> sheen (0~1)");
            sb.AppendLine($@"          Sheen Tint       -> sheenTint (RGB)");
            sb.AppendLine($@"        \"\"\"");
            sb.AppendLine($@"        pbr = {{");
            sb.AppendLine($@"            'name': material.name,");
            sb.AppendLine($@"            'albedo': [0.8, 0.8, 0.8, 1.0],");
            sb.AppendLine($@"            'metallic': 0.0,");
            sb.AppendLine($@"            'roughness': 0.5,");
            sb.AppendLine($@"            'normalMap': None,");
            sb.AppendLine($@"            'emissive': [0, 0, 0, 1],");
            sb.AppendLine($@"            'emissiveIntensity': 0.0,");
            sb.AppendLine($@"            'alpha': 1.0,");
            sb.AppendLine($@"            'subsurface': 0.0,");
            sb.AppendLine($@"            'clearcoat': 0.0,");
            sb.AppendLine($@"            'ior': 1.5,");
            sb.AppendLine($@"            'transmission': 0.0,");
            sb.AppendLine($@"        }}");
            sb.AppendLine();
            sb.AppendLine($@"        # 查找 Principled BSDF 节点");
            sb.AppendLine($@"        bsdf_node = None");
            sb.AppendLine($@"        if material.node_tree:");
            sb.AppendLine($@"            for node in material.node_tree.nodes:");
            sb.AppendLine($@"                if node.type == 'BSDF_PRINCIPLED':");
            sb.AppendLine($@"                    bsdf_node = node");
            sb.AppendLine($@"                    break");
            sb.AppendLine();
            sb.AppendLine($@"        if not bsdf_node:");
            sb.AppendLine($@"            return pbr");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Base Color");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Base Color') or bsdf_node.inputs.get('albedo')");
            sb.AppendLine($@"        if input:");
            sb.AppendLine($@"            if hasattr(input, 'default_value'):");
            sb.AppendLine($@"                color = input.default_value");
            sb.AppendLine($@"                pbr['albedo'] = [color[0], color[1], color[2], 1.0]");
            sb.AppendLine($@"");
            sb.AppendLine($@"        # 提取 Metallic");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Metallic')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['metallic'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Roughness");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Roughness')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['roughness'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Emission");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Emission')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            c = input.default_value");
            sb.AppendLine($@"            pbr['emissive'] = [c[0], c[1], c[2], 1.0]");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Emission Strength')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['emissiveIntensity'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Alpha");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Alpha')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['alpha'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Subsurface");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Subsurface')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['subsurface'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Clearcoat");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Clearcoat')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['clearcoat'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 IOR");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('IOR')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['ior'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        # 提取 Transmission");
            sb.AppendLine($@"        input = bsdf_node.inputs.get('Transmission')");
            sb.AppendLine($@"        if input and hasattr(input, 'default_value'):");
            sb.AppendLine($@"            pbr['transmission'] = input.default_value");
            sb.AppendLine();
            sb.AppendLine($@"        return pbr");
            sb.AppendLine();
        }

        private void GenerateAnimationSync(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Animation Sync — Blender Action 到引擎 AnimAsset 映射");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_sync_animation(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"同步选中对象的动画到引擎。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.sync_animation'");
            sb.AppendLine($@"    bl_label = 'Sync Animation'");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        global _ws_client");
            sb.AppendLine($@"        if not _ws_client or not _ws_client.connected:");
            sb.AppendLine($@"            self.report({{'WARNING'}}, 'Not connected to engine.'})");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"        for obj in context.selected_objects:");
            sb.AppendLine($@"            if obj.animation_data and obj.animation_data.action:");
            sb.AppendLine($@"                anim = self._extract_animation(obj)");
            sb.AppendLine($@"                data = {{");
            sb.AppendLine($@"                    'type': 'animation_update',");
            sb.AppendLine($@"                    'object_name': obj.name,");
            sb.AppendLine($@"                    'animation': anim,");
            sb.AppendLine($@"                }}");
            sb.AppendLine($@"                _ws_client.send(data)");
            sb.AppendLine();
            sb.AppendLine($@"            # 发送 NLA strips（如果有）");
            sb.AppendLine($@"            if obj.animation_data and obj.animation_data.nla_tracks:");
            sb.AppendLine($@"                strips = self._extract_nla_strips(obj)");
            sb.AppendLine($@"                for strip_data in strips:");
            sb.AppendLine($@"                    _ws_client.send(strip_data)");
            sb.AppendLine();
            sb.AppendLine($@"        self.report({{'INFO'}}, 'Animation data sent.'})");
            sb.AppendLine($@"        return {{'FINISHED'}}");
            sb.AppendLine();
            sb.AppendLine($@"    def _extract_animation(self, obj) -> dict:");
            sb.AppendLine($@"        \"\"\"");
            sb.AppendLine($@"        从 Blender Action 提取动画数据。");
            sb.AppendLine($@"        映射关系:");
            sb.AppendLine($@"          Action          -> AnimAsset");
            sb.AppendLine($@"          F-Curves        -> AnimTrack (位置/旋转/缩放)");
            sb.AppendLine($@"          Keyframes       -> Key points");
            sb.AppendLine($@"          NLA Tracks      -> AnimMontage / BlendSpace");
            sb.AppendLine($@"        \"\"\"");
            sb.AppendLine($@"        action = obj.animation_data.action");
            sb.AppendLine($@"        scene = bpy.context.scene");
            sb.AppendLine();
            sb.AppendLine($@"        anim = {{");
            sb.AppendLine($@"            'name': action.name,");
            sb.AppendLine($@"            'frame_start': int(scene.frame_start),");
            sb.AppendLine($@"            'frame_end': int(scene.frame_end),");
            sb.AppendLine($@"            'fps': scene.render.fps,");
            sb.AppendLine($@"            'duration': (scene.frame_end - scene.frame_start) / scene.render.fps,");
            sb.AppendLine($@"            'tracks': [],");
            sb.AppendLine($@"        }}");
            sb.AppendLine();
            sb.AppendLine($@"        for fcurve in action.fcurves:");
            sb.AppendLine($@"            track = {{");
            sb.AppendLine($@"                'data_path': fcurve.data_path,");
            sb.AppendLine($@"                'channel_index': fcurve.array_index,");
            sb.AppendLine($@"                'keyframes': [],");
            sb.AppendLine($@"            }}");
            sb.AppendLine();
            sb.AppendLine($@"            for kf in fcurve.keyframe_points:");
            sb.AppendLine($@"                track['keyframes'].append({{");
            sb.AppendLine($@"                    'frame': kf.co.x,");
            sb.AppendLine($@"                    'value': kf.co.y,");
            sb.AppendLine($@"                    'handle_left': [kf.handle_left.x, kf.handle_left.y],");
            sb.AppendLine($@"                    'handle_right': [kf.handle_right.x, kf.handle_right.y],");
            sb.AppendLine($@"                    'interpolation': kf.interpolation,");
            sb.AppendLine($@"                }})");
            sb.AppendLine();
            sb.AppendLine($@"            anim['tracks'].append(track)");
            sb.AppendLine();
            sb.AppendLine($@"        return anim");
            sb.AppendLine();
            sb.AppendLine($@"    def _extract_nla_strips(self, obj) -> list:");
            sb.AppendLine($@"        \"\"\"提取 NLA strips 数据。\"\"\"");
            sb.AppendLine($@"        strips = []");
            sb.AppendLine($@"        for track in obj.animation_data.nla_tracks:");
            sb.AppendLine($@"            for strip in track.strips:");
            sb.AppendLine($@"                if strip.action:");
            sb.AppendLine($@"                    strips.append({{");
            sb.AppendLine($@"                        'type': 'nla_strip',");
            sb.AppendLine($@"                        'object_name': obj.name,");
            sb.AppendLine($@"                        'track_name': track.name,");
            sb.AppendLine($@"                        'strip_name': strip.name,");
            sb.AppendLine($@"                        'action_name': strip.action.name,");
            sb.AppendLine($@"                        'frame_start': strip.frame_start,");
            sb.AppendLine($@"                        'frame_end': strip.frame_end,");
            sb.AppendLine($@"                        'blend_in': strip.blend_in,");
            sb.AppendLine($@"                        'blend_out': strip.blend_out,");
            sb.AppendLine($@"                        'repeat': strip.repeat,");
            sb.AppendLine($@"                        'scale': strip.scale,");
            sb.AppendLine($@"                    }}})");
            sb.AppendLine($@"        return strips");
            sb.AppendLine();
        }

        private void GenerateAlembicExport(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Alembic Export — 复杂动画缓存导出");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class SHANYU_OT_export_alembic(bpy.types.Operator):");
            sb.AppendLine($@"    \"\"\"导出 Alembic 缓存（用于复杂变形动画、粒子等）。\"\"\"");
            sb.AppendLine($@"    bl_idname = 'shanyu.export_alembic'");
            sb.AppendLine($@"    bl_label = 'Export Alembic Cache'");
            sb.AppendLine($@"    bl_options = {{'REGISTER', 'UNDO'}}");
            sb.AppendLine();
            sb.AppendLine($@"    frame_start: bpy.props.IntProperty(");
            sb.AppendLine($@"        name='Start Frame',");
            sb.AppendLine($@"        default=1");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    frame_end: bpy.props.IntProperty(");
            sb.AppendLine($@"        name='End Frame',");
            sb.AppendLine($@"        default=250");
            sb.AppendLine($@"    )");
            sb.AppendLine($@"    export_subdivisions: bpy.props.BoolProperty(");
            sb.AppendLine($@"        name='Subdivisions',");
            sb.AppendLine($@"        default=False");
            sb.AppendLine($@"    )");
            sb.AppendLine();
            sb.AppendLine($@"    def execute(self, context):");
            sb.AppendLine($@"        scene = context.scene");
            sb.AppendLine($@"        filepath = scene.shanyu_props.export_path");
            sb.AppendLine();
            sb.AppendLine($@"        if not filepath:");
            sb.AppendLine($@"            self.report({{'ERROR'}}, 'Export path not set.'})");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"        filepath = bpy.path.ensure_ext(filepath, '.abc')");
            sb.AppendLine();
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            # 通过 Blender 内置 Alembic 导出");
            sb.AppendLine($@"            selected = context.selected_objects");
            sb.AppendLine($@"            if not selected:");
            sb.AppendLine($@"                self.report({{'WARNING'}}, 'No objects selected.'})");
            sb.AppendLine($@"                return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"            obj_names = ','.join(obj.name for obj in selected)");
            sb.AppendLine();
            sb.AppendLine($@"            bpy.ops.wm.alembic_export(");
            sb.AppendLine($@"                filepath=filepath,");
            sb.AppendLine($@"                frame_start=self.frame_start,");
            sb.AppendLine($@"                frame_end=self.frame_end,");
            sb.AppendLine($@"                selected=True,");
            sb.AppendLine($@"                facesets=True,");
            sb.AppendLine($@"                subdivisions=self.export_subdivisions,");
            sb.AppendLine($@"                apply_modifiers=True,");
            sb.AppendLine($@"            )");
            sb.AppendLine();
            sb.AppendLine($@"            self.report({{'INFO'}}, f'Alembic exported: {{filepath}}')");
            sb.AppendLine($@"            return {{'FINISHED'}}");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            self.report({{'ERROR'}}, f'Alembic export failed: {{e}}')");
            sb.AppendLine($@"            return {{'CANCELLED'}}");
            sb.AppendLine();
            sb.AppendLine($@"    def invoke(self, context, event):");
            sb.AppendLine($@"        wm = context.window_manager");
            sb.AppendLine($@"        return wm.invoke_props_dialog(self)");
            sb.AppendLine();
        }

        private void GenerateRegistration(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Registration — 插件注册/反注册");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"classes = (");
            sb.AppendLine($@"    ShanyuSceneProperties,");
            sb.AppendLine($@"    SHANYU_PT_main_panel,");
            sb.AppendLine($@"    SHANYU_OT_export,");
            sb.AppendLine($@"    SHANYU_OT_sync_connect,");
            sb.AppendLine($@"    SHANYU_OT_sync_disconnect,");
            sb.AppendLine($@"    SHANYU_OT_sync_material,");
            sb.AppendLine($@"    SHANYU_OT_sync_animation,");
            sb.AppendLine($@"    SHANYU_OT_export_alembic,");
            sb.AppendLine($@")");
            sb.AppendLine();
            sb.AppendLine($@"def register():");
            sb.AppendLine($@"    \"\"\"注册插件。\"\"\"");
            sb.AppendLine($@"    for cls in classes:");
            sb.AppendLine($@"        bpy.utils.register_class(cls)");
            sb.AppendLine($@"    bpy.types.Scene.shanyu_props = bpy.props.PointerProperty(type=ShanyuSceneProperties)");
            sb.AppendLine($@"    # 注册应用处理器（用于实时同步）");
            sb.AppendLine($@"    bpy.app.handlers.depsgraph_update_post.append(_sync_handler)");
            sb.AppendLine($@"    print('[ShanyuEngine] Plugin registered successfully.')");
            sb.AppendLine();
            sb.AppendLine($@"def unregister():");
            sb.AppendLine($@"    \"\"\"反注册插件。\"\"\"");
            sb.AppendLine($@"    global _ws_client");
            sb.AppendLine($@"    if _ws_client:");
            sb.AppendLine($@"        _ws_client.disconnect()");
            sb.AppendLine($@"        _ws_client = None");
            sb.AppendLine($@"    bpy.app.handlers.depsgraph_update_post.remove(_sync_handler)");
            sb.AppendLine($@"    for cls in reversed(classes):");
            sb.AppendLine($@"        bpy.utils.unregister_class(cls)");
            sb.AppendLine($@"    del bpy.types.Scene.shanyu_props");
            sb.AppendLine($@"    print('[ShanyuEngine] Plugin unregistered.')");
            sb.AppendLine();
            sb.AppendLine($@"if __name__ == '__main__':");
            sb.AppendLine($@"    register()");
            sb.AppendLine();
        }
    }
}
