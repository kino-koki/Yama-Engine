using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.ECS;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// Smart Objects 智能物体系统深度测试。覆盖槽位定义/标签过滤/空间哈希查询/
    /// 排序模式/预留释放/多用户容量/校验委托/ECS 组件与系统同步。
    /// </summary>
    public static class SmartObjectsDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── 枚举 ──

        public static void TestTagValues()
        {
            AssertEqual(0, (int)SmartObjectTag.None, "None=0");
            AssertEqual(1, (int)SmartObjectTag.Seat, "Seat=1");
            AssertEqual(2, (int)SmartObjectTag.Cover, "Cover=2");
            AssertEqual(4, (int)SmartObjectTag.Sentry, "Sentry=4");
            AssertEqual(8, (int)SmartObjectTag.Work, "Work=8");
            AssertEqual(16, (int)SmartObjectTag.Sleep, "Sleep=16");
            AssertEqual(32, (int)SmartObjectTag.Vendor, "Vendor=32");
            AssertEqual(64, (int)SmartObjectTag.Crafting, "Crafting=64");
            AssertEqual(128, (int)SmartObjectTag.Healing, "Healing=128");
            AssertEqual(256, (int)SmartObjectTag.Ambush, "Ambush=256");
            AssertEqual(512, (int)SmartObjectTag.Custom, "Custom=512");
        }

        public static void TestTagFlags()
        {
            var combo = SmartObjectTag.Seat | SmartObjectTag.Cover;
            AssertTrue((combo & SmartObjectTag.Seat) != 0, "combo has seat");
            AssertTrue((combo & SmartObjectTag.Cover) != 0, "combo has cover");
            AssertFalse((combo & SmartObjectTag.Work) != 0, "combo no work");
        }

        public static void TestSlotStateValues()
        {
            AssertEqual(0, (int)SmartObjectSlotState.Free, "Free=0");
            AssertEqual(1, (int)SmartObjectSlotState.Occupied, "Occupied=1");
            AssertEqual(2, (int)SmartObjectSlotState.Reserved, "Reserved=2");
            AssertEqual(3, (int)SmartObjectSlotState.Disabled, "Disabled=3");
        }

        public static void TestQuerySortModeValues()
        {
            AssertEqual(0, (int)SmartObjectQuerySortMode.Nearest, "Nearest=0");
            AssertEqual(1, (int)SmartObjectQuerySortMode.Random, "Random=1");
            AssertEqual(2, (int)SmartObjectQuerySortMode.Priority, "Priority=2");
        }

        // ── SlotRef ──

        public static void TestSlotRefValidity()
        {
            AssertFalse(default(SlotRef).IsValid, "default invalid");
            AssertTrue(new SlotRef(3, 1).IsValid, "obj=3 valid");
            AssertFalse(new SlotRef(0, 0).IsValid, "obj=0 invalid");
            AssertEqual(SlotRef.Invalid.ObjectId, 0, "Invalid.ObjectId=0");
        }

        public static void TestSlotRefEquality()
        {
            var a = new SlotRef(5, 2);
            var b = new SlotRef(5, 2);
            var c = new SlotRef(5, 3);
            AssertTrue(a == b, "a==b");
            AssertTrue(a != c, "a!=c");
            AssertTrue(a.Equals(b), "a.Equals(b)");
            AssertFalse(a.Equals(c), "a.Equals(c) false");
            AssertEqual(a.GetHashCode(), b.GetHashCode(), "hashcode equal");
        }

        public static void TestSlotRefToString()
        {
            var r = new SlotRef(7, 4);
            string s = r.ToString();
            AssertTrue(s.Contains("Obj=7"), "contains Obj=7");
            AssertTrue(s.Contains("Idx=4"), "contains Idx=4");
        }

        // ── SmartObjectSlot 默认值 ──

        public static void TestSlotDefaults()
        {
            var s = new SmartObjectSlot();
            AssertEqual(SmartObjectTag.None, s.Tags, "default tags None");
            AssertEqual(SmartObjectSlotState.Free, s.State, "default Free");
            AssertEqual(-1, s.BoundEntityId, "default BoundEntityId=-1");
            AssertEqual(1, s.MaxUsers, "default MaxUsers=1");
            AssertEqual(0, s.UserCount, "default UserCount=0");
            AssertTrue(s.Users == null, "default Users null");
            AssertEqual(0f, s.Priority, "default Priority=0");
            AssertTrue(s.ValidationCondition == null, "default no validation");
        }

        // ── 数据库 ──

        public static void TestDbDefaults()
        {
            var db = new SmartObjectDatabase();
            Near(16f, db.CellSize, 1e-5f, "default cellSize=16");
            AssertEqual(0, db.ObjectCount, "empty");
        }

        public static void TestDbCustomCellSize()
        {
            var db = new SmartObjectDatabase(8f);
            Near(8f, db.CellSize, 1e-5f, "custom cellSize=8");
            var db2 = new SmartObjectDatabase(0.5f); // clamps to min 1
            Near(1f, db2.CellSize, 1e-5f, "min cellSize=1");
        }

        public static void TestDbRegisterAssignsId()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            int id = db.Register(entry);
            AssertTrue(id > 0, "id assigned > 0");
            AssertEqual(1, db.ObjectCount, "count=1");
            AssertEqual(id, entry.ObjectId, "entry.ObjectId set");
        }

        public static void TestDbRegisterPreservesId()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 42 };
            int id = db.Register(entry);
            AssertEqual(42, id, "id=42 preserved");
            var entry2 = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            int id2 = db.Register(entry2);
            AssertTrue(id2 > 42, "next id > 42");
        }

        public static void TestDbRegisterNullReturnsZero()
        {
            var db = new SmartObjectDatabase();
            AssertEqual(0, db.Register(null!), "null register returns 0");
        }

        public static void TestDbUnregister()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot());
            int id = db.Register(entry);
            AssertEqual(1, db.ObjectCount, "count=1");
            db.Unregister(id);
            AssertEqual(0, db.ObjectCount, "count=0 after unregister");
            AssertTrue(db.GetEntry(id) == null, "entry gone");
        }

        public static void TestDbUnregisterUnknownSafe()
        {
            var db = new SmartObjectDatabase();
            db.Unregister(9999); // should not throw
        }

        public static void TestDbGetEntryGetSlot()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            entry.Slots.Add(new SmartObjectSlot());
            int id = db.Register(entry);
            AssertTrue(db.GetEntry(id) != null, "entry found");
            AssertTrue(db.GetSlot(new SlotRef(id, 0)) != null, "slot0 found");
            AssertTrue(db.GetSlot(new SlotRef(id, 1)) != null, "slot1 found");
            AssertTrue(db.GetSlot(new SlotRef(id, 99)) == null, "slot99 not found");
            AssertTrue(db.GetSlot(new SlotRef(9999, 0)) == null, "unknown obj not found");
        }

        public static void TestDbEnumerateObjectIds()
        {
            var db = new SmartObjectDatabase();
            db.Register(new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } });
            db.Register(new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } });
            int count = 0;
            foreach (var id in db.EnumerateObjectIds()) count++;
            AssertEqual(2, count, "enumerate 2 ids");
        }

        // ── 世界变换重算 ──

        public static void TestSlotWorldTransform()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot
            {
                LocalPosition = new Vector3(2, 0, 0)
            };
            var entry = new SmartObjectEntry
            {
                ObjectId = 0,
                Transform = new TransformComponent
                {
                    Position = new Vector3(10, 0, 5),
                    Rotation = Quaternion.Identity,
                    Scale = Vector3.One
                }
            };
            entry.Slots.Add(slot);
            db.Register(entry);
            // Identity rotation: world = owner + local
            Near(12f, slot.WorldPosition.X, 1e-4f, "worldX=10+2");
            Near(5f, slot.WorldPosition.Z, 1e-4f, "worldZ=5");
        }

        public static void TestSlotWorldTransformWithScale()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(2, 0, 0) };
            var entry = new SmartObjectEntry
            {
                ObjectId = 0,
                Transform = new TransformComponent
                {
                    Position = Vector3.Zero,
                    Rotation = Quaternion.Identity,
                    Scale = new Vector3(3, 3, 3)
                }
            };
            entry.Slots.Add(slot);
            db.Register(entry);
            Near(6f, slot.WorldPosition.X, 1e-4f, "worldX=2*3");
        }

        public static void TestSlotWorldTransformWithRotation()
        {
            var db = new SmartObjectDatabase();
            // 90° around Y: local +X -> world -Z (in Unity-style, but let's verify rotation applied)
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0) };
            var entry = new SmartObjectEntry
            {
                ObjectId = 0,
                Transform = new TransformComponent
                {
                    Position = Vector3.Zero,
                    Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f),
                    Scale = Vector3.One
                }
            };
            entry.Slots.Add(slot);
            db.Register(entry);
            // Y-rotation by +90° maps +X to -Z (in Unity convention Forward=-Z)
            Near(-1f, slot.WorldPosition.Z, 1e-4f, "worldZ ~ -1 (rotated +X to -Z)");
            Near(0f, slot.WorldPosition.X, 1e-4f, "worldX ~ 0");
        }

        // ── 预留 / 释放 ──

        public static void TestReserveRelease()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot());
            int id = db.Register(entry);
            var sr = new SlotRef(id, 0);
            AssertTrue(db.Reserve(sr, 10), "reserve");
            var slot = db.GetSlot(sr);
            AssertEqual(SmartObjectSlotState.Reserved, slot!.State, "state=Reserved");
            AssertEqual(10, slot.BoundEntityId, "bound=10");
            AssertEqual(1, slot.UserCount, "userCount=1");
            AssertTrue(db.Release(sr, 10), "release");
            AssertEqual(SmartObjectSlotState.Free, slot.State, "state=Free after release");
            AssertEqual(-1, slot.BoundEntityId, "bound reset");
            AssertEqual(0, slot.UserCount, "userCount=0");
        }

        public static void TestReserveDisabledFails()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot { State = SmartObjectSlotState.Disabled });
            int id = db.Register(entry);
            AssertFalse(db.Reserve(new SlotRef(id, 0), 1), "reserve disabled fails");
        }

        public static void TestReserveWrongUserReleaseFails()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot());
            int id = db.Register(entry);
            var sr = new SlotRef(id, 0);
            db.Reserve(sr, 5);
            AssertFalse(db.Release(sr, 99), "wrong user release fails");
            // correct user still can release
            AssertTrue(db.Release(sr, 5), "correct user release ok");
        }

        public static void TestReserveUnknownSlotFails()
        {
            var db = new SmartObjectDatabase();
            AssertFalse(db.Reserve(new SlotRef(9999, 0), 1), "unknown slot fails");
        }

        public static void TestMultiUserSlot()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot { MaxUsers = 3 });
            int id = db.Register(entry);
            var sr = new SlotRef(id, 0);
            AssertTrue(db.Reserve(sr, 10), "user 10 reserve");
            AssertTrue(db.Reserve(sr, 20), "user 20 reserve");
            AssertFalse(db.Reserve(sr, 20), "duplicate reserve fails");
            var slot = db.GetSlot(sr);
            AssertEqual(2, slot!.UserCount, "userCount=2");
            AssertTrue(slot.Users!.Contains(10), "users has 10");
            AssertTrue(slot.Users!.Contains(20), "users has 20");
            AssertEqual(10, slot.BoundEntityId, "first user is bound");
            // Release one user -> still reserved
            db.Release(sr, 10);
            AssertEqual(1, slot.UserCount, "userCount=1 after release");
            AssertEqual(20, slot.BoundEntityId, "bound updated to remaining user");
            AssertTrue(db.Release(sr, 20), "release last user");
            AssertEqual(SmartObjectSlotState.Free, slot.State, "state back to Free");
        }

        // ── 查询 ──

        public static void TestQueryEmptyDb()
        {
            var db = new SmartObjectDatabase();
            var results = db.QuerySlots(new SmartObjectSlotQuery { RequiredTags = SmartObjectTag.Seat },
                Vector3.Zero);
            AssertEqual(0, results.Count, "empty db");
        }

        public static void TestQueryReturnsMatch()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(5, 0, 0), Tags = SmartObjectTag.Seat };
            var entry = new SmartObjectEntry { ObjectId = 0, Tags = SmartObjectTag.None, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            int id = db.Register(entry);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(1, results.Count, "one match");
            AssertEqual(id, results[0].ObjectId, "object id match");
            Near(5f, results[0].Distance, 1e-4f, "distance=5");
            AssertTrue(results[0].IsValid, "result valid");
        }

        public static void TestQueryTagUnion()
        {
            // Slot tags + Object tags combine via OR
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0), Tags = SmartObjectTag.None };
            var entry = new SmartObjectEntry { ObjectId = 0, Tags = SmartObjectTag.Seat, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            db.Register(entry);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(1, results.Count, "tag union match (obj tag only)");
        }

        public static void TestQueryExcludedTags()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0), Tags = SmartObjectTag.Seat | SmartObjectTag.Vendor };
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            db.Register(entry);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                ExcludedTags = SmartObjectTag.Vendor,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, results.Count, "excluded tag filters out");
        }

        public static void TestQueryDistanceFilter()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(20, 0, 0), Tags = SmartObjectTag.Seat };
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            db.Register(entry);
            // Max=10, slot at X=20 -> filtered out
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxDistance = 10f,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, results.Count, "too far");
        }

        public static void TestQueryOccupiedFilteredOut()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0), Tags = SmartObjectTag.Seat });
            int id = db.Register(entry);
            var sr = new SlotRef(id, 0);
            db.Reserve(sr, 1);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, results.Count, "occupied slot not queryable");
        }

        public static void TestQueryNearestSort()
        {
            var db = new SmartObjectDatabase();
            var e1 = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            e1.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(20, 0, 0), Tags = SmartObjectTag.Seat });
            int id1 = db.Register(e1);
            var e2 = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            e2.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(5, 0, 0), Tags = SmartObjectTag.Seat });
            int id2 = db.Register(e2);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                SortMode = SmartObjectQuerySortMode.Nearest,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(2, results.Count, "two results");
            AssertEqual(id2, results[0].ObjectId, "nearest first (id2 at X=5)");
        }

        public static void TestQueryPrioritySort()
        {
            var db = new SmartObjectDatabase();
            var e1 = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            e1.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(5, 0, 0), Tags = SmartObjectTag.Seat, Priority = 1f });
            int id1 = db.Register(e1);
            var e2 = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            e2.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(20, 0, 0), Tags = SmartObjectTag.Seat, Priority = 10f });
            int id2 = db.Register(e2);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                SortMode = SmartObjectQuerySortMode.Priority,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(2, results.Count, "two results");
            AssertEqual(id2, results[0].ObjectId, "higher priority first (id2 P=10)");
        }

        public static void TestQueryMaxResultsTruncation()
        {
            var db = new SmartObjectDatabase();
            for (int i = 0; i < 5; i++)
            {
                var e = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
                e.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(i + 1, 0, 0), Tags = SmartObjectTag.Seat });
                db.Register(e);
            }
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 2
            }, Vector3.Zero);
            AssertEqual(2, results.Count, "truncated to 2");
        }

        public static void TestQueryValidationCondition()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry
            {
                ObjectId = 0,
                ValidationCondition = (userId) => userId == 5 // only user 5 allowed
            };
            entry.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0), Tags = SmartObjectTag.Seat });
            db.Register(entry);
            // Querier 99 fails validation
            var r99 = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                QuerierEntityId = 99,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, r99.Count, "querier 99 rejected");
            // Querier 5 passes
            var r5 = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                QuerierEntityId = 5,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(1, r5.Count, "querier 5 accepted");
        }

        public static void TestQuerySlotLevelValidation()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot
            {
                LocalPosition = new Vector3(1, 0, 0),
                Tags = SmartObjectTag.Seat,
                ValidationCondition = (userId) => userId != 3 // user 3 rejected
            });
            db.Register(entry);
            var r3 = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                QuerierEntityId = 3,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, r3.Count, "slot rejects user 3");
            var r4 = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                QuerierEntityId = 4,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(1, r4.Count, "slot accepts user 4");
        }

        public static void TestQueryDisabledEntry()
        {
            var db = new SmartObjectDatabase();
            var entry = new SmartObjectEntry { ObjectId = 0, Enabled = false, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0), Tags = SmartObjectTag.Seat });
            db.Register(entry);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(0, results.Count, "disabled entry not queryable");
        }

        public static void TestQueryResultFields()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(3, 0, 4), Tags = SmartObjectTag.Seat };
            var entry = new SmartObjectEntry { ObjectId = 0, Transform = new TransformComponent { Scale = Vector3.One, Rotation = Quaternion.Identity } };
            entry.Slots.Add(slot);
            int id = db.Register(entry);
            var results = db.QuerySlots(new SmartObjectSlotQuery
            {
                RequiredTags = SmartObjectTag.Seat,
                MaxResults = 10
            }, Vector3.Zero);
            AssertEqual(1, results.Count, "1 result");
            AssertEqual(id, results[0].ObjectId, "object id");
            AssertEqual(0, results[0].Slot.SlotIndex, "slot index 0");
            Near(5f, results[0].Distance, 1e-4f, "distance = sqrt(9+16) = 5");
            Near(3f, results[0].WorldPosition.X, 1e-4f, "worldX=3");
            Near(4f, results[0].WorldPosition.Z, 1e-4f, "worldZ=4");
        }

        // ── UpdateTransform ──

        public static void TestUpdateTransformRecalculatesWorld()
        {
            var db = new SmartObjectDatabase();
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(1, 0, 0) };
            var entry = new SmartObjectEntry
            {
                ObjectId = 0,
                Transform = new TransformComponent { Position = Vector3.Zero, Scale = Vector3.One, Rotation = Quaternion.Identity }
            };
            entry.Slots.Add(slot);
            int id = db.Register(entry);
            Near(1f, slot.WorldPosition.X, 1e-4f, "initial worldX=1");
            // Move owner to (10, 0, 0)
            db.UpdateTransform(id, new TransformComponent { Position = new Vector3(10, 0, 0), Scale = Vector3.One, Rotation = Quaternion.Identity });
            Near(11f, slot.WorldPosition.X, 1e-4f, "after move worldX=11");
        }

        public static void TestUpdateTransformUnknownSafe()
        {
            var db = new SmartObjectDatabase();
            db.UpdateTransform(9999, new TransformComponent()); // should not throw
        }

        // ── ECS 集成 ──

        public static void TestEcsComponents()
        {
            var comp = new SmartObjectComponent { ObjectId = 5, Tags = SmartObjectTag.Seat, Enabled = true };
            AssertEqual(5, comp.ObjectId, "ObjectId=5");
            AssertEqual(SmartObjectTag.Seat, comp.Tags, "Tags=Seat");
            AssertTrue(comp.Enabled, "enabled");

            var user = new SmartObjectUserComponent
            {
                OccupiedSlot = new SlotRef(7, 2),
                IsInteracting = true
            };
            AssertTrue(user.IsOccupied, "IsOccupied when slot valid");
            AssertTrue(user.IsInteracting, "interacting");
            user.OccupiedSlot = default;
            AssertFalse(user.IsOccupied, "not occupied when slot invalid");
        }

        public static void TestEcsSystemDefaults()
        {
            var sys = new SmartObjectsSystem();
            AssertEqual("SmartObjectsSystem", sys.Name, "system name");
            AssertTrue(sys.Database != null, "database created");
            AssertEqual(0, sys.Database.ObjectCount, "empty db");
            // Active is set by constructor
            AssertTrue(SmartObjectDatabase.Active == sys.Database, "Active set");
        }

        public static void TestEcsSystemRegisterObject()
        {
            var ecs = new EntityComponentSystem();
            var sys = new SmartObjectsSystem();
            int entityId = ecs.CreateEntity("bench", new Vector3(5, 0, 0));
            AssertTrue(entityId > 0, "entity created");
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(0, 0, 0), Tags = SmartObjectTag.Seat };
            int objId = sys.RegisterObject(ecs, entityId, SmartObjectTag.Seat, new List<SmartObjectSlot> { slot });
            AssertTrue(objId > 0, "object registered");
            AssertEqual(1, sys.Database.ObjectCount, "db has 1 object");
            // ECS component added
            AssertTrue(ecs.HasComponent<SmartObjectComponent>(entityId), "SmartObjectComponent added");
            var comp = ecs.GetComponent<SmartObjectComponent>(entityId);
            AssertEqual(objId, comp.ObjectId, "component ObjectId matches");
        }

        public static void TestEcsSystemRegisterObjectNoEntity()
        {
            var ecs = new EntityComponentSystem();
            var sys = new SmartObjectsSystem();
            // Non-existent entity -> returns 0
            AssertEqual(0, sys.RegisterObject(ecs, 9999, SmartObjectTag.Seat), "no entity -> 0");
        }

        public static void TestEcsSystemUpdateSyncsTransform()
        {
            var ecs = new EntityComponentSystem();
            var sys = new SmartObjectsSystem();
            int entityId = ecs.CreateEntity("bench", new Vector3(0, 0, 0));
            var slot = new SmartObjectSlot { LocalPosition = new Vector3(2, 0, 0), Tags = SmartObjectTag.Seat };
            int objId = sys.RegisterObject(ecs, entityId, SmartObjectTag.Seat, new List<SmartObjectSlot> { slot });
            Near(2f, slot.WorldPosition.X, 1e-4f, "initial worldX=2");

            // Move entity
            var tf = ecs.GetComponent<TransformComponent>(entityId);
            tf.Position = new Vector3(10, 0, 0);
            ecs.SetComponent(entityId, tf);

            // Update should sync
            sys.Update(0.016f, ecs);
            Near(12f, slot.WorldPosition.X, 1e-4f, "after update worldX=12");
        }

        public static void TestEcsSystemUpdateCleansDeadObject()
        {
            var ecs = new EntityComponentSystem();
            var sys = new SmartObjectsSystem();
            int entityId = ecs.CreateEntity("bench", new Vector3(0, 0, 0));
            var slot = new SmartObjectSlot { LocalPosition = Vector3.Zero, Tags = SmartObjectTag.Seat };
            sys.RegisterObject(ecs, entityId, SmartObjectTag.Seat, new List<SmartObjectSlot> { slot });
            AssertEqual(1, sys.Database.ObjectCount, "db=1");

            // Remove entity (simulating destroy)
            ecs.DestroyEntity(entityId);
            sys.Update(0.016f, ecs);
            AssertEqual(0, sys.Database.ObjectCount, "dead object unregistered");
        }

        public static void TestEcsSystemUpdateCleansDeadUser()
        {
            var ecs = new EntityComponentSystem();
            var sys = new SmartObjectsSystem();
            // Register object
            int objEntity = ecs.CreateEntity("bench", new Vector3(0, 0, 0));
            var slot = new SmartObjectSlot { LocalPosition = Vector3.Zero, Tags = SmartObjectTag.Seat };
            int objId = sys.RegisterObject(ecs, objEntity, SmartObjectTag.Seat, new List<SmartObjectSlot> { slot });

            // Create AI user entity
            int userId = ecs.CreateEntity("ai", new Vector3(1, 0, 0));
            var sr = new SlotRef(objId, 0);
            AssertTrue(sys.Database.Reserve(sr, userId), "user reserves slot");
            AssertEqual(SmartObjectSlotState.Reserved, sys.Database.GetSlot(sr)!.State, "slot reserved");

            // Destroy the AI user
            ecs.DestroyEntity(userId);
            sys.Update(0.016f, ecs);

            // Slot should be auto-released
            AssertEqual(SmartObjectSlotState.Free, sys.Database.GetSlot(sr)!.State, "slot released after user death");
        }

        // ── Interaction 三阶段接口 ──

        public static void TestInteractionInterface()
        {
            // Smoke test: verify ISmartObjectInteraction interface and base class exist
            ISmartObjectInteraction interaction = new SitInteraction();
            AssertTrue(interaction != null, "SitInteraction implements interface");
            // SmartObjectInteractionBase abstract check via cast
            AssertTrue(interaction is SmartObjectInteractionBase, "SitInteraction extends base");
        }

        public static void TestSitInteractionDefaults()
        {
            var sit = new SitInteraction();
            Near(3.5f, sit.MoveSpeed, 1e-5f, "MoveSpeed=3.5");
            Near(0.4f, sit.AcceptanceRadius, 1e-5f, "AcceptanceRadius=0.4");
            Near(8f, sit.TurnSpeed, 1e-5f, "TurnSpeed=8");
            Near(0.6f, sit.SitDownTime, 1e-5f, "SitDownTime=0.6");
            Near(5f, sit.SitDuration, 1e-5f, "SitDuration=5");
            Near(0.5f, sit.StandUpTime, 1e-5f, "StandUpTime=0.5");
        }

        public static void TestCoverInteractionExists()
        {
            var cover = new CoverInteraction();
            AssertTrue(cover != null, "CoverInteraction instantiable");
        }

        public static void TestPatrolSentryInteractionExists()
        {
            var sentry = new PatrolSentryInteraction();
            AssertTrue(sentry != null, "PatrolSentryInteraction instantiable");
        }
    }
}
