# Yama Engine 3ds Max Bridge

Autodesk 3ds Max 2024/2025 integration plugin for Yama Engine.

## Features

- **Live Link**: Real-time scene synchronization with Yama Editor
- **Scene Export**: FBX, OBJ export
- **PBR Materials**: Physical Material support
- **Camera Sync**: Camera data transfer
- **Light Sync**: All light types

## Installation

### Method 1: Scripts Folder

1. Copy the `MaxBridge` folder to:
   `C:\Program Files\Autodesk\3ds Max 2025\scripts\YamaBridge\`

2. In 3ds Max, go to **Scripting > MaxScript Editor**

3. Execute:
   ```maxscript
   python.ExecuteFile "C:/Program Files/Autodesk/3ds Max 2025/scripts/YamaBridge/yama_max_plugin.py"
   ```

### Method 2: Auto-Load on Startup

1. Create a file at:
   `C:\Program Files\Autodesk\3ds Max 2025\scripts\startup\yama_autoload.ms`

2. Add this content:
   ```maxscript
   python.ExecuteFile "C:/Program Files/Autodesk/3ds Max 2025/scripts/YamaBridge/yama_max_plugin.py"
   ```

## Usage

### Menu Items

After installation, find **Yama Engine** in the main menu bar:

| Menu Item | Function |
|-----------|----------|
| Connect to Yama Engine | Connect to Yama Editor |
| Disconnect | Disconnect from Editor |
| Sync Scene | Send full scene to Editor |
| Sync Selection | Send selected objects |
| Export FBX | Export as FBX format |
| Export OBJ | Export as OBJ format |
| Create PBR Material | Create Physical Material |

### Live Link

1. Start Yama Editor with LiveLink enabled (port 18888)
2. In 3ds Max, click **Connect to Yama Engine**
3. The scene will automatically sync

### Python Commands

You can also use Python commands:

```python
import yama_max_plugin as yama

yama.connect_to_yama()
yama.sync_scene()
yama.disconnect_from_yama()

yama.export_fbx("C:/output/scene.fbx")
yama.export_obj("C:/output/scene.obj")

mat = yama.create_physical_material()
```

## Supported Features

### Geometry
- Editable Poly
- Editable Mesh
- UVW Maps
- Multiple materials per mesh

### Materials
- Physical Material (PBR)
- Standard Material
- Arch & Design Material

### Lights
- Target Directional Light
- Target Spot
- Free Spot
- Omni Light
- Skylight

### Cameras
- Target Camera
- Free Camera
- FOV
- Near/Far clip planes

## Configuration

Default settings:
- Host: `localhost`
- Port: `18888`

To change, modify in the script:
```python
DEFAULT_HOST = "192.168.1.100"
DEFAULT_PORT = 18889
```

## Troubleshooting

### "Connection failed"
- Ensure Yama Editor is running
- Check that LiveLink is enabled (port 18888)
- Verify firewall allows the connection

### Menu doesn't appear
- Check if Python is enabled in 3ds Max
- Run the script manually and check for errors in the MAXScript Listener

### Export issues
- For FBX, ensure the FBX plugin is loaded
- Check file permissions for the output directory

## Requirements

- Autodesk 3ds Max 2024 or 2025
- Yama Engine Editor
- Python (included with 3ds Max)
