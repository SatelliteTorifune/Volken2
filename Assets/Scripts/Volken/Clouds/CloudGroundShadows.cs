using System;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Clouds
{
    // 逐接收点的太阳透射率乘入 BIRP 原生屏幕阴影,保留环境光、自发光与实体阴影。
    internal sealed class CloudGroundShadows : IDisposable
    {
        private readonly List<CloudLayer> _layers = new List<CloudLayer>();
        private readonly Dictionary<Camera, View> _views = new Dictionary<Camera, View>();
        private readonly List<Camera> _staleCameras = new List<Camera>();
        private float _nextWarning;
        internal string State { get; private set; } = "inactive";
        internal static float Distance(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 20000f : Mathf.Clamp(value,1000f,100000f);

        internal View Prepare(Camera camera, Light sun)
        {
            State = "inactive";
            var clouds = VolumetricClouds.Instance;
            if (!Game.InFlightScene || clouds == null || !clouds.CurrentPlanet.SupportsAtmosphereEffects || camera == null) return null;
            clouds.FillActiveLayers(_layers);
            bool any = false;
            foreach (var layer in _layers)
                if (layer.config.groundShadowsEnabled) { any = true; break; }
            if (!any) return null;
            if (sun == null || !sun.isActiveAndEnabled || sun.type != LightType.Directional || sun.shadows == LightShadows.None ||
                sun.cookie != null || QualitySettings.shadows == ShadowQuality.Disable || camera.actualRenderingPath != RenderingPath.Forward)
            {
                State = "no-native-shadows";
                return null;
            }
            // 单级图无法同时保留近景实体投影精度和远距云影,不替用户改变阴影质量。
            if (QualitySettings.shadowCascades < 2) { State = "single-cascade"; return null; }
            if (Game.Instance?.FlightScene?.CraftNode?.ReferenceFrame == null ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) { State = "unavailable"; return null; }
            try
            {
                CloudRenderer.AdvanceGlobalCloudState();
                _staleCameras.Clear();
                foreach (var pair in _views) if (pair.Key == null) _staleCameras.Add(pair.Key);
                foreach (var stale in _staleCameras) { _views[stale].Dispose(); _views.Remove(stale); }
                if (!_views.TryGetValue(camera, out var view))
                {
                    view = new View();
                    _views.Add(camera, view);
                }
                view.Prepare(camera, sun, _layers);
                State = "screen-mask";
                return view;
            }
            catch (Exception ex)
            {
                State = "unavailable";
                if (Time.realtimeSinceStartup >= _nextWarning)
                {
                    _nextWarning = Time.realtimeSinceStartup + 5f;
                    Mod.Log("Volken: cloud ground shadows unavailable: " + ex.Message);
                }
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var view in _views.Values) view.Dispose();
            _views.Clear();
            _staleCameras.Clear();
            State = "inactive";
        }

        internal sealed class View : IDisposable
        {
            private readonly Dictionary<CloudLayer, Material> _materials = new Dictionary<CloudLayer, Material>();
            private readonly CommandBuffer _queryCommands = new CommandBuffer { name = "Volken Cloud Shadow Query" };
            private readonly CommandBuffer _applyCommands = new CommandBuffer { name = "Volken Cloud Shadow Apply" };
            private Mesh _triangle;
            private Material _apply;
            private RenderTexture _queryMask;
            private GameObject _carrier;
            private MeshRenderer _carrierRenderer;
            private Light _attached;
            internal float MaximumDistance { get; private set; }

            internal void Prepare(Camera camera, Light sun, List<CloudLayer> layers)
            {
                _queryCommands.Clear(); _applyCommands.Clear();
                if (_triangle == null)
                {
                    _triangle = new Mesh { name = "VolkenGroundShadowTriangle", hideFlags = HideFlags.HideAndDontSave };
                    _triangle.vertices = new[] { new Vector3(-1,-1,0), new Vector3(3,-1,0), new Vector3(-1,3,0) };
                    _triangle.triangles = new[] { 0,1,2 };
                    _triangle.RecalculateBounds();
                    _triangle.UploadMeshData(true);
                }
                float scale = Mathf.Min(0.25f, 256f / Mathf.Max(1, Mathf.Max(camera.pixelWidth, camera.pixelHeight)));
                int width = Mathf.Max(1, Mathf.CeilToInt(camera.pixelWidth * scale));
                int height = Mathf.Max(1, Mathf.CeilToInt(camera.pixelHeight * scale));
                var inverse = (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix).inverse;
                if (_queryMask == null || _queryMask.width != width || _queryMask.height != height || !_queryMask.IsCreated())
                {
                    ReleaseTexture();
                    _queryMask = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                    { name = "VolkenGroundShadow", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                    if (!_queryMask.Create()) throw new InvalidOperationException("ground shadow texture creation failed");
                }
                _queryCommands.SetRenderTarget(_queryMask);
                _queryCommands.ClearRenderTarget(false, true, Color.white);
                MaximumDistance = 0f;
                foreach (var layer in layers)
                {
                    if (!layer.config.groundShadowsEnabled || layer.material == null) continue;
                    MaximumDistance = Mathf.Max(MaximumDistance, Distance(layer.config.groundShadowDistance));
                    if (!_materials.TryGetValue(layer, out var material))
                    {
                        material = new Material(layer.material) { hideFlags = HideFlags.HideAndDontSave };
                        _materials.Add(layer, material);
                    }
                    material.CopyPropertiesFromMaterial(layer.material);
                    layer.SetStaticShaderProperties(material);
                    CloudRenderer.SetCloudWorldProperties(material, layer);
                    material.SetMatrix("_GroundShadowInverseVP", inverse);
                    material.SetMatrix("_GroundShadowView", camera.worldToCameraMatrix);
                    material.SetVector("_GroundShadowCameraPosition", camera.transform.position);
                    material.SetVector("_GroundShadowSize", new Vector4(width, height, 1f / width, 1f / height));
                    int query = material.FindPass("GroundShadowQuery");
                    if (query < 0) throw new InvalidOperationException("GroundShadowQuery pass missing");
                    _queryCommands.DrawMesh(_triangle, Matrix4x4.identity, material, 0, query);
                    if (_apply == null) _apply = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
                }
                if (_apply == null) throw new InvalidOperationException("ground shadow material missing");
                _apply.SetVector("_GroundShadowSize", new Vector4(width, height, 1f / width, 1f / height));
                _apply.SetMatrix("_GroundShadowInverseVP", inverse);
                _apply.SetMatrix("_GroundShadowView", camera.worldToCameraMatrix);
                _apply.SetTexture("_VolkenGroundShadowTex", _queryMask);
                int apply = _apply.FindPass("GroundShadowApply");
                if (apply < 0 || _apply.FindPass("GroundShadowCaster") < 0) throw new InvalidOperationException("ground shadow passes missing");
                // 独立 CB 从原生阴影目标开始;不能在 query 切 RT 后再用 CurrentActive 恢复它。
                _applyCommands.SetRenderTarget(BuiltinRenderTextureType.CurrentActive);
                _applyCommands.DrawMesh(_triangle, Matrix4x4.identity, _apply, 0, apply);
                if (_carrier == null)
                {
                    _carrier = new GameObject("Volken Empty Shadow Caster") { hideFlags = HideFlags.HideAndDontSave };
                    _carrier.AddComponent<MeshFilter>().sharedMesh = _triangle;
                    _carrierRenderer = _carrier.AddComponent<MeshRenderer>();
                    _carrierRenderer.sharedMaterial = _apply;
                    _carrierRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    _carrierRenderer.receiveShadows = false;
                    _carrierRenderer.enabled = false;
                }
                int mask = camera.cullingMask & sun.cullingMask;
                if (mask == 0) throw new InvalidOperationException("camera and sun have no common layer");
                int layerIndex = 0;
                while ((mask & (1 << layerIndex)) == 0) layerIndex++;
                _carrier.layer = layerIndex;
                _carrier.transform.position = camera.ViewportToWorldPoint(new Vector3(0.5f,0.5f,Mathf.Max(1f,camera.nearClipPlane+0.25f)));
                _carrier.transform.rotation = camera.transform.rotation;
            }

            internal void Apply(Light sun)
            {
                Restore();
                _attached = sun;
                _carrierRenderer.enabled = true;
                sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _queryCommands);
                sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _applyCommands);
            }

            internal void Restore()
            {
                if (_attached != null)
                {
                    _attached.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _queryCommands);
                    _attached.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _applyCommands);
                }
                _attached = null;
                if (_carrierRenderer != null) _carrierRenderer.enabled = false;
            }

            private void ReleaseTexture()
            {
                if (_queryMask != null) { _queryMask.Release(); UnityEngine.Object.Destroy(_queryMask); _queryMask = null; }
            }

            public void Dispose()
            {
                Restore();
                _queryCommands.Release(); _applyCommands.Release();
                foreach (var material in _materials.Values) UnityEngine.Object.Destroy(material);
                _materials.Clear();
                if (_carrier != null) UnityEngine.Object.Destroy(_carrier);
                if (_apply != null) UnityEngine.Object.Destroy(_apply);
                if (_triangle != null) UnityEngine.Object.Destroy(_triangle);
                ReleaseTexture();
                _apply = null; _triangle = null; _carrier = null; _carrierRenderer = null;
            }
        }
    }
}
