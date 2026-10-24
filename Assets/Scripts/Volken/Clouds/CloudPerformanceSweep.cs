using System;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;

namespace Volken.Clouds
{
    // 自动诊断只借用运行态开关；完成、中断及离场都归还设置，不写预设。
    internal sealed class CloudPerformanceSweep : MonoBehaviour
    {
        internal struct Phase
        {
            internal string Name;
            internal int Round;
            internal bool Fast, Empty, LightReuse, Frozen, NewRound;
            internal CloudPerformanceFreeze.Omit Omit;
        }
        private sealed class SavedLayer
        {
            internal CloudLayer Layer;
            internal CloudConfig Config;
            internal string Preset;
            internal bool Temporal, LightReuse, Empty, Logged;
            internal int Revision;
        }
        private struct Measurement
        {
            internal Phase Phase;
            internal CloudPerformance.Result Result;
            internal bool Effective;
        }
        private static CloudPerformanceSweep _instance;
        private readonly List<SavedLayer> _saved = new List<SavedLayer>();
        private readonly List<CloudLayer> _active = new List<CloudLayer>();
        private readonly List<Measurement> _results = new List<Measurement>();
        private readonly CloudPerformanceFreeze _freeze = new CloudPerformanceFreeze();
        private Camera _camera;
        private CloudRenderer _renderer;
        private VolumetricClouds _clouds;
        private int _mode, _index = -1, _vSync, _targetFps, _width, _height;
        private bool _originalFast, _ownsSettings, _running, _capturing, _observed, _effective;
        private bool _waterReflection;
        private float _waitUntil, _deadline, _fov;
        private Quaternion _rotation;
        private Phase _phase;

        internal static int PhaseCount(int mode) => mode == 1 ? 9 : mode == 2 ? 12 : mode == 4 ? 9 : mode == 5 ? 6 : 21;

        internal static Phase PhaseAt(int mode, int index)
        {
            if (mode == 5)
            {
                int round = index / 2, slot = index % 2;
                bool reuse = (round % 2 == 0 ? slot : 1 - slot) == 1;
                return new Phase { Name = reuse ? "lighting-reuse" : "lighting-direct", Round = round,
                    Fast = true, LightReuse = reuse, Frozen = true, NewRound = slot == 0 };
            }
            if (mode == 4)
            {
                int round = index / 3, slot = index % 3;
                int item = round % 2 == 0 ? slot : 2 - slot;
                var omit = item == 0 ? CloudPerformanceFreeze.Omit.None : item == 1
                    ? CloudPerformanceFreeze.Omit.NoSolar : CloudPerformanceFreeze.Omit.Trace;
                return new Phase { Name = "frozen-" + omit, Round = round, Fast = true,
                    Frozen = true, NewRound = slot == 0, Omit = omit };
            }
            int features = mode == 2 ? 0 : 9;
            if (index < features)
            {
                int round = index / 3, slot = index % 3;
                int item = round % 2 == 0 ? slot : 2 - slot;
                string[] names = { "base", "fast", "empty" };
                return new Phase { Name = names[item], Round = round, Fast = item != 0,
                    Empty = item == 2 };
            }
            index -= features;
            int r = index / 4, s = index % 4;
            int omission = r % 2 == 0 ? s : 3 - s;
            return new Phase { Name = "frozen-" + (CloudPerformanceFreeze.Omit)omission, Round = r,
                Fast = true, Frozen = true, NewRound = s == 0, Omit = (CloudPerformanceFreeze.Omit)omission };
        }

        internal static void Run(int mode)
        {
            if (mode == 0) { Cancel("manual-stop"); return; }
            if (ModSettings.Instance?.DevMode?.Value != true) return;
            if (mode < 1 || mode > 5) { Mod.Log("CloudBench usage: 1=feature sweep, 2=frozen omission, 3=both, 4=solar split, 5=frozen direct/lighting-reuse comparison (shader 21), 0=stop."); return; }
            Cancel("restarted");
            CloudLightReuseComparison.Restore();
            CloudPerformance.CaptureForBenchmark(0, "", null);
            if (!Game.InFlightScene || Volken.Weather.GamePause.IsPaused || WeatherActive()) { Mod.Log("CloudBench requires an unpaused flight scene with rain, fog, lens rain and lightning off."); return; }
            var camera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
            var renderer = camera != null ? camera.GetComponent<CloudRenderer>() : null;
            var clouds = VolumetricClouds.Instance;
            if (renderer == null || clouds == null) { Mod.Log("CloudBench main cloud renderer unavailable."); return; }
            var go = new GameObject("VolkenCloudBenchmark");
            DontDestroyOnLoad(go);
            var owner = go.AddComponent<CloudPerformanceSweep>();
            _instance = owner;
            try { owner.Begin(mode, camera, renderer, clouds); }
            catch (Exception e) { Mod.Log("CloudBench setup failed: {0}", e); owner.Stop("setup-failed"); }
        }

        private void Begin(int mode, Camera camera, CloudRenderer renderer, VolumetricClouds clouds)
        {
            _mode = mode; _camera = camera; _renderer = renderer; _clouds = clouds;
            clouds.FillActiveLayers(_active);
            if (_active.Count == 0) { Stop("no-active-clouds"); return; }
            foreach (var layer in _active)
            {
                int minimumShader = mode == 5 ? 21 : mode == 4 ? 19 : 18;
                if (layer.material == null || layer.material.GetFloat("_CloudVisibilityRevision") < minimumShader ||
                    mode == 4 && !layer.material.shader.keywordSpace.FindKeyword("VOLKEN_CLOUD_BENCH_NO_SOLAR").isValid ||
                    mode == 5 && !layer.material.shader.keywordSpace.FindKeyword(CloudLightReuse.Keyword).isValid)
                    throw new InvalidOperationException("CloudBench requires cloud shader " + minimumShader + " or later with the requested diagnostic variant.");
                _saved.Add(new SavedLayer { Layer = layer, Config = layer.config, Preset = layer.currentConfigName,
                    Temporal = layer.config.useTemporalUpscale, LightReuse = layer.config.solarLightReuseEnabled,
                    Empty = layer.config.emptySpaceSkippingEnabled, Revision = layer.ShaderRevision });
            }
            _vSync = QualitySettings.vSyncCount; _targetFps = Application.targetFrameRate;
            _originalFast = CloudPerformance.FastPathsEnabled; _fov = camera.fieldOfView; _rotation = camera.transform.rotation;
            _waterReflection = ModSettings.Instance.WaterReflection.Value;
            _ownsSettings = _running = true;
            clouds.PlanetChanged += PlanetChanged;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            _waitUntil = Time.realtimeSinceStartup + 10;
            Mod.Log("CloudBench version=4 begin mode={0} phases={1} rounds=3 sampleSeconds=30 warmupSeconds=5 startupSeconds=10 originalVSync={2} originalTargetFPS={3}; TSS forced on, no XML writes. Close console and keep a stationary camera. Frozen tests hold opaque input; Composite omission retains one output copy. NoSolar removes solar queries and dependent shading, not density/traversal. Mode 5 shares one frozen input per direct/reuse pair, middle round reversed; displayed output is frozen. GPU timestamps may be n/a.",
                mode, PhaseCount(mode), _vSync, _targetFps);
        }

        private bool Valid()
        {
            if (!Game.InFlightScene || Volken.Weather.GamePause.IsPaused || WeatherActive() || ModSettings.Instance?.DevMode?.Value != true ||
                _camera == null || _renderer == null || VolumetricClouds.Instance != _clouds ||
                Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera != _camera)
                return false;
            if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1 || ModSettings.Instance.WaterReflection.Value != _waterReflection ||
                _index >= 0 && (Mathf.Abs(_camera.fieldOfView - _fov) > .001f || Quaternion.Angle(_rotation, _camera.transform.rotation) > .5f))
                return false;
            _clouds.FillActiveLayers(_active);
            if (_active.Count != _saved.Count) return false;
            foreach (var item in _saved)
                if (!_active.Contains(item.Layer) || item.Layer.config != item.Config || item.Layer.currentConfigName != item.Preset ||
                    item.Layer.ShaderRevision != item.Revision) return false;
            return true;
        }

        private void Update()
        {
            if (!_running) return;
            try
            {
                if (!Valid()) { Stop("scene-config-camera-or-cap-changed"); return; }
                float now = Time.realtimeSinceStartup;
                if (_index < 0) { if (now >= _waitUntil) Next(); return; }
                if (_capturing) return;
                if (now > _deadline) { Stop("main-view-or-freeze-timeout"); return; }
                if (now < _waitUntil || !_observed) return;
                if (_phase.Frozen && !_freeze.Ready)
                {
                    if (!_freeze.Capturing) _freeze.Arm();
                    return;
                }
                _capturing = true;
                CloudPerformance.CaptureForBenchmark(30, Label, Completed);
            }
            catch (Exception e) { Mod.Log("CloudBench failed: {0}", e); Stop("exception"); }
        }

        private string Label => "bench:" + _phase.Name + ":round=" + (_phase.Round + 1);

        private void Next()
        {
            _index++;
            if (_index >= PhaseCount(_mode)) { Stop("complete"); return; }
            if (_index == 0) { _fov = _camera.fieldOfView; _rotation = _camera.transform.rotation; }
            _phase = PhaseAt(_mode, _index);
            if (!_phase.Frozen || _phase.NewRound)
            {
                _freeze.Dispose();
                _renderer.InvalidateDiagnosticHistory();
            }
            CloudPerformance.SetBenchmarkFastPaths(_phase.Fast);
            foreach (var item in _saved)
            {
                item.Config.useTemporalUpscale = true;
                item.Config.solarLightReuseEnabled = _phase.LightReuse;
                item.Config.emptySpaceSkippingEnabled = _phase.Empty;
                item.Layer.SetStaticShaderProperties();
                item.Revision = item.Layer.ShaderRevision;
            }
            _freeze.Omitted = _phase.Omit;
            foreach (var item in _saved) item.Logged = false;
            _capturing = _observed = false; _effective = true;
            _waitUntil = Time.realtimeSinceStartup + 3;
            _deadline = Time.realtimeSinceStartup + 15;
            Mod.Log("CloudBench phase={0}/{1} label={2} TSS=true fast={3} lightReuse={4} empty={5} frozen={6} omission={7}",
                _index + 1, PhaseCount(_mode), Label, _phase.Fast, _phase.LightReuse, _phase.Empty, _phase.Frozen, _phase.Omit);
        }

        private void Completed(CloudPerformance.Result result)
        {
            if (!_running) return;
            if (!Valid()) { Stop("context-changed-before-result"); return; }
            if (result.Reason != "complete" || result.Mixed || result.Samples == 0) { Stop("capture-" + result.Reason); return; }
            _results.Add(new Measurement { Phase = _phase, Result = result, Effective = _effective });
            Mod.Log("CloudBench result label={0} effective={1} samples={2} meanMs={3:F3} p50Ms={4:F3} p95Ms={5:F3} p99Ms={6:F3}; marginal whole-frame data, not GPU timestamps.",
                Label, _effective, result.Samples, result.Mean, result.P50, result.P95, result.P99);
            try { Next(); }
            catch (Exception e) { Mod.Log("CloudBench phase failed: {0}", e); Stop("phase-failed"); }
        }

        private void Summarize()
        {
            foreach (string name in new[] { "base", "fast", "empty", "frozen-None", "frozen-Trace", "frozen-Upscale", "frozen-Composite", "frozen-NoSolar", "lighting-direct", "lighting-reuse" })
            {
                var means = new List<double>(); var p95 = new List<double>();
                foreach (var measurement in _results)
                    if (measurement.Phase.Name == name && measurement.Effective)
                    { means.Add(measurement.Result.Mean); p95.Add(measurement.Result.P95); }
                if (means.Count == 0) continue;
                means.Sort(); p95.Sort();
                Mod.Log("CloudBench summary case={0} validRounds={1} medianMeanMs={2:F3} medianP95Ms={3:F3}",
                    name, means.Count, means[means.Count / 2], p95[p95.Count / 2]);
            }
            if (_mode == 5)
            {
                for (int round = 0; round < 3; round++)
                {
                    double direct = double.NaN, reuse = double.NaN;
                    foreach (var measurement in _results)
                    {
                        if (measurement.Phase.Round != round || !measurement.Effective) continue;
                        if (measurement.Phase.LightReuse) reuse = measurement.Result.Mean;
                        else direct = measurement.Result.Mean;
                    }
                    if (double.IsNaN(direct) || double.IsNaN(reuse)) continue;
                    Mod.Log("CloudBench reuse round={0} directMinusReuseMs={1:F3}; positive means reuse is faster; shared frozen input, whole-frame differences, not GPU timestamps.",
                        round + 1, direct - reuse);
                }
                return;
            }
            if (_mode != 4) return;
            for (int round = 0; round < 3; round++)
            {
                double full = double.NaN, noSolar = double.NaN, noTrace = double.NaN;
                foreach (var measurement in _results)
                {
                    if (measurement.Phase.Round != round || !measurement.Effective) continue;
                    if (measurement.Phase.Omit == CloudPerformanceFreeze.Omit.None) full = measurement.Result.Mean;
                    else if (measurement.Phase.Omit == CloudPerformanceFreeze.Omit.NoSolar) noSolar = measurement.Result.Mean;
                    else if (measurement.Phase.Omit == CloudPerformanceFreeze.Omit.Trace) noTrace = measurement.Result.Mean;
                }
                if (double.IsNaN(full) || double.IsNaN(noSolar) || double.IsNaN(noTrace)) continue;
                Mod.Log("CloudBench split round={0} solarMarginalMs={1:F3} residualTraceMs={2:F3} totalTraceMs={3:F3}; solar includes dependent shading, residual includes density/traversal/ambient; whole-frame differences, not GPU timestamps.",
                    round + 1, full - noSolar, noSolar - noTrace, full - noTrace);
            }
        }

        private void Stop(string reason)
        {
            if (!_running && !_ownsSettings)
            {
                Mod.Log("CloudBench not started reason={0}", reason);
                if (_instance == this) _instance = null;
                Destroy(gameObject); return;
            }
            _running = false;
            if (_instance == this) _instance = null;
            try
            {
                CloudPerformance.CaptureForBenchmark(0, "", null);
                _freeze.Dispose();
            }
            catch (Exception e) { Mod.Log("CloudBench capture cleanup failed: {0}", e); }
            finally
            {
                if (_ownsSettings)
                {
                    _ownsSettings = false;
                    try
                    {
                        _clouds.PlanetChanged -= PlanetChanged;
                        CloudPerformance.SetBenchmarkFastPaths(_originalFast);
                        foreach (var item in _saved)
                        {
                            bool sameConfig = item.Layer.config == item.Config;
                            if (sameConfig && item.Layer.currentConfigName != item.Preset) continue;
                            item.Config.useTemporalUpscale = item.Temporal;
                            item.Config.solarLightReuseEnabled = item.LightReuse;
                            item.Config.emptySpaceSkippingEnabled = item.Empty;
                            try { if (sameConfig) item.Layer.SetStaticShaderProperties(); }
                            catch (Exception e) { Mod.Log("CloudBench material restore failed: {0}", e); }
                        }
                        try { if (_renderer != null) _renderer.InvalidateDiagnosticHistory(); }
                        catch (Exception e) { Mod.Log("CloudBench history reset failed: {0}", e); }
                    }
                    finally { QualitySettings.vSyncCount = _vSync; Application.targetFrameRate = _targetFps; }
                }
            }
            Summarize();
            Mod.Log("CloudBench end reason={0} restored=true vSync={1} targetFPS={2} fast={3}",
                reason, QualitySettings.vSyncCount, Application.targetFrameRate, CloudPerformance.FastPathsEnabled);
            Destroy(gameObject);
        }

        private void PlanetChanged(PlanetEnvironment environment) => Stop("planet-changed");
        private void OnDisable() { if (_running || _ownsSettings) Stop("disabled"); }
        private void OnDestroy() { if (_running || _ownsSettings) Stop("destroyed"); }
        private void OnApplicationFocus(bool focus) { if (!focus && _running) Stop("focus-lost"); }

        private static bool WeatherActive()
        {
            var weather = Volken.Weather.VolkenWeather.Instance;
            var config = weather?.Config;
            return weather?.IsActive == true && (config?.rain?.enabled == true || config?.fog?.enabled == true ||
                config?.rainLens?.enabled == true || config?.lightning?.enabled == true);
        }

        internal static void Cancel(string reason) { if (_instance != null) _instance.Stop(reason); }
        internal static void CancelForCamera(Camera camera, string reason) => For(camera)?.Stop(reason);
        private static CloudPerformanceSweep For(Camera camera) => _instance != null && _instance._running && _instance._camera == camera ? _instance : null;

        internal static void Observe(Camera camera, RenderTexture source)
        {
            var owner = For(camera); if (owner == null) return;
            if (owner._width != 0 && (owner._width != source.width || owner._height != source.height)) { owner.Stop("resolution-changed"); return; }
            owner._width = source.width; owner._height = source.height;
        }

        internal static void Prepared(Camera camera, CloudLayerView view)
        {
            var owner = For(camera); if (owner == null || owner._index < 0) return;
            owner._observed = true;
            var mat = view.layer.material;
            bool reuse = mat.IsKeywordEnabled(CloudLightReuse.Keyword), empty = mat.GetFloat("_CloudEmptySpaceEnabled") > .5f;
            if (reuse != owner._phase.LightReuse || owner._phase.Empty && !empty) owner._effective = false;
            foreach (var item in owner._saved)
            {
                if (item.Layer != view.layer || item.Logged) continue;
                item.Logged = true;
                Mod.Log("CloudBench runtime label={0} layer={1} rays={2}x{3} shader={4} lightReuseVariant={5} emptyActive={6} densityBounds={7} historyFast={8} position={9} rotation={10}",
                    owner.Label, view.layer.layerIndex, view.cloudTex.width, view.cloudTex.height, mat.GetFloat("_CloudVisibilityRevision"),
                    reuse, empty, mat.GetFloat("_CloudDensityBounds"), mat.GetFloat("_CloudHistoryFastPath"), camera.transform.position, camera.transform.rotation);
            }
        }

        internal static void BeginFrame(Camera camera, List<CloudLayerView> views, bool fog) => For(camera)?._freeze.BeginFrame(views, fog);
        internal static void CompleteFrame(Camera camera) => For(camera)?._freeze.CompleteFrame();
        internal static Material Trace(Camera camera, CloudLayerView view, Material material)
        {
            var owner = For(camera);
            return owner?._freeze.Trace(view, material, owner._mode == 5 ? owner._phase.LightReuse : (bool?)null) ?? material;
        }
        internal static bool SkipTrace(Camera camera) { var f = For(camera)?._freeze; return f != null && f.Ready && f.Omitted == CloudPerformanceFreeze.Omit.Trace; }
        internal static bool SkipUpscale(Camera camera) { var f = For(camera)?._freeze; return f != null && f.Ready && f.Omitted == CloudPerformanceFreeze.Omit.Upscale; }
        internal static bool Upscale(Camera camera, CloudLayerView view, Material material, int pass) => For(camera)?._freeze.Upscale(view, material, pass) ?? false;
        internal static Texture CloudDepth(Camera camera, CloudLayerView view) => For(camera)?._freeze.CloudDepth(view) ?? view.cloudDepthTex;
        internal static Texture SceneDepth(Camera camera, Texture source) => For(camera)?._freeze.SceneDepth(source) ?? source;
        internal static bool Composite(Camera camera, CloudLayerView view, Texture source, RenderTexture destination, Material material, int pass) =>
            For(camera)?._freeze.Composite(view, source, destination, material, pass) ?? false;
    }
}
