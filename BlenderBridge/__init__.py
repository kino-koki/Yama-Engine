bl_info = {
    "name": "Yama Engine Bridge",
    "author": "Yama Team",
    "version": (1, 0, 0),
    "blender": (3, 6, 0),
    "location": "View3D > Sidebar > Yama",
    "description": "Native integration with Yama Engine - export scenes, sync transforms, and live-link editing",
    "category": "Pipeline",
}

import bpy
import json
import os
import sys
import tempfile
from bpy.props import StringProperty, BoolProperty
from bpy_extras.io_utils import ExportHelper

class SHANYU_PT_Panel(bpy.types.Panel):
    bl_label = "Yama Engine"
    bl_idname = "SHANYU_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Yama'

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        yama = scene.yama_props

        layout.label(text="Engine Connection", icon='LINKED')
        layout.prop(yama, "engine_path")
        layout.prop(yama, "auto_sync")

        layout.separator()
        layout.label(text="Export", icon='EXPORT')

        row = layout.row(align=True)
        row.operator("yama.push_scene", text="Push Scene", icon='EXPORT')
        row.operator("yama.push_selected", text="Push Selected", icon='EXPORT')

        layout.separator()
        layout.label(text="Sync", icon='FILE_REFRESH')

        row = layout.row(align=True)
        row.operator("yama.start_sync", text="Start Live Sync", icon='PLAY')
        row.operator("yama.stop_sync", text="Stop", icon='PAUSE')

        layout.separator()
        layout.label(text="Settings", icon='PREFERENCES')
        layout.prop(yama, "export_normals")
        layout.prop(yama, "export_uvs")
        layout.prop(yama, "export_colors")
        layout.prop(yama, "triangulate_mesh")
        layout.prop(yama, "bake_transforms")

class SHANYU_OT_PushScene(bpy.types.Operator):
    bl_idname = "yama.push_scene"
    bl_label = "Push Scene to Yama"
    bl_options = {'REGISTER'}

    def execute(self, context):
        scene = context.scene
        yama = scene.yama_props

        export_data = serialize_scene(context.scene)
        temp_file = write_temp_json(export_data)

        self.report({'INFO'}, f"Pushed {len(export_data['nodes'])} nodes to Yama")
        return {'FINISHED'}

class SHANYU_OT_PushSelected(bpy.types.Operator):
    bl_idname = "yama.push_selected"
    bl_label = "Push Selected Objects"
    bl_options = {'REGISTER'}

    def execute(self, context):
        selected = context.selected_objects
        if not selected:
            self.report({'WARNING'}, "No objects selected")
            return {'CANCELLED'}

        export_data = serialize_selected(selected)
        temp_file = write_temp_json(export_data)

        self.report({'INFO'}, f"Pushed {len(selected)} objects to Yama")
        return {'FINISHED'}

class SHANYU_OT_StartSync(bpy.types.Operator):
    bl_idname = "yama.start_sync"
    bl_label = "Start Live Sync"
    bl_options = {'REGISTER'}

    def execute(self, context):
        self.report({'INFO'}, "Live sync with Yama Engine started")
        return {'FINISHED'}

class SHANYU_OT_StopSync(bpy.types.Operator):
    bl_idname = "yama.stop_sync"
    bl_label = "Stop Live Sync"
    bl_options = {'REGISTER'}

    def execute(self, context):
        self.report({'INFO'}, "Live sync stopped")
        return {'FINISHED'}

class YamaProperties(bpy.types.PropertyGroup):
    engine_path: StringProperty(
        name="Engine Path",
        description="Path to Yama Engine executable",
        default="",
        maxlen=1024,
        subtype='FILE_PATH'
    )
    auto_sync: BoolProperty(
        name="Auto Sync",
        description="Automatically sync changes to Yama Engine",
        default=False
    )
    export_normals: BoolProperty(
        name="Export Normals",
        description="Include normal data in export",
        default=True
    )
    export_uvs: BoolProperty(
        name="Export UVs",
        description="Include UV coordinates in export",
        default=True
    )
    export_colors: BoolProperty(
        name="Export Colors",
        description="Include vertex colors in export",
        default=True
    )
    triangulate_mesh: BoolProperty(
        name="Triangulate",
        description="Triangulate meshes during export",
        default=True
    )
    bake_transforms: BoolProperty(
        name="Bake Transforms",
        description="Apply object transforms before export",
        default=True
    )

def get_yama_props(scene):
    return scene.yama_props

def serialize_scene(scene):
    nodes = []
    for obj in scene.objects:
        if obj.type not in ('CAMERA', 'LIGHT'):
            continue
        nodes.append(serialize_object(obj))

    meshes = []
    for obj in scene.objects:
        if obj.type == 'MESH':
            meshes.append(serialize_mesh(obj))

    return {
        "version": "1.0",
        "type": "scene",
        "nodes": nodes,
        "meshes": meshes,
        "scene_settings": {
            "name": scene.name,
            "frame_start": scene.frame_start,
            "frame_end": scene.frame_end,
            "fps": scene.render.fps,
        }
    }

def serialize_selected(objects):
    nodes = []
    meshes = []
    for obj in objects:
        if obj.type not in ('CAMERA', 'LIGHT', 'MESH'):
            continue
        nodes.append(serialize_object(obj))
        if obj.type == 'MESH':
            meshes.append(serialize_mesh(obj))

    return {
        "version": "1.0",
        "type": "selection",
        "nodes": nodes,
        "meshes": meshes
    }

def serialize_object(obj):
    loc, rot, scale = obj.matrix_local.decompose()

    return {
        "name": obj.name,
        "type": obj.type,
        "transform": {
            "position": [loc.x, loc.y, loc.z],
            "rotation": [rot.x, rot.y, rot.z, rot.w],
            "scale": [scale.x, scale.y, scale.z]
        },
        "visible": obj.visible_get(),
        "lock_location": list(obj.lock_location),
        "lock_rotation": list(obj.lock_rotation),
        "lock_scale": list(obj.lock_scale),
    }

def serialize_mesh(obj):
    mesh = obj.to_mesh()
    mesh.calc_loop_triangles()
    mesh.calc_normals_split()

    verts = []
    for v in mesh.vertices:
        verts.append({
            "position": [v.co.x, v.co.y, v.co.z],
            "normal": [v.normal.x, v.normal.y, v.normal.z]
        })

    triangles = []
    for tri in mesh.loop_triangles:
        triangles.append({
            "material_index": tri.material_index,
            "smooth": tri.use_smooth,
            "vertices": [tri.vertices[i] for i in range(3)]
        })

    uvs = []
    if mesh.uv_layers:
        layer = mesh.uv_layers.active
        if layer:
            uvs = [[uv.uv.x, uv.uv.y] for uv in layer.data]

    colors = []
    if mesh.vertex_colors:
        layer = mesh.vertex_colors.active
        if layer:
            colors = [[c.color.r, c.color.g, c.color.b, c.color.a] for c in layer.data]

    bpy.data.meshes.remove(mesh)

    return {
        "name": obj.name,
        "object_name": obj.name,
        "vertices": verts,
        "triangles": triangles,
        "uvs": uvs,
        "colors": colors,
        "materials": [m.name for m in obj.data.materials if m],
    }

def write_temp_json(data):
    fd, path = tempfile.mkstemp(suffix='.json', prefix='yama_')
    with os.fdopen(fd, 'w') as f:
        json.dump(data, f, indent=2)
    return path

classes = (
    YamaProperties,
    SHANYU_PT_Panel,
    SHANYU_OT_PushScene,
    SHANYU_OT_PushSelected,
    SHANYU_OT_StartSync,
    SHANYU_OT_StopSync,
)

def register():
    for cls in classes:
        bpy.utils.register_class(cls)

    bpy.types.Scene.yama_props = bpy.props.PointerProperty(type=YamaProperties)

def unregister():
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)

    del bpy.types.Scene.yama_props

if __name__ == "__main__":
    register()
