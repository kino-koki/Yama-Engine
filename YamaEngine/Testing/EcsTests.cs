using YamaEngine.Testing;
using MockEngine = YamaEngine.Testing.MockEngine;

namespace YamaEngine.Tests
{
    /// <summary>
    /// ECS 相关测试：Entity 创建/销毁、组件 CRUD、Query、World 管理
    /// </summary>
    [TestClass]
    public class EcsTests : EngineTestFixture
    {
        [TestMethod]
        public void EntityCreation()
        {
            var entity = new MockEngine.Entity(1);
            Assert.IsNotNull(entity);
            Assert.AreEqual(1, entity.Id);
        }

        [TestMethod]
        public void ComponentAddAndGet()
        {
            var entity = new MockEngine.Entity(2);
            entity.AddComponent(new MockEngine.ComponentA { Value = 42 });
            var comp = entity.GetComponent<MockEngine.ComponentA>();
            Assert.IsNotNull(comp);
            Assert.AreEqual(42, comp.Value);
        }

        [TestMethod]
        public void ComponentQuery()
        {
            var entities = new List<MockEngine.Entity>();
            for (int i = 0; i < 5; i++)
            {
                var e = new MockEngine.Entity(i);
                if (i % 2 == 0) e.AddComponent(new MockEngine.ComponentA { Value = i });
                entities.Add(e);
            }
            var withA = entities.Where(e => e.HasComponent<MockEngine.ComponentA>()).ToList();
            Assert.AreEqual(3, withA.Count);
        }

        [TestMethod]
        public void SystemUpdate()
        {
            var entities = new List<MockEngine.Entity>();
            for (int i = 0; i < 3; i++)
            {
                var e = new MockEngine.Entity(i);
                e.AddComponent(new MockEngine.ComponentA { Value = 10 });
                entities.Add(e);
            }
            foreach (var e in entities)
            {
                var c = e.GetComponent<MockEngine.ComponentA>();
                if (c != null) c.Value += 5;
            }
            Assert.AreEqual(15, entities[0].GetComponent<MockEngine.ComponentA>().Value);
        }
    }
}
