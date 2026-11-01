using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    // ============================================================================================
    //  CanvasRenderer.cs
    //
    //  Canvas 渲染器：负责 UI 图元的绘制指令收集与执行。
    //  将绘制命令（矩形、线条、文本、圆角矩形等）缓存为批次列表，
    //  最终由渲染后端一次性提交 GPU 绘制。
    //  相当于 UE Slate 的 FSlateDrawElement / Unity Canvas 2D 渲染管线。
    // ============================================================================================

    /// <summary>绘制命令类型。</summary>
    public enum DrawCmdType
    {
        FillRect,
        StrokeRect,
        FillRoundedRect,
        StrokeRoundedRect,
        DrawLine,
        DrawText,
        DrawImage,
        ClipRect,
        PushLayer,
        PopLayer,
    }

    /// <summary>绘制命令基类。</summary>
    public class DrawCmd
    {
        public DrawCmdType Type;
        public float Opacity = 1f;
        public Rectangle ClipRect = Rectangle.Empty;
        public Matrix4x4 Transform = Matrix4x4.Identity;
        protected DrawCmd(DrawCmdType type) { Type = type; }
    }

    /// <summary>填充矩形命令。</summary>
    internal sealed class FillRectCmd : DrawCmd
    {
        public Vector2 Position;
        public Vector2 Size;
        public Color Color;
        public float CornerRadius;
        public FillRectCmd(Vector2 pos, Vector2 size, Color color, float cornerRadius = 0f)
            : base(DrawCmdType.FillRect)
        { Position = pos; Size = size; Color = color; CornerRadius = cornerRadius; }
    }

    /// <summary>描边矩形命令。</summary>
    internal sealed class StrokeRectCmd : DrawCmd
    {
        public Vector2 Position;
        public Vector2 Size;
        public Color Color;
        public float Thickness;
        public float CornerRadius;
        public StrokeRectCmd(Vector2 pos, Vector2 size, Color color, float thickness = 1f, float cornerRadius = 0f)
            : base(DrawCmdType.StrokeRect)
        { Position = pos; Size = size; Color = color; Thickness = thickness; CornerRadius = cornerRadius; }
    }

    /// <summary>绘制直线命令。</summary>
    internal sealed class DrawLineCmd : DrawCmd
    {
        public Vector2 Start;
        public Vector2 End;
        public Color Color;
        public float Thickness;
        public DrawLineCmd(Vector2 start, Vector2 end, Color color, float thickness = 1f)
            : base(DrawCmdType.DrawLine)
        { Start = start; End = end; Color = color; Thickness = thickness; }
    }

    /// <summary>绘制文本命令。</summary>
    internal sealed class DrawTextCmd : DrawCmd
    {
        public string Text = string.Empty;
        public Vector2 Position;
        public float FontSize = 14f;
        public Color Color;
        public string FontName = "Default";
        public float MaxWidth;
        public bool WrapText;
        public DrawTextCmd(string text, Vector2 pos, Color color, float fontSize = 14f, string fontName = "Default")
            : base(DrawCmdType.DrawText)
        { Text = text; Position = pos; Color = color; FontSize = fontSize; FontName = fontName; }
    }

    /// <summary>绘制图片命令。</summary>
    internal sealed class DrawImageCmd : DrawCmd
    {
        public object Texture;
        public Vector2 Position;
        public Vector2 Size;
        public Color Tint;
        public DrawImageCmd(object texture, Vector2 pos, Vector2 size, Color tint)
            : base(DrawCmdType.DrawImage)
        { Texture = texture; Position = pos; Size = size; Tint = tint; }
    }

    /// <summary>裁剪矩形命令。</summary>
    internal sealed class ClipRectCmd : DrawCmd
    {
        public Rectangle Clip;
        public ClipRectCmd(Rectangle clip) : base(DrawCmdType.ClipRect) { Clip = clip; }
    }

    // ──────────────────────────────────────────────────────────────
    //  CanvasRenderer
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Canvas 渲染器：收集 UI 绘制命令，支持裁剪/透明度/变换层级，
    /// 最终批量提交渲染后端。
    /// 相当于 UE Slate 渲染器 / Unity Canvas 渲染管线。
    /// </summary>
    public sealed class CanvasRenderer
    {
        /// <summary>绘制命令列表（每帧清空重建）。</summary>
        private readonly List<DrawCmd> _commands = new();

        /// <summary>当前透明度栈。</summary>
        private readonly Stack<float> _opacityStack = new();

        /// <summary>当前裁剪矩形栈。</summary>
        private readonly Stack<Rectangle> _clipStack = new();

        /// <summary>当前变换矩阵栈。</summary>
        private readonly Stack<Matrix4x4> _transformStack = new();

        /// <summary>当前累计透明度。</summary>
        public float CurrentOpacity => _opacityStack.Count > 0 ? _opacityStack.Peek() : 1f;

        /// <summary>当前裁剪矩形。</summary>
        public Rectangle CurrentClip => _clipStack.Count > 0 ? _clipStack.Peek() : Rectangle.Empty;

        /// <summary>当前变换矩阵。</summary>
        public Matrix4x4 CurrentTransform => _transformStack.Count > 0 ? _transformStack.Peek() : Matrix4x4.Identity;

        /// <summary>视口尺寸。</summary>
        public float ViewportWidth { get; set; }
        public float ViewportHeight { get; set; }

        /// <summary>是否正在帧内。</summary>
        private bool _inFrame;

        /// <summary>命令数量（调试用）。</summary>
        public int CommandCount => _commands.Count;

        /// <summary>开始新帧（清空命令缓冲）。</summary>
        public void BeginFrame()
        {
            if (_inFrame) return;
            _inFrame = true;
            _commands.Clear();
            _opacityStack.Clear();
            _clipStack.Clear();
            _transformStack.Clear();
        }

        /// <summary>结束帧（可在此处执行实际渲染或提交批次）。</summary>
        public void EndFrame()
        {
            if (!_inFrame) return;
            _inFrame = false;
            FlushCommands();
        }

        /// <summary>清空命令（不结束帧）。</summary>
        public void ClearCommands() => _commands.Clear();

        // ── 透明度 ────────────────────────────────────────────────

        /// <summary>压入透明度层级。</summary>
        public void PushOpacity(float opacity)
        {
            float current = CurrentOpacity;
            _opacityStack.Push(current * Math.Clamp(opacity, 0f, 1f));
        }

        /// <summary>弹出透明度层级。</summary>
        public void PopOpacity()
        {
            if (_opacityStack.Count > 0) _opacityStack.Pop();
        }

        // ── 裁剪 ────────────────────────────────────────────────

        /// <summary>压入裁剪矩形（与已有裁剪取交集）。</summary>
        public void PushClip(Rectangle rect)
        {
            if (_clipStack.Count > 0)
            {
                var parent = _clipStack.Peek();
                float x = Math.Max(rect.X, parent.X);
                float y = Math.Max(rect.Y, parent.Y);
                float r = Math.Min(rect.Right, parent.Right);
                float b = Math.Min(rect.Bottom, parent.Bottom);
                _clipStack.Push(new Rectangle(x, y, Math.Max(0, r - x), Math.Max(0, b - y)));
            }
            else
            {
                _clipStack.Push(rect);
            }
        }

        /// <summary>弹出裁剪矩形。</summary>
        public void PopClip()
        {
            if (_clipStack.Count > 0) _clipStack.Pop();
        }

        // ── 变换 ────────────────────────────────────────────────

        /// <summary>压入变换矩阵（与已有矩阵累乘）。</summary>
        public void PushTransform(Matrix4x4 transform)
        {
            _transformStack.Push(transform * CurrentTransform);
        }

        /// <summary>弹出变换矩阵。</summary>
        public void PopTransform()
        {
            if (_transformStack.Count > 0) _transformStack.Pop();
        }

        // ── 绘制命令 ─────────────────────────────────────────────

        /// <summary>填充矩形。</summary>
        public void FillRect(Vector2 position, Vector2 size, Color color, float cornerRadius = 0f)
        {
            var cmd = new FillRectCmd(position, size, color, cornerRadius);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>填充矩形（使用 Rectangle）。</summary>
        public void FillRect(Rectangle rect, Color color, float cornerRadius = 0f)
        {
            FillRect(new Vector2(rect.X, rect.Y), new Vector2(rect.Width, rect.Height), color, cornerRadius);
        }

        /// <summary>描边矩形。</summary>
        public void StrokeRect(Vector2 position, Vector2 size, Color color, float thickness = 1f, float cornerRadius = 0f)
        {
            var cmd = new StrokeRectCmd(position, size, color, thickness, cornerRadius);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制直线。</summary>
        public void DrawLine(Vector2 start, Vector2 end, Color color, float thickness = 1f)
        {
            var cmd = new DrawLineCmd(start, end, color, thickness);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制文本。</summary>
        public void DrawText(string text, Vector2 position, Color color, float fontSize = 14f, string fontName = "Default")
        {
            var cmd = new DrawTextCmd(text, position, color, fontSize, fontName);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制文本（自动换行）。</summary>
        public void DrawTextWrapped(string text, Vector2 position, Color color, float maxWidth, float fontSize = 14f, string fontName = "Default")
        {
            var cmd = new DrawTextCmd(text, position, color, fontSize, fontName) { WrapText = true, MaxWidth = maxWidth };
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制图片。</summary>
        public void DrawImage(object texture, Vector2 position, Vector2 size, Color tint)
        {
            var cmd = new DrawImageCmd(texture, position, size, tint);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        // ── 新样式控件（WidgetControls 等）使用的方法 ─────────────

        /// <summary>绘制文本（带对齐/换行等完整参数）。</summary>
        public void DrawText(Vector2 position, Vector2 size, string text, string fontName,
            float fontSize, Color color, HAlign horizontalAlignment, VAlign verticalAlignment,
            bool autoWrap, bool richText)
        {
            var cmd = new DrawTextCmd(text, position, color, fontSize, fontName) { WrapText = autoWrap };
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制纹理（按资源名解析）。</summary>
        public void DrawTexture(Vector2 position, Vector2 size, string textureName, Vector4 uvRegion, Color color)
        {
            var cmd = new DrawImageCmd(textureName, position, size, color);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>九宫格绘制纹理。</summary>
        public void DrawNineSlice(Vector2 position, Vector2 size, string textureName, Vector4 uvRegion, float margin, Color color)
        {
            var cmd = new DrawImageCmd(textureName, position, size, color);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>填充圆角矩形。</summary>
        public void FillRoundedRect(Vector2 position, Vector2 size, float cornerRadius, Color color)
        {
            var cmd = new FillRectCmd(position, size, color, cornerRadius);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        /// <summary>绘制矩形描边。</summary>
        public void DrawRect(Vector2 position, Vector2 size, float thickness, Color color)
        {
            var cmd = new StrokeRectCmd(position, size, color, thickness);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        // ── 兼容器旧控件（ProgressBar/TextBox 等）使用的方法 ─────

        /// <summary>填充矩形（旧控件便捷重载，颜色为 Vector4）。</summary>
        public void DrawRect(float x, float y, float w, float h, Vector4 color)
            => FillRect(new Vector2(x, y), new Vector2(w, h), new Color(color.X, color.Y, color.Z, color.W));

        /// <summary>填充圆角矩形（旧控件便捷重载，颜色为 Vector4）。</summary>
        public void DrawRect(float x, float y, float w, float h, Vector4 color, float cornerRadius)
            => FillRect(new Vector2(x, y), new Vector2(w, h), new Color(color.X, color.Y, color.Z, color.W), cornerRadius);

        /// <summary>绘制矩形描边（旧控件便捷重载，颜色为 Vector4）。</summary>
        public void DrawRectOutline(float x, float y, float w, float h, Vector4 color, float thickness, float cornerRadius)
            => StrokeRect(new Vector2(x, y), new Vector2(w, h), new Color(color.X, color.Y, color.Z, color.W), thickness, cornerRadius);

        /// <summary>绘制圆形（以正方形近似渲染圆形图元）。</summary>
        public void DrawCircle(Vector2 center, float radius, Vector4 color)
            => FillRect(new Vector2(center.X - radius, center.Y - radius),
                        new Vector2(radius * 2, radius * 2),
                        new Color(color.X, color.Y, color.Z, color.W));

        /// <summary>绘制胶囊/药丸形。</summary>
        public void DrawPill(float x, float y, float w, float h, Vector4 color)
            => FillRect(new Vector2(x, y), new Vector2(w, h), new Color(color.X, color.Y, color.Z, color.W), h * 0.5f);

        /// <summary>绘制文本（旧控件便捷重载，位置为两浮点，颜色为 Vector4）。</summary>
        public void DrawText(string text, float x, float y, Vector4 color, float fontSize)
        {
            var cmd = new DrawTextCmd(text, new Vector2(x, y), new Color(color.X, color.Y, color.Z, color.W), fontSize);
            ApplyContext(cmd);
            _commands.Add(cmd);
        }

        // ── 辅助 ────────────────────────────────────────────────

        /// <summary>将当前上下文（透明度/裁剪/变换）应用到命令。</summary>
        private void ApplyContext(DrawCmd cmd)
        {
            cmd.Opacity = CurrentOpacity;
            cmd.ClipRect = CurrentClip;
            cmd.Transform = CurrentTransform;
        }

        /// <summary>刷新命令到渲染后端（实际渲染实现）。</summary>
        private void FlushCommands()
        {
            // TODO: 实现实际的渲染提交逻辑
            // 在此遍历 _commands，转换为渲染后端的 DrawCall
            // 例如：提交到 Vulkan/D3D12 命令缓冲
            //
            // 目前为空实现 — 渲染后端集成时填充
        }

        /// <summary>获取只读命令列表（供调试/测试）。</summary>
        public IReadOnlyList<DrawCmd> GetCommands() => _commands.AsReadOnly();
    }
}
