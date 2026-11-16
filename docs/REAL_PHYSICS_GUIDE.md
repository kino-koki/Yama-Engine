# yama真实物理引擎使用文档

## 简介

yama真实物理引擎是一个**基于牛顿力学**、追求**真实物理效果**的物理引擎，完全去除了花哨的特性，专注于：
- ✅ 可预测的物理行为
- ✅ 稳定的数值积分
- ✅ 真实的材质响应
- ✅ 高效的碰撞检测
- ✅ 完整的约束系统

## 核心特点

### 1. 真实的物理材质
```csharp
// 预定义真实材质
var ice = PhysicsMaterial.Ice;       // 冰：低摩擦
var wood = PhysicsMaterial.Wood;     // 木头：中等摩擦
var metal = PhysicsMaterial.Metal;   // 金属：高弹性
var rubber = PhysicsMaterial.Rubber; // 橡胶：高摩擦高弹性
var concrete = PhysicsMaterial.Concrete; // 混凝土：低弹性

// 自定义材质
var customMaterial = new PhysicsMaterial
{
    Name = "Custom",
    StaticFriction = 0.7f,      // 静摩擦系数
    DynamicFriction = 0.6f,     // 动摩擦系数
    Restitution = 0.3f          // 恢复系数（弹性）
};
```

### 2. 牛顿力学基础
- **刚体动力学**: 遵循牛顿第二定律 F=ma
- **角动量守恒**: 正确的转动惯量计算
- **能量守恒**: 碰撞中动量守恒
- **真实的摩擦**: 库仑摩擦模型

### 3. 稳定的数值积分
```csharp
// Semi-Implicit Euler 积分器
// 1. 先更新速度（考虑力）
body.Velocity += (body.Force / body.Mass) * deltaTime;
body.AngularVelocity += body.InverseInertiaTensor * body.Torque * deltaTime;

// 2. 再更新位置（使用新速度）
body.Position += body.Velocity * deltaTime;
body.Orientation = Integrate(body.Orientation, body.AngularVelocity, deltaTime);
```

## 快速开始

### 1. 创建物理引擎
```csharp
var settings = new PhysicsSettings
{
    Gravity = new Vector3(0, -9.81f, 0),  // 标准重力
    SolverIterations = 8,                 // 约束求解迭代次数
    EnableSleep = true,                   // 启用睡眠优化
    SleepThreshold = 0.05f,               // 睡眠速度阈值
    ContactErp = 0.2f,                    // 接触穿透修正系数
    ContactSlop = 0.01f                    // 允许的穿透深度
};

var physics = new RealPhysicsEngine(settings);
```

### 2. 创建刚体
```csharp
// 动态刚体
var bodyDesc = new RigidbodyDescription
{
    Name = "Player",
    Type = RigidbodyType.Dynamic,
    Position = new Vector3(0, 1, 0),
    Orientation = Quaternion.Identity,
    Mass = 70.0f,              // 70kg 人
    LinearDamping = 0.01f,
    AngularDamping = 0.01f,
    Material = PhysicsMaterial.Wood,
    EnableCollision = true,
    CollisionLayer = 1,         // 默认层
    CollisionMask = -1          // 与所有层碰撞
};

var body = physics.CreateRigidbody(bodyDesc);
```

### 3. 添加碰撞体
```csharp
// 盒子碰撞体
var boxColliderDesc = new ColliderDescription
{
    Type = ColliderType.Box,
    Center = Vector3.Zero,
    Size = new Vector3(1, 2, 0.5f),  // 1m宽, 2m高, 0.5m深
    IsTrigger = false,
    Material = PhysicsMaterial.Wood
};

var boxCollider = physics.CreateCollider(boxColliderDesc, body);

// 球体碰撞体
var sphereColliderDesc = new ColliderDescription
{
    Type = ColliderType.Sphere,
    Radius = 0.5f,
    Material = PhysicsMaterial.Rubber
};

var sphereCollider = physics.CreateCollider(sphereColliderDesc, body);
```

### 4. 运行物理模拟
```csharp
float deltaTime = 1.0f / 60.0f;  // 60fps

// 应用外力
body.ApplyForce(new Vector3(0, 100, 0));  // 向上施加100N力

// 应用冲量（瞬间改变速度）
body.ApplyImpulse(new Vector3(10, 0, 0));  // 瞬间获得水平速度

// 物理步进
physics.Step(deltaTime);

// 获取结果
Console.WriteLine($"位置: {body.Position}");
Console.WriteLine($"速度: {body.Velocity}");
Console.WriteLine($"角速度: {body.AngularVelocity}");
```

## 高级功能

### 1. 约束系统

#### 铰链约束
```csharp
// 创建铰链约束（门的旋转轴）
var doorHinge = physics.CreateHingeConstraint(
    bodyA: doorBody,
    bodyB: frameBody,
    anchorA: new Vector3(0, 1, 0),     // 门上的锚点
    anchorB: new Vector3(0, 1, 0),     // 框架上的锚点
    axis: new Vector3(0, 1, 0)          // Y轴为旋转轴
);

// 设置角度限制
doorHinge.SetLimits(
    minAngle: -MathF.PI / 2,    // -90度
    maxAngle: MathF.PI / 2       // +90度
);

// 设置电机驱动
doorHinge.SetMotor(
    speed: 2.0f,                 // 2弧度/秒
    maxTorque: 100.0f            // 最大扭矩100Nm
);
```

#### 弹簧约束
```csharp
// 创建弹簧约束
var spring = physics.CreateSpringConstraint(
    bodyA: anchorBody,
    bodyB: weightBody,
    anchorA: new Vector3(0, 0, 0),
    anchorB: new Vector3(0, 0, 0),
    stiffness: 100.0f,   // 刚度系数
    damping: 5.0f        // 阻尼系数
);

// 调整参数
spring.SetStiffness(200.0f);  // 更硬的弹簧
spring.SetDamping(10.0f);     // 更强的阻尼
spring.SetRestLength(2.0f);   // 静态长度2米
```

### 2. 碰撞检测

#### 射线检测
```csharp
// 检测射线与物理世界的交点
var hit = physics.Raycast(
    origin: new Vector3(0, 10, 0),
    direction: new Vector3(0, -1, 0),
    maxDistance: 20.0f
);

if (hit.HasValue)
{
    var result = hit.Value;
    Console.WriteLine($"命中: {result.Point}");
    Console.WriteLine($"法线: {result.Normal}");
    Console.WriteLine($"距离: {result.Distance}");
    Console.WriteLine($"碰撞体: {result.Collider}");
    Console.WriteLine($"刚体: {result.Rigidbody?.Name}");
}
```

### 3. 材质组合
```csharp
// 当两个物体碰撞时，自动计算最终的摩擦系数和恢复系数
var (combinedFriction, combinedRestitution) = 
    PhysicsMaterial.Combine(materialA, materialB);

// 摩擦系数使用几何平均（更符合物理现实）
// 恢复系数使用最小值（能量损失更真实）
```

## 与场景系统集成

### 1. 添加物理组件
```csharp
// 为场景对象添加物理组件
var physicsComponent = new PhysicsComponent
{
    Mass = 70.0f,
    Type = RigidbodyType.Dynamic,
    Material = PhysicsMaterial.Wood,
    MainColliderType = ColliderType.Box,
    ColliderSize = new Vector3(1, 2, 0.5f),
    EnableGravity = true
};

// 添加到场景对象
sceneObject.Components.Add(physicsComponent);

// 初始化物理组件
physicsComponent.Initialize(physicsEngine);
```

### 2. 同步物理和渲染
```csharp
// 游戏循环中
while (isRunning)
{
    float deltaTime = GetDeltaTime();
    
    // 1. 应用游戏逻辑力
    if (input.IsKeyPressed(Key.Space))
    {
        physicsComponent.ApplyForce(new Vector3(0, 1000, 0));
    }
    
    // 2. 物理模拟
    physicsEngine.Step(deltaTime);
    
    // 3. 同步位置到渲染
    physicsComponent.SyncToSceneObject(sceneObject);
    
    // 4. 渲染
    RenderScene();
}
```

## 真实物理场景示例

### 1. 车辆模拟
```csharp
// 创建车身
var carBody = physics.CreateRigidbody(new RigidbodyDescription
{
    Name = "CarBody",
    Type = RigidbodyType.Dynamic,
    Position = new Vector3(0, 0.5f, 0),
    Mass = 1500.0f,  // 1500kg 车
    Material = PhysicsMaterial.Metal
});

// 创建4个轮子（作为悬挂系统）
var wheels = new List<Rigidbody>();
for (int i = 0; i < 4; i++)
{
    var wheelDesc = new RigidbodyDescription
    {
        Name = $"Wheel{i}",
        Type = RigidbodyType.Dynamic,
        Position = new Vector3(
            (i % 2 == 0 ? -1 : 1) * 0.8f,
            0.3f,
            (i / 2 == 0 ? 1 : -1) * 1.5f
        ),
        Mass = 20.0f,
        Material = PhysicsMaterial.Rubber
    };
    
    var wheel = physics.CreateRigidbody(wheelDesc);
    wheels.Add(wheel);
    
    // 添加轮胎碰撞体
    var tireColliderDesc = new ColliderDescription
    {
        Type = ColliderType.Sphere,
        Radius = 0.3f
    };
    
    physics.CreateCollider(tireColliderDesc, wheel);
    
    // 创建悬挂弹簧
    var suspension = physics.CreateSpringConstraint(
        bodyA: carBody,
        bodyB: wheel,
        anchorA: new Vector3(wheel.Position.X - carBody.Position.X, 0, wheel.Position.Z - carBody.Position.Z),
        anchorB: Vector3.Zero,
        stiffness: 5000.0f,  // 较硬的悬挂
        damping: 500.0f
    );
}
```

### 2. 布料模拟（简化）
```csharp
// 使用网格刚体和弹簧约束模拟布料
var clothNodes = new List<Rigidbody>();
var clothSize = 5;
var spacing = 0.2f;

// 创建布料节点
for (int x = 0; x < clothSize; x++)
{
    for (int z = 0; z < clothSize; z++)
    {
        var nodeDesc = new RigidbodyDescription
        {
            Name = $"ClothNode_{x}_{z}",
            Type = RigidbodyType.Dynamic,
            Position = new Vector3((x - clothSize / 2) * spacing, 5, (z - clothSize / 2) * spacing),
            Mass = 0.1f,
            Material = PhysicsMaterial.Wood
        };
        
        var node = physics.CreateRigidbody(nodeDesc);
        
        // 固定顶部节点
        if (x == 0 || x == clothSize - 1)
        {
            node.SetStatic();
        }
        
        clothNodes.Add(node);
    }
}

// 创建弹簧连接节点
for (int x = 0; x < clothSize; x++)
{
    for (int z = 0; z < clothSize; z++)
{
    int index = x * clothSize + z;
    var nodeA = clothNodes[index];
    
    // 水平连接
    if (x < clothSize - 1)
    {
        var nodeB = clothNodes[(x + 1) * clothSize + z];
        physics.CreateSpringConstraint(nodeA, nodeB, Vector3.Zero, Vector3.Zero, 100.0f, 1.0f);
    }
    
    // 垂直连接
    if (z < clothSize - 1)
    {
        var nodeB = clothNodes[x * clothSize + (z + 1)];
        physics.CreateSpringConstraint(nodeA, nodeB, Vector3.Zero, Vector3.Zero, 100.0f, 1.0f);
    }
}
}
```

## 性能优化

### 1. 睡眠优化
```csharp
// 启用睡眠优化（默认启用）
settings.EnableSleep = true;
settings.SleepThreshold = 0.05f;  // 速度小于0.05m/s时睡眠

// 手动唤醒刚体
body.WakeUp();
```

### 2. 碰撞层过滤
```csharp
// 定义碰撞层
const int PLAYER_LAYER = 1;
const int ENEMY_LAYER = 2;
const int WALL_LAYER = 4;

// 玩家与墙壁、敌人碰撞，但不与友军碰撞
playerBody.CollisionLayer = PLAYER_LAYER;
playerBody.CollisionMask = WALL_LAYER | ENEMY_LAYER;

// 敌人与墙壁、玩家碰撞，但不与其他敌人碰撞
enemyBody.CollisionLayer = ENEMY_LAYER;
enemyBody.CollisionMask = WALL_LAYER | PLAYER_LAYER;
```

### 3. 静态物体优化
```csharp
// 地面、墙壁等不动的物体应该设置为静态
var groundDesc = new RigidbodyDescription
{
    Type = RigidbodyType.Static,  // 静态刚体不计算动力学
    Position = Vector3.Zero
};

var ground = physics.CreateRigidbody(groundDesc);
```

## 调试和验证

### 1. 运行测试
```csharp
// 运行物理引擎测试
RealPhysicsTests.RunAllTests(tempPath);

// 测试包括：
// - 自由落体（验证重力）
// - 球体碰撞（验证动量守恒）
// - 堆叠稳定性（验证约束求解）
// - 弹簧约束（验证胡克定律）
// - 摩擦力（验证库仑摩擦模型）
// - 弹性碰撞（验证恢复系数）
```

### 2. 验证物理行为
```csharp
// 验证自由落体：h = 0.5 * g * t^2
float expectedY = initialY - 0.5f * 9.81f * time * time;
float actualY = body.Position.Y;
float error = MathF.Abs(expectedY - actualY);

if (error < 0.01f)  // 1cm误差内认为正确
{
    Console.WriteLine("物理正确！");
}
```

## 最佳实践

### 1. 合理的质量设置
```csharp
// 使用真实的质量值（kg）
playerBody.Mass = 70.0f;      // 70kg 人
carBody.Mass = 1500.0f;       // 1500kg 车
bulletBody.Mass = 0.01f;      // 10g 子弹

// 避免过小或过大的质量
body.Mass = MathF.Max(0.001f, body.Mass);  // 最小1g
```

### 2. 正确的迭代次数
```csharp
// 简单场景：较少迭代次数
settings.SolverIterations = 4;

// 复杂场景：更多迭代次数
settings.SolverIterations = 12;

// 堆叠场景：更多迭代次数确保稳定性
settings.SolverIterations = 16;
```

### 3. 合理的时间步长
```csharp
// 固定时间步长（更稳定）
float fixedDt = 1.0f / 60.0f;
float accumulator = 0f;

while (isRunning)
{
    float frameDt = GetDeltaTime();
    accumulator += frameDt;
    
    while (accumulator >= fixedDt)
    {
        physics.Step(fixedDt);
        accumulator -= fixedDt;
    }
    
    RenderScene();
}
```

## 故障排除

### 常见问题

**Q: 物体穿透穿墙？**
A: 增加求解迭代次数或减小时间步长
```csharp
settings.SolverIterations = 12;
settings.ContactErp = 0.3f;  // 增加穿透修正系数
```

**Q: 物体抖动不稳定？**
A: 检查质量设置和迭代次数
```csharp
body.Mass = MathF.Max(0.1f, body.Mass);  // 避免过小质量
settings.SolverIterations = 8;            // 增加迭代次数
```

**Q: 约束松弛？**
A: 增加约束迭代次数或调整约束参数
```csharp
settings.SolverIterations = 16;  // 更多迭代
spring.SetStiffness(200.0f);      // 更硬的弹簧
```

**Q: 性能问题？**
A: 使用睡眠优化和碰撞层过滤
```csharp
settings.EnableSleep = true;
body.CollisionMask = requiredLayers;  // 只与需要的层碰撞
```

## 总结

yama真实物理引擎专注于**真实的物理效果**，通过：
- 牛顿力学基础
- 稳定的数值积分
- 真实的材质响应
- 高效的碰撞检测
- 完整的约束系统

提供了**可预测、稳定、符合物理规律**的物理模拟。

**与其他物理引擎的对比：**

| 特性 | yama真实物理引擎 | Unity PhysX | Godot |
|------|----------------|-------------|-------|
| 真实物理行为 | ✅ 专注真实 | ✅ 成熟 | ⚠️ 简化 |
| 稳定性 | ✅ 高度稳定 | ✅ 成熟 | ⚠️ 一般 |
| 材质真实性 | ✅ 真实材质 | ⚠️ 有限 | ⚠️ 基础 |
| 约束系统 | ✅ 完整 | ✅ 成熟 | ⚠️ 基础 |
| 性能 | ✅ 高效 | ✅ 成熟 | ✅ 轻量 |
| 易用性 | ✅ 简洁API | ✅ 完善编辑器 | ✅ 直观 |

yama真实物理引擎是一个**为真实物理效果而设计的物理引擎**，适合需要**精确物理模拟**的应用场景。