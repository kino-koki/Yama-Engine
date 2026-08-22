"""
Shanyu Engine Maya 2024/2025 Bridge Plugin
============================================
Installation:
1. Copy this folder to: C:/Users/<user>/Documents/maya/scripts/shanyu_bridge/
2. Or: C:/Program Files/Autodesk/Maya2025/scripts/shanyu_bridge/
3. In Maya Script Editor, run:
   import shanyu_maya_plugin
   shanyu_maya_plugin.install_shelf()
4. The Shanyu shelf will appear in Maya

Features:
- Live Link real-time sync with Shanyu Editor
- Scene export (FBX, OBJ, USD)
- PBR Material transfer
- Animation export
- Camera sync
"""

import maya.cmds as cmds
import maya.mel as mel
import maya.OpenMaya as om
import maya.OpenMayaUI as omui
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

class MayaTransform:
    @staticmethod
    def get_world_matrix(dag_path) -> List[List[float]]:
        m = om.MMatrix()
        dag_path.inclusiveMatrix().get(m)
        return [[m(i, j) for j in range(4)] for i in range(4)]
    
    @staticmethod
    def get_position(dag_path) -> Dict[str, float]:
        fn = om.MFnTransform(dag_path)
        translation = fn.translation(om.MSpace.kWorld)
        return {"x": translation.x, "y": translation.y, "z": translation.z}
    
    @staticmethod
    def get_rotation(dag_path) -> Dict[str, float]:
        fn = om.MFnTransform(dag_path)
        rotation = fn.rotation()
        return {"x": math.degrees(rotation.x), "y": math.degrees(rotation.y), "z": math.degrees(rotation.z)}
    
    @staticmethod
    def get_scale(dag_path) -> Dict[str, float]:
        fn = om.MFnTransform(dag_path)
        scale = om.MVector()
        fn.getScale(scale)
        return {"x": scale.x, "y": scale.y, "z": scale.z}

class MayaMesh:
    def __init__(self, dag_path):
        self.name = dag_path.partialPathName()
        self.transform = self._get_transform_data(dag_path)
        self.vertices = []
        self.normals = []
        self.uvs = []
        self.indices = []
        self.materials = []
        self.bounds = {"min": [0, 0, 0], "max": [1, 1, 1]}
        self.triangle_count = 0
        self.vertex_count = 0
        
        self._extract_mesh_data(dag_path)
    
    def _get_transform_data(self, dag_path) -> Dict:
        return {
            "position": MayaTransform.get_position(dag_path),
            "rotation": MayaTransform.get_rotation(dag_path),
            "scale": MayaTransform.get_scale(dag_path),
            "matrix": MayaTransform.get_world_matrix(dag_path)
        }
    
    def _extract_mesh_data(self, dag_path):
        try:
            mesh_fn = om.MFnMesh(dag_path)
            
            self.vertex_count = mesh_fn.numVertices
            self.triangle_count = mesh_fn.numPolygons
            
            vertices = om.MPointArray()
            mesh_fn.getPoints(vertices, om.MSpace.kWorld)
            self.vertices = [[vertices[i].x, vertices[i].y, vertices[i].z] for i in range(vertices.length())]
            
            normals = om.MVectorArray()
            mesh_fn.getNormals(normals)
            self.normals = [[normals[i].x, normals[i].y, normals[i].z] for i in range(normals.length())]
            
            uv_coords = om.MFloatArray()
            mesh_fn.getUVs(uv_coords, om.MFloatArray())
            self.uvs = [[uv_coords[i*2], uv_coords[i*2+1]] for i in range(uv_coords.length()//2)]
            
            mesh_fn.getTriangleOffsets(om.MIntArray(), om.MIntArray())
            num_triangles = mesh_fn.numPolygons
            triangle_counts = om.MIntArray()
            triangles = om.MIntArray()
            mesh_fn.getTriangles(triangle_counts, triangles)
            
            for i in range(triangles.length()):
                self.indices.append(triangles[i])
            
            self._get_materials(dag_path)
            self._get_bounds(dag_path)
            
        except Exception as e:
            print(f"[Shanyu] Error extracting mesh data: {e}")
    
    def _get_materials(self, dag_path):
        try:
            shading_engines = om.MObjectArray()
            indices = om.MIntArray()
            
            mesh_fn = om.MFnMesh(dag_path)
            mesh_fn.getConnectedShaders(0, shading_engines, indices)
            
            for i in range(shading_engines.length()):
                se = om.MFnDependencyNode(shading_engines[i])
                se_name = se.name()
                
                plugs = se.findPlug("surfaceShader", False)
                conn = om.MPlugArray()
                plugs.connectedTo(conn, True, False)
                
                if conn.length() > 0:
                    shader = om.MFnDependencyNode(conn[0].node())
                    mat_data = self._parse_shader(shader)
                    mat_data["name"] = se_name.replace("SG", "Mat")
                    self.materials.append(mat_data)
                    
        except Exception as e:
            print(f"[Shanyu] Material extraction error: {e}")
            self.materials.append({
                "name": "Default",
                "diffuseColor": [0.8, 0.8, 0.8],
                "metallic": 0.0,
                "roughness": 0.5
            })
    
    def _parse_shader(self, shader) -> Dict:
        mat = {
            "diffuseColor": [0.8, 0.8, 0.8],
            "metallic": 0.0,
            "roughness": 0.5,
            "emission": [0, 0, 0],
            "opacity": 1.0
        }
        
        shader_type = shader.typeName()
        
        try:
            if shader_type == "lambert":
                color_plug = shader.findPlug("color", False)
                mat["diffuseColor"] = self._get_color(color_plug)
                
            elif shader_type == "phong":
                color_plug = shader.findPlug("color", False)
                mat["diffuseColor"] = self._get_color(color_plug)
                spec_plug = shader.findPlug("specularColor", False)
                mat["specularColor"] = self._get_color(spec_plug)
                
            elif shader_type == "blinn":
                color_plug = shader.findPlug("color", False)
                mat["diffuseColor"] = self._get_color(color_plug)
                
            elif shader_type == "standardSurface":
                base_color_plug = shader.findPlug("baseColor", False)
                mat["diffuseColor"] = self._get_color(base_color_plug)
                
                metal_plug = shader.findPlug("metalness", False)
                mat["metallic"] = metal_plug.asFloat()
                
                spec_rough_plug = shader.findPlug("specularRoughness", False)
                mat["roughness"] = spec_rough_plug.asFloat()
                
                emit_color_plug = shader.findPlug("emissionColor", False)
                mat["emission"] = self._get_color(emit_color_plug)
                
        except Exception as e:
            print(f"[Shanyu] Shader parse error: {e}")
        
        return mat
    
    def _get_color(self, plug) -> List[float]:
        try:
            return [plug.child(i).asFloat() for i in range(3)]
        except:
            return [0.8, 0.8, 0.8]
    
    def _get_bounds(self, dag_path):
        try:
            bbox = om.MBoundingBox()
            mesh_fn = om.MFnMesh(dag_path)
            bbox = mesh_fn.boundingBox()
            
            self.bounds = {
                "min": [bbox.min().x, bbox.min().y, bbox.min().z],
                "max": [bbox.max().x, bbox.max().y, bbox.max().z]
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

class MayaLight:
    def __init__(self, dag_path):
        self.name = dag_path.partialPathName()
        self.transform = {
            "position": MayaTransform.get_position(dag_path),
            "rotation": MayaTransform.get_rotation(dag_path),
            "matrix": MayaTransform.get_world_matrix(dag_path)
        }
        self._extract_light_data(dag_path)
    
    def _extract_light_data(self, dag_path):
        try:
            light_fn = om.MFnLight(dag_path)
            
            self.color = [light_fn.color().r, light_fn.color().g, light_fn.color().b]
            self.intensity = light_fn.intensity()
            
            node = om.MFnDependencyNode(dag_path.node())
            self.type = node.typeName()
            
            if self.type == "directionalLight":
                self.type = "SUN"
            elif self.type == "pointLight":
                self.type = "POINT"
                decay_plug = node.findPlug("decayRate", False)
                self.decay_rate = decay_plug.asInt()
            elif self.type == "spotLight":
                self.type = "SPOT"
                cone_plug = node.findPlug("coneAngle", False)
                self.spot_angle = math.degrees(cone_plug.asFloat())
                pen_plug = node.findPlug("penumbraAngle", False)
                self.penumbra = math.degrees(pen_plug.asFloat())
            elif self.type == "areaLight":
                self.type = "AREA"
            
            shadow_plug = node.findPlug("useDepthMapShadows", False)
            self.use_shadow = shadow_plug.asBool()
            
        except Exception as e:
            print(f"[Shanyu] Light extraction error: {e}")
            self.type = "POINT"
            self.color = [1, 1, 1]
            self.intensity = 1.0
    
    def to_dict(self) -> Dict:
        d = {"name": self.name, "type": self.type, **self.transform}
        d["color"] = self.color
        d["intensity"] = self.intensity
        d["useShadow"] = getattr(self, "use_shadow", True)
        
        if hasattr(self, "spot_angle"):
            d["spotAngle"] = self.spot_angle
        if hasattr(self, "decay_rate"):
            d["decayRate"] = self.decay_rate
            
        return d

class MayaCamera:
    def __init__(self, dag_path):
        self.name = dag_path.partialPathName()
        self.transform = {
            "position": MayaTransform.get_position(dag_path),
            "rotation": MayaTransform.get_rotation(dag_path),
            "matrix": MayaTransform.get_world_matrix(dag_path)
        }
        self._extract_camera_data(dag_path)
    
    def _extract_camera_data(self, dag_path):
        try:
            cam_fn = om.MFnCamera(dag_path)
            
            self.fov = math.degrees(cam_fn.horizontalFieldOfView())
            self.aspect_ratio = cam_fn.aspectRatio()
            self.near_clip = cam_fn.nearClippingPlane()
            self.far_clip = cam_fn.farClippingPlane()
            self.focal_length = cam_fn.focalLength()
            
            self.dof_enabled = cam_fn.isDepthOfField()
            if self.dof_enabled:
                self.f_stop = cam_fn.fStop()
                self.focus_distance = cam_fn.focusDistance()
                
        except Exception as e:
            print(f"[Shanyu] Camera extraction error: {e}")
            self.fov = 60.0
            self.aspect_ratio = 1.777
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
            if self.dof_enabled:
                d["fstop"] = self.f_stop
                d["focusDistance"] = self.focus_distance
                
        return d

class MayaSceneData:
    def __init__(self):
        self.name = cmds.file(query=True, sceneName=True) or "Untitled"
        self.meshes = []
        self.lights = []
        self.cameras = []
        self.materials = []
        self.animations = []
        
        self._collect_scene()
    
    def _collect_scene(self):
        sel = om.MSelectionList()
        om.MGlobal.getActiveSelectionList(sel)
        
        dag_it = om.MItDag(om.MItDag.kBreadthFirst)
        
        while not dag_it.isDone():
            dag_path = om.MDagPath()
            dag_it.getPath(dag_path)
            
            if dag_path.isValid():
                node_type = dag_path.node().apiType()
                
                if node_type == om.MFn.kMesh:
                    if not dag_path.fullPathName().endswith("Orig"):
                        self.meshes.append(MayaMesh(dag_path))
                        
                elif node_type == om.MFn.kDirectionalLight or \
                     node_type == om.MFn.kPointLight or \
                     node_type == om.MFn.kSpotLight or \
                     node_type == om.MFn.kAreaLight:
                    self.lights.append(MayaLight(dag_path))
                    
                elif node_type == om.MFn.kCamera:
                    if dag_path.fullPathName().startswith("|persp") or \
                       dag_path.fullPathName().startswith("|top") or \
                       dag_path.fullPathName().startswith("|front") or \
                       dag_path.fullPathName().startswith("|side"):
                        pass
                    else:
                        self.cameras.append(MayaCamera(dag_path))
            
            dag_it.next()
    
    def to_dict(self) -> Dict:
        return {
            "version": SHANYU_VERSION,
            "source": "Maya 2025",
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
        self.callbacks = {}
    
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
        print("[Shanyu] Disconnected")
    
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
        scene = MayaSceneData()
        data = {"type": "SCENE_UPDATE", "payload": scene.to_dict()}
        return self.send(data)
    
    def send_selection(self):
        selection = cmds.ls(selection=True, long=True)
        data = {"type": "SELECTION_CHANGED", "payload": {"objects": selection}}
        return self.send(data)
    
    def send_transform(self, obj_name: str):
        sel = om.MSelectionList()
        sel.add(obj_name)
        dag_path = om.MDagPath()
        sel.getDagPath(0, dag_path)
        
        transform = {
            "position": MayaTransform.get_position(dag_path),
            "rotation": MayaTransform.get_rotation(dag_path),
            "matrix": MayaTransform.get_world_matrix(dag_path)
        }
        data = {"type": "TRANSFORM_UPDATED", "payload": {"name": obj_name, "transform": transform}}
        return self.send(data)
    
    def register_callback(self, msg_type: str, callback):
        self.callbacks[msg_type] = callback
    
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
                        except json.JSONDecodeError as e:
                            print(f"[Shanyu] JSON decode error: {e}")
                            
            except socket.timeout:
                continue
            except Exception as e:
                print(f"[Shanyu] Receive error: {e}")
                break
        
        self.connected = False
    
    def _handle_message(self, data: Dict):
        msg_type = data.get("type", "")
        payload = data.get("payload", {})
        
        if msg_type in self.callbacks:
            self.callbacks[msg_type](payload)
        elif msg_type == "PING":
            self.send({"type": "PONG"})
        elif msg_type == "REQUEST_SCENE":
            self.send_scene()
        elif msg_type == "SELECT_OBJECT":
            self._select_in_maya(payload.get("name"))
    
    def _select_in_maya(self, obj_name: str):
        try:
            cmds.select(clear=True)
            cmds.select(obj_name, replace=True)
        except Exception as e:
            print(f"[Shanyu] Select error: {e}")

bridge = None

def get_bridge() -> WebSocketBridge:
    global bridge
    if bridge is None:
        bridge = WebSocketBridge()
    return bridge

def connect_to_shanyu(*args):
    b = get_bridge()
    if b.connect():
        cmds.confirmDialog(
            title="Shanyu Engine",
            message=f"Connected to Shanyu Engine on port {b.port}",
            button=["OK"],
            defaultButton="OK"
        )
        b.send_scene()
    else:
        cmds.confirmDialog(
            title="Shanyu Engine",
            message="Connection failed.\nMake sure Shanyu Editor is running with LiveLink enabled.",
            button=["OK"],
            defaultButton="OK"
        )

def disconnect_from_shanyu(*args):
    get_bridge().disconnect()

def sync_scene(*args):
    b = get_bridge()
    if b.connected:
        b.send_scene()
        cmds.confirmDialog(
            title="Shanyu Engine",
            message="Scene synced to Shanyu Engine",
            button=["OK"]
        )
    else:
        cmds.confirmDialog(
            title="Shanyu Engine",
            message="Not connected. Please connect first.",
            button=["OK"]
        )

def sync_selection(*args):
    b = get_bridge()
    if b.connected:
        b.send_selection()
    else:
        print("[Shanyu] Not connected")

def export_fbx(*args):
    filepath = cmds.fileDialog2(
        fileFilter="FBX Files (*.fbx)",
        fileMode=0,
        caption="Export FBX for Shanyu Engine"
    )
    
    if filepath:
        filepath = filepath[0]
        if not filepath.endswith(".fbx"):
            filepath += ".fbx"
        
        try:
            mel.eval(f'FBXExport -f "{filepath}" -s')
            print(f"[Shanyu] Exported FBX to {filepath}")
        except Exception as e:
            cmds.confirmDialog(
                title="Export Error",
                message=f"FBX export failed: {e}",
                button=["OK"]
            )

def export_obj(*args):
    filepath = cmds.fileDialog2(
        fileFilter="OBJ Files (*.obj)",
        fileMode=0,
        caption="Export OBJ for Shanyu Engine"
    )
    
    if filepath:
        filepath = filepath[0]
        if not filepath.endswith(".obj"):
            filepath += ".obj"
        
        try:
            cmds.file(filepath, exportAll=True, type="OBJ", force=True)
            print(f"[Shanyu] Exported OBJ to {filepath}")
        except Exception as e:
            cmds.confirmDialog(
                title="Export Error",
                message=f"OBJ export failed: {e}",
                button=["OK"]
            )

def export_usd(*args):
    filepath = cmds.fileDialog2(
        fileFilter="USD Files (*.usd *.usda)",
        fileMode=0,
        caption="Export USD for Shanyu Engine"
    )
    
    if filepath:
        filepath = filepath[0]
        if not filepath.endswith((".usd", ".usda")):
            filepath += ".usda"
        
        try:
            cmds.mayaUSDExport(file=filepath, exportSkels="auto", exportBlendShapes=True)
            print(f"[Shanyu] Exported USD to {filepath}")
        except Exception as e:
            cmds.confirmDialog(
                title="Export Error",
                message=f"USD export failed: {e}\n\nMake sure Maya USD Plugin is loaded.",
                button=["OK"]
            )

def create_shanyu_material(*args):
    mat_name = cmds.shadingNode("standardSurface", asShader=True, name="shanyu_pbr_")
    sg_name = mat_name + "SG"
    cmds.sets(renderable=True, noSurfaceShader=True, empty=True, name=sg_name)
    cmds.connectAttr(mat_name + ".outColor", sg_name + ".surfaceShader")
    
    cmds.setAttr(mat_name + ".base", 1.0)
    cmds.setAttr(mat_name + ".baseColor", 0.8, 0.8, 0.8, type="double3")
    cmds.setAttr(mat_name + ".metalness", 0.0)
    cmds.setAttr(mat_name + ".specularRoughness", 0.5)
    
    selection = cmds.ls(selection=True)
    if selection:
        cmds.sets(selection, edit=True, forceElement=sg_name)
    
    print(f"[Shanyu] Created PBR material: {mat_name}")

def install_shelf():
    shelf_name = "Shanyu"
    
    if cmds.shelfLayout(shelf_name, exists=True):
        cmds.deleteUI(shelf_name)
    
    cmds.shelfLayout(shelf_name, parent="ShelfLayout")
    
    buttons = [
        ("Connect", "Connect to Shanyu Engine", connect_to_shanyu, "connect.png"),
        ("Disconnect", "Disconnect from Shanyu Engine", disconnect_from_shanyu, "disconnect.png"),
        ("---", "", None, ""),
        ("Sync Scene", "Sync scene to Shanyu Engine", sync_scene, "scene.png"),
        ("Sync Selection", "Sync selection to Shanyu Engine", sync_selection, "select.png"),
        ("---", "", None, ""),
        ("Export FBX", "Export as FBX", export_fbx, "fbx.png"),
        ("Export OBJ", "Export as OBJ", export_obj, "obj.png"),
        ("Export USD", "Export as USD", export_usd, "usd.png"),
        ("---", "", None, ""),
        ("PBR Material", "Create PBR material", create_shanyu_material, "material.png"),
    ]
    
    for label, tooltip, command, icon in buttons:
        if label == "---":
            cmds.shelfSeparator(parent=shelf_name)
        else:
            cmds.shelfButton(
                parent=shelf_name,
                label=label,
                annotation=tooltip,
                command=command,
                image=icon if icon else "commandButton.png",
                width=32,
                height=32
            )
    
    print(f"[Shanyu] Installed shelf: {shelf_name}")

def on_script_job():
    b = get_bridge()
    if b.connected:
        selection = cmds.ls(selection=True)
        if selection:
            b.send_selection()

def setup_script_jobs():
    try:
        script_jobs = cmds.scriptJob(listJobs=True)
        for job in script_jobs:
            if "shanyu" in job.lower():
                job_id = int(job.split(":")[0])
                cmds.scriptJob(kill=job_id, force=True)
        
        cmds.scriptJob(event=["SelectionChanged", on_script_job], killWithScene=True)
        print("[Shanyu] Script jobs installed")
        
    except Exception as e:
        print(f"[Shanyu] Script job setup error: {e}")

def initialize():
    try:
        install_shelf()
        setup_script_jobs()
        print(f"[Shanyu Engine] Maya Bridge v{SHANYU_VERSION} loaded")
        print("[Shanyu Engine] Find the Shanyu shelf in Maya")
    except Exception as e:
        print(f"[Shanyu] Initialization error: {e}")

if __name__ == "__main__":
    initialize()
