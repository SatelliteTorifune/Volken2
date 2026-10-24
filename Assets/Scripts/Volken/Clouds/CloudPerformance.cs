using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Assets.Scripts;
using ModApi;
using UnityEngine;
using UnityEngine.Profiling;

namespace Volken.Clouds
{
    // 手动 A/B 只覆盖相邻点太阳照明复用，不改预设；帧率是整帧数据，不是 GPU 计时。
    internal sealed class CloudLightReuseComparison : MonoBehaviour
    {
        private static CloudLightReuseComparison _instance;
        internal static bool? EnabledOverride => _instance != null ? _instance._reuse : (bool?)null;
        private bool _reuse;
        private VolumetricClouds _clouds;
        private string _planet;
        private Camera _camera;
        private int _renderedFrame = -10, _volume, _enabled, _orbit, _frames;
        private float _warmUntil;
        private double _elapsed, _meanMs;
        private int _contextHash;
        private bool _hasContext;

        internal static void SetMode(bool? enabled)
        {
            CloudPerformanceSweep.Cancel("manual-ui-comparison");
            CloudPerformance.CaptureForBenchmark(0, "", null);
            if (!enabled.HasValue) { Restore(); return; }
            if (_instance == null)
            {
                var go = new GameObject("VolkenCloudLightReuseComparison");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<CloudLightReuseComparison>();
            }
            var owner = _instance;
            owner._reuse = enabled.Value;
            owner._clouds = VolumetricClouds.Instance;
            owner._planet = owner._clouds?.CurrentPlanetName;
            owner._camera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
            owner._renderedFrame = -10;
            owner._volume = owner._enabled = owner._orbit = 0;
            owner._hasContext = false;
            owner.ResetTiming();
            Refresh();
        }

        internal static void Restore()
        {
            if (_instance == null) return;
            var owner = _instance;
            _instance = null;
            Refresh();
            Destroy(owner.gameObject);
        }

        private static void Refresh()
        {
            var clouds = VolumetricClouds.Instance;
            if (clouds != null)
                foreach (var layer in clouds.layers) layer?.SetStaticShaderProperties();
        }

        private void ResetTiming()
        {
            _warmUntil = Time.realtimeSinceStartup + 2f;
            _elapsed = _meanMs = 0;
            _frames = 0;
        }

        private void Update()
        {
            if (!Game.InFlightScene || VolumetricClouds.Instance != _clouds || _clouds == null ||
                _clouds.CurrentPlanetName != _planet)
            { Restore(); return; }
            var camera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
            if (_camera != camera)
            {
                _camera = camera;
                _renderedFrame = -10;
                ResetTiming();
            }
            if (!Application.isFocused || Volken.Weather.GamePause.IsPaused ||
                Time.frameCount - _renderedFrame > 1 || _volume == 0)
            { ResetTiming(); return; }
            if (Time.realtimeSinceStartup < _warmUntil) return;
            float seconds = Time.unscaledDeltaTime;
            if (!(seconds > 0) || float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
            _elapsed += seconds;
            _frames++;
            if (_elapsed < 2) return;
            _meanMs = _elapsed * 1000 / _frames;
            _elapsed = 0;
            _frames = 0;
        }

        internal static void BeginFrame(Camera camera)
        {
            var owner = _instance;
            if (owner == null || camera == null || camera != owner._camera) return;
            owner._renderedFrame = Time.frameCount;
            owner._volume = owner._enabled = owner._orbit = 0;
        }

        internal static void Prepared(Camera camera, Material material, bool orbit)
        {
            var owner = _instance;
            if (owner == null || camera == null || camera != owner._camera) return;
            if (orbit) { owner._orbit++; return; }
            owner._volume++;
            if (material != null && material.IsKeywordEnabled(CloudLightReuse.Keyword)) owner._enabled++;
        }

        internal static void Observe(Camera camera, RenderTexture source, List<CloudLayer> layers)
        {
            var owner = _instance;
            if (owner == null || camera == null || camera != owner._camera) return;
            int hash = source.width * 397 ^ source.height ^ camera.fieldOfView.GetHashCode() ^ layers.Count;
            foreach (var layer in layers)
                hash = unchecked(hash * 397 ^ layer.ShaderRevision ^ layer.config.GetHashCode() ^
                    layer.config.resolutionScale.GetHashCode());
            if (!owner._hasContext || hash != owner._contextHash)
            {
                owner._hasContext = true;
                owner._contextHash = hash;
                owner.ResetTiming();
            }
        }

        internal static void AbortFrame(Camera camera)
        {
            var owner = _instance;
            if (owner == null || camera == null || camera != owner._camera) return;
            owner._renderedFrame = -10;
            owner._volume = owner._enabled = owner._orbit = 0;
            owner.ResetTiming();
        }

        internal static string ModeText => Locale.GetString(!EnabledOverride.HasValue ? "Volken.UI.LightReuseComparePreset" :
            EnabledOverride.Value ? "Volken.UI.LightReuseCompareReuse" : "Volken.UI.LightReuseCompareDirect");

        internal static string StateText
        {
            get
            {
                var owner = _instance;
                if (owner == null) return Locale.GetString("Volken.UI.LightReuseComparePreset");
                if (Time.frameCount - owner._renderedFrame > 1)
                    return Locale.GetString("Volken.UI.LightReuseCompareWaiting");
                return string.Format(Locale.GetString("Volken.UI.LightReuseCompareState"), owner._enabled,
                    owner._volume - owner._enabled, owner._orbit);
            }
        }

        internal static string TimingText => _instance == null || _instance._meanMs <= 0
            ? Locale.GetString("Volken.UI.LightReuseCompareWaiting")
            : string.Format(Locale.GetString("Volken.UI.LightReuseCompareTiming"), 1000 / _instance._meanMs, _instance._meanMs);

        private void OnApplicationFocus(bool focused) => ResetTiming();
        private void OnApplicationPause(bool paused) => ResetTiming();

        private void OnDisable()
        {
            if (_instance != this) return;
            _instance = null;
            Refresh();
        }
    }

    // 按需记录主视图；CPU 提交与延迟 GPU 时间分开，不强制开启全局 Profiler。
    internal sealed class CloudPerformance : MonoBehaviour
    {
        internal enum Stage { Total, Depth, Prepare, Lightning, Trace, Orbit, Upscale, Fog, Composite, Count }
        private static CloudPerformance _instance;
        internal static bool FastPathsEnabled { get; private set; } = true;
        private readonly CustomSampler[] _samplers = new CustomSampler[(int)Stage.Count];
        private readonly Recorder[] _recorders = new Recorder[(int)Stage.Count];
        private readonly double[] _cpu = new double[(int)Stage.Count];
        private readonly Stats[] _cpuStats = new Stats[(int)Stage.Count];
        private readonly Stats[] _gpuStats = new Stats[(int)Stage.Count];
        private readonly Stats _frames = new Stats(), _main = new Stats(), _render = new Stats(), _gpu = new Stats();
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private float _start, _end, _nextReport;
        private ulong _lastTiming;
        private int _lastFrame = -1, _contextHash;
        private bool _hasContext, _mixed, _frameTiming;
        private string _label = "manual";
        private Action<Result> _completed;

        internal struct Result
        {
            internal string Reason;
            internal double Mean, P50, P95, P99;
            internal int Samples;
            internal bool Mixed;
        }

        internal static void SetBenchmarkFastPaths(bool enabled) => FastPathsEnabled = enabled;

        internal static void SetFastPaths(int enabled)
        {
            if (ModSettings.Instance?.DevMode?.Value != true) return;
            CloudPerformanceSweep.Cancel("manual-fastpath");
            FastPathsEnabled = enabled != 0;
            var clouds = VolumetricClouds.Instance;
            if (clouds != null)
                foreach (var layer in clouds.layers) layer?.SetStaticShaderProperties();
            Mod.Log("CloudPerf volume-view fastPaths={0}; density rejection and history shortcuts only, not light-reuse settings.", FastPathsEnabled);
        }

        internal static void Capture(int seconds)
        {
            CloudPerformanceSweep.Cancel("manual-capture");
            CaptureForBenchmark(seconds, "manual", null);
        }

        internal static void CaptureForBenchmark(int seconds, string label, Action<Result> completed)
        {
            if (seconds <= 0) { if (_instance != null) _instance.Finish("stopped"); return; }
            if (ModSettings.Instance?.DevMode?.Value != true) return;
            if (_instance != null) _instance.Finish("restarted");
            var go = new GameObject("VolkenCloudPerformance");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CloudPerformance>();
            _instance._label = label;
            _instance._completed = completed;
            _instance.StartCapture(Mathf.Clamp(seconds, 10, 120));
        }

        private void StartCapture(int seconds)
        {
            _start = Time.realtimeSinceStartup;
            _end = _start + 2 + seconds;
            _nextReport = _start + 7;
            _frameTiming = FrameTimingManager.IsFeatureEnabled();
            for (int i = 0; i < (int)Stage.Count; i++)
            {
                _samplers[i] = CustomSampler.Create("Volken.CloudPerf." + (Stage)i, true);
                _recorders[i] = _samplers[i]?.GetRecorder();
                if (_recorders[i] != null) _recorders[i].enabled = true;
                _cpuStats[i] = new Stats(); _gpuStats[i] = new Stats();
            }
            Mod.Log("CloudPerf start seconds={0} warmup=2 main-view-only gpuRecorder={1} frameTiming={2} API={3} vSync={4} targetFPS={5} label={6}. GPU unavailable is n/a; CPU-submit includes driver waits, not GPU execution.",
                seconds, SystemInfo.supportsGpuRecorder, _frameTiming, SystemInfo.graphicsDeviceType, QualitySettings.vSyncCount, Application.targetFrameRate, _label);
        }

        internal static bool ForCamera(Camera camera) => _instance != null && camera != null &&
            camera == Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;

        internal static bool Describe(Camera camera, RenderTexture source, List<CloudLayer> layers)
        {
            if (!ForCamera(camera)) return false;
            int hash = source.width * 397 ^ source.height;
            foreach (var layer in layers) hash = unchecked(hash * 397 ^ layer.ShaderRevision ^ layer.layerIndex);
            hash = unchecked(hash * 397 ^ layers.Count);
            hash = unchecked(hash * 397 ^ (CloudLightReuseComparison.EnabledOverride.HasValue
                ? CloudLightReuseComparison.EnabledOverride.Value ? 2 : 1 : 0));
            if (!_instance._hasContext || hash != _instance._contextHash)
            {
                if (_instance._hasContext) _instance._mixed = true;
                _instance._hasContext = true; _instance._contextHash = hash;
                Mod.Log("CloudPerf context output={0}x{1} layers={2} waterReflection={3} shader={4} fastPaths={5} fov={6} position={7} lightReuseOverride={8}", source.width, source.height,
                    layers.Count, ModSettings.Instance?.WaterReflection?.Value,
                    layers.Count > 0 ? layers[0].material.GetFloat("_CloudVisibilityRevision") : -1, FastPathsEnabled, camera.fieldOfView, camera.transform.position,
                    CloudLightReuseComparison.EnabledOverride.HasValue
                        ? CloudLightReuseComparison.EnabledOverride.Value ? "reuse" : "direct" : "preset");
                foreach (var layer in layers)
                {
                    var c = layer.config;
                    Mod.Log("CloudPerf layer={0} preset={1} scale={2} TSS={3} grid={4}x{5} lightSamples={6} step={7} lightReuse={8} emptySkip={9} sunOcclusion={10} groundShadow={11}",
                        layer.layerIndex, layer.currentConfigName, c.resolutionScale, c.useTemporalUpscale, c.upscaleX, c.upscaleY,
                        c.numLightSamplePoints, c.stepSize, c.solarLightReuseEnabled, c.emptySpaceSkippingEnabled, c.sunOcclusionEnabled, c.groundShadowsEnabled);
                }
            }
            return true;
        }

        internal static Scope Measure(Stage stage, bool enabled) => new Scope(enabled ? _instance : null, stage);

        internal struct Scope : IDisposable
        {
            private readonly CloudPerformance _owner;
            private readonly Stage _stage;
            private readonly long _stamp;
            internal Scope(CloudPerformance owner, Stage stage)
            {
                _owner = owner; _stage = stage; _stamp = 0;
                if (owner == null) return;
                _stamp = Stopwatch.GetTimestamp();
                owner._samplers[(int)stage]?.Begin();
            }
            public void Dispose()
            {
                if (_owner == null) return;
                _owner._samplers[(int)_stage]?.End();
                _owner._cpu[(int)_stage] += (Stopwatch.GetTimestamp() - _stamp) * (1000.0 / Stopwatch.Frequency);
            }
        }

        private void Update()
        {
            if (_instance != this) return;
            if (ModSettings.Instance?.DevMode?.Value != true || !Game.InFlightScene) { Finish("inactive"); return; }
            if (_lastFrame == Time.frameCount) return;
            _lastFrame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            if (now >= _start + 2)
            {
                _frames.Add(Time.unscaledDeltaTime * 1000.0);
                for (int i = 0; i < (int)Stage.Count; i++)
                {
                    _cpuStats[i].Add(_cpu[i]);
                    var r = _recorders[i];
                    if (r != null && r.isValid && r.gpuSampleBlockCount > 0 && r.gpuElapsedNanoseconds > 0)
                        _gpuStats[i].Add(r.gpuElapsedNanoseconds * 1e-6);
                }
                if (_frameTiming && FrameTimingManager.GetLatestTimings(1, _timings) > 0 && _timings[0].frameStartTimestamp != _lastTiming)
                {
                    var t = _timings[0]; _lastTiming = t.frameStartTimestamp;
                    if (t.cpuMainThreadFrameTime > 0) _main.Add(t.cpuMainThreadFrameTime);
                    if (t.cpuRenderThreadFrameTime > 0) _render.Add(t.cpuRenderThreadFrameTime);
                    if (t.gpuFrameTime > 0) _gpu.Add(t.gpuFrameTime);
                }
            }
            Array.Clear(_cpu, 0, _cpu.Length);
            if (_frameTiming) FrameTimingManager.CaptureFrameTimings();
            if (now >= _end) { Finish("complete"); return; }
            if (now >= _nextReport) { Report("progress"); _nextReport = now + 5; }
        }

        private void Report(string state)
        {
            Mod.Log("CloudPerf {0} mixedContext={1} frameMs[{2}] mainMs[{3}] renderMs[{4}] wholeGpuMs[{5}] GCcollections={6}/{7}/{8} label={9}; stage GPU delayed ~3 frames, valid samples only; Total includes children, not additive.",
                state, _mixed, _frames, _main, _render, _gpu, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _label);
            for (int i = 0; i < (int)Stage.Count; i++)
                Mod.Log("CloudPerf stage={0} cpuSubmitMs[{1}] gpuMs[{2}]", (Stage)i, _cpuStats[i], _gpuStats[i]);
        }

        private void Finish(string reason)
        {
            Report(reason);
            for (int i = 0; i < _recorders.Length; i++) if (_recorders[i] != null) _recorders[i].enabled = false;
            if (_instance == this) _instance = null;
            Destroy(gameObject);
            var completed = _completed;
            _completed = null;
            completed?.Invoke(new Result { Reason = reason, Mean = _frames.Mean, P50 = _frames.Percentile(.5),
                P95 = _frames.Percentile(.95), P99 = _frames.Percentile(.99), Samples = _frames.Count, Mixed = _mixed });
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            for (int i = 0; i < _recorders.Length; i++) if (_recorders[i] != null) _recorders[i].enabled = false;
            _instance = null;
        }

        private sealed class Stats
        {
            private readonly double[] _samples = new double[8192];
            private int _count;
            private double _sum;
            internal int Count => _count;
            internal double Mean => _count > 0 ? _sum / _count : double.NaN;
            internal double Percentile(double p)
            {
                if (_count == 0) return double.NaN;
                int n = Math.Min(_count, _samples.Length);
                var sorted = new double[n]; Array.Copy(_samples, sorted, n); Array.Sort(sorted);
                return sorted[(int)((n - 1) * p)];
            }
            internal void Add(double value) { _samples[_count % _samples.Length] = value; _count++; _sum += value; }
            public override string ToString()
            {
                if (_count == 0) return "n/a n=0";
                int n = Math.Min(_count, _samples.Length);
                var sorted = new double[n]; Array.Copy(_samples, sorted, n); Array.Sort(sorted);
                return string.Format(CultureInfo.InvariantCulture, "n={0} mean={1:F3} p50={2:F3} p95={3:F3} p99={4:F3} percentileN={5}",
                    _count, _sum / _count, sorted[(n - 1) / 2], sorted[(int)((n - 1) * .95)], sorted[(int)((n - 1) * .99)], n);
            }
        }
    }
}
