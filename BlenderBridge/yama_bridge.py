# Yama Engine Blender 5.1 Bridge Addon
# ==========================================
# 安装方法:
# 1. 将此文件复制到: %APPDATA%\Blender Foundation\Blender\5.1\scripts\addons\
# 2. 启动 Blender 5.1
# 3. 编辑 → 偏好设置 → 插件 → 搜索 "Yama" → 启用
# 4. 在侧边栏 (N面板) 找到 "Yama Engine" 选项卡

bl_info = {
    "name": "Yama Engine Bridge",
    "author": "Yama Engine Team",
    "version": (1, 0, 0),
    "blender": (5, 1, 0),
    "location": "View3D > Sidebar > Yama Engine",
    "description": "Native integration with Yama Engine - Live Link, Scene Export, Asset Pipeline",
    "category": "Import-Export",
    "doc_url": "https://yama-engine.dev/docs/blender",
    "tracker_url": "https://github.com/yama/yama-engine/issues",
    "support": "COMMUNITY",
}

import bpy
import bpy.props
import bpy.types
import bpy.utils
import json
import struct
import math
import mathutils
import os
import sys
import socket
import threading
import time
from bpy.app.handlers import persistent
from bpy.props import (
    StringProperty,
    BoolProperty,
    IntProperty,
    FloatProperty,
    EnumProperty,
    FloatVectorProperty,
)

# ============================================================
# 常量定义
# ============================================================

SHANYU_VERSION = "1.0.0"
DEFAULT_PORT = 18888
DEFAULT_HOST = "localhost"
SUPPORTED_FORMATS = [
    ('FBX', 'FBX', 'Autodesk FBX - Industry standard'),
    ('GLTF', 'glTF 2.0', 'Khronos glTF 2.0 - Web friendly'),
    ('OBJ', 'Wavefront OBJ', 'Alias Wavefront OBJ'),
    ('GLB', 'glTF Binary', 'glTF with embedded textures'),
]

# ============================================================
# 数据结构
# ============================================================

class YamaTransform:
    """变换矩阵"""
    def __init__(self, obj):
        self.position = obj.location[:]
        self.rotation = obj.rotation_euler[:] if obj.rotation_mode == 'XYZ' else obj.rotation_quaternion[:]
        self.scale = obj.scale[:]
        self.matrix_world = [list(row) for row in obj.matrix_local]
    
    def to_dict(self):
        return {
            "position": {"x": self.position[0], "y": self.position[1], "z": self.position[2]},
            "rotation": {"x": self.rotation[0], "y": self.rotation[1], "z": self.rotation[2]},
            "scale": {"x": self.scale[0], "y": self.scale[1], "z": self.scale[2]},
            "matrix": self.matrix_world
        }

class YamaMesh:
    """网格数据"""
    def __init__(self, obj, apply_modifiers=True, triangulate=True):
        self.name = obj.name
        self.transform = YamaTransform(obj)
        
        if obj.type != 'MESH':
            self.vertices = []
            self.normals = []
            self.uvs = []
            self.tangents = []
            self.colors = []
            self.indices = []
            self.materials = []
            return
        
        depsgraph = bpy.context.depsgraph if apply_modifiers else None
        mesh = obj.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
        
        if triangulate:
            import bmesh
            bm = bmesh.new()
            bm.from_mesh(mesh)
            bmesh.ops.triangulate(bm, faces=bm.faces)
            bm.to_mesh(mesh)
            bm.free()
            mesh.calc_triangulate()
        
        self.vertices = [list(v.co) for v in mesh.vertices]
        self.normals = [list(v.normal) for v in mesh.vertices]
        
        if mesh.uv_layers:
            self.uvs = [list(uv.uv) for uv in mesh.uv_layers[0].data]
        else:
            self.uvs = [[0, 0] for _ in mesh.vertices]
        
        if mesh.vertex_colors:
            self.colors = [list(col.color) for col in mesh.vertex_colors[0].data]
        else:
            self.colors = [[1, 1, 1, 1] for _ in mesh.vertices]
        
        self.indices = [list(poly.vertices) for poly in mesh.polygons]
        self.loop_indices = [list(poly.loop_indices) for poly in mesh.polygons]
        
        self.materials = []
        for mat in obj.material_slots:
            if mat.material:
                self.materials.append({
                    "name": mat.material.name,
                    " diffuse_color": list(mat.material.diffuse_color)[:3],
                    "specular_color": list(mat.material.specular_color)[:3],
                    "metallic": getattr(mat.material, 'metallic', 0.0),
                    "roughness": getattr(mat.material, 'roughness', 0.5),
                    "emission": list(getattr(mat.material, 'emission_color', (0,0,0)))[:3],
                })
        
        self.bounds = {
            "min": list(mesh.bounds[0]) if mesh.bounds else [0,0,0],
            "max": list(mesh.bounds[6]) if len(mesh.bounds) > 6 else [1,1,1]
        }
        
        self.triangle_count = len(mesh.polygons)
        self.vertex_count = len(mesh.vertices)
        
        if apply_modifiers:
            obj.to_mesh_clear()
    
    def to_dict(self):
        return {
            "name": self.name,
            "transform": self.transform.to_dict(),
            "vertices": self.vertices,
            "normals": self.normals,
            "uvs": self.uvs,
            "colors": self.colors,
            "indices": self.indices,
            "materials": self.materials,
            "bounds": self.bounds,
            "triangleCount": self.triangle_count,
            "vertexCount": self.vertex_count
        }

class YamaMaterial:
    """材质数据"""
    def __init__(self, material):
        self.name = material.name if material else "Default"
        
        if not material:
            self.diffuse = [0.8, 0.8, 0.8]
            self.specular = [0.5, 0.5, 0.5]
            self.metallic = 0.0
            self.roughness = 0.5
            self.emission = [0, 0, 0]
            self.opacity = 1.0
            self.alpha_threshold = 0.5
            return
        
        self.diffuse = list(material.diffuse_color)[:3] if hasattr(material, 'diffuse_color') else [0.8, 0.8, 0.8]
        self.specular = list(material.specular_color)[:3] if hasattr(material, 'specular_color') else [0.5, 0.5, 0.5]
        self.metallic = getattr(material, 'metallic', 0.0)
        self.roughness = getattr(material, 'roughness', 0.5)
        self.emission = list(getattr(material, 'emission_color', (0,0,0)))[:3] if hasattr(material, 'emission_color') else [0,0,0]
        self.opacity = 1.0 - getattr(material, ' transparency', 0.0)
        self.alpha_threshold = 0.5
        
        # 尝试从Principled BSDF读取
        if material.use_nodes:
            for node in material.node_tree.nodes:
                if node.type == 'BSDF_PRINCIPLED':
                    self.diffuse = list(node.inputs['Base Color'].default_value)[:3]
                    self.metallic = node.inputs['Metallic'].default_value
                    self.roughness = node.inputs['Roughness'].default_value
                    if 'Emission Color' in node.inputs:
                        self.emission = list(node.inputs['Emission Color'].default_value)[:3]
                    if 'Alpha' in node.inputs:
                        self.opacity = 1.0 - node.inputs['Alpha'].default_value
                    break
    
    def to_dict(self):
        return {
            "name": self.name,
            "diffuseColor": self.diffuse,
            "specularColor": self.specular,
            "metallic": self.metallic,
            "roughness": self.roughness,
            "emission": self.emission,
            "opacity": self.opacity,
            "alphaThreshold": self.alpha_threshold
        }

class YamaLight:
    """灯光数据"""
    def __init__(self, obj):
        self.name = obj.name
        self.transform = YamaTransform(obj)
        
        light = obj.data
        self.type = light.type
        
        if light.type == 'SUN':
            self.color = list(light.color)
            self.intensity = light.energy
            self.angle = math.degrees(light.angle)
        elif light.type == 'POINT':
            self.color = list(light.color)
            self.intensity = light.energy
            self.distance = light.distance
            self.falloff = light.falloff_type
        elif light.type == 'SPOT':
            self.color = list(light.color)
            self.intensity = light.energy
            self.distance = light.distance
            self.spot_angle = math.degrees(light.spot_size) / 2
            self.spot_blend = light.spot_blend
        elif light.type == 'AREA':
            self.color = list(light.color)
            self.intensity = light.energy
            self.size = light.size
            self.shape = light.shape
        
        self.use_shadow = light.use_shadow
        self.shadow_bias = light.shadow_buffer_bias
    
    def to_dict(self):
        d = self.transform.to_dict()
        d.update({
            "type": self.type,
            "color": self.color,
            "intensity": self.intensity,
            "useShadow": self.use_shadow,
            "shadowBias": self.shadow_bias
        })
        if hasattr(self, 'distance'): d["distance"] = self.distance
        if hasattr(self, 'spot_angle'): d["spotAngle"] = self.spot_angle
        if hasattr(self, 'shape'): d["shape"] = self.shape
        return d

class YamaCamera:
    """相机数据"""
    def __init__(self, obj):
        self.name = obj.name
        self.transform = YamaTransform(obj)
        
        camera = obj.data
        self.fov = math.degrees(camera.angle_x)
        self.aspect = camera.pixel_aspect_x / camera.pixel_aspect_y
        self.near_clip = camera.clip_start
        self.far_clip = camera.clip_end
        self.dof_enabled = camera.dof.use_dof
        self.dof_aperture = camera.dof.aperture_fstop if self.dof_enabled else 0
        self.dof_focus_distance = camera.dof.focus_distance if self.dof_enabled else 10
    
    def to_dict(self):
        d = self.transform.to_dict()
        d.update({
            "fov": self.fov,
            "aspect": self.aspect,
            "nearClip": self.near_clip,
            "farClip": self.far_clip,
            "dofEnabled": self.dof_enabled,
            "dofAperture": self.dof_aperture,
            "dofFocusDistance": self.dof_focus_distance
        })
        return d

class YamaArmature:
    """骨骼/骨架数据"""
    def __init__(self, obj):
        self.name = obj.name
        self.transform = YamaTransform(obj)
        self.bones = []
        self.bind_poses = []
        
        if obj.type != 'ARMATURE':
            return
        
        armature = obj.data
        
        for bone in armature.bones:
            bone_data = {
                "name": bone.name,
                "head": list(bone.head_local),
                "tail": list(bone.tail_local),
                "length": bone.length,
                "roll": bone.roll,
                "has_children": len(bone.children) > 0,
                "parent": bone.parent.name if bone.parent else None,
                "matrix_local": [list(row) for row in bone.matrix_local],
                "matrix_world": [list(row) for row in bone.matrix_local]  # 简化
            }
            self.bones.append(bone_data)
    
    def to_dict(self):
        return {
            "name": self.name,
            "transform": self.transform.to_dict(),
            "bones": self.bones,
            "boneCount": len(self.bones)
        }

class YamaAnimation:
    """动画数据"""
    def __init__(self, obj, action=None):
        self.name = action.name if action else obj.name + "_Anim"
        self.object = obj.name
        self.frame_start = int(action.frame_range[0]) if action else 1
        self.frame_end = int(action.frame_range[1]) if action else 250
        self.fps = bpy.context.scene.render.fps
        
        self.tracks = []
        
        if not action:
            return
        
        # 提取关键帧
        for fcurve in action.fcurves:
            data_path = fcurve.data_path
            array_index = fcurve.array_index
            
            # 解析 data_path (e.g., "location", "rotation_euler", "scale")
            prop_name = data_path.split('"')[1] if '"' in data_path else data_path.split('.')[0]
            
            for keyframe in fcurve.keyframe_points:
                frame = int(keyframe.co.x)
                value = keyframe.co.y
                
                track = {
                    "property": prop_name,
                    "index": array_index,
                    "frame": frame,
                    "value": value,
                    "interpolation": str(keyframe.interpolation),
                    "handle_left": [keyframe.handle_left.x, keyframe.handle_left.y],
                    "handle_right": [keyframe.handle_right.x, keyframe.handle_right.y]
                }
                self.tracks.append(track)
    
    def to_dict(self):
        return {
            "name": self.name,
            "object": self.object,
            "frameStart": self.frame_start,
            "frameEnd": self.frame_end,
            "fps": self.fps,
            "duration": self.frame_end - self.frame_start,
            "tracks": self.tracks
        }

class YamaSceneData:
    """完整场景数据"""
    def __init__(self):
        self.name = bpy.context.scene.name
        self.frame_start = bpy.context.scene.frame_start
        self.frame_end = bpy.context.scene.frame_end
        self.frame_current = bpy.context.scene.frame_current
        self.fps = bpy.context.scene.render.fps
        self.unit_scale = bpy.context.scene.unit_settings.scale_length
        self.axis_forward = str(bpy.context.scene.unit_settings.axis_forward)
        self.axis_up = str(bpy.context.scene.unit_settings.axis_up)
        
        self.meshes = []
        self.materials = []
        self.lights = []
        self.cameras = []
        self.armatures = []
        self.animations = []
        
        # 收集所有对象
        for obj in bpy.context.scene.objects:
            if obj.type == 'MESH':
                self.meshes.append(YamaMesh(obj))
            elif obj.type == 'LIGHT':
                self.lights.append(YamaLight(obj))
            elif obj.type == 'CAMERA':
                self.cameras.append(YamaCamera(obj))
            elif obj.type == 'ARMATURE':
                self.armatures.append(YamaArmature(obj))
        
        # 收集所有材质
        for mat in bpy.data.materials:
            if mat.use_nodes:
                self.materials.append(YamaMaterial(mat))
        
        # 收集所有动画
        for action in bpy.data.actions:
            if action.users > 0:
                for obj in bpy.context.scene.objects:
                    if obj.animation_data and obj.animation_data.action == action:
                        self.animations.append(YamaAnimation(obj, action))
                        break
    
    def to_dict(self):
        return {
            "version": SHANYU_VERSION,
            "source": "Blender 5.1",
            "name": self.name,
            "frame": {
                "start": self.frame_start,
                "end": self.frame_end,
                "current": self.frame_current,
                "fps": self.fps
            },
            "unit": {
                "scale": self.unit_scale,
                "forward": self.axis_forward,
                "up": self.axis_up
            },
            "meshes": [m.to_dict() for m in self.meshes],
            "materials": [m.to_dict() for m in self.materials],
            "lights": [l.to_dict() for l in self.lights],
            "cameras": [c.to_dict() for c in self.cameras],
            "armatures": [a.to_dict() for a in self.armatures],
            "animations": [a.to_dict() for a in self.animations],
            "stats": {
                "meshCount": len(self.meshes),
                "materialCount": len(self.materials),
                "lightCount": len(self.lights),
                "cameraCount": len(self.cameras),
                "armatureCount": len(self.armatures),
                "animationCount": len(self.animations),
                "totalTriangles": sum(m.triangle_count for m in self.meshes),
                "totalVertices": sum(m.vertex_count for m in self.meshes)
            }
        }
    
    def to_json(self, indent=2):
        return json.dumps(self.to_dict(), indent=indent, ensure_ascii=False)
    
    def to_binary(self):
        """转换为二进制格式 (更高效的传输)"""
        data = self.to_dict()
        return json.dumps(data).encode('utf-8')

# ============================================================
# WebSocket 通信
# ============================================================

class WebSocketBridge:
    """WebSocket 风格的 TCP 通信桥"""
    
    def __init__(self, host=DEFAULT_HOST, port=DEFAULT_PORT):
        self.host = host
        self.port = port
        self.socket = None
        self.connected = False
        self.receive_thread = None
        self.callbacks = {}
    
    def connect(self):
        """连接到 Yama Editor"""
        try:
            self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.socket.settimeout(5.0)
            self.socket.connect((self.host, self.port))
            self.connected = True
            print(f"[Yama] Connected to {self.host}:{self.port}")
            
            self.receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self.receive_thread.start()
            return True
        except Exception as e:
            print(f"[Yama] Connection failed: {e}")
            self.connected = False
            return False
    
    def disconnect(self):
        """断开连接"""
        self.connected = False
        if self.socket:
            try:
                self.socket.close()
            except:
                pass
            self.socket = None
        print("[Yama] Disconnected")
    
    def send(self, data):
        """发送数据"""
        if not self.connected or not self.socket:
            return False
        
        try:
            message = json.dumps(data).encode('utf-8')
            length = len(message)
            # 添加 4 字节长度前缀
            header = struct.pack('>I', length)
            self.socket.sendall(header + message)
            return True
        except Exception as e:
            print(f"[Yama] Send failed: {e}")
            self.connected = False
            return False
    
    def send_scene(self, scene_data=None):
        """发送完整场景"""
        if scene_data is None:
            scene_data = YamaSceneData()
        
        data = {
            "type": "SCENE_UPDATE",
            "payload": scene_data.to_dict()
        }
        return self.send(data)
    
    def send_selection(self, selected_objects):
        """发送选中对象"""
        data = {
            "type": "SELECTION_CHANGED",
            "payload": {"objects": [obj.name for obj in selected_objects]}
        }
        return self.send(data)
    
    def send_transform(self, obj):
        """发送变换更新"""
        transform = YamaTransform(obj)
        data = {
            "type": "TRANSFORM_UPDATED",
            "payload": {
                "name": obj.name,
                "transform": transform.to_dict()
            }
        }
        return self.send(data)
    
    def register_callback(self, msg_type, callback):
        """注册消息回调"""
        self.callbacks[msg_type] = callback
    
    def _receive_loop(self):
        """接收消息循环"""
        buffer = b''
        while self.connected:
            try:
                chunk = self.socket.recv(4096)
                if not chunk:
                    break
                buffer += chunk
                
                while len(buffer) >= 4:
                    length = struct.unpack('>I', buffer[:4])[0]
                    if len(buffer) >= 4 + length:
                        message = buffer[4:4+length]
                        buffer = buffer[4+length:]
                        
                        try:
                            data = json.loads(message.decode('utf-8'))
                            self._handle_message(data)
                        except json.JSONDecodeError as e:
                            print(f"[Yama] JSON decode error: {e}")
            except socket.timeout:
                continue
            except Exception as e:
                print(f"[Yama] Receive error: {e}")
                break
        
        self.connected = False
    
    def _handle_message(self, data):
        """处理收到的消息"""
        msg_type = data.get("type", "")
        payload = data.get("payload", {})
        
        if msg_type in self.callbacks:
            self.callbacks[msg_type](payload)
        elif msg_type == "PING":
            self.send({"type": "PONG"})
        elif msg_type == "REQUEST_SCENE":
            self.send_scene()
        elif msg_type == "SELECT_OBJECT":
            self._select_in_blender(payload.get("name"))
        elif msg_type == "SET_ACTIVE":
            bpy.context.view_layer.objects.active = bpy.data.objects.get(payload.get("name"))
    
    def _select_in_blender(self, obj_name):
        """在 Blender 中选中对象"""
        obj = bpy.data.objects.get(obj_name)
        if obj:
            bpy.ops.object.select_all(action='DESELECT')
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj

# 全局 WebSocket 桥
bridge = None

def get_bridge():
    global bridge
    if bridge is None:
        bridge = WebSocketBridge()
    return bridge

# ============================================================
# Blender 操作符
# ============================================================

class SHANYU_OT_Connect(bpy.types.Operator):
    """连接到 Yama Editor"""
    bl_idname = "yama.connect"
    bl_label = "Connect to Yama"
    bl_options = {'REGISTER'}
    
    def execute(self, context):
        b = get_bridge()
        if b.connect():
            self.report({'INFO'}, f"Connected to Yama Engine")
            # 发送初始场景
            b.send_scene()
            return {'FINISHED'}
        else:
            self.report({'ERROR'}, "Connection failed - is Yama Editor running?")
            return {'CANCELLED'}

class SHANYU_OT_Disconnect(bpy.types.Operator):
    """断开连接"""
    bl_idname = "yama.disconnect"
    bl_label = "Disconnect"
    bl_options = {'REGISTER'}
    
    def execute(self, context):
        get_bridge().disconnect()
        return {'FINISHED'}

class SHANYU_OT_ExportScene(bpy.types.Operator):
    """导出场景到 Yama 格式"""
    bl_idname = "yama.export_scene"
    bl_label = "Export Scene"
    bl_options = {'REGISTER'}
    
    filepath: bpy.props.StringProperty(subtype='FILE_PATH')
    filter_glob: bpy.props.StringProperty(default="*.json", options={'HIDDEN'})
    
    def execute(self, context):
        scene_data = YamaSceneData()
        
        if self.filepath:
            with open(self.filepath, 'w', encoding='utf-8') as f:
                f.write(scene_data.to_json())
            self.report({'INFO'}, f"Exported to {self.filepath}")
        else:
            self.report({'WARNING'}, "No filepath specified")
        
        return {'FINISHED'}
    
    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

class SHANYU_OT_ExportFBX(bpy.types.Operator):
    """导出为 FBX 格式"""
    bl_idname = "yama.export_fbx"
    bl_label = "Export FBX"
    bl_options = {'REGISTER'}
    
    filepath: bpy.props.StringProperty(subtype='FILE_PATH')
    
    def execute(self, context):
        if not self.filepath:
            self.report({'ERROR'}, "No filepath")
            return {'CANCELLED'}
        
        bpy.ops.export_scene.fbx(
            filepath=self.filepath,
            use_selection=False,
            apply_scale_options='FBX_SCALE_ALL',
            axis_forward='-Z',
            axis_up='Y',
            bake_space_transform=True,
            object_types={'MESH', 'ARMATURE', 'LIGHT', 'CAMERA'},
            use_mesh_modifiers=True,
            mesh_smooth_type='FACE',
            use_tspace=True,
            use_custom_props=False,
            add_leaf_bones=True,
            primary_bone_axis='Y',
            secondary_bone_axis='X',
            use_metadata=True
        )
        
        self.report({'INFO'}, f"FBX exported to {self.filepath}")
        return {'FINISHED'}
    
    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

class SHANYU_OT_ExportGLTF(bpy.types.Operator):
    """导出为 glTF 2.0 格式"""
    bl_idname = "yama.export_gltf"
    bl_label = "Export glTF"
    bl_options = {'REGISTER'}
    
    filepath: bpy.props.StringProperty(subtype='FILE_PATH')
    export_format: bpy.props.EnumProperty(
        name="Format",
        items=[('GLB', 'GLB', 'Binary glTF'), ('GLTF_SEPARATE', 'gLTF', 'Separate files')]
    )
    
    def execute(self, context):
        if not self.filepath:
            self.report({'ERROR'}, "No filepath")
            return {'CANCELLED'}
        
        bpy.ops.export_scene.gltf(
            filepath=self.filepath,
            export_format=self.export_format,
            export_materials='EXPORT',
            export_colors=True,
            export_normals=True,
            export_tangents=True,
            export_attributes=True,
            use_selection=False,
            export_yup=True,
            export_animations=True,
            export_frame_range=True,
            export_frame_step=1,
            export_meshes=True,
            export_lights=True,
            export_cameras=True,
            export_selected=False
        )
        
        self.report({'INFO'}, f"glTF exported to {self.filepath}")
        return {'FINISHED'}
    
    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

class SHANYU_OT_SyncScene(bpy.types.Operator):
    """同步场景到 Yama Editor"""
    bl_idname = "yama.sync_scene"
    bl_label = "Sync Scene"
    bl_options = {'REGISTER', 'UNDO'}
    
    def execute(self, context):
        b = get_bridge()
        if b.connected:
            b.send_scene()
            self.report({'INFO'}, "Scene synced to Yama Engine")
        else:
            self.report({'WARNING'}, "Not connected to Yama Engine")
        return {'FINISHED'}

class SHANYU_OT_SyncSelection(bpy.types.Operator):
    """同步选中对象"""
    bl_idname = "yama.sync_selection"
    bl_label = "Sync Selection"
    bl_options = {'REGISTER'}
    
    def execute(self, context):
        b = get_bridge()
        if b.connected:
            b.send_selection(context.selected_objects)
            self.report({'INFO'}, f"Synced {len(context.selected_objects)} objects")
        else:
            self.report({'WARNING'}, "Not connected")
        return {'FINISHED'}

class SHANYU_OT_SyncTransform(bpy.types.Operator):
    """同步变换 (需要选中对象)"""
    bl_idname = "yama.sync_transform"
    bl_label = "Sync Transform"
    bl_options = {'REGISTER'}
    
    def execute(self, context):
        b = get_bridge()
        if not b.connected:
            self.report({'WARNING'}, "Not connected")
            return {'CANCELLED'}
        
        if context.active_object:
            b.send_transform(context.active_object)
            self.report({'INFO'}, f"Transform synced: {context.active_object.name}")
        else:
            self.report({'WARNING'}, "No active object")
        return {'CANCELLED'}
    
    def invoke(self, context, event):
        return self.execute(context)

class SHANYU_OT_CreatePrimitive(bpy.types.Operator):
    """创建基础图元"""
    bl_idname = "yama.create_primitive"
    bl_label = "Create Primitive"
    bl_options = {'REGISTER', 'UNDO'}
    
    type: bpy.props.EnumProperty(
        name="Type",
        items=[
            ('BOX', 'Box', 'Cube'),
            ('SPHERE', 'Sphere', 'UV Sphere'),
            ('CYLINDER', 'Cylinder', 'Cylinder'),
            ('CONE', 'Cone', 'Cone'),
            ('TORUS', 'Torus', 'Torus'),
            ('PLANE', 'Plane', 'Plane'),
            ('ICO_SPHERE', 'Ico Sphere', 'Ico Sphere'),
        ]
    )
    
    def execute(self, context):
        location = context.scene.cursor.location
        rotation = context.scene.cursor.rotation_euler
        
        mesh = None
        if self.type == 'BOX':
            bpy.ops.mesh.primitive_cube_add(size=2, location=location, rotation=rotation)
        elif self.type == 'SPHERE':
            bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=16, size=1, location=location)
        elif self.type == 'CYLINDER':
            bpy.ops.mesh.primitive_cylinder_add(radius=0.5, depth=2, vertices=32, location=location, rotation=rotation)
        elif self.type == 'CONE':
            bpy.ops.mesh.primitive_cone_add(radius1=1, depth=2, vertices=32, location=location, rotation=rotation)
        elif self.type == 'TORUS':
            bpy.ops.mesh.primitive_torus_add(major_radius=1, minor_radius=0.25, location=location)
        elif self.type == 'PLANE':
            bpy.ops.mesh.primitive_plane_add(size=2, location=location, rotation=rotation)
        elif self.type == 'ICO_SPHERE':
            bpy.ops.mesh.primitive_ico_sphere_add(radius=1, subdivisions=3, location=location)
        
        obj = context.active_object
        if obj:
            obj.name = f"SM_{self.type}_{context.scene.name}"
            
            # 添加默认材质
            mat = bpy.data.materials.new(name=f"Mat_{self.type}")
            mat.use_nodes = True
            obj.data.materials.append(mat)
        
        return {'FINISHED'}

class SHANYU_OT_ApplyTransforms(bpy.types.Operator):
    """应用变换 (Alt+G位置, Alt+R旋转, Alt+S缩放)"""
    bl_idname = "yama.apply_transforms"
    bl_label = "Apply Transforms"
    bl_options = {'REGISTER', 'UNDO'}
    
    apply_location: bpy.props.BoolProperty(name="Location", default=True)
    apply_rotation: bpy.props.BoolProperty(name="Rotation", default=True)
    apply_scale: bpy.props.BoolProperty(name="Scale", default=True)
    
    def execute(self, context):
        bpy.ops.object.transform_apply(
            location=self.apply_location,
            rotation=self.apply_rotation,
            scale=self.apply_scale
        )
        self.report({'INFO'}, "Transforms applied")
        return {'FINISHED'}

class SHANYU_OT_RenameObject(bpy.types.Operator):
    """重命名对象 (Yama 命名规范)"""
    bl_idname = "yama.rename_object"
    bl_label = "Rename for Yama"
    bl_options = {'REGISTER', 'UNDO'}
    
    prefix: bpy.props.EnumProperty(
        name="Prefix",
        items=[
            ('SM', 'SM_', 'Static Mesh'),
            ('SK', 'SK_', 'Skeletal Mesh'),
            ('BP', 'BP_', 'Blueprint'),
            ('LGT', 'LGT_', 'Light'),
            ('CAM', 'CAM_', 'Camera'),
        ]
    )
    
    def execute(self, context):
        for obj in context.selected_objects:
            obj.name = f"{self.prefix}{obj.name}"
        return {'FINISHED'}

class SHANYU_OT_GenerateLODs(bpy.types.Operator):
    """生成细节层次 (LOD)"""
    bl_idname = "yama.generate_lods"
    bl_label = "Generate LODs"
    bl_options = {'REGISTER', 'UNDO'}
    
    lod_levels: bpy.props.IntProperty(name="LOD Levels", default=4, min=2, max=6)
    lod_ratio: bpy.props.FloatProperty(name="Decimation Ratio", default=0.5, min=0.1, max=0.9)
    
    def execute(self, context):
        original = context.active_object
        if not original or original.type != 'MESH':
            self.report({'ERROR'}, "Select a mesh object")
            return {'CANCELLED'}
        
        original.select_set(True)
        
        for i in range(1, self.lod_levels):
            ratio = self.lod_ratio ** i
            vert_count = int(len(original.data.vertices) * ratio)
            
            if vert_count < 10:
                break
            
            # 创建 LOD
            lod_mesh = original.data.copy()
            lod_obj = original.copy()
            lod_obj.data = lod_mesh
            lod_obj.name = f"{original.name}_LOD{i}"
            
            # 简化
            bpy.context.view_layer.objects.active = lod_obj
            bpy.ops.object.mode_set(mode='EDIT')
            ratio_actual = vert_count / len(original.data.vertices)
            bpy.ops.mesh.decimate(ratio=ratio_actual)
            bpy.ops.object.mode_set(mode='OBJECT')
            
            context.collection.objects.link(lod_obj)
        
        original.select_set(True)
        self.report({'INFO'}, f"Generated LODs for {original.name}")
        return {'FINISHED'}

class SHANYU_OT_CreateMaterial(bpy.types.Operator):
    """创建 PBR 材质"""
    bl_idname = "yama.create_material"
    bl_label = "Create PBR Material"
    bl_options = {'REGISTER'}
    
    material_type: bpy.props.EnumProperty(
        name="Type",
        items=[
            ('METAL', 'Metal', 'Metallic PBR'),
            ('PLASTIC', 'Plastic', 'Roughness PBR'),
            ('GLASS', 'Glass', 'Transparent'),
            ('EMISSIVE', 'Emissive', 'Self-illuminating'),
        ]
    )
    
    def execute(self, context):
        mat = bpy.data.materials.new(name=f"Mat_{self.material_type}")
        mat.use_nodes = True
        
        # 清空默认节点
        mat.node_tree.nodes.clear()
        
        # 创建 Principled BSDF
        bsdf = mat.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        bsdf.location = (0, 0)
        
        # 创建输出
        output = mat.node_tree.nodes.new('ShaderNodeOutputMaterial')
        output.location = (300, 0)
        
        mat.node_tree.links.new(bsdf.outputs['BSDF'], output.inputs['Surface'])
        
        if self.material_type == 'METAL':
            bsdf.inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1)
            bsdf.inputs['Metallic'].default_value = 1.0
            bsdf.inputs['Roughness'].default_value = 0.2
        elif self.material_type == 'PLASTIC':
            bsdf.inputs['Base Color'].default_value = (0.3, 0.5, 0.8, 1)
            bsdf.inputs['Metallic'].default_value = 0.0
            bsdf.inputs['Roughness'].default_value = 0.4
        elif self.material_type == 'GLASS':
            bsdf.inputs['Base Color'].default_value = (0.9, 0.9, 1.0, 1)
            bsdf.inputs['Metallic'].default_value = 0.0
            bsdf.inputs['Roughness'].default_value = 0.0
            bsdf.inputs['Alpha'].default_value = 0.3
            bsdf.inputs['Transmission Weight'].default_value = 0.95
        elif self.material_type == 'EMISSIVE':
            bsdf.inputs['Base Color'].default_value = (1.0, 0.8, 0.2, 1)
            bsdf.inputs['Emission Color'].default_value = (1.0, 0.8, 0.2, 1)
            bsdf.inputs['Emission Strength'].default_value = 5.0
        
        # 如果有选中对象,应用材质
        if context.selected_objects:
            for obj in context.selected_objects:
                if obj.type == 'MESH':
                    obj.data.materials.append(mat)
        
        self.report({'INFO'}, f"Created {mat.name}")
        return {'FINISHED'}

# ============================================================
# UI 面板
# ============================================================

class SHANYU_PT_MainPanel(bpy.types.Panel):
    """Yama Engine 主面板"""
    bl_label = "Yama Engine"
    bl_idname = "SHANYU_PT_main"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Yama"
    
    def draw(self, context):
        layout = self.layout
        
        # 连接状态
        b = get_bridge()
        box = layout.box()
        row = box.row()
        
        if b.connected:
            row.label(text=f"Connected: {b.host}:{b.port}", icon='URL')
            row.operator("yama.disconnect", text="", icon='X', emboss=False)
        else:
            row.label(text="Not Connected", icon='URL')
            row.operator("yama.connect", text="Connect", icon='PLAY')
        
        layout.separator()
        
        # 同步按钮
        box = layout.box()
        col = box.column(align=True)
        col.label(text="Sync", icon='ARROW_LEFTRIGHT')
        col.operator("yama.sync_scene", text="Sync Scene", icon='SCENE')
        col.operator("yama.sync_selection", text="Sync Selection", icon='RESTRICT_SELECT_OFF')
        col.operator("yama.sync_transform", text="Sync Transform", icon='OBJECT_DATA')

class SHANYU_PT_ExportPanel(bpy.types.Panel):
    """导出面板"""
    bl_label = "Export"
    bl_idname = "SHANYU_PT_export"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Yama"
    bl_parent_id = "SHANYU_PT_main"
    bl_order = 1
    
    def draw(self, context):
        layout = self.layout
        col = layout.column(align=True)
        
        col.label(text="Native Formats", icon='EXPORT')
        col.operator("yama.export_scene", text="Export .yama", icon='TEXT')
        
        layout.separator()
        
        col.label(text="Industry Formats", icon='EXPORT')
        col.operator("yama.export_fbx", text="Export FBX", icon='MONKEY')
        col.operator("yama.export_gltf", text="Export glTF", icon='LATTICE')

class SHANYU_PT_CreatePanel(bpy.types.Panel):
    """创建面板"""
    bl_label = "Create"
    bl_idname = "SHANYU_PT_create"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Yama"
    bl_parent_id = "SHANYU_PT_main"
    bl_order = 2
    
    def draw(self, context):
        layout = self.layout
        col = layout.column(align=True)
        
        col.label(text="Primitives", icon='OUTLINER_OB_MESH')
        
        row = col.row(align=True)
        row.operator("yama.create_primitive", text="Cube").type = 'BOX'
        row.operator("yama.create_primitive", text="Sphere").type = 'SPHERE'
        
        row = col.row(align=True)
        row.operator("yama.create_primitive", text="Cylinder").type = 'CYLINDER'
        row.operator("yama.create_primitive", text="Cone").type = 'CONE'
        
        row = col.row(align=True)
        row.operator("yama.create_primitive", text="Torus").type = 'TORUS'
        row.operator("yama.create_primitive", text="Plane").type = 'PLANE'
        
        layout.separator()
        
        col.label(text="Materials", icon='MATERIAL')
        col.menu("SHANYU_MT_materials", text="Create PBR Material", icon='ADD')

class SHANYU_MT_materials(bpy.types.Menu):
    """材质创建菜单"""
    bl_idname = "SHANYU_MT_materials"
    bl_label = "Create Material"
    
    def draw(self, context):
        layout = self.layout
        layout.operator("yama.create_material", text="Metal", icon='SPHERE').material_type = 'METAL'
        layout.operator("yama.create_material", text="Plastic", icon='SPHERE').material_type = 'PLASSIC'
        layout.operator("yama.create_material", text="Glass", icon='SPHERE').material_type = 'GLASS'
        layout.operator("yama.create_material", text="Emissive", icon='SPHERE').material_type = 'EMISSIVE'

class SHANYU_PT_ToolsPanel(bpy.types.Panel):
    """工具面板"""
    bl_label = "Tools"
    bl_idname = "SHANYU_PT_tools"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Yama"
    bl_parent_id = "SHANYU_PT_main"
    bl_order = 3
    
    def draw(self, context):
        layout = self.layout
        
        col = layout.column(align=True)
        col.label(text="Utilities", icon='TOOL_SETTINGS')
        
        col.operator("yama.apply_transforms", text="Apply Transforms", icon='OBJECT_ORIGIN')
        col.operator("yama.rename_object", text="Rename (SM_)", icon='SORTALPHA').prefix = 'SM'
        col.operator("yama.generate_lods", text="Generate LODs", icon='LOD')
        
        layout.separator()
        
        col = layout.column(align=True)
        col.label(text="Statistics", icon='INFO')
        
        scene_data = YamaSceneData()
        stats = scene_data.to_dict()["stats"]
        
        col.label(text=f"Meshes: {stats['meshCount']}")
        col.label(text=f"Materials: {stats['materialCount']}")
        col.label(text=f"Lights: {stats['lightCount']}")
        col.label(text=f"Cameras: {stats['cameraCount']}")
        col.label(text=f"Triangles: {stats['totalTriangles']:,}")
        col.label(text=f"Vertices: {stats['totalVertices']:,}")

# ============================================================
# 菜单项
# ============================================================

def menu_func_export(self, context):
    self.layout.operator("yama.export_scene", text="Yama Engine (.yama)")
    self.layout.separator()
    self.layout.operator("yama.export_fbx", text="Yama FBX (.fbx)")
    self.layout.operator("yama.export_gltf", text="Yama glTF (.gltf/.glb)")

def menu_func_import(self, context):
    self.layout.separator()
    self.layout.label(text="Yama Engine", icon='URL')

# ============================================================
# 实时同步 (动画回调)
# ============================================================

@persistent
def scene_update_handler(scene):
    """场景更新处理器 - 用于实时同步"""
    b = get_bridge()
    if b.connected:
        # 节流: 每 10 帧同步一次
        if scene.frame_current % 10 == 0:
            b.send_scene()

@persistent
def depsgraph_update_handler(scene, depsgraph):
    """依赖图更新处理器"""
    b = get_bridge()
    if b.connected:
        # 检测选中对象变化
        pass

# ============================================================
# 注册/反注册
# ============================================================

classes = (
    SHANYU_OT_Connect,
    SHANYU_OT_Disconnect,
    SHANYU_OT_ExportScene,
    SHANYU_OT_ExportFBX,
    SHANYU_OT_ExportGLTF,
    SHANYU_OT_SyncScene,
    SHANYU_OT_SyncSelection,
    SHANYU_OT_SyncTransform,
    SHANYU_OT_CreatePrimitive,
    SHANYU_OT_ApplyTransforms,
    SHANYU_OT_RenameObject,
    SHANYU_OT_GenerateLODs,
    SHANYU_OT_CreateMaterial,
    SHANYU_PT_MainPanel,
    SHANYU_PT_ExportPanel,
    SHANYU_PT_CreatePanel,
    SHANYU_PT_ToolsPanel,
    SHANYU_MT_materials,
)

def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    
    # 注册菜单
    bpy.types.TOPBAR_MT_file_export.append(menu_func_export)
    
    # 注册处理器
    bpy.app.handlers.depsgraph_update_post.append(depsgraph_update_handler)
    
    print(f"[Yama Engine] Bridge addon v{SHANYU_VERSION} loaded")
    print("[Yama Engine] Access via: View3D > Sidebar > Yama")

def unregister():
    # 断开连接
    get_bridge().disconnect()
    
    # 反注册菜单
    bpy.types.TOPBAR_MT_file_export.remove(menu_func_export)
    
    # 移除处理器
    if depsgraph_update_handler in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(depsgraph_update_handler)
    
    # 反注册类
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
    
    print("[Yama Engine] Bridge addon unloaded")

if __name__ == "__main__":
    register()
