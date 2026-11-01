using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace YamaEngine.DCC
{
    /// <summary>
    /// WebSocket 风格的 TCP 实时同步服务器
    /// 在编辑器端运行,接收 Blender 发送的场景更新
    /// </summary>
    public class LiveLinkServer
    {
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private readonly int _port;
        private bool _isRunning;
        private Task _receiveTask;
        private Task _heartbeatTask;
        private readonly List<byte> _receiveBuffer = new();
        
        // UDP 支持 (用于高频动画数据和广播发现)
        private UdpClient _udpClient;
        private Task _udpReceiveTask;
        private readonly int _udpPort;
        
        // 时间同步状态
        private double _clockOffsetMs;      // 引擎时钟相对 DCC 的偏移 (ms)
        private double _roundTripTimeMs;     // 最近一次 RTT (ms)
        private long _timeSyncCounter;
        
        public event Action<string> OnLogMessage;
        public event Action<DCCSceneData> OnSceneReceived;
        public event Action<string[]> OnSelectionReceived;
        public event Action<string, Matrix4x4> OnTransformReceived;
        public event Action<LiveLinkAnimationFrame> OnAnimationFrameReceived;
        public event Action<TimeSyncResult> OnTimeSyncCompleted;
        public event Action OnClientConnected;
        public event Action OnClientDisconnected;

        public bool IsConnected => _client?.Connected ?? false;
        public bool IsRunning => _isRunning;
        public string ClientAddress => _client?.Client.RemoteEndPoint?.ToString() ?? "Not connected";
        /// <summary>最近一次时间同步的往返延迟 (毫秒)。</summary>
        public double RoundTripTimeMs => _roundTripTimeMs;
        /// <summary>时钟偏移 (毫秒), DCC 时间 = 引擎时间 + 偏移。</summary>
        public double ClockOffsetMs => _clockOffsetMs;

        public LiveLinkServer(int port = 18888, int udpPort = 18889)
        {
            _port = port;
            _udpPort = udpPort;
        }

        public bool Start()
        {
            if (_isRunning) return true;

            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _isRunning = true;
                _cts = new CancellationTokenSource();

                Log($"LiveLink server started on port {_port}");
                
                // 启动 UDP 监听 (用于高频动画数据)
                StartUdp();
                
                _ = AcceptClientsAsync();
                _ = StartHeartbeatAsync();
                
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to start server: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            
            try { _udpClient?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            
            Log("LiveLink server stopped");
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    
                    if (_client?.Connected == true)
                    {
                        Log($"Rejected connection from {client.Client.RemoteEndPoint} - already connected");
                        client.Close();
                        continue;
                    }

                    _client = client;
                    _stream = _client.GetStream();
                    _client.ReceiveBufferSize = 65536;
                    _client.SendBufferSize = 65536;
                    
                    Log($"Client connected: {client.Client.RemoteEndPoint}");
                    OnClientConnected?.Invoke();

                    _receiveTask = ReceiveLoopAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning) Log($"Accept error: {ex.Message}");
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65536];
            
            while (_isRunning && _client?.Connected == true && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                    
                    if (bytesRead == 0)
                    {
                        Log("Client disconnected");
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                        _receiveBuffer.Add(buffer[i]);

                    ProcessBuffer();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Receive error: {ex.Message}");
                    break;
                }
            }

            OnClientDisconnected?.Invoke();
            _client?.Close();
            _client = null;
            _stream = null;
            _receiveBuffer.Clear();
        }

        private void ProcessBuffer()
        {
            while (_receiveBuffer.Count >= 4)
            {
                // 读取 4 字节长度头 (大端序)
                int length = (_receiveBuffer[0] << 24) | (_receiveBuffer[1] << 16) | 
                            (_receiveBuffer[2] << 8) | _receiveBuffer[3];

                if (_receiveBuffer.Count < 4 + length)
                    break; // 数据不完整,等待更多

                var messageBytes = _receiveBuffer.GetRange(4, length).ToArray();
                _receiveBuffer.RemoveRange(0, 4 + length);

                try
                {
                    string json = Encoding.UTF8.GetString(messageBytes);
                    var message = System.Text.Json.JsonSerializer.Deserialize<LiveLinkMessage>(json);
                    
                    if (message != null)
                        HandleMessage(message);
                }
                catch (Exception ex)
                {
                    Log($"Parse error: {ex.Message}");
                }
            }
        }

        private void HandleMessage(LiveLinkMessage msg)
        {
            switch (msg.Type)
            {
                case "SCENE_UPDATE":
                    if (msg.Payload is System.Text.Json.JsonElement payload)
                    {
                        var sceneData = ParseSceneData(payload);
                        if (sceneData != null)
                        {
                            OnSceneReceived?.Invoke(sceneData);
                            Log($"Scene received: {sceneData.Meshes.Count} meshes, {sceneData.Materials.Count} materials");
                        }
                    }
                    break;

                case "SELECTION_CHANGED":
                    if (msg.Payload is System.Text.Json.JsonElement selPayload &&
                        selPayload.TryGetProperty("objects", out var objects))
                    {
                        var names = new List<string>();
                        foreach (var item in objects.EnumerateArray())
                            names.Add(item.GetString());
                        OnSelectionReceived?.Invoke(names.ToArray());
                    }
                    break;

                case "TRANSFORM_UPDATED":
                    if (msg.Payload is System.Text.Json.JsonElement trPayload &&
                        trPayload.TryGetProperty("name", out var name) &&
                        trPayload.TryGetProperty("transform", out var transform))
                    {
                        var matrix = ParseTransform(transform);
                        OnTransformReceived?.Invoke(name.GetString(), matrix);
                    }
                    break;

                case "PONG":
                    // 心跳响应
                    break;

                case "ANIMATION_FRAME":
                    if (msg.Payload is System.Text.Json.JsonElement animPayload)
                    {
                        var frame = ParseAnimationFrame(animPayload);
                        if (frame != null)
                        {
                            OnAnimationFrameReceived?.Invoke(frame);
                        }
                    }
                    break;

                case "TIME_SYNC_RESPONSE":
                    if (msg.Payload is System.Text.Json.JsonElement tsPayload)
                    {
                        ProcessTimeSyncResponse(tsPayload);
                    }
                    break;

                default:
                    Log($"Unknown message type: {msg.Type}");
                    break;
            }
        }

        private DCCSceneData ParseSceneData(System.Text.Json.JsonElement root)
        {
            var scene = new DCCSceneData();
            
            try
            {
                if (root.TryGetProperty("name", out var nameEl))
                    scene.Name = nameEl.GetString();
                if (root.TryGetProperty("source", out var sourceEl))
                    scene.SourceApplication = ParseDCCApp(sourceEl.GetString());

                if (root.TryGetProperty("meshes", out var meshesEl))
                {
                    scene.Meshes = new List<DCCMeshData>();
                    foreach (var meshEl in meshesEl.EnumerateArray())
                    {
                        var mesh = ParseMesh(meshEl);
                        if (mesh != null) scene.Meshes.Add(mesh);
                    }
                }

                if (root.TryGetProperty("materials", out var matsEl))
                {
                    scene.Materials = new List<DCCMaterialData>();
                    foreach (var matEl in matsEl.EnumerateArray())
                    {
                        var mat = ParseMaterial(matEl);
                        if (mat != null) scene.Materials.Add(mat);
                    }
                }

                if (root.TryGetProperty("lights", out var lightsEl))
                {
                    scene.Lights = new List<DCCLightData>();
                    foreach (var lightEl in lightsEl.EnumerateArray())
                    {
                        var light = ParseLight(lightEl);
                        if (light != null) scene.Lights.Add(light);
                    }
                }

                if (root.TryGetProperty("cameras", out var camsEl))
                {
                    scene.Cameras = new List<DCCCameraData>();
                    foreach (var camEl in camsEl.EnumerateArray())
                    {
                        var cam = ParseCamera(camEl);
                        if (cam != null) scene.Cameras.Add(cam);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Parse scene error: {ex.Message}");
            }

            return scene;
        }

        private DCCMeshData ParseMesh(System.Text.Json.JsonElement el)
        {
            var mesh = new DCCMeshData
            {
                Name = el.TryGetProperty("name", out var n) ? n.GetString() : "Unknown",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string>(),
                MaterialColors = new List<System.Numerics.Vector3>()
            };

            try
            {
                if (el.TryGetProperty("vertices", out var vertsEl))
                {
                    foreach (var v in vertsEl.EnumerateArray())
                    {
                        var arr = v.EnumerateArray().ToArray();
                        mesh.Vertices.Add(new DCCVertex
                        {
                            Position = new System.Numerics.Vector3(
                                GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2)),
                            Color = new System.Numerics.Vector4(1, 1, 1, 1),
                            MaterialIndex = 0
                        });
                    }
                }

                if (el.TryGetProperty("indices", out var idxEl) || el.TryGetProperty("faces", out idxEl))
                {
                    foreach (var idx in idxEl.EnumerateArray())
                    {
                        if (idx.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var i in idx.EnumerateArray())
                                mesh.Indices.Add(i.GetInt32());
                        }
                        else
                        {
                            mesh.Indices.Add(idx.GetInt32());
                        }
                    }
                }

                if (el.TryGetProperty("uvs", out var uvsEl))
                {
                    int vi = 0;
                    foreach (var uv in uvsEl.EnumerateArray())
                    {
                        var arr = uv.EnumerateArray().ToArray();
                        if (vi < mesh.Vertices.Count)
                        {
                            mesh.Vertices[vi].UV0 = new System.Numerics.Vector2(GetFloat(arr, 0), GetFloat(arr, 1));
                            mesh.HasUVs = true;
                        }
                        vi++;
                    }
                }

                if (el.TryGetProperty("normals", out var normsEl))
                {
                    int ni = 0;
                    foreach (var norm in normsEl.EnumerateArray())
                    {
                        var arr = norm.EnumerateArray().ToArray();
                        if (ni < mesh.Vertices.Count)
                        {
                            mesh.Vertices[ni].Normal = new System.Numerics.Vector3(
                                GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2));
                            mesh.HasNormals = true;
                        }
                        ni++;
                    }
                }

                if (el.TryGetProperty("materials", out var matsEl))
                {
                    foreach (var m in matsEl.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var mn))
                        {
                            mesh.MaterialNames.Add(mn.GetString());
                            if (m.TryGetProperty("diffuseColor", out var dc))
                            {
                                var arr = dc.EnumerateArray().ToArray();
                                mesh.MaterialColors.Add(new System.Numerics.Vector3(
                                    GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2)));
                            }
                            else
                            {
                                mesh.MaterialColors.Add(new System.Numerics.Vector3(0.8f, 0.8f, 0.8f));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Parse mesh error: {ex.Message}");
            }

            return mesh;
        }

        private DCCMaterialData ParseMaterial(System.Text.Json.JsonElement el)
        {
            var mat = new DCCMaterialData
            {
                Name = el.TryGetProperty("name", out var n) ? n.GetString() : "Default"
            };

            if (el.TryGetProperty("diffuseColor", out var dc))
            {
                var arr = dc.EnumerateArray().ToArray();
                mat.BaseColor = new System.Numerics.Vector3(GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2));
            }
            
            mat.Metallic = el.TryGetProperty("metallic", out var m) ? (float)m.GetDouble() : 0f;
            mat.Roughness = el.TryGetProperty("roughness", out var r) ? (float)r.GetDouble() : 0.5f;
            
            if (el.TryGetProperty("emission", out var em))
            {
                var arr = em.EnumerateArray().ToArray();
                mat.Emissive = GetFloat(arr, 0);
            }

            return mat;
        }

        private DCCLightData ParseLight(System.Text.Json.JsonElement el)
        {
            var light = new DCCLightData
            {
                Name = el.TryGetProperty("name", out var n) ? n.GetString() : "Light"
            };

            if (el.TryGetProperty("type", out var t))
                light.Type = t.GetString();
            
            if (el.TryGetProperty("color", out var c))
            {
                var arr = c.EnumerateArray().ToArray();
                light.Color = new System.Numerics.Vector3(GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2));
            }
            
            light.Intensity = el.TryGetProperty("intensity", out var i) ? (float)i.GetDouble() : 1f;
            light.Range = el.TryGetProperty("distance", out var d) ? (float)d.GetDouble() : 10f;

            return light;
        }

        private DCCCameraData ParseCamera(System.Text.Json.JsonElement el)
        {
            var cam = new DCCCameraData
            {
                Name = el.TryGetProperty("name", out var n) ? n.GetString() : "Camera"
            };
            
            cam.FOV = el.TryGetProperty("fov", out var fov) ? (float)fov.GetDouble() : 50f;
            cam.AspectRatio = el.TryGetProperty("aspect", out var ar) ? (float)ar.GetDouble() : 1.777f;
            cam.NearClip = el.TryGetProperty("nearClip", out var nc) ? (float)nc.GetDouble() : 0.1f;
            cam.FarClip = el.TryGetProperty("farClip", out var fc) ? (float)fc.GetDouble() : 1000f;

            return cam;
        }

        private DCCApplication ParseDCCApp(string app)
        {
            return app?.ToLower() switch
            {
                "blender 5.1" or "blender5.1" => DCCApplication.Blender51,
                "blender 4.2" => DCCApplication.Blender42,
                "blender 3.6" => DCCApplication.Blender36,
                "maya 2025" => DCCApplication.Maya2025,
                "maya 2024" => DCCApplication.Maya2024,
                "3ds max 2025" => DCCApplication.Max2025,
                "3ds max 2024" => DCCApplication.Max2024,
                "houdini 20" or "houdini" => DCCApplication.Houdini20,
                _ => DCCApplication.Blender51
            };
        }

        private float GetFloat(System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement> arr, int index)
        {
            if (index >= 0 && index < arr.Count) return (float)arr[index].GetDouble();
            return 0f;
        }

        private System.Numerics.Matrix4x4 ParseTransform(System.Text.Json.JsonElement el)
        {
            if (el.TryGetProperty("matrix", out var matrixEl))
            {
                var values = new float[16];
                int idx = 0;
                foreach (var row in matrixEl.EnumerateArray())
                {
                    foreach (var val in row.EnumerateArray())
                    {
                        if (idx < 16) values[idx] = (float)val.GetDouble();
                        idx++;
                    }
                }
                
                return new System.Numerics.Matrix4x4(
                    values[0], values[4], values[8], values[12],
                    values[1], values[5], values[9], values[13],
                    values[2], values[6], values[10], values[14],
                    values[3], values[7], values[11], values[15]
                );
            }
            
            return System.Numerics.Matrix4x4.Identity;
        }

        public void SendCommand(string type, object payload = null)
        {
            if (_client?.Connected != true || _stream == null) return;

            var message = new LiveLinkMessage { Type = type, Payload = payload };
            var json = System.Text.Json.JsonSerializer.Serialize(message);
            
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var length = BitConverter.GetBytes(bytes.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(length);
                
                lock (_stream)
                {
                    _stream.Write(length, 0, 4);
                    _stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Log($"Send error: {ex.Message}");
            }
        }

        public void RequestScene() => SendCommand("REQUEST_SCENE");
        public void SelectObject(string name) => SendCommand("SELECT_OBJECT", new { name });
        public void SetActiveObject(string name) => SendCommand("SET_ACTIVE", new { name });

        private async Task StartHeartbeatAsync()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(5000, _cts.Token);
                
                if (_client?.Connected == true)
                {
                    try
                    {
                        SendCommand("PING");
                    }
                    catch { }
                }
            }
        }

        private void Log(string message)
        {
            Debug.WriteLine($"[LiveLink] {message}");
            OnLogMessage?.Invoke(message);
        }

        // ──────────────────────────────────────────────────────────────
        // UDP 支持 (高频动画数据流)
        // ──────────────────────────────────────────────────────────────

        private void StartUdp()
        {
            try
            {
                _udpClient = new UdpClient(_udpPort);
                _udpReceiveTask = UdpReceiveLoopAsync();
                Log($"UDP listener started on port {_udpPort}");
            }
            catch (Exception ex)
            {
                Log($"Failed to start UDP: {ex.Message}");
            }
        }

        private async Task UdpReceiveLoopAsync()
        {
            while (_isRunning && _udpClient != null && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    string json = Encoding.UTF8.GetString(result.Buffer);
                    var message = System.Text.Json.JsonSerializer.Deserialize<LiveLinkMessage>(json);

                    if (message != null)
                    {
                        // UDP 消息走同一个 HandleMessage 路径
                        HandleMessage(message);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) Log($"UDP receive error: {ex.Message}");
                }
            }
        }

        /// <summary>通过 UDP 发送数据 (用于高频命令, 无可靠保证)。</summary>
        public void SendUdp(string host, int port, string type, object payload = null)
        {
            if (_udpClient == null) return;
            var message = new LiveLinkMessage { Type = type, Payload = payload };
            var json = System.Text.Json.JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            try { _udpClient.Send(bytes, bytes.Length, host, port); }
            catch (Exception ex) { Log($"UDP send error: {ex.Message}"); }
        }

        // ──────────────────────────────────────────────────────────────
        // 动画数据流
        // ──────────────────────────────────────────────────────────────

        private LiveLinkAnimationFrame ParseAnimationFrame(System.Text.Json.JsonElement el)
        {
            var frame = new LiveLinkAnimationFrame();
            try
            {
                frame.FrameTime = el.TryGetProperty("frameTime", out var ft) ? (float)ft.GetDouble() : 0f;
                frame.FrameIndex = el.TryGetProperty("frameIndex", out var fi) ? fi.GetInt32() : 0;
                frame.Fps = el.TryGetProperty("fps", out var fps) ? (float)fps.GetDouble() : 30f;

                if (el.TryGetProperty("boneTransforms", out var bonesEl))
                {
                    frame.BoneTransforms = new List<LiveLinkBoneTransform>();
                    foreach (var bone in bonesEl.EnumerateArray())
                    {
                        var bt = new LiveLinkBoneTransform
                        {
                            BoneName = bone.TryGetProperty("name", out var bn) ? bn.GetString() : ""
                        };
                        if (bone.TryGetProperty("position", out var pos))
                        {
                            var arr = pos.EnumerateArray().ToArray();
                            bt.Position = new Vector3(GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2));
                        }
                        if (bone.TryGetProperty("rotation", out var rot))
                        {
                            var arr = rot.EnumerateArray().ToArray();
                            bt.Rotation = new Quaternion(GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2), GetFloat(arr, 3));
                        }
                        if (bone.TryGetProperty("scale", out var scl))
                        {
                            var arr = scl.EnumerateArray().ToArray();
                            bt.Scale = new Vector3(GetFloat(arr, 0), GetFloat(arr, 1), GetFloat(arr, 2));
                        }
                        frame.BoneTransforms.Add(bt);
                    }
                }

                if (el.TryGetProperty("morphTargetWeights", out var morphEl))
                {
                    frame.MorphTargetWeights = new Dictionary<string, float>();
                    if (morphEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in morphEl.EnumerateObject())
                            frame.MorphTargetWeights[prop.Name] = (float)prop.Value.GetDouble();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Parse animation frame error: {ex.Message}");
            }
            return frame;
        }

        /// <summary>发送动画播放控制命令到 DCC。</summary>
        public void PlayAnimation(string clipName, float startTime = 0f) =>
            SendCommand("PLAY_ANIMATION", new { clip = clipName, startTime });
        public void StopAnimation() => SendCommand("STOP_ANIMATION");
        public void SetAnimationTime(float time) => SendCommand("SET_ANIM_TIME", new { time });

        // ──────────────────────────────────────────────────────────────
        // 时间同步 (NTP 风格)
        // ──────────────────────────────────────────────────────────────

        /// <summary>发起时间同步请求 (测量 RTT 和时钟偏移)。</summary>
        public void RequestTimeSync()
        {
            _timeSyncCounter++;
            var engineTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SendCommand("TIME_SYNC_REQUEST", new
            {
                syncId = _timeSyncCounter,
                engineTime = engineTimeMs
            });
        }

        /// <summary>处理 DCC 返回的时间同步响应。</summary>
        private void ProcessTimeSyncResponse(System.Text.Json.JsonElement el)
        {
            try
            {
                long syncId = el.TryGetProperty("syncId", out var si) ? si.GetInt64() : 0;
                long engineSendTime = el.TryGetProperty("engineTime", out var et) ? et.GetInt64() : 0;
                long dccTime = el.TryGetProperty("dccTime", out var dt) ? dt.GetInt64() : 0;

                long engineReceiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // RTT = 接收时间 - 发送时间
                _roundTripTimeMs = engineReceiveTime - engineSendTime;
                // 时钟偏移 = DCC 时间 - 引擎时间 - RTT/2
                _clockOffsetMs = dccTime - engineSendTime - _roundTripTimeMs / 2.0;

                var result = new TimeSyncResult
                {
                    SyncId = syncId,
                    RoundTripTimeMs = _roundTripTimeMs,
                    ClockOffsetMs = _clockOffsetMs,
                    EngineTimeMs = engineReceiveTime,
                    DccTimeMs = engineReceiveTime + (long)_clockOffsetMs
                };
                OnTimeSyncCompleted?.Invoke(result);

                Log($"Time sync: RTT={_roundTripTimeMs:F1}ms, Offset={_clockOffsetMs:F1}ms");
            }
            catch (Exception ex)
            {
                Log($"Time sync error: {ex.Message}");
            }
        }

        /// <summary>获取同步后的 DCC 当前时间 (毫秒)。</summary>
        public long GetSyncedDccTimeMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)_clockOffsetMs;
        }

        /// <summary>获取同步后的 DCC 当前时间 (秒)。</summary>
        public double GetSyncedDccTimeSeconds() => GetSyncedDccTimeMs() / 1000.0;

        public List<string> GetSupportedFormats()
        {
            return new List<string>
            {
                "FBX (*.fbx) — Autodesk FBX, industry standard",
                "OBJ (*.obj) — Wavefront Alias OBJ",
                "glTF 2.0 (*.gltf) — Khronos Group glTF",
                "GLB (*.glb) — glTF Binary with embedded textures",
                "Collada DAE (*.dae) — Khronos Collada",
                "PLY (*.ply) — Stanford Polygon Format",
                "STL (*.stl) — Stereolithography",
                "3DS (*.3ds) — Autodesk 3D Studio",
                "BLEND (*.blend) — Blender native format",
                "USD (*.usd) — Pixar Universal Scene Description"
            };
        }
    }

    public class LiveLinkMessage
    {
        public string Type { get; set; }
        public object Payload { get; set; }
    }

    /// <summary>LiveLink 动画帧数据: 包含骨骼变换和变形目标权重。</summary>
    public class LiveLinkAnimationFrame
    {
        /// <summary>帧时间 (秒)。</summary>
        public float FrameTime { get; set; }
        /// <summary>帧索引。</summary>
        public int FrameIndex { get; set; }
        /// <summary>帧率 (FPS)。</summary>
        public float Fps { get; set; } = 30f;
        /// <summary>骨骼变换列表。</summary>
        public List<LiveLinkBoneTransform> BoneTransforms { get; set; } = new();
        /// <summary>变形目标 (Morph Target) 权重。</summary>
        public Dictionary<string, float> MorphTargetWeights { get; set; } = new();
    }

    /// <summary>单个骨骼的变换数据。</summary>
    public struct LiveLinkBoneTransform
    {
        public string BoneName;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public readonly Matrix4x4 ToMatrix() =>
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>时间同步结果。</summary>
    public class TimeSyncResult
    {
        public long SyncId { get; set; }
        public double RoundTripTimeMs { get; set; }
        public double ClockOffsetMs { get; set; }
        public long EngineTimeMs { get; set; }
        public long DccTimeMs { get; set; }
    }
}
