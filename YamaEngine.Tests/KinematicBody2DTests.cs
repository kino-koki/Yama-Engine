// KinematicBody2DTests.cs —— 追平 Godot 3 的 KinematicBody2D + move_and_slide
// 覆盖：自由移动、落地 is_on_floor、撞墙 is_on_wall、撞天花板、轴分离滑动、碰撞信息、层掩码。

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class KinematicBody2DTests
    {
        private const float Tol = 1e-3f;

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol = Tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }

        // ── 自由移动：无障碍时按速度 × dt 移动 ──
        public static void TestFreeMove()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var body = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(5f),
                Position = new Vector2(0, 0),
            };
            world.AddBody(body);

            var remaining = body.MoveAndSlide(new Vector2(60f, 0f), world); // 60 px/s × 1/60s = 1 px
            AssertNear(1f, body.Position.X, "Free move X advances by v*dt.", 0.05f);
            AssertNear(0f, body.Position.Y, "Free move Y unchanged.", 0.05f);
            AssertNear(60f, remaining.X, "Remaining velocity unchanged (no collision).", 0.05f);
            AssertEqual(0, body.GetSlideCount, "No slide collisions.");
            AssertFalse(body.IsOnFloor, "Not on floor in free move.");
        }

        // ── 落地：向下移动撞到地板 StaticBody2D → is_on_floor，Y 速度归零 ──
        public static void TestLandsOnFloor()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
            };
            // 地板中心 (0,20) hh=10 → 上表面 y=10。玩家 (0,0) r10 → 下沿 y=10。
            // 起点距离=20（刚好不重叠），一步 velocity=1000*1/60≈16.7 → 落到 (0,16.7) 距离3.3<20 重叠。
            var floor = new StaticBody2D("Floor")
            {
                Shape = new RectangleShape2D(100f, 10f),
                Position = new Vector2(0, 20f),
            };
            world.AddBody(player);
            world.AddBody(floor);

            var remaining = player.MoveAndSlide(new Vector2(0f, 1000f), world);

            AssertTrue(player.IsOnFloor, "Lands on floor -> is_on_floor true.");
            AssertNear(0f, remaining.Y, "Y velocity zeroed on floor.", 0.05f);
            AssertEqual(1, player.GetSlideCount, "One slide collision (floor).");
            var col = player.GetSlideCollision(0);
            AssertTrue(col != null, "Slide collision exists.");
            AssertEqual("Floor", col!.Collider.Name, "Collided with Floor.");
            AssertEqual(CollisionNormal.Floor, col.Classification, "Classified as Floor.");
        }

        // ── 撞墙：向右移动撞到墙 → is_on_wall，X 速度归零（滑动） ──
        public static void TestHitsWall()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
            };
            // 墙中心 (20,0) hw=10。起点距离 X=20（不重叠），一步 dx≈16.7 → (16.7,0) 距离 3.3<20 重叠。
            var wall = new StaticBody2D("Wall")
            {
                Shape = new RectangleShape2D(10f, 100f),
                Position = new Vector2(20f, 0),
            };
            world.AddBody(player);
            world.AddBody(wall);

            var remaining = player.MoveAndSlide(new Vector2(1000f, 50f), world);

            AssertTrue(player.IsOnWall, "Hits wall -> is_on_wall true.");
            AssertNear(0f, remaining.X, "X velocity zeroed on wall.", 0.05f);
            AssertNear(50f, remaining.Y, "Y velocity preserved (slide along wall).", 0.05f);
            AssertNear(0.83f, player.Position.Y, "Y advances along wall (50/60≈0.83).", 0.05f);
            var col = player.GetSlideCollision(0);
            AssertEqual(CollisionNormal.Wall, col!.Classification, "Classified as Wall.");
        }

        // ── 撞天花板：向上移动 → is_on_ceiling ──
        public static void TestHitsCeiling()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
            };
            // 天花板中心 (0,-20) hh=10。起点距离 Y=20（不重叠），一步 dy≈-16.7 → 重叠。
            var ceil = new StaticBody2D("Ceiling")
            {
                Shape = new RectangleShape2D(100f, 10f),
                Position = new Vector2(0, -20f),
            };
            world.AddBody(player);
            world.AddBody(ceil);

            var remaining = player.MoveAndSlide(new Vector2(0f, -1000f), world);

            AssertTrue(player.IsOnCeiling, "Hits ceiling -> is_on_ceiling true.");
            AssertNear(0f, remaining.Y, "Y velocity zeroed on ceiling.", 0.05f);
            var col = player.GetSlideCollision(0);
            AssertEqual(CollisionNormal.Ceiling, col!.Classification, "Classified as Ceiling.");
        }

        // ── 轴分离：X 被墙挡、Y 空闲 → 沿墙滑动（Y 仍前进） ──
        public static void TestSlideAlongWall()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
            };
            // 墙在 X 方向挡住，Y 空闲 → 沿墙滑动
            var wall = new StaticBody2D("Wall")
            {
                Shape = new RectangleShape2D(10f, 100f),
                Position = new Vector2(20f, 0),
            };
            world.AddBody(player);
            world.AddBody(wall);

            var beforeY = player.Position.Y;
            player.MoveAndSlide(new Vector2(1000f, 1000f), world);

            // X 被墙挡住（不前进），Y 继续走
            AssertNear(0f, player.Position.X, "X blocked by wall (no penetration).", 0.05f);
            AssertTrue(player.Position.Y > beforeY, "Y advances along wall (sliding).");
        }

        // ── 碰撞层掩码：mask 不命中则穿过 ──
        public static void TestCollisionMaskFilter()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
                CollisionMask = 0b10, // 只检测层 2
            };
            // 墙在层 1（bit0）
            var wall = new StaticBody2D("Wall")
            {
                Shape = new RectangleShape2D(10f, 100f),
                Position = new Vector2(20f, 0),
                CollisionLayer = 0b01,
            };
            world.AddBody(player);
            world.AddBody(wall);

            var remaining = player.MoveAndSlide(new Vector2(1000f, 0f), world);

            AssertFalse(player.IsOnWall, "Wall on layer 1 not detected (mask=layer2).");
            AssertNear(16.7f, player.Position.X, "Passes through wall (mask mismatch).", 0.5f);
            AssertNear(1000f, remaining.X, "Velocity preserved.", 0.5f);
        }

        // ── 碰撞层命中：mask 命中则检测到 ──
        public static void TestCollisionMaskHit()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
                CollisionMask = 0b10, // 检测层 2
            };
            var wall = new StaticBody2D("Wall")
            {
                Shape = new RectangleShape2D(10f, 100f),
                Position = new Vector2(20f, 0),
                CollisionLayer = 0b10, // 在层 2
            };
            world.AddBody(player);
            world.AddBody(wall);

            player.MoveAndSlide(new Vector2(1000f, 0f), world);

            AssertTrue(player.IsOnWall, "Wall on layer 2 detected (mask=layer2).");
            AssertNear(0f, player.Position.X, "X blocked by matching-layer wall.", 0.05f);
        }

        // ── 无形状的区域不参与物理投射 ──
        public static void TestNoShapePassesThrough()
        {
            var world = new PhysicsWorld { TimeStep = 1f / 60f };
            var player = new KinematicBody2D("P")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0, 0),
            };
            var ghost = new StaticBody2D("Ghost")
            {
                Shape = null, // 无碰撞形状
                Position = new Vector2(20f, 0),
            };
            world.AddBody(player);
            world.AddBody(ghost);

            player.MoveAndSlide(new Vector2(1000f, 0f), world);

            AssertFalse(player.IsOnWall, "No shape -> no collision.");
            AssertNear(16.7f, player.Position.X, "Passes through shapeless body.", 0.5f);
        }

        // ── FloorNormal 默认 (0,-1)：屏幕坐标 Y 向下 ──
        public static void TestFloorNormalDefault()
        {
            var body = new KinematicBody2D("P");
            AssertNear(0f, body.FloorNormal.X, "FloorNormal.X=0.");
            AssertNear(-1f, body.FloorNormal.Y, "FloorNormal.Y=-1 (screen up).");
        }
    }
}
