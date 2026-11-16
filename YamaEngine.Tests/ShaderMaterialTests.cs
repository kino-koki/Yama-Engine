// ShaderMaterialTests.cs —— 追平 Godot 3 的 Shader/ShaderMaterial（uniform 参数/类型/脏标记/TIME）

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class ShaderMaterialTests
    {
        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        // ── Shader 声明 uniform：Has/Get/Count ──
        public static void TestShaderDeclareUniform()
        {
            var s = new Shader();
            s.DeclareUniform("speed", ShaderUniformType.Float);
            s.DeclareUniform("color", ShaderUniformType.Vector4);
            AssertTrue(s.HasUniform("speed"), "Has speed.");
            AssertFalse(s.HasUniform("missing"), "No missing.");
            AssertEqual(2, s.UniformCount, "Two uniforms.");
            AssertEqual(ShaderUniformType.Float, s.GetUniformType("speed")!.Value, "speed is Float.");
        }

        // ── Shader 默认类型 CanvasItem ──
        public static void TestShaderDefaultMode()
        {
            var s = new Shader();
            AssertEqual(ShaderMode.CanvasItem, s.Mode, "Default mode CanvasItem.");
            s.Mode = ShaderMode.Spatial;
            AssertEqual(ShaderMode.Spatial, s.Mode, "Mode changed.");
        }

        // ── DeclareUniform 链式返回 this ──
        public static void TestShaderDeclareChain()
        {
            var s = new Shader();
            var ret = s.DeclareUniform("a", ShaderUniformType.Float)
                       .DeclareUniform("b", ShaderUniformType.Int);
            AssertTrue(ReferenceEquals(s, ret), "Declare returns same shader (chain).");
        }

        // ── ShaderMaterial 初始状态：脏、无参数、Time=0 ──
        public static void TestMaterialInitialState()
        {
            var m = new ShaderMaterial();
            AssertTrue(m.IsDirty, "New material is dirty.");
            AssertEqual(0, m.ParameterCount, "No params.");
            AssertEqual(0f, m.Time, "Time=0.");
            AssertTrue(m.Shader == null, "No shader bound.");
        }

        // ── 设置参数触发脏标记 ──
        public static void TestSetParameterMarksDirty()
        {
            var m = new ShaderMaterial();
            m.ClearDirty();
            AssertFalse(m.IsDirty, "Clean after ClearDirty.");

            m.SetShaderParameter("speed", 2.0f);
            AssertTrue(m.IsDirty, "Set param marks dirty.");
            AssertEqual(1, m.ParameterCount, "One param.");
        }

        // ── 读回参数（object 重载） ──
        public static void TestGetParameterObject()
        {
            var m = new ShaderMaterial();
            m.SetShaderParameter("speed", 3.5f);
            var v = m.GetShaderParameter("speed");
            AssertTrue(v is float f && MathF.Abs(f - 3.5f) < 1e-6, "Read back 3.5.");
        }

        // ── 读未设置参数返回 null ──
        public static void TestGetMissingReturnsNull()
        {
            var m = new ShaderMaterial();
            AssertTrue(m.GetShaderParameter("nope") == null, "Missing param → null.");
        }

        // ── 强类型读取：类型匹配返回，否则 fallback ──
        public static void TestGetParameterTyped()
        {
            var m = new ShaderMaterial();
            m.SetShaderParameter("speed", 5.0f);
            m.SetShaderParameter("count", 7);

            AssertNear(5f, m.GetShaderParameter<float>("speed", 0f), "Float read.");
            AssertEqual(7, m.GetShaderParameter<int>("count", 0), "Int read.");
            // 类型不符返回 fallback
            AssertNear(9f, m.GetShaderParameter<float>("count", 9f), "Type mismatch → fallback.");
        }

        // ── Vector4（颜色）参数存取 ──
        public static void TestColorParameter()
        {
            var m = new ShaderMaterial();
            m.SetShaderParameter("tint", new Vector4(1f, 0.5f, 0f, 1f));
            var c = m.GetShaderParameter<Vector4>("tint", default);
            AssertNear(1f, c.X, "R=1.");
            AssertNear(0.5f, c.Y, "G=0.5.");
            AssertNear(0f, c.Z, "B=0.");
            AssertNear(1f, c.W, "A=1.");
        }

        // ── HasShaderParameter ──
        public static void TestHasParameter()
        {
            var m = new ShaderMaterial();
            AssertFalse(m.HasShaderParameter("x"), "No x.");
            m.SetShaderParameter("x", 1f);
            AssertTrue(m.HasShaderParameter("x"), "Has x.");
        }

        // ── RemoveShaderParameter ──
        public static void TestRemoveParameter()
        {
            var m = new ShaderMaterial();
            m.SetShaderParameter("x", 1f);
            m.ClearDirty();
            AssertTrue(m.RemoveShaderParameter("x"), "Removed.");
            AssertFalse(m.HasShaderParameter("x"), "Gone.");
            AssertTrue(m.IsDirty, "Remove marks dirty.");
            AssertFalse(m.RemoveShaderParameter("x"), "Second remove → false.");
        }

        // ── ClearShaderParameters ──
        public static void TestClearParameters()
        {
            var m = new ShaderMaterial();
            m.SetShaderParameter("a", 1f);
            m.SetShaderParameter("b", 2f);
            m.ClearShaderParameters();
            AssertEqual(0, m.ParameterCount, "All cleared.");
            AssertTrue(m.IsDirty, "Clear marks dirty.");
        }

        // ── Update 推进 TIME ──
        public static void TestTimeAdvance()
        {
            var m = new ShaderMaterial();
            m.Update(0.1f);
            m.Update(0.2f);
            AssertNear(0.3f, m.Time, "Time=0.3.");
        }

        // ── UpdateFrame 每帧标脏（TIME 变化需重传） ──
        public static void TestUpdateFrameMarksDirty()
        {
            var m = new ShaderMaterial();
            m.ClearDirty();
            m.UpdateFrame(0.016f);
            AssertTrue(m.IsDirty, "UpdateFrame marks dirty (TIME changed).");
            AssertNear(0.016f, m.Time, "Time advanced.");
        }

        // ── 换 Shader 触发脏标记 ──
        public static void TestChangeShaderMarksDirty()
        {
            var m = new ShaderMaterial();
            m.ClearDirty();
            var s = new Shader();
            m.Shader = s;
            AssertTrue(m.IsDirty, "Bind shader marks dirty.");

            m.ClearDirty();
            m.Shader = s; // 同一实例不触发
            AssertFalse(m.IsDirty, "Same shader → no dirty.");
        }

        // ── MarkDirty / ClearDirty 手动 ──
        public static void TestManualDirty()
        {
            var m = new ShaderMaterial();
            m.ClearDirty();
            AssertFalse(m.IsDirty, "Clean.");
            m.MarkDirty();
            AssertTrue(m.IsDirty, "MarkDirty.");
        }

        // ── null 参数抛异常（对应 Godot 不允许 null uniform 值） ──
        public static void TestNullParameterThrows()
        {
            var m = new ShaderMaterial();
            bool threw = false;
            try { m.SetShaderParameter("x", null!); }
            catch (ArgumentNullException) { threw = true; }
            AssertTrue(threw, "null value throws ArgumentNullException.");
        }
    }
}
