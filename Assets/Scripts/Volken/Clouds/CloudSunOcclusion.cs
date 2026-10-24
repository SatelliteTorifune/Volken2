using System;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Clouds
{
    // 每相机的太阳 / 半球透射率;空间和数值更新保留结果,生命周期失效拒绝旧回读。
    internal sealed class CloudSunOcclusion : IDisposable
    {
        internal const int RayCount = 9;
        private readonly List<CloudLayer> _layers = new List<CloudLayer>();
        private readonly Dictionary<CloudLayer, Material> _materials = new Dictionary<CloudLayer, Material>();
        private RenderTexture _probe;
        private Mesh _triangle;
        private int _generation, _signature, _revision;
        private bool _pending, _valid;
        private float _nextUpdate, _nextWarning;
        private Vector3 _position, _sunDirection;
        private Vector3 _result = Vector3.one;

        internal static float Strength(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);

        internal Vector3 Factors => _valid &&
            _signature == Signature() ? _result : Vector3.one;

        internal string State => _signature == 0 || _signature != Signature() ? "inactive" :
            _valid ? (_pending ? "refreshing" : "ready") : (_pending ? "warming" : "unavailable");

        private static int Signature() => Signature(out _);

        private static bool IsActive(CloudLayer layer) => layer?.config != null && layer.config.enabled &&
            !layer.EnvironmentSuppressed && (layer.config.sunOcclusionEnabled || layer.config.groundShadowsEnabled) && layer.material != null;

        private static int Signature(out int revision)
        {
            revision = 17;
            var clouds = VolumetricClouds.Instance;
            if (!Game.InFlightScene || clouds == null || !clouds.CurrentPlanet.SupportsAtmosphereEffects) return 0;
            unchecked
            {
                int hash = 17;
                bool any = false;
                foreach (var layer in clouds.layers)
                {
                    if (!IsActive(layer)) continue;
                    any = true;
                    hash = hash * 31 + layer.GetHashCode();
                    hash = hash * 31 + layer.config.GetHashCode();
                    hash = hash * 31 + layer.material.GetInstanceID();
                    hash = hash * 31 + (layer.currentConfigName?.GetHashCode() ?? 0);
                    hash = hash * 31 + (layer.config.sunOcclusionEnabled ? 1 : 0) + (layer.config.groundShadowsEnabled ? 2 : 0);
                    revision = revision * 31 + layer.ShaderRevision;
                }
                return any ? hash : 0;
            }
        }

        internal void Tick(Camera camera)
        {
            int signature = Signature(out int revision);
            if (signature != _signature) { Invalidate(); _signature = signature; _revision = revision; }
            else if (revision != _revision) { _revision = revision; RequestRefresh(); }
            if (signature == 0 || camera == null || !SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) return;
            CloudRenderer.AdvanceGlobalCloudState();
            var node = Game.Instance?.FlightScene?.CraftNode;
            var sun = Game.Instance?.FlightScene?.ViewManager?.GameView?.SunLight;
            if (node?.ReferenceFrame == null || node.Parent == null || sun == null) { Invalidate(); return; }
            Vector3 center = node.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            Vector3 position = camera.transform.position, toSun = -sun.transform.forward;
            Vector3 planetPosition = (Vector3)node.ReferenceFrame.FrameToPlanetVector(position - center);
            Vector3 planetSun = (Vector3)node.ReferenceFrame.FrameToPlanetVector(toSun);
            if ((planetPosition - _position).sqrMagnitude > 62500f || Vector3.Dot(planetSun, _sunDirection) < 0.999f)
            {
                RequestRefresh();
            }
            if (_pending || Time.realtimeSinceStartup < _nextUpdate) return;
            _nextUpdate = Time.realtimeSinceStartup + 0.1f;
            _position = planetPosition;
            _sunDirection = planetSun;
            var previous = RenderTexture.active;
            try
            {
                EnsureResources();
                VolumetricClouds.Instance.FillActiveLayers(_layers);
                Graphics.SetRenderTarget(_probe);
                GL.Clear(false, true, Color.white);
                foreach (var layer in _layers)
                {
                    if (!IsActive(layer)) continue;
                    if (!_materials.TryGetValue(layer, out var material))
                    {
                        material = new Material(layer.material) { hideFlags = HideFlags.HideAndDontSave };
                        _materials.Add(layer, material);
                    }
                    material.CopyPropertiesFromMaterial(layer.material);
                    layer.SetStaticShaderProperties(material);
                    CloudRenderer.SetCloudWorldProperties(material, layer);
                    material.SetVector("_OcclusionOrigin", position);
                    material.SetVector("_OcclusionUp", (position - center).normalized);
                    int pass = material.FindPass("SunOcclusion");
                    if (pass < 0 || !material.SetPass(pass)) { Unavailable("SunOcclusion pass missing"); return; }
                    Graphics.DrawMeshNow(_triangle, Matrix4x4.identity);
                }
                int generation = _generation;
                _pending = true;
                AsyncGPUReadback.Request(_probe, 0, TextureFormat.RGBAFloat, request =>
                {
                    _pending = false;
                    if (generation != _generation) return;
                    if (request.hasError) { Unavailable("GPU readback failed"); return; }
                    var data = request.GetData<Color>();
                    float ambient = 0f;
                    for (int i = 1; i < RayCount; i++) ambient += data[i].b / (RayCount - 1);
                    _result = new Vector3(Strength(data[0].r, 1f), Strength(data[0].g, 1f), Strength(ambient, 1f));
                    _valid = true;
                });
            }
            catch (Exception ex)
            {
                _pending = false;
                Unavailable(ex.Message);
            }
            finally { RenderTexture.active = previous; }
        }

        private void EnsureResources()
        {
            if (_probe == null)
            {
                _probe = new RenderTexture(RayCount, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    name = "VolkenSunOcclusion", filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
                };
            }
            if (!_probe.IsCreated() && !_probe.Create()) throw new InvalidOperationException("probe texture creation failed");
            if (_triangle == null)
            {
                _triangle = new Mesh { name = "VolkenSunOcclusionTriangle", hideFlags = HideFlags.HideAndDontSave };
                _triangle.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) };
                _triangle.triangles = new[] { 0, 1, 2 };
                _triangle.UploadMeshData(true);
            }
        }

        internal void Invalidate()
        {
            _generation++;
            _valid = false;
            _nextUpdate = 0f;
            _result = Vector3.one;
        }

        // 不取消在途回读;连续滑条修改 / 重定位时也必须有能完成的查询。
        internal void RequestRefresh()
        {
            if (_valid || _pending) _nextUpdate = 0f;
        }

        private void Unavailable(string reason)
        {
            Invalidate();
            _nextUpdate = Time.realtimeSinceStartup + 2f;
            if (Time.realtimeSinceStartup < _nextWarning) return;
            _nextWarning = Time.realtimeSinceStartup + 5f;
            Mod.Log("Volken: cloud sun occlusion unavailable: " + reason);
        }

        public void Dispose()
        {
            Invalidate();
            if (_probe != null) { _probe.Release(); UnityEngine.Object.Destroy(_probe); _probe = null; }
            if (_triangle != null) { UnityEngine.Object.Destroy(_triangle); _triangle = null; }
            foreach (var material in _materials.Values) UnityEngine.Object.Destroy(material);
            _materials.Clear();
        }
    }
}
