using ShanyuEngine.Assets;
using ShanyuEngine.Input;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Runtime
{
    public sealed class RuntimeGameLoop
    {
        private readonly RuntimeWorld _world;
        private readonly IRenderDevice _renderDevice;
        private readonly RuntimeLoopOptions _options;
        private readonly RenderGraph? _renderGraph;
        private float _accumulatorSeconds;

        public int FrameIndex { get; private set; }
        public float TotalTimeSeconds { get; private set; }
        public float AccumulatorSeconds => _accumulatorSeconds;

        /// <summary>获取当前绑定的渲染设备。</summary>
        public IRenderDevice RenderDevice => _renderDevice;

        /// <summary>获取渲染设备是否支持窗口尺寸调整。</summary>
        public bool SupportsResize => _renderDevice is IResizableRenderDevice;

        public RuntimeGameLoop(
            RuntimeWorld world,
            IRenderDevice renderDevice,
            RuntimeLoopOptions? options = null,
            RenderGraph? renderGraph = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _renderDevice = renderDevice ?? throw new ArgumentNullException(nameof(renderDevice));
            _options = options ?? new RuntimeLoopOptions();
            _renderGraph = renderGraph;
        }

        public RuntimeTickResult Tick(float deltaTime)
        {
            var safeDelta = MathF.Max(0f, deltaTime);
            _options.Input?.Update(safeDelta);
            var fixedSteps = _options.UseFixedStep
                ? RunFixedSteps(safeDelta)
                : RunVariableStep(safeDelta);

            _world.RenderScene = RenderSceneBuilder.FromObjects(_world.Objects, _world.SceneName);
            var frameStats = RenderFrame(safeDelta);
            var inputSnapshot = RuntimeInputSnapshot.FromInputSystem(_options.Input);
            FrameIndex++;

            return new RuntimeTickResult
            {
                FrameIndex = FrameIndex,
                DeltaTimeSeconds = safeDelta,
                FixedStepCount = fixedSteps,
                TotalTimeSeconds = TotalTimeSeconds,
                AccumulatorSeconds = _accumulatorSeconds,
                FrameStats = frameStats,
                Input = inputSnapshot
            };
        }

        private int RunVariableStep(float deltaTime)
        {
            UpdateComponents(deltaTime);
            TotalTimeSeconds += deltaTime;
            return deltaTime > 0f ? 1 : 0;
        }

        private int RunFixedSteps(float deltaTime)
        {
            var fixedDelta = _options.FixedDeltaTime <= 0f ? 1f / 60f : _options.FixedDeltaTime;
            var maxSteps = Math.Max(1, _options.MaxFixedStepsPerTick);
            var steps = 0;

            _accumulatorSeconds += deltaTime;
            while (_accumulatorSeconds + 0.000001f >= fixedDelta && steps < maxSteps)
            {
                UpdateComponents(fixedDelta);
                _accumulatorSeconds -= fixedDelta;
                TotalTimeSeconds += fixedDelta;
                steps++;
            }

            if (_options.DropExcessAccumulatedTime && steps == maxSteps && _accumulatorSeconds >= fixedDelta)
            {
                _accumulatorSeconds = _accumulatorSeconds % fixedDelta;
            }

            return steps;
        }

        private void UpdateComponents(float deltaTime)
        {
            foreach (var obj in _world.Objects)
            {
                if (obj == null) continue;
                foreach (var component in obj.Components.ToArray())
                {
                    if (component is IRuntimeInputComponent inputComponent && _options.Input != null)
                    {
                        inputComponent.UpdateInput(deltaTime, obj, _options.Input);
                    }
                    component?.Update(deltaTime, obj);
                }
            }
        }

        private FrameStats RenderFrame(float deltaTime)
        {
            if (_renderDevice is NullRenderDevice nullRender && _renderGraph != null)
            {
                return nullRender.Render(_world.RenderScene, _renderGraph, deltaTime);
            }

            return _renderDevice.Render(_world.RenderScene, deltaTime);
        }
    }

    public sealed class RuntimeLoopOptions
    {
        public bool UseFixedStep { get; set; }
        public float FixedDeltaTime { get; set; } = 1f / 60f;
        public int MaxFixedStepsPerTick { get; set; } = 4;
        public bool DropExcessAccumulatedTime { get; set; }
        public InputSystem? Input { get; set; }
    }

    public sealed class RuntimeTickResult
    {
        public int FrameIndex { get; set; }
        public float DeltaTimeSeconds { get; set; }
        public int FixedStepCount { get; set; }
        public float TotalTimeSeconds { get; set; }
        public float AccumulatorSeconds { get; set; }
        public FrameStats FrameStats { get; set; } = new();
        public RuntimeInputSnapshot Input { get; set; } = new();
    }

    public interface IRuntimeInputComponent
    {
        void UpdateInput(float deltaTime, SceneObject owner, InputSystem input);
    }

    public sealed class RuntimeInputSnapshot
    {
        public int PressedKeyCount { get; set; }
        public int ActiveActionCount { get; set; }
        public IReadOnlyList<string> ActiveActions { get; set; } = Array.Empty<string>();

        public static RuntimeInputSnapshot FromInputSystem(InputSystem? input)
        {
            if (input == null) return new RuntimeInputSnapshot();

            var activeActions = new List<string>();
            foreach (var context in input.GetAllContexts())
            {
                if (!context.IsActive) continue;
                foreach (var pair in context.Actions)
                {
                    if (pair.Value.IsPressed)
                    {
                        activeActions.Add($"{context.Name}/{pair.Key}");
                    }
                }
            }

            var pressedKeys = Enum.GetValues<InputKey>()
                .Count(key => key != InputKey.None && input.IsKeyDown(key));

            return new RuntimeInputSnapshot
            {
                PressedKeyCount = pressedKeys,
                ActiveActionCount = activeActions.Count,
                ActiveActions = activeActions
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }
}
