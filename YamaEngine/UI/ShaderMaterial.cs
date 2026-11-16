// =============================================================================
//  ShaderMaterial.cs
//  yama引擎 - 2D 着色器材质（对应 Godot 3 的 Shader + ShaderMaterial）
//
//  对应 Godot 3 语义：
//    * Shader：着色器程序资源（源码 + 类型，缓存编译结果）
//    * ShaderMaterial：着色器材质实例（持有 Shader + uniform 参数字典）
//    * set_shader_parameter / get_shader_parameter：按名读写 uniform
//    * uniform 类型：float / Vector2 / Vector4(Color) / int / bool / 纹理(标识)
//    * 内建常量：TIME（累计时间，Update 推进）
//    * 参数变更脏标记：IsDirty（供渲染层判断是否重新上传 uniform）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>着色器类型（对应 Godot Shader.Mode）。</summary>
    public enum ShaderMode
    {
        Spatial,    // 3D（对应 Godot MODE_SPATIAL）
        CanvasItem, // 2D CanvasItem（对应 Godot MODE_CANVAS_ITEM）
        Particles,  // 粒子
        Sky,        // 天空
    }

    /// <summary>uniform 参数类型（简化为运行时值装箱的判别）。</summary>
    public enum ShaderUniformType
    {
        Float,
        Int,
        Bool,
        Vector2,
        Vector3,
        Vector4, // 颜色也归此类（对应 Godot vec4）
        Texture, // 纹理用路径/句柄字符串标识
    }

    /// <summary>着色器源码资源（对应 Godot Shader）。</summary>
    public sealed class Shader
    {
        /// <summary>着色器代码（GLSL/HLSL 源，测试可为占位）。</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>着色器类型。</summary>
        public ShaderMode Mode { get; set; } = ShaderMode.CanvasItem;

        /// <summary>是否已声明 uniform 集合（缓存，避免重复解析）。</summary>
        private readonly Dictionary<string, ShaderUniformType> _uniforms = new();

        /// <summary>声明 uniform（对应 shader 里的 uniform 声明）。</summary>
        public Shader DeclareUniform(string name, ShaderUniformType type)
        {
            _uniforms[name] = type;
            return this;
        }

        /// <summary>是否存在该 uniform 声明。</summary>
        public bool HasUniform(string name) => _uniforms.ContainsKey(name);

        /// <summary>取 uniform 类型（不存在返回 null）。</summary>
        public ShaderUniformType? GetUniformType(string name)
            => _uniforms.TryGetValue(name, out var t) ? t : null;

        /// <summary>uniform 声明数量。</summary>
        public int UniformCount => _uniforms.Count;

        /// <summary>所有 uniform 名（稳定枚举）。</summary>
        public IReadOnlyCollection<string> UniformNames => _uniforms.Keys;
    }

    /// <summary>
    /// 着色器材质（对应 Godot ShaderMaterial）。持有 Shader + uniform 参数 + 脏标记 + TIME。
    /// </summary>
    public sealed class ShaderMaterial
    {
        private readonly Dictionary<string, object> _params = new();
        private float _time;
        private bool _dirty = true;
        private Shader? _shader;

        /// <summary>绑定的着色器程序。</summary>
        public Shader? Shader
        {
            get => _shader;
            set
            {
                if (!ReferenceEquals(_shader, value))
                {
                    _shader = value;
                    MarkDirty();
                }
            }
        }

        /// <summary>累计时间（对应 Godot 内建 TIME uniform，Update 推进）。</summary>
        public float Time => _time;

        /// <summary>是否有未上传的参数变更（脏标记）。</summary>
        public bool IsDirty => _dirty;

        /// <summary>已设置的参数个数（不含未设置的声明）。</summary>
        public int ParameterCount => _params.Count;

        /// <summary>设置 uniform 参数（按名，装箱存值，触发脏标记）。</summary>
        public void SetShaderParameter(string name, object value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _params[name] = value;
            _dirty = true;
        }

        /// <summary>取 uniform 参数（未设置返回 null，对应 Godot get_shader_parameter 的默认 null）。</summary>
        public object? GetShaderParameter(string name)
            => _params.TryGetValue(name, out var v) ? v : null;

        /// <summary>取 uniform 参数（强类型，未设置或类型不符返回默认）。</summary>
        public T GetShaderParameter<T>(string name, T fallback = default!)
        {
            if (_params.TryGetValue(name, out var v) && v is T typed)
                return typed;
            return fallback;
        }

        /// <summary>是否存在该参数（已显式设置）。</summary>
        public bool HasShaderParameter(string name) => _params.ContainsKey(name);

        /// <summary>移除参数（回到默认，触发脏标记）。</summary>
        public bool RemoveShaderParameter(string name)
        {
            bool removed = _params.Remove(name);
            if (removed) _dirty = true;
            return removed;
        }

        /// <summary>清空所有参数。</summary>
        public void ClearShaderParameters()
        {
            _params.Clear();
            _dirty = true;
        }

        /// <summary>推进累计时间（对应 Godot 每帧 TIME += delta）。</summary>
        public void Update(float delta) => _time += delta;

        /// <summary>推进时间并标记一帧需要重新上传（TIME 每帧变化）。</summary>
        public void UpdateFrame(float delta)
        {
            _time += delta;
            _dirty = true;
        }

        /// <summary>手动标记脏（外部修改底层资源后调用）。</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>标记参数已上传（渲染层上传 uniform 后调用，清除脏标记）。</summary>
        public void ClearDirty() => _dirty = false;
    }
}
