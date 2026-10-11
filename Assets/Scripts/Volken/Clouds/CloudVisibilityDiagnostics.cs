using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Clouds
{
    // Main-camera diagnostics only; all textures in a batch are read from the same rendered frame.
    internal sealed class CloudVisibilityDiagnostics
    {
        private static readonly Vector2[] Points = {
            new Vector2(0.5f, 0.5f), new Vector2(0.25f, 0.75f),
            new Vector2(0.75f, 0.75f), new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f)
        };
        private enum Kind { Raw, Depth, Upscaled, Scene, Motion }
        private sealed class Sample
        {
            public Color Raw, Upscaled, Motion;
            public float Depth, Scene;
            public int Errors;
        }
        private sealed class Batch
        {
            public int Generation, Remaining;
            public string Context;
            public readonly Sample[] Samples = new Sample[Points.Length];
        }

        private float _nextSample;
        private int _generation, _pending, _recenterCount;
        private double _lastPositionDelta, _lastVelocityDelta;

        public void Cancel()
        {
            _generation++;
            _pending = 0;
            _nextSample = 0f;
        }

        public void RecordRecenter(double positionDelta, double velocityDelta)
        {
            _recenterCount++;
            _lastPositionDelta = positionDelta;
            _lastVelocityDelta = velocityDelta;
        }

        public void Tick(Camera camera, List<CloudLayerView> views, RenderTexture scene,
            FarCameraScript far, float altitude, bool fogActive)
        {
            if (ModSettings.Instance == null || !ModSettings.Instance.DevMode || camera == null) return;
            if (_pending != 0 || Time.realtimeSinceStartup < _nextSample) return;
            _nextSample = Time.realtimeSinceStartup + 5f;
            try
            {
                string context = string.Format(
                    "build=8 frame={0} camera='{1}' altitude={2:F1}m fov={3:F1} clip={4:F2}/{5:F0} fog={6} layers={7} farReady={8} farMax={9:G6} recenterCount={10} lastDelta={11:G6}/{12:G6}m,m/s",
                    Time.frameCount, camera.name, altitude, camera.fieldOfView, camera.nearClipPlane,
                    camera.farClipPlane, fogActive, views.Count, far != null && Ready(far.farDepthTex),
                    far != null ? far.maxFarDepth : 0f, _recenterCount, _lastPositionDelta, _lastVelocityDelta);
                Mod.Log("Volken:CloudVisibility state " + context);
                foreach (var view in views)
                {
                    var layer = view.layer;
                    var cfg = layer.config;
                    var mat = layer.material;
                    if (cfg == null || mat == null) continue;
                    string layerContext = context + string.Format(
                        " L={0} preset='{1}' tss={2} fade={3:F3} jitter={4:G6} densityCfg/Mat={5:G6}/{6:G6} coverageCfg/Mat={7:G6}/{8:G6} maxHCfg/Mat={9:G6}/{10:G6} maxDepthMat={11:G6} rawRT={12} upRT={13} shader={14:G4} orbitDepthSource={15}",
                        layer.layerIndex, layer.currentConfigName, cfg.useTemporalUpscale, view.orbitFade,
                        mat.GetFloat("blueNoiseStrength"), cfg.density, mat.GetFloat("cloudDensity"),
                        cfg.coverage, mat.GetFloat("cloudCoverage"), cfg.maxCloudHeight,
                        mat.GetFloat("maxCloudHeight"), mat.GetFloat("maxDepth"), Size(view.cloudTex), Size(view.upscaledCloudTex),
                        mat.HasProperty("_CloudVisibilityRevision") ? mat.GetFloat("_CloudVisibilityRevision") : 0f,
                        mat.GetTexture("DepthTex") == scene && Ready(scene) ? "scene" : "other");
                    var shape = mat.GetTexture("CloudShapeTex") as RenderTexture;
                    var detail = mat.GetTexture("CloudDetailTex") as RenderTexture;
                    layerContext += string.Format(" shapeNoise={0} noiseMips={1}/{2}",
                        shape != null ? shape.name : "other", shape != null ? shape.mipmapCount : 0,
                        detail != null ? detail.mipmapCount : 0);
                    if (view.orbitFade >= 0.999f)
                    {
                        Mod.Log("Volken:CloudVisibility skipped-volume " + layerContext);
                        continue;
                    }
                    if (!Ready(view.cloudTex) || !Ready(view.cloudDepthTex) || !Ready(view.upscaledCloudTex) ||
                        !Ready(view.cloudMVTex) || !Ready(scene) || !SystemInfo.supportsAsyncGPUReadback)
                    {
                        Mod.Log("Volken:CloudVisibility unavailable " + layerContext + string.Format(
                            " async={0} raw/depth/up/mv/scene={1}/{2}/{3}/{4}/{5}", SystemInfo.supportsAsyncGPUReadback,
                            Ready(view.cloudTex), Ready(view.cloudDepthTex), Ready(view.upscaledCloudTex), Ready(view.cloudMVTex), Ready(scene)));
                        continue;
                    }
                    var batch = new Batch { Generation = _generation, Remaining = Points.Length * 5, Context = layerContext };
                    _pending++;
                    for (int i = 0; i < Points.Length; i++)
                    {
                        batch.Samples[i] = new Sample();
                        int x = Mathf.Clamp((int)(Points[i].x * view.cloudTex.width), 0, view.cloudTex.width - 1);
                        int y = Mathf.Clamp((int)(Points[i].y * view.cloudTex.height), 0, view.cloudTex.height - 1);
                        Vector2 uv = new Vector2((x + 0.5f) / view.cloudTex.width, (y + 0.5f) / view.cloudTex.height);
                        Read(batch, i, Kind.Raw, view.cloudTex, uv);
                        Read(batch, i, Kind.Depth, view.cloudDepthTex, uv);
                        Read(batch, i, Kind.Upscaled, view.upscaledCloudTex, uv);
                        Read(batch, i, Kind.Scene, scene, uv);
                        Read(batch, i, Kind.Motion, view.cloudMVTex, uv);
                    }
                }
            }
            catch (Exception ex) { Mod.Log("Volken:CloudVisibility submit ERROR " + ex); }
        }

        private void Read(Batch batch, int index, Kind kind, RenderTexture texture, Vector2 uv)
        {
            int x = Mathf.Clamp((int)(uv.x * texture.width), 0, texture.width - 1);
            int y = Mathf.Clamp((int)(uv.y * texture.height), 0, texture.height - 1);
            TextureFormat format = kind == Kind.Depth || kind == Kind.Scene ? TextureFormat.RFloat : TextureFormat.RGBAFloat;
            try
            {
                AsyncGPUReadback.Request(texture, 0, x, 1, y, 1, 0, 1, format, request =>
                {
                    if (batch.Generation != _generation) return;
                    try
                    {
                        if (request.hasError) batch.Samples[index].Errors |= 1 << (int)kind;
                        else
                        {
                            var sample = batch.Samples[index];
                            switch (kind)
                            {
                                case Kind.Raw: sample.Raw = request.GetData<Color>()[0]; break;
                                case Kind.Depth: sample.Depth = request.GetData<float>()[0]; break;
                                case Kind.Upscaled: sample.Upscaled = request.GetData<Color>()[0]; break;
                                case Kind.Scene: sample.Scene = request.GetData<float>()[0]; break;
                                case Kind.Motion: sample.Motion = request.GetData<Color>()[0]; break;
                            }
                        }
                    }
                    catch { batch.Samples[index].Errors |= 1 << (int)kind; }
                    finally { Complete(batch); }
                });
            }
            catch
            {
                batch.Samples[index].Errors |= 1 << (int)kind;
                Complete(batch);
            }
        }

        private void Complete(Batch batch)
        {
            if (--batch.Remaining != 0) return;
            _pending--;
            int zeroWithColor = 0, nearWithColor = 0, lost = 0, lostCurrent = 0, invalidMotion = 0, errors = 0;
            var details = new StringBuilder();
            for (int i = 0; i < batch.Samples.Length; i++)
            {
                var s = batch.Samples[i];
                if (s.Errors != 0) errors++;
                else
                {
                    bool zero = Finite(s.Depth) && Mathf.Abs(s.Depth) <= 1e-6f && MaxRgb(s.Raw) > 1f / 255f;
                    if (zero) zeroWithColor++;
                    if (zero && MaxRgb(s.Upscaled) < MaxRgb(s.Raw) * 0.05f) lost++;
                    bool hit = Finite(s.Depth) && s.Depth > 0f && MaxRgb(s.Raw) > 1f / 255f;
                    if (hit && s.Depth <= 0.001001f) nearWithColor++;
                    if (hit && MaxRgb(s.Upscaled) < MaxRgb(s.Raw) * 0.05f) lostCurrent++;
                    if (!Finite(s.Motion.r) || !Finite(s.Motion.g)) invalidMotion++;
                }
                details.AppendFormat(" | P{0} raw=({1:G4},{2:G4},{3:G4},{4:G4}) depth={5:G7} up=({6:G4},{7:G4},{8:G4},{9:G4}) scene={10:G7} mv=({11:G4},{12:G4}) err={13}",
                    i, s.Raw.r, s.Raw.g, s.Raw.b, s.Raw.a, s.Depth,
                    s.Upscaled.r, s.Upscaled.g, s.Upscaled.b, s.Upscaled.a, s.Scene, s.Motion.r, s.Motion.g, s.Errors);
            }
            Mod.Log("Volken:CloudVisibility GPU " + batch.Context + string.Format(
                " zeroDepthWithColor={0}/5 lostAfterUpscale={1}/5 invalidMV={2}/5 errors={3}/5 nearDepthWithColor={4}/5 lostCurrentCloud={5}/5",
                zeroWithColor, lost, invalidMotion, errors, nearWithColor, lostCurrent) + details);
        }

        private static bool Ready(RenderTexture rt) => rt != null && rt.IsCreated();
        private static string Size(RenderTexture rt) => rt != null ? rt.width + "x" + rt.height : "null";
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float MaxRgb(Color value) => Mathf.Max(value.r, Mathf.Max(value.g, value.b));
    }
}
