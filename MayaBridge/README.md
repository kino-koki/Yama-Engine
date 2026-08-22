# Shanyu Engine Maya Bridge

Autodesk Maya 2024/2025 integration plugin for Shanyu Engine.

## Features

- **Live Link**: Real-time scene synchronization with Shanyu Editor
- **Scene Export**: FBX, OBJ, USD export
- **PBR Materials**: Standard Surface material support
- **Camera Sync**: Camera data transfer
- **Light Sync**: All light types (Directional, Point, Spot, Area)

## Installation

### Method 1: User Scripts Folder (Recommended)

1. Copy the `MayaBridge` folder to:
   - Windows: `C:\Users\<username>\Documents\maya\scripts\`
   - macOS: `~/Library/Preferences/Autodesk/maya/scripts/`
   - Linux: `~/maya/scripts/`

2. In Maya, open Script Editor (Python tab) and run:
   ```python
   import shanyu_maya_plugin
   shanyu_maya_plugin.initialize()
   ```

### Method 2: Maya Install Folder

Copy to:
- Windows: `C:\Program Files\Autodesk\Maya2025\scripts\`
- The plugin will auto-load on Maya startup

## Usage

### Live Link

1. Start Shanyu Editor with LiveLink enabled (port 18888)
2. In Maya, click **Connect** in the Shanyu shelf
3. The scene will automatically sync

### Shelf Buttons

| Button | Function |
|--------|----------|
| Connect | Connect to Shanyu Editor |
| Disconnect | Disconnect from Editor |
| Sync Scene | Send full scene to Editor |
| Sync Selection | Send selected objects |
| Export FBX | Export as FBX format |
| Export OBJ | Export as OBJ format |
| Export USD | Export as Pixar USD |
| PBR Material | Create Standard Surface material |

## Supported Features

### Geometry
- Polygon meshes with UVs
- Vertex colors
- Multiple materials per mesh
- Transforms (position, rotation, scale)

### Materials
- Standard Surface (PBR)
- Lambert
- Phong
- Blinn

### Lights
- Directional Light (Sun)
- Point Light
- Spot Light
- Area Light

### Cameras
- FOV
- Near/Far clip planes
- Depth of Field

## Configuration

Default settings:
- Host: `localhost`
- Port: `18888`

To change, modify in the script:
```python
DEFAULT_HOST = "192.168.1.100"  # Remote machine
DEFAULT_PORT = 18889            # Custom port
```

## Troubleshooting

### "Connection failed"
- Ensure Shanyu Editor is running
- Check that LiveLink is enabled (port 18888)
- Verify firewall allows the connection

### "USD export failed"
- Load the Maya USD plugin: Window > Settings/Preferences > Plug-in Manager
- Enable `mayaUsdPlugin`

### Script errors
- Check Maya Output Window for details
- Verify Python version compatibility (Python 3.x for Maya 2024+)

## Requirements

- Autodesk Maya 2024 or 2025
- Shanyu Engine Editor
- Python 3.x (included with Maya)
