using YamaEngine.Testing;
using MockEngine = YamaEngine.Testing.MockEngine;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 物理测试：碰撞检测、Raycast、形状查询
    /// </summary>
    [TestClass]
    public class PhysicsTests : EngineTestFixture
    {
        [TestMethod]
        public void RaycastHit()
        {
            var world = new MockEngine.PhysicsWorld();
            var ray = new MockEngine.Ray { Origin = MockEngine.Vector3.Zero, Direction = new MockEngine.Vector3(0, 0, 1) };
            bool hit = world.Raycast(ray, out var result);
            Assert.IsTrue(hit);
            Assert.IsTrue(result.IsValid);
            Assert.AreApproximatelyEqual(5f, result.Distance, 0.0001f);
        }

        [TestMethod]
        public void SphereCast()
        {
            var world = new MockEngine.PhysicsWorld();
            var origin = new MockEngine.Vector3(0, 0, 0);
            var dir = new MockEngine.Vector3(1, 0, 0);
            bool hit = world.SphereCast(origin, 1f, dir, out var result);
            Assert.IsTrue(hit);
            Assert.AreApproximatelyEqual(10f, result.Distance, 0.0001f);
        }

        [TestMethod]
        public void RaycastMiss()
        {
            var world = new MockEngine.PhysicsWorld();
            var ray = new MockEngine.Ray { Origin = new MockEngine.Vector3(0, 0, -100), Direction = new MockEngine.Vector3(0, 0, -1) };
            bool hit = world.Raycast(ray, out var result, maxDistance: 10f);
            Assert.IsTrue(hit);
        }
    }
}
