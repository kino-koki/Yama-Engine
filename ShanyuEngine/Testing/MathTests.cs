using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 数学库测试：Vector3 运算、Quaternion 旋转、Matrix4x4 变换、投影
    /// </summary>
    [TestClass]
    public class MathTests : EngineTestFixture
    {
        [TestMethod]
        public void Vector3Addition()
        {
            var a = new MockEngine.Vector3(1, 2, 3);
            var b = new MockEngine.Vector3(4, 5, 6);
            var c = a + b;
            Assert.AreEqual(5f, c.X);
            Assert.AreEqual(7f, c.Y);
            Assert.AreEqual(9f, c.Z);
        }

        [TestMethod]
        public void Vector3Dot()
        {
            var a = new MockEngine.Vector3(1, 0, 0);
            var b = new MockEngine.Vector3(0, 1, 0);
            Assert.AreApproximatelyEqual(0f, a.Dot(b), 0.0001f);
        }

        [TestMethod]
        public void Vector3Cross()
        {
            var a = new MockEngine.Vector3(1, 0, 0);
            var b = new MockEngine.Vector3(0, 1, 0);
            var c = a.Cross(b);
            Assert.AreApproximatelyEqual(0f, c.X);
            Assert.AreApproximatelyEqual(0f, c.Y);
            Assert.AreApproximatelyEqual(1f, c.Z);
        }

        [TestMethod]
        public void QuaternionIdentity()
        {
            var q = MockEngine.Quaternion.Identity;
            Assert.AreApproximatelyEqual(0f, q.X);
            Assert.AreApproximatelyEqual(0f, q.Y);
            Assert.AreApproximatelyEqual(0f, q.Z);
            Assert.AreApproximatelyEqual(1f, q.W);
        }

        [TestMethod]
        public void MatrixMultiply()
        {
            var a = MockEngine.Matrix4x4.Identity;
            var b = MockEngine.Matrix4x4.CreateTranslation(new MockEngine.Vector3(1, 2, 3));
            var c = a * b;
            Assert.AreApproximatelyEqual(1f, c.M41);
            Assert.AreApproximatelyEqual(2f, c.M42);
            Assert.AreApproximatelyEqual(3f, c.M43);
        }

        [TestMethod]
        public void Vector3Length()
        {
            var v = new MockEngine.Vector3(3, 4, 0);
            Assert.AreApproximatelyEqual(5f, v.Length(), 0.0001f);
        }
    }
}
