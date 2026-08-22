"""
Shanyu Engine 3ds Max 2024/2025 Bridge Plugin
==============================================
Installation:
1. Copy this folder to: C:/Program Files/Autodesk/3ds Max 2025/scripts/ShanyuBridge/
2. In 3ds Max, go to: Scripting > MaxScript Editor
3. Execute: python.ExecuteFile "C:/Program Files/Autodesk/3ds Max 2025/scripts/ShanyuBridge/shanyu_max_plugin.py"
4. Or add to startup: Add to C:/Program Files/Autodesk/3ds Max 2025/scripts/startup/

Features:
- Live Link real-time sync with Shanyu Editor
- Scene export (FBX, OBJ, USD)
- PBR Material transfer (Physical Material)
- Animation export
- Camera sync
"""

import pymxs
import MaxPlus
import json
import socket
import struct
import threading
import math
import os
import time
from typing import List, Dict, Any, Optional

SHANYU_VERSION = "1.0.0"
DEFAULT_PORT = 18888
DEFAULT_HOST = "localhost"

rt = pymxs.runtime

class MaxTransform:
    @staticmethod
    def get_position(obj) -> Dict[str, float]:
        pos = obj.position
        return {"x": pos.x, "y": pos.z, "z": pos.y}
    
    @staticmethod
    def get_rotation(obj) -> Dict[str, float]:
        rot = obj.rotation
        euler = rt.quatToEuler(rot)
        return {
            "x": math.degrees(euler.x),
            "y": math.degrees(euler.z),
            "z": math.degrees(euler.y)
        }
    
    @staticmethod
    def get_scale(obj) -> Dict[str, float]:
        scale = obj.scale
        return {"x": scale.x, "y": scale.z, "z": scale.y}
    
    @staticmethod
    def get_matrix(obj) -> List[List[float]]:
        tm = obj.transform
        matrix = []
        for row in range(4):
            matrix_row = []
            for col in range(4):
                val = tm[row][col]
                matrix_row.append(val)
            matrix.append(matrix_row)
        return matrix

class MaxMesh:
    def __init__(self, obj):
        self.name = obj.name
        self.transform = {
            "position": MaxTransform.get_position(obj),
            "rotation": MaxTransform.get_rotation(obj),
            "scale": MaxTransform.get_scale(obj),
            "matrix": MaxTransform.get_matrix(obj)
        }
        self.vertices = []
        self.normals = []
        self.uvs = []
        self.indices = []
        self.materials = []
        self.bounds = {"min": [0, 0, 0], "max": [1, 1, 1]}
        self.triangle_count = 0
        self.vertex_count = 0
        
        self._extract_mesh_data(obj)
    
    def _extract_mesh_data(self, obj):
        try:
            mesh = obj.mesh
            num_verts = mesh.numVerts
            num_faces = mesh.numFaces
            
            self.vertex_count = num_verts
            self.triangle_count = num_faces
            
            for i in range(1, num_verts + 1):
                v = mesh.verts[i]
                self.vertices.append([v.pos.x, v.pos.z, v.pos.y])
            
            for i in range(1, num_faces + 1):
                face = mesh.faces[i]
                self.indices.append(face.x - 1)
                self.indices.append(face.y - 1)
                self.indices.append(face.z - 1)
            
            for i in range(1, num_verts + 1):
                normal = mesh.getNormal(i)
                self.normals.append([normal.x, normal.z, normal.y])
            
            num_tverts = mesh.numTVerts
            if num_tverts > 0:
                for i in range(1, num_tverts + 1):
                    tv = mesh.tVerts[i]
                    self.uvs.append([tv.x, tv.y])
                self.has_uvs = True
            else:
                self.has_uvs = False
            
            self._get_materials(obj)
            self._get_bounds(obj)
            
        except Exception as e:
            print(f"[Shanyu] Mesh extraction error: {e}")
    
    def _get_materials(self, obj):
        try:
            mat = obj.material
            if mat is None:
                self.materials.append({
                    "name": "Default",
                    "diffuseColor": [0.8, 0.8, 0.8],
                    "metallic": 0.0,
                    "roughness": 0.5
                })
                return
            
            if rt.classOf(mat) == rt.MultiMaterial:
                for i, submat in enumerate(mat.materialList):
                    mat_data = self._parse_material(submat)
                    mat_data["name"] = submat.name if hasattr(submat, 'name') else f"Material_{i}"
                    self.materials.append(mat_data)
            else:
                mat_data = self._parse_material(mat)
                mat_data["name"] = mat.name if hasattr(mat, 'name') else "Material"
                self.materials.append(mat_data)
                
        except Exception as e:
            print(f"[Shanyu] Material extraction error: {e}")
            self.materials.append({
                "name": "Default",
                "diffuseColor": [0.8, 0.8, 0.8],
                "metallic": 0.0,
                "roughness": 0.5
            })
    
    def _parse_material(self, mat) -> Dict:
        mat_data = {
            "diffuseColor": [0.8, 0.8, 0.8],
            "metallic": 0.0,
            "roughness": 0.5,
            "emission": [0, 0, 0],
            "opacity": 1.0
        }
        
        try:
            mat_class = rt.classOf(mat)
            
            if mat_class == rt.PhysicalMaterial:
                color = mat.base_color
                mat_data["diffuseColor"] = [color.r, color.g, color.b]
                mat_data["metallic"] = mat.metalness
                mat_data["roughness"] = mat.roughness
                
                if mat.emission > 0:
                    emit_color = mat.emission_color
                    mat_data["emission"] = [emit_color.r * mat.emission, emit_color.g * mat.emission, emit_color.b * mat.emission]
                
            elif mat_class == rt.StandardMaterial:
                color = mat.diffuse
                mat_data["diffuseColor"] = [color.r, color.g, color.b]
                mat_data["roughness"] = 1.0 - (mat.glossiness if hasattr(mat, 'glossiness') else 0.5)
                
            elif mat_class == rt.Arch___Design__mi_:
                color = mat.diffuse_color
                mat_data["diffuseColor"] = [color.r, color.g, color.b]
                
        except Exception as e:
            print(f"[Shanyu] Material parse error: {e}")
        
        return mat_data
    
    def _get_bounds(self, obj):
        try:
            bbox = obj.max - obj.min
            min_pt = obj.min
            max_pt = obj.max
            
            self.bounds = {
                "min": [min_pt.x, min_pt.z, min_pt.y],
                "max": [max_pt.x, max_pt.z, max_pt.y]
            }
        except:
            pass
    
    def to_dict(self) -> Dict:
        return {
            "name": self.name,
            "transform": self.transform,
            "vertices": self.vertices,
            "normals": self.normals,
            "uvs": self.uvs,
            "indices": self.indices,
            "materials": self.materials,
            "bounds": self.bounds,
            "triangleCount": self.triangle_count,
            "vertexCount": self.vertex_count
        }

class MaxLight:
    def __init__(self, obj):
        self.name = obj.name
        self.transform = {
            "position": MaxTransform.get_position(obj),
            "rotation": MaxTransform.get_rotation(obj),
            "matrix": MaxTransform.get_matrix(obj)
        }
        self._extract_light_data(obj)
    
    def _extract_light_data(self, obj):
        try:
            light_class = rt.classOf(obj)
            
            self.color = [1, 1, 1]
            self.intensity = 1.0
            self.use_shadow = True
            
            if light_class == rt.TargetDirectionalLight or light_class == rt.DirectionalLight:
                self.type = "SUN"
            elif light_class == rt.TargetSpot or light_class == rt.FreeSpot:
                self.type = "SPOT"
                if hasattr(obj, 'hotspot'):
                    self.spot_angle = obj.hotspot
            elif light_class == rt.OmniLight:
                self.type = "POINT"
            elif light_class == rt.Skylight:
                self.type = "SKY"
            else:
                self.type = "POINT"
            
            if hasattr(obj, 'rgb'):
                self.color = [obj.rgb.r, obj.rgb.g, obj.rgb.b]
            
            if hasattr(obj, 'multiplier'):
                self.intensity = obj.multiplier
            
            if hasattr(obj, 'shadow'):
                self.use_shadow = obj.shadow
                
        except Exception as e:
            print(f"[Shanyu] Light extraction error: {e}")
            self.type = "POINT"
    
    def to_dict(self) -> Dict:
        d = {"name": self.name, "type": self.type, **self.transform}
        d["color"] = self.color
        d["intensity"] = self.intensity
        d["useShadow"] = self.use_shadow
        
        if hasattr(self, "spot_angle"):
            d["spotAngle"] = self.spot_angle
            
        return d

class MaxCamera:
    def __init__(self, obj):
        self.name = obj.name
        self.transform = {
            "position": MaxTransform.get_position(obj),
            "rotation": MaxTransform.get_rotation(obj),
            "matrix": MaxTransform.get_matrix(obj)
        }
        self._extract_camera_data(obj)
    
    def _extract_camera_data(self, obj):
        try:
            self.fov = obj.fov if hasattr(obj, 'fov') else 45.0
            self.near_clip = obj.nearclip if hasattr(obj, 'nearclip') else 0.1
            self.far_clip = obj.farclip if hasattr(obj, 'farclip') else 1000.0
            self.aspect_ratio = rt.renderWidth() / rt.renderHeight() if rt.renderHeight() > 0 else 1.777
            
            self.dof_enabled = False
            if hasattr(obj, 'dof'):
                self.dof_enabled = obj.dof
                if self.dof_enabled and hasattr(obj, 'fstop'):
                    self.f_stop = obj.fstop
                    
        except Exception as e:
            print(f"[Shanyu] Camera extraction error: {e}")
            self.fov = 45.0
            self.near_clip = 0.1
            self.far_clip = 1000.0
    
    def to_dict(self) -> Dict:
        d = {"name": self.name, **self.transform}
        d["fov"] = self.fov
        d["aspect"] = self.aspect_ratio
        d["nearClip"] = self.near_clip
        d["farClip"] = self.far_clip
        
        if hasattr(self, "dof_enabled"):
            d["dofEnabled"] = self.dof_enabled
            if self.dof_enabled and hasattr(self, "f_stop"):
                d["fstop"] = self.f_stop
                
        return d

class MaxSceneData:
    def __init__(self):
        self.name = rt.maxFileName or "Untitled"
        self.meshes = []
        self.lights = []
        self.cameras = []
        self.materials = []
        
        self._collect_scene()
    
    def _collect_scene(self):
        geometry = rt.geometry
        for obj in geometry:
            if rt.classOf(obj) in [rt.Editable_Mesh, rt.Editable_Poly, rt.PolyMeshObject]:
                self.meshes.append(MaxMesh(obj))
        
        lights = rt.lights
        for obj in lights:
            self.lights.append(MaxLight(obj))
        
        cameras = rt.cameras
        for obj in cameras:
            if obj.name not in ["Top", "Front", "Left", "Perspective", "Bottom", "Back", "Right"]:
                self.cameras.append(MaxCamera(obj))
    
    def to_dict(self) -> Dict:
        return {
            "version": SHANYU_VERSION,
            "source": "3ds Max 2025",
            "name": self.name,
            "meshes": [m.to_dict() for m in self.meshes],
            "materials": self.materials,
            "lights": [l.to_dict() for l in self.lights],
            "cameras": [c.to_dict() for c in self.cameras],
            "stats": {
                "meshCount": len(self.meshes),
                "lightCount": len(self.lights),
                "cameraCount": len(self.cameras),
                "totalTriangles": sum(m.triangle_count for m in self.meshes),
                "totalVertices": sum(m.vertex_count for m in self.meshes)
            }
        }
    
    def to_json(self) -> str:
        return json.dumps(self.to_dict(), indent=2)

class WebSocketBridge:
    def __init__(self, host=DEFAULT_HOST, port=DEFAULT_PORT):
        self.host = host
        self.port = port
        self.socket = None
        self.connected = False
        self.receive_thread = None
    
    def connect(self) -> bool:
        try:
            self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.socket.settimeout(5.0)
            self.socket.connect((self.host, self.port))
            self.connected = True
            print(f"[Shanyu] Connected to {self.host}:{self.port}")
            
            self.receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self.receive_thread.start()
            return True
            
        except Exception as e:
            print(f"[Shanyu] Connection failed: {e}")
            self.connected = False
            return False
    
    def disconnect(self):
        self.connected = False
        if self.socket:
            try:
                self.socket.close()
            except:
                pass
            self.socket = None
    
    def send(self, data: Dict) -> bool:
        if not self.connected or not self.socket:
            return False
        
        try:
            message = json.dumps(data).encode('utf-8')
            length = len(message)
            header = struct.pack('>I', length)
            self.socket.sendall(header + message)
            return True
        except Exception as e:
            print(f"[Shanyu] Send failed: {e}")
            self.connected = False
            return False
    
    def send_scene(self):
        scene = MaxSceneData()
        data = {"type": "SCENE_UPDATE", "payload": scene.to_dict()}
        return self.send(data)
    
    def send_selection(self):
        selection = rt.selection
        names = [obj.name for obj in selection]
        data = {"type": "SELECTION_CHANGED", "payload": {"objects": names}}
        return self.send(data)
    
    def _receive_loop(self):
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
                        except json.JSONDecodeError:
                            pass
                            
            except socket.timeout:
                continue
            except Exception:
                break
        
        self.connected = False
    
    def _handle_message(self, data: Dict):
        msg_type = data.get("type", "")
        payload = data.get("payload", {})
        
        if msg_type == "PING":
            self.send({"type": "PONG"})
        elif msg_type == "REQUEST_SCENE":
            self.send_scene()
        elif msg_type == "SELECT_OBJECT":
            self._select_in_max(payload.get("name"))
    
    def _select_in_max(self, obj_name: str):
        try:
            rt.select(rt.getNodeByName(obj_name))
        except:
            pass

bridge = None

def get_bridge() -> WebSocketBridge:
    global bridge
    if bridge is None:
        bridge = WebSocketBridge()
    return bridge

def connect_to_shanyu():
    b = get_bridge()
    if b.connect():
        print("[Shanyu] Connected successfully")
        b.send_scene()
    else:
        print("[Shanyu] Connection failed")

def disconnect_from_shanyu():
    get_bridge().disconnect()
    print("[Shanyu] Disconnected")

def sync_scene():
    b = get_bridge()
    if b.connected:
        b.send_scene()
        print("[Shanyu] Scene synced")
    else:
        print("[Shanyu] Not connected")

def sync_selection():
    b = get_bridge()
    if b.connected:
        b.send_selection()
    else:
        print("[Shanyu] Not connected")

def export_fbx(filepath: str = None):
    if filepath is None:
        filepath = rt.getSaveFileName(
            caption="Export FBX",
            filename="scene.fbx",
            types="FBX Files (*.fbx)"
        )
    
    if filepath:
        if not filepath.endswith(".fbx"):
            filepath += ".fbx"
        
        rt.FBXExporterSetParam("Animation", True)
        rt.FBXExporterSetParam("Cameras", True)
        rt.FBXExporterSetParam("Lights", True)
        rt.FBXExporterSetParam("EmbedTextures", True)
        rt.exportFile(filepath, rt.name("noPrompt"), rt.name("selectedOnly") if rt.selection else rt.name("noDefault"))
        print(f"[Shanyu] Exported FBX to {filepath}")

def export_obj(filepath: str = None):
    if filepath is None:
        filepath = rt.getSaveFileName(
            caption="Export OBJ",
            filename="scene.obj",
            types="OBJ Files (*.obj)"
        )
    
    if filepath:
        if not filepath.endswith(".obj"):
            filepath += ".obj"
        
        rt.exportFile(filepath, rt.name("noPrompt"), using=rt.ObjExp)
        print(f"[Shanyu] Exported OBJ to {filepath}")

def create_physical_material():
    mat = rt.PhysicalMaterial()
    mat.name = "Shanyu_PBR_" + str(int(rt.timestamp()))
    mat.base_color = rt.color(0.8, 0.8, 0.8)
    mat.metalness = 0.0
    mat.roughness = 0.5
    
    if rt.selection:
        for obj in rt.selection:
            obj.material = mat
    
    print(f"[Shanyu] Created Physical Material: {mat.name}")
    return mat

def create_toolbar():
    try:
        menu_name = "Shanyu Engine"
        
        main_menu = rt.menuMan.findMenu("Main Menu Bar")
        if main_menu is not None:
            shanyu_menu = rt.menuMan.findMenu(menu_name)
            if shanyu_menu is not None:
                rt.menuMan.unRegisterMenu(shanyu_menu)
            
            new_menu = rt.menuMan.createMenu(menu_name)
            
            new_menu.addItem(rt.menuMan.createActionItem("connect_to_shanyu", "Connect to Shanyu Engine"))
            new_menu.addItem(rt.menuMan.createActionItem("disconnect_from_shanyu", "Disconnect"))
            new_menu.addItem(rt.menuMan.createSeparatorItem())
            new_menu.addItem(rt.menuMan.createActionItem("sync_scene", "Sync Scene"))
            new_menu.addItem(rt.menuMan.createActionItem("sync_selection", "Sync Selection"))
            new_menu.addItem(rt.menuMan.createSeparatorItem())
            new_menu.addItem(rt.menuMan.createActionItem("export_fbx", "Export FBX"))
            new_menu.addItem(rt.menuMan.createActionItem("export_obj", "Export OBJ"))
            new_menu.addItem(rt.menuMan.createSeparatorItem())
            new_menu.addItem(rt.menuMan.createActionItem("create_physical_material", "Create PBR Material"))
            
            main_menu.addItem(rt.menuMan.createSubMenuItem(menu_name, new_menu), -1)
            
            print("[Shanyu] Menu installed successfully")
    except Exception as e:
        print(f"[Shanyu] Menu creation error: {e}")

def initialize():
    try:
        create_toolbar()
        print(f"[Shanyu Engine] 3ds Max Bridge v{SHANYU_VERSION} loaded")
        print("[Shanyu Engine] Find the Shanyu Engine menu in the main menu bar")
    except Exception as e:
        print(f"[Shanyu] Initialization error: {e}")

if __name__ == "__main__":
    initialize()
