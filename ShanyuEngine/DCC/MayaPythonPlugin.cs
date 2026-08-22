// ==================================================================================================
//  MayaPythonPlugin.cs — 山雨引擎 Maya Python 插件生成器
//
//  命名空间: ShanyuEngine.DCC
//  依赖:     System.IO, System.Text
//
//  功能:
//    生成可直接安装到 Autodesk Maya 2022+ 的 Python 插件源码 (.py 文件)。
//    插件功能:
//      1. MayaSyncPlugin: Maya 2022/2023/2024/2025 兼容 (Python 3)
//      2. 自定义 Shelf 按钮: 导出/同步/设置
//      3. 实时同步: 通过 TCP 连接引擎 LiveLink 服务器
//      4. 场景导出: FBX / Alembic / OBJ
//      5. 对象选择同步
//      6. 材质同步 (Maya shading nodes → 引擎 PBR 参数)
//      7. 动画同步 (Maya 动画曲线 → 引擎 AnimAsset)
//      8. 摄像机同步
//    生成文件: 输出为 .py 文件，可拖入 Maya 或放入 modules 目录
// ==================================================================================================

using System;
using System.IO;
using System.Text;

namespace ShanyuEngine.DCC
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: MayaPythonPlugin — 插件生成器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maya Python 插件生成器。
    /// 生成可直接安装到 Autodesk Maya 2022+ 的 Python 插件源码。
    /// 插件实现 Maya 与山雨引擎之间的数据桥接。
    /// </summary>
    public sealed class MayaPythonPlugin
    {
        private string _pluginName = "ShanyuEngine Bridge for Maya";
        private string _pluginId = "shanyu_engine_maya_bridge";
        private string _author = "ShanyuEngine Team";
        private string _version = "1.0.0";
        private string _description = "山雨引擎 Maya 实时同步插件";
        private string _tcpHost = "127.0.0.1";
        private int _tcpPort = 18888;
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
        /// <summary>LiveLink 服务器 IP 地址。</summary>
        public string TcpHost { get => _tcpHost; set => _tcpHost = value; }
        /// <summary>LiveLink 服务器 TCP 端口。</summary>
        public int TcpPort { get => _tcpPort; set => _tcpPort = value; }
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
            GenerateTcpClient(sb);
            GenerateSceneExporter(sb);
            GenerateMaterialSync(sb);
            GenerateAnimationSync(sb);
            GenerateCameraSync(sb);
            GenerateShelfUI(sb);
            GenerateCallbackHandlers(sb);
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
        /// 生成 Python 插件文件到 Maya scripts 目录。
        /// </summary>
        /// <param name="mayaScriptsDir">Maya scripts 目录路径 (通常为 ~/Documents/maya/scripts)。</param>
        /// <returns>生成的文件路径。</returns>
        public string InstallToMayaScripts(string mayaScriptsDir)
        {
            if (!Directory.Exists(mayaScriptsDir))
                Directory.CreateDirectory(mayaScriptsDir);

            string fileName = $"shanyu_engine_maya_bridge_{_version.Replace(".", "_")}.py";
            string fullPath = Path.Combine(mayaScriptsDir, fileName);
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
            sb.AppendLine($@"# {_pluginName}");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# Compatible with: Maya 2022, 2023, 2024, 2025 (Python 3)");
            sb.AppendLine($@"# Author: {_author}");
            sb.AppendLine($@"# Version: {_version}");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# {_description}");
            sb.AppendLine($@"#");
            sb.AppendLine($@"# Installation:");
            sb.AppendLine($@"#   Method 1 (Script Editor):");
            sb.AppendLine($@"#     1. Open Maya > Window > General Editors > Script Editor");
            sb.AppendLine($@"#     2. In Python tab, execute: exec(open(r'<path_to_this_file>').read())");
            sb.AppendLine($@"#   Method 2 (Shelf Button):");
            sb.AppendLine($@"#     1. Place this file in ~/Documents/maya/scripts/");
            sb.AppendLine($@"#     2. In Maya Script Editor (Python):");
            sb.AppendLine($@"#        import shanyu_engine_maya_bridge");
            sb.AppendLine($@"#        shanyu_engine_maya_bridge.create_shelf()");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
        }

        private void GenerateImports(StringBuilder sb)
        {
            sb.AppendLine($@"import maya.cmds as cmds");
            sb.AppendLine($@"import maya.mel as mel");
            sb.AppendLine($@"import maya.api.OpenMaya as om");
            sb.AppendLine($@"import os");
            sb.AppendLine($@"import sys");
            sb.AppendLine($@"import json");
            sb.AppendLine($@"import struct");
            sb.AppendLine($@"import time");
            sb.AppendLine($@"import socket");
            sb.AppendLine($@"import threading");
            sb.AppendLine($@"import traceback");
            sb.AppendLine($@"from typing import Optional, Dict, List, Any, Tuple");
            sb.AppendLine();
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 全局状态");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"_g_client = None");
            sb.AppendLine($@"_g_connected = False");
            sb.AppendLine($@"_g_sync_active = False");
            sb.AppendLine($@"_g_sync_timer = None");
            sb.AppendLine($@"_g_last_selection = []");
            sb.AppendLine($@"_g_shelf_name = 'ShanyuEngine'");
            sb.AppendLine();
        }

        private void GenerateTcpClient(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# TCP 客户端 — 与山雨引擎 LiveLink 服务器通信");
            sb.AppendLine($@"# 协议: 4 字节大端长度头 + JSON 载荷");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"class ShanyuTcpClient:");
            sb.AppendLine($@"    """"""与山雨引擎 LiveLink 服务器通信的 TCP 客户端。""""""");
            sb.AppendLine();
            sb.AppendLine($@"    def __init__(self, host: str = '{_tcpHost}', port: int = {_tcpPort}):");
            sb.AppendLine($@"        self.host = host");
            sb.AppendLine($@"        self.port = port");
            sb.AppendLine($@"        self._sock: Optional[socket.socket] = None");
            sb.AppendLine($@"        self._recv_buf = bytearray()");
            sb.AppendLine($@"        self._lock = threading.Lock()");
            sb.AppendLine($@"        self._running = False");
            sb.AppendLine($@"        self._thread: Optional[threading.Thread] = None");
            sb.AppendLine($@"        self._on_scene_cb = None");
            sb.AppendLine($@"        self._on_selection_cb = None");
            sb.AppendLine($@"        self._on_transform_cb = None");
            sb.AppendLine($@"        self._on_anim_cb = None");
            sb.AppendLine();
            sb.AppendLine($@"    @property");
            sb.AppendLine($@"    def connected(self) -> bool:");
            sb.AppendLine($@"        return self._sock is not None");
            sb.AppendLine();
            sb.AppendLine($@"    def connect(self) -> bool:");
            sb.AppendLine($@"        """"""连接到 LiveLink 服务器。""""""");
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)");
            sb.AppendLine($@"            self._sock.settimeout(5.0)");
            sb.AppendLine($@"            self._sock.connect((self.host, self.port))");
            sb.AppendLine($@"            self._sock.settimeout(None)");
            sb.AppendLine($@"            self._running = True");
            sb.AppendLine($@"            self._thread = threading.Thread(target=self._recv_loop, daemon=True)");
            sb.AppendLine($@"            self._thread.start()");
            sb.AppendLine($@"            print(f'[ShanyuEngine] Connected to {{self.host}}:{{self.port}}')");
            sb.AppendLine($@"            return True");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            print(f'[ShanyuEngine] Connection failed: {{e}}')");
            sb.AppendLine($@"            self._sock = None");
            sb.AppendLine($@"            return False");
            sb.AppendLine();
            sb.AppendLine($@"    def disconnect(self):");
            sb.AppendLine($@"        """"""断开连接。""""""");
            sb.AppendLine($@"        self._running = False");
            sb.AppendLine($@"        if self._sock:");
            sb.AppendLine($@"            try: self._sock.close()");
            sb.AppendLine($@"            except: pass");
            sb.AppendLine($@"        self._sock = None");
            sb.AppendLine($@"        self._recv_buf.clear()");
            sb.AppendLine($@"        print('[ShanyuEngine] Disconnected')");
            sb.AppendLine();
            sb.AppendLine($@"    def send(self, msg_type: str, payload: dict):");
            sb.AppendLine($@"        """"""发送 JSON 消息 (长度头 + 载荷)。""""""");
            sb.AppendLine($@"        if not self._sock: return False");
            sb.AppendLine($@"        try:");
            sb.AppendLine($@"            data = json.dumps({{'type': msg_type, 'payload': payload}}).encode('utf-8')");
            sb.AppendLine($@"            header = struct.pack('>I', len(data))");
            sb.AppendLine($@"            with self._lock:");
            sb.AppendLine($@"                self._sock.sendall(header + data)");
            sb.AppendLine($@"            return True");
            sb.AppendLine($@"        except Exception as e:");
            sb.AppendLine($@"            print(f'[ShanyuEngine] Send error: {{e}}')");
            sb.AppendLine($@"            return False");
            sb.AppendLine();
            sb.AppendLine($@"    def _recv_loop(self):");
            sb.AppendLine($@"        """"""接收消息循环。""""""");
            sb.AppendLine($@"        while self._running and self._sock:");
            sb.AppendLine($@"            try:");
            sb.AppendLine($@"                chunk = self._sock.recv(65536)");
            sb.AppendLine($@"                if not chunk: break");
            sb.AppendLine($@"                self._recv_buf.extend(chunk)");
            sb.AppendLine($@"                self._process_buffer()");
            sb.AppendLine($@"            except Exception as e:");
            sb.AppendLine($@"                if self._running: print(f'[ShanyuEngine] Recv error: {{e}}')");
            sb.AppendLine($@"                break");
            sb.AppendLine($@"        self._sock = None");
            sb.AppendLine($@"        print('[ShanyuEngine] Connection lost')");
            sb.AppendLine();
            sb.AppendLine($@"    def _process_buffer(self):");
            sb.AppendLine($@"        """"""解析长度前缀消息。""""""");
            sb.AppendLine($@"        while len(self._recv_buf) >= 4:");
            sb.AppendLine($@"            length = struct.unpack('>I', self._recv_buf[:4])[0]");
            sb.AppendLine($@"            if len(self._recv_buf) < 4 + length: break");
            sb.AppendLine($@"            raw = self._recv_buf[4:4+length]");
            sb.AppendLine($@"            del self._recv_buf[:4+length]");
            sb.AppendLine($@"            try:");
            sb.AppendLine($@"                msg = json.loads(raw.decode('utf-8'))");
            sb.AppendLine($@"                self._handle_message(msg)");
            sb.AppendLine($@"            except Exception as e:");
            sb.AppendLine($@"                print(f'[ShanyuEngine] Parse error: {{e}}')");
            sb.AppendLine();
            sb.AppendLine($@"    def _handle_message(self, msg: dict):");
            sb.AppendLine($@"        """"""处理接收到的消息。""""""");
            sb.AppendLine($@"        mtype = msg.get('type', '')");
            sb.AppendLine($@"        payload = msg.get('payload', {{}})");
            sb.AppendLine($@"        if mtype == 'SCENE_UPDATE' and self._on_scene_cb:");
            sb.AppendLine($@"            self._on_scene_cb(payload)");
            sb.AppendLine($@"        elif mtype == 'SELECTION_CHANGED' and self._on_selection_cb:");
            sb.AppendLine($@"            self._on_selection_cb(payload)");
            sb.AppendLine($@"        elif mtype == 'TRANSFORM_UPDATED' and self._on_transform_cb:");
            sb.AppendLine($@"            self._on_transform_cb(payload)");
            sb.AppendLine($@"        elif mtype == 'ANIMATION_FRAME' and self._on_anim_cb:");
            sb.AppendLine($@"            self._on_anim_cb(payload)");
            sb.AppendLine($@"        elif mtype == 'PONG': pass");
            sb.AppendLine();
        }

        private void GenerateSceneExporter(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 场景导出 — 从 Maya 提取场景数据并发送给引擎");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def collect_scene_data() -> dict:");
            sb.AppendLine($@"    """"""收集当前 Maya 场景中的网格、材质、灯光、摄像机数据。""""""");
            sb.AppendLine($@"    scene = {{");
            sb.AppendLine($@"        'name': cmds.file(q=True, sceneName=True) or 'Untitled',");
            sb.AppendLine($@"        'source': 'Maya',");
            sb.AppendLine($@"        'meshes': [],");
            sb.AppendLine($@"        'materials': [],");
            sb.AppendLine($@"        'lights': [],");
            sb.AppendLine($@"        'cameras': []");
            sb.AppendLine($@"    }}");
            sb.AppendLine();
            sb.AppendLine($@"    # 收集网格");
            sb.AppendLine($@"    meshes = cmds.ls(type='mesh', l=True)");
            sb.AppendLine($@"    for mesh in meshes:");
            sb.AppendLine($@"        parent = cmds.listRelatives(mesh, parent=True, fullPath=True)");
            sb.AppendLine($@"        if not parent: continue");
            sb.AppendLine($@"        parent = parent[0]");
            sb.AppendLine($@"        mesh_name = parent.split('|')[-1]");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取顶点");
            sb.AppendLine($@"        verts = cmds.xform(mesh + '.vtx[*]', q=True, t=True, ws=True)");
            sb.AppendLine($@"        vert_count = len(verts) // 3");
            sb.AppendLine($@"        vert_list = []");
            sb.AppendLine($@"        for i in range(vert_count):");
            sb.AppendLine($@"            vert_list.append([verts[i*3], verts[i*3+1], verts[i*3+2]])");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取面");
            sb.AppendLine($@"        faces = cmds.polyInfo(mesh, faceToVertex=True) or []");
            sb.AppendLine($@"        indices = []");
            sb.AppendLine($@"        for face_str in faces:");
            sb.AppendLine($@"            parts = face_str.strip().split()");
            sb.AppendLine($@"            count = int(parts[1])");
            sb.AppendLine($@"            for j in range(2, 2 + count):");
            sb.AppendLine($@"                indices.append(int(parts[j]))");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取 UV");
            sb.AppendLine($@"        uvs = cmds.polyEditUV(mesh + '.map[*]', q=True, u=True, v=True) or []");
            sb.AppendLine($@"        uv_list = []");
            sb.AppendLine($@"        for i in range(0, len(uvs) - 1, 2):");
            sb.AppendLine($@"            uv_list.append([uvs[i], uvs[i+1]])");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取法线");
            sb.AppendLine($@"        normals = cmds.polyNormalPerVertex(mesh, q=True, xyz=True) or []");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取变换矩阵");
            sb.AppendLine($@"        mat = cmds.xform(parent, q=True, matrix=True, ws=True)");
            sb.AppendLine();
            sb.AppendLine($@"        scene['meshes'].append({{");
            sb.AppendLine($@"            'name': mesh_name,");
            sb.AppendLine($@"            'vertices': vert_list,");
            sb.AppendLine($@"            'indices': indices,");
            sb.AppendLine($@"            'uvs': uv_list,");
            sb.AppendLine($@"            'transform': mat,");
            sb.AppendLine($@"            'material': _get_mesh_material(mesh)");
            sb.AppendLine($@"        }})");
            sb.AppendLine();
            sb.AppendLine($@"    # 收集灯光");
            sb.AppendLine($@"    lights = cmds.ls(lights=True)");
            sb.AppendLine($@"    for light in lights:");
            sb.AppendLine($@"        ltype = cmds.nodeType(light)");
            sb.AppendLine($@"        pos = cmds.xform(light, q=True, t=True, ws=True)");
            sb.AppendLine($@"        color = cmds.light(light, q=True, color=True) or [1,1,1]");
            sb.AppendLine($@"        intensity = cmds.light(light, q=True, intensity=True) or 1.0");
            sb.AppendLine($@"        scene['lights'].append({{");
            sb.AppendLine($@"            'name': light,");
            sb.AppendLine($@"            'type': ltype,");
            sb.AppendLine($@"            'position': pos,");
            sb.AppendLine($@"            'color': color,");
            sb.AppendLine($@"            'intensity': intensity");
            sb.AppendLine($@"        }})");
            sb.AppendLine();
            sb.AppendLine($@"    # 收集摄像机");
            sb.AppendLine($@"    cams = cmds.ls(cameras=True)");
            sb.AppendLine($@"    for cam in cams:");
            sb.AppendLine($@"        pos = cmds.xform(cam, q=True, t=True, ws=True)");
            sb.AppendLine($@"        rot = cmds.xform(cam, q=True, ro=True, ws=True)");
            sb.AppendLine($@"        foc = cmds.camera(cam, q=True, focalLength=True) or 35.0");
            sb.AppendLine($@"        scene['cameras'].append({{");
            sb.AppendLine($@"            'name': cam,");
            sb.AppendLine($@"            'position': pos,");
            sb.AppendLine($@"            'rotation': rot,");
            sb.AppendLine($@"            'focalLength': foc");
            sb.AppendLine($@"        }})");
            sb.AppendLine();
            sb.AppendLine($@"    return scene");
            sb.AppendLine();
            sb.AppendLine($@"def _get_mesh_material(mesh: str) -> str:");
            sb.AppendLine($@"    """"""获取网格关联的材质名称。""""""");
            sb.AppendLine($@"    shading_groups = cmds.listConnections(mesh, type='shadingEngine')");
            sb.AppendLine($@"    if shading_groups:");
            sb.AppendLine($@"        return shading_groups[0]");
            sb.AppendLine($@"    return 'default'");
            sb.AppendLine();
            sb.AppendLine($@"def export_fbx(filepath: str):");
            sb.AppendLine($@"    """"""导出场景为 FBX。""""""");
            sb.AppendLine($@"    old_selection = cmds.ls(sl=True)");
            sb.AppendLine($@"    cmds.select(allObjects=True)");
            sb.AppendLine($@"    mel.eval(f'FBXExport -f \"{{filepath}}\" -s')");
            sb.AppendLine($@"    if old_selection: cmds.select(old_selection)");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Exported FBX: {{filepath}}')");
            sb.AppendLine();
            sb.AppendLine($@"def export_alembic(filepath: str, frame_range: Tuple[int,int] = None):");
            sb.AppendLine($@"    """"""导出场景为 Alembic 缓存。""""""");
            sb.AppendLine($@"    start, end = frame_range or (cmds.playbackOptions(q=True, min=True), cmds.playbackOptions(q=True, max=True))");
            sb.AppendLine($@"    mel.eval(f'AbcExport -j \"-frameRange {{start}} {{end}} -writeVisibility -dataFormat ogawa -f \\\"{{filepath}}\\\"\"')");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Exported Alembic: {{filepath}}')");
            sb.AppendLine();
            sb.AppendLine($@"def export_obj(filepath: str):");
            sb.AppendLine($@"    """"""导出场景为 OBJ。""""""");
            sb.AppendLine($@"    old_selection = cmds.ls(sl=True)");
            sb.AppendLine($@"    cmds.select(allObjects=True)");
            sb.AppendLine($@"    cmds.file(filepath, force=True, options='groups=1;ptgroups=1;materials=1;smoothing=1;normals=1', type='OBJ', exportSelected=True)");
            sb.AppendLine($@"    if old_selection: cmds.select(old_selection)");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Exported OBJ: {{filepath}}')");
            sb.AppendLine();
        }

        private void GenerateMaterialSync(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 材质同步 — Maya shading nodes → 引擎 PBR 参数");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def collect_materials() -> list:");
            sb.AppendLine($@"    """"""收集所有材质并转换为 PBR 参数。""""""");
            sb.AppendLine($@"    materials = []");
            sb.AppendLine($@"    shading_engines = cmds.ls(type='shadingEngine')");
            sb.AppendLine($@"    for sg in shading_engines:");
            sb.AppendLine($@"        # 获取表面着色器");
            sb.AppendLine($@"        cons = cmds.listConnections(sg + '.surfaceShader')");
            sb.AppendLine($@"        if not cons: continue");
            sb.AppendLine($@"        shader = cons[0]");
            sb.AppendLine($@"        shader_type = cmds.nodeType(shader)");
            sb.AppendLine();
            sb.AppendLine($@"        mat = {{");
            sb.AppendLine($@"            'name': sg,");
            sb.AppendLine($@"            'shaderType': shader_type,");
            sb.AppendLine($@"            'baseColor': [0.8, 0.8, 0.8],");
            sb.AppendLine($@"            'metallic': 0.0,");
            sb.AppendLine($@"            'roughness': 0.5,");
            sb.AppendLine($@"            'emissive': [0, 0, 0],");
            sb.AppendLine($@"            'opacity': 1.0");
            sb.AppendLine($@"        }}");
            sb.AppendLine();
            sb.AppendLine($@"        if shader_type == 'aiStandardSurface':");
            sb.AppendLine($@"            # Arnold 标准表面着色器");
            sb.AppendLine($@"            color_attr = cmds.getAttr(shader + '.baseColor')");
            sb.AppendLine($@"            if color_attr: mat['baseColor'] = list(color_attr[0])");
            sb.AppendLine($@"            mat['metallic'] = cmds.getAttr(shader + '.metalness') or 0.0");
            sb.AppendLine($@"            mat['roughness'] = cmds.getAttr(shader + '.specularRoughness') or 0.5");
            sb.AppendLine($@"            emis = cmds.getAttr(shader + '.emissionColor')");
            sb.AppendLine($@"            if emis: mat['emissive'] = list(emis[0])");
            sb.AppendLine();
            sb.AppendLine($@"        elif shader_type in ('lambert', 'phong', 'blinn'):");
            sb.AppendLine($@"            # Maya 经典着色器");
            sb.AppendLine($@"            color_attr = cmds.getAttr(shader + '.color')");
            sb.AppendLine($@"            if color_attr: mat['baseColor'] = list(color_attr[0])");
            sb.AppendLine($@"            if shader_type == 'blinn':");
            sb.AppendLine($@"                mat['roughness'] = 1.0 - (cmds.getAttr(shader + '.eccentricity') or 0.3)");
            sb.AppendLine($@"            elif shader_type == 'phong':");
            sb.AppendLine($@"                cos_val = cmds.getAttr(shader + '.cosinePower') or 20.0");
            sb.AppendLine($@"                mat['roughness'] = max(0.01, 1.0 - (cos_val / 100.0))");
            sb.AppendLine();
            sb.AppendLine($@"        # 检查纹理连接");
            sb.AppendLine($@"        tex_conn = cmds.listConnections(shader + '.baseColor' if shader_type == 'aiStandardSurface' else shader + '.color', source=True)");
            sb.AppendLine($@"        if tex_conn:");
            sb.AppendLine($@"            file_node = tex_conn[0]");
            sb.AppendLine($@"            if cmds.nodeType(file_node) == 'file':");
            sb.AppendLine($@"                tex_path = cmds.getAttr(file_node + '.fileTextureName')");
            sb.AppendLine($@"                mat['baseColorTexture'] = tex_path");
            sb.AppendLine();
            sb.AppendLine($@"        materials.append(mat)");
            sb.AppendLine();
            sb.AppendLine($@"    return materials");
            sb.AppendLine();
            sb.AppendLine($@"def send_materials():");
            sb.AppendLine($@"    """"""发送材质数据到引擎。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    mats = collect_materials()");
            sb.AppendLine($@"    _g_client.send('MATERIAL_UPDATE', {{'materials': mats}})");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Sent {{len(mats)}} materials')");
            sb.AppendLine();
        }

        private void GenerateAnimationSync(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 动画同步 — Maya 动画曲线 → 引擎");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def collect_animation_data() -> dict:");
            sb.AppendLine($@"    """"""收集当前场景中的动画数据。""""""");
            sb.AppendLine($@"    anim = {{");
            sb.AppendLine($@"        'frameStart': cmds.playbackOptions(q=True, min=True),");
            sb.AppendLine($@"        'frameEnd': cmds.playbackOptions(q=True, max=True),");
            sb.AppendLine($@"        'frameRate': cmds.playbackOptions(q=True, fps=True),");
            sb.AppendLine($@"        'currentFrame': cmds.currentTime(q=True),");
            sb.AppendLine($@"        'objects': []");
            sb.AppendLine($@"    }}");
            sb.AppendLine();
            sb.AppendLine($@"    # 查找有动画的对象");
            sb.AppendLine($@"    animated = cmds.ls(animated=True)");
            sb.AppendLine($@"    for obj in animated:");
            sb.AppendLine($@"        obj_data = {{'name': obj, 'channels': []}}");
            sb.AppendLine();
            sb.AppendLine($@"        # 获取动画曲线");
            sb.AppendLine($@"        anim_curves = cmds.listConnections(obj, type='animCurve')");
            sb.AppendLine($@"        if not anim_curves: continue");
            sb.AppendLine();
            sb.AppendLine($@"        for curve in anim_curves:");
            sb.AppendLine($@"            curve_type = cmds.nodeType(curve)");
            sb.AppendLine($@"            times = cmds.keyframe(curve, q=True, timeChange=True) or []");
            sb.AppendLine($@"            values = cmds.keyframe(curve, q=True, valueChange=True) or []");
            sb.AppendLine($@"            obj_data['channels'].append({{");
            sb.AppendLine($@"                'curve': curve,");
            sb.AppendLine($@"                'type': curve_type,");
            sb.AppendLine($@"                'times': times,");
            sb.AppendLine($@"                'values': values");
            sb.AppendLine($@"            }})");
            sb.AppendLine();
            sb.AppendLine($@"        anim['objects'].append(obj_data)");
            sb.AppendLine();
            sb.AppendLine($@"    return anim");
            sb.AppendLine();
            sb.AppendLine($@"def send_animation_data():");
            sb.AppendLine($@"    """"""发送动画数据到引擎。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    anim = collect_animation_data()");
            sb.AppendLine($@"    _g_client.send('ANIMATION_DATA', anim)");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Sent animation data: {{len(anim[\"objects\"])}} objects')");
            sb.AppendLine();
            sb.AppendLine($@"def on_time_changed():");
            sb.AppendLine($@"    """"""时间轴变化回调,实时发送当前帧。""""""");
            sb.AppendLine($@"    if not _g_sync_active or not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    current = cmds.currentTime(q=True)");
            sb.AppendLine($@"    _g_client.send('TIME_SYNC', {{'frame': current}})");
            sb.AppendLine();
        }

        private void GenerateCameraSync(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 摄像机同步 — Maya 摄像机 → 引擎视口");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def get_active_camera() -> Optional[str]:");
            sb.AppendLine($@"    """"""获取当前活动摄像机。""""""");
            sb.AppendLine($@"    panel = cmds.getPanel(withFocus=True)");
            sb.AppendLine($@"    if panel and cmds.getPanel(typeOf=panel) == 'modelPanel':");
            sb.AppendLine($@"        cam = cmds.modelEditor(panel, q=True, camera=True)");
            sb.AppendLine($@"        if cam: return cam");
            sb.AppendLine($@"    # 回退到默认摄像机");
            sb.AppendLine($@"    cams = cmds.ls(cameras=True)");
            sb.AppendLine($@"    return cams[0] if cams else None");
            sb.AppendLine();
            sb.AppendLine($@"def send_camera_transform():");
            sb.AppendLine($@"    """"""发送当前摄像机变换到引擎。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    cam = get_active_camera()");
            sb.AppendLine($@"    if not cam: return");
            sb.AppendLine($@"    pos = cmds.xform(cam, q=True, t=True, ws=True)");
            sb.AppendLine($@"    rot = cmds.xform(cam, q=True, ro=True, ws=True)");
            sb.AppendLine($@"    foc = cmds.camera(cam, q=True, focalLength=True)");
            sb.AppendLine($@"    _g_client.send('CAMERA_UPDATE', {{");
            sb.AppendLine($@"        'name': cam,");
            sb.AppendLine($@"        'position': pos,");
            sb.AppendLine($@"        'rotation': rot,");
            sb.AppendLine($@"        'focalLength': foc");
            sb.AppendLine($@"    }})");
            sb.AppendLine();
        }

        private void GenerateShelfUI(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# Shelf UI — 在 Maya 中创建工具栏按钮");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def create_shelf():");
            sb.AppendLine($@"    """"""在 Maya 中创建 ShanyuEngine 工具架。""""""");
            sb.AppendLine($@"    global _g_shelf_name");
            sb.AppendLine();
            sb.AppendLine($@"    # 删除已有 shelf");
            sb.AppendLine($@"    if cmds.shelfLayout(_g_shelf_name, exists=True):");
            sb.AppendLine($@"        cmds.deleteUI(_g_shelf_name, layout=True)");
            sb.AppendLine();
            sb.AppendLine($@"    # 创建新 shelf");
            sb.AppendLine($@"    shelf = cmds.shelfLayout(_g_shelf_name, parent='Globals')");
            sb.AppendLine();
            sb.AppendLine($@"    # 连接按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Connect',");
            sb.AppendLine($@"        command=connect_to_engine,");
            sb.AppendLine($@"        image='commanderConnect.png',");
            sb.AppendLine($@"        annotation='连接到山雨引擎 LiveLink 服务器',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 断开按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Disconnect',");
            sb.AppendLine($@"        command=disconnect_from_engine,");
            sb.AppendLine($@"        image='commanderDisconnect.png',");
            sb.AppendLine($@"        annotation='断开与山雨引擎的连接',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 同步场景按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Sync Scene',");
            sb.AppendLine($@"        command=sync_scene,");
            sb.AppendLine($@"        image='cube.svg',");
            sb.AppendLine($@"        annotation='同步当前场景到山雨引擎',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 同步材质按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Sync Mats',");
            sb.AppendLine($@"        command=send_materials,");
            sb.AppendLine($@"        image='shader.svg',");
            sb.AppendLine($@"        annotation='同步材质到山雨引擎',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 同步动画按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Sync Anim',");
            sb.AppendLine($@"        command=send_animation_data,");
            sb.AppendLine($@"        image='time.svg',");
            sb.AppendLine($@"        annotation='同步动画数据到山雨引擎',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 导出 FBX 按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Export FBX',");
            sb.AppendLine($@"        command=export_scene_fbx,");
            sb.AppendLine($@"        image='fileExport.png',");
            sb.AppendLine($@"        annotation='导出场景为 FBX 文件',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    # 切换实时同步按钮");
            sb.AppendLine($@"    cmds.shelfButton(");
            sb.AppendLine($@"        label='Live Sync',");
            sb.AppendLine($@"        command=toggle_live_sync,");
            sb.AppendLine($@"        image='chainLink.png',");
            sb.AppendLine($@"        annotation='切换实时同步模式',");
            sb.AppendLine($@"        parent=shelf)");
            sb.AppendLine();
            sb.AppendLine($@"    print('[ShanyuEngine] Shelf created')");
            sb.AppendLine();
        }

        private void GenerateCallbackHandlers(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 回调函数 — 连接、同步、导出");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def connect_to_engine(*args):");
            sb.AppendLine($@"    """"""连接到山雨引擎 LiveLink 服务器。""""""");
            sb.AppendLine($@"    global _g_client");
            sb.AppendLine($@"    if _g_client and _g_client.connected:");
            sb.AppendLine($@"        print('[ShanyuEngine] Already connected')");
            sb.AppendLine($@"        return");
            sb.AppendLine($@"    _g_client = ShanyuTcpClient()");
            sb.AppendLine($@"    if _g_client.connect():");
            sb.AppendLine($@"        _g_client._on_scene_cb = on_scene_received");
            sb.AppendLine($@"        _g_client._on_selection_cb = on_selection_received");
            sb.AppendLine($@"        _g_client._on_transform_cb = on_transform_received");
            sb.AppendLine($@"        _g_client._on_anim_cb = on_animation_received");
            sb.AppendLine($@"        cmds.inViewMessage(amg='ShanyuEngine: <hl>Connected</hl>', pos='botLeft', fade=True)");
            sb.AppendLine();
            sb.AppendLine($@"def disconnect_from_engine(*args):");
            sb.AppendLine($@"    """"""断开与山雨引擎的连接。""""""");
            sb.AppendLine($@"    global _g_client, _g_sync_active");
            sb.AppendLine($@"    _g_sync_active = False");
            sb.AppendLine($@"    if _g_client:");
            sb.AppendLine($@"        _g_client.disconnect()");
            sb.AppendLine($@"        _g_client = None");
            sb.AppendLine($@"    cmds.inViewMessage(amg='ShanyuEngine: <hl>Disconnected</hl>', pos='botLeft', fade=True)");
            sb.AppendLine();
            sb.AppendLine($@"def sync_scene(*args):");
            sb.AppendLine($@"    """"""同步当前场景到山雨引擎。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected:");
            sb.AppendLine($@"        cmds.warning('[ShanyuEngine] Not connected! Use Connect button first.')");
            sb.AppendLine($@"        return");
            sb.AppendLine($@"    scene = collect_scene_data()");
            sb.AppendLine($@"    _g_client.send('SCENE_UPDATE', scene)");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Sent scene: {{len(scene[\"meshes\"])}} meshes, {{len(scene[\"lights\"])}} lights')");
            sb.AppendLine();
            sb.AppendLine($@"def export_scene_fbx(*args):");
            sb.AppendLine($@"    """"""导出场景为 FBX。""""""");
            sb.AppendLine($@"    filepath = cmds.fileDialog2(fileFilter='FBX Files (*.fbx)', dialogStyle=2, fileMode=0)");
            sb.AppendLine($@"    if filepath:");
            sb.AppendLine($@"        export_fbx(filepath[0])");
            sb.AppendLine();
            sb.AppendLine($@"def toggle_live_sync(*args):");
            sb.AppendLine($@"    """"""切换实时同步模式。""""""");
            sb.AppendLine($@"    global _g_sync_active, _g_sync_timer");
            sb.AppendLine($@"    _g_sync_active = not _g_sync_active");
            sb.AppendLine($@"    if _g_sync_active:");
            sb.AppendLine($@"        # 注册时间轴变化回调");
            sb.AppendLine($@"        cmds.timeChange(on_time_changed)");
            sb.AppendLine($@"        # 注册选择变化回调");
            sb.AppendLine($@"        cmds.selectionChanged(on_selection_changed)");
            sb.AppendLine($@"        # 启动周期性同步定时器 (每 500ms 同步一次变换)");
            sb.AppendLine($@"        _g_sync_timer = cmds.timerJob(interval=500, repeat=True, command=sync_transforms)");
            sb.AppendLine($@"        cmds.inViewMessage(amg='ShanyuEngine: <hl>Live Sync ON</hl>', pos='botLeft', fade=True)");
            sb.AppendLine($@"    else:");
            sb.AppendLine($@"        if _g_sync_timer:");
            sb.AppendLine($@"            cmds.killTimer(_g_sync_timer)");
            sb.AppendLine($@"            _g_sync_timer = None");
            sb.AppendLine($@"        try: cmds.timeChange(removeCallback=on_time_changed)");
            sb.AppendLine($@"        except: pass");
            sb.AppendLine($@"        cmds.inViewMessage(amg='ShanyuEngine: <hl>Live Sync OFF</hl>', pos='botLeft', fade=True)");
            sb.AppendLine();
            sb.AppendLine($@"def sync_transforms(*args):");
            sb.AppendLine($@"    """"""发送所有对象的变换数据。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    transforms = cmds.ls(transforms=True, l=True)");
            sb.AppendLine($@"    for t in transforms[:50]:  # 限制数量避免过载");
            sb.AppendLine($@"        mat = cmds.xform(t, q=True, matrix=True, ws=True)");
            sb.AppendLine($@"        _g_client.send('TRANSFORM_UPDATED', {{'name': t, 'transform': mat}})");
            sb.AppendLine();
            sb.AppendLine($@"def on_selection_changed(*args):");
            sb.AppendLine($@"    """"""选择变化回调。""""""");
            sb.AppendLine($@"    if not _g_client or not _g_client.connected: return");
            sb.AppendLine($@"    global _g_last_selection");
            sb.AppendLine($@"    sel = cmds.ls(sl=True, l=True) or []");
            sb.AppendLine($@"    if sel != _g_last_selection:");
            sb.AppendLine($@"        _g_last_selection = sel");
            sb.AppendLine($@"        _g_client.send('SELECTION_CHANGED', {{'objects': sel}})");
            sb.AppendLine();
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 引擎 → Maya 回调 (处理从引擎发来的消息)");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def on_scene_received(payload):");
            sb.AppendLine($@"    """"""接收到引擎发来的场景数据。""""""");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Received scene: {{payload.get(\"name\", \"?\")}}')");
            sb.AppendLine();
            sb.AppendLine($@"def on_selection_received(payload):");
            sb.AppendLine($@"    """"""接收到引擎发来的选择数据。""""""");
            sb.AppendLine($@"    objs = payload.get('objects', [])");
            sb.AppendLine($@"    print(f'[ShanyuEngine] Selection from engine: {{len(objs)}} objects')");
            sb.AppendLine();
            sb.AppendLine($@"def on_transform_received(payload):");
            sb.AppendLine($@"    """"""接收到引擎发来的变换数据。""""""");
            sb.AppendLine($@"    name = payload.get('name', '')");
            sb.AppendLine($@"    mat = payload.get('transform', [])");
            sb.AppendLine($@"    if cmds.objExists(name) and len(mat) == 16:");
            sb.AppendLine($@"        cmds.xform(name, matrix=mat, ws=True)");
            sb.AppendLine();
            sb.AppendLine($@"def on_animation_received(payload):");
            sb.AppendLine($@"    """"""接收到引擎发来的动画帧。""""""");
            sb.AppendLine($@"    frame = payload.get('frame')");
            sb.AppendLine($@"    if frame is not None:");
            sb.AppendLine($@"        cmds.currentTime(frame)");
            sb.AppendLine();
        }

        private void GenerateRegistration(StringBuilder sb)
        {
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 注册 — 自动创建工具架和回调");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"def auto_init():");
            sb.AppendLine($@"    """"""插件加载时自动初始化。""""""");
            sb.AppendLine($@"    try:");
            sb.AppendLine($@"        create_shelf()");
            sb.AppendLine($@"        print('[ShanyuEngine] Plugin initialized successfully.')");
            sb.AppendLine($@"        print('[ShanyuEngine] Use the shelf buttons to connect and sync.')");
            sb.AppendLine($@"    except Exception as e:");
            sb.AppendLine($@"        print(f'[ShanyuEngine] Init error: {{e}}')");
            sb.AppendLine($@"        traceback.print_exc()");
            sb.AppendLine();
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine($@"# 主入口");
            sb.AppendLine($@"# =============================================================================");
            sb.AppendLine();
            sb.AppendLine($@"if __name__ == '__main__' or True:");
            sb.AppendLine($@"    # 在 Maya Script Editor 中执行时自动初始化");
            sb.AppendLine($@"    auto_init()");
            sb.AppendLine();
        }
    }
}
