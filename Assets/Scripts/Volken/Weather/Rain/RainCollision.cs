using System;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Weather
{
    /// <summary>每相机的同帧降雨深度捕获、GPU 碰撞回收和有界水花池;不回读逐滴位置。</summary>
    internal sealed class RainCollision : IDisposable
    {
        internal const int SplashCapacity = 2048;
        internal const int MaxImpactsPerFrame = 128;
        private const int TerrainLayer = 29; // JNO QuadSphereScript.DrawQuads submits terrain on this layer.
        // JNO's actual SrStandard terrain/object/part shaders have no RenderType tag.
        // These physical layers need unfiltered replacement; never apply it to water/clouds/UI.
        private const int PhysicalLayers = (1 << 26) | (1 << TerrainLayer) | (1 << 31);
        private const int ExcludedLayers = (1 << 4) | (1 << 5) | (1 << 8) | (1 << 27);
        private const string AssetRoot = "Assets/Scripts/Volken/Weather/Rain/Shader/";
        private static readonly uint[] Zero = { 0 };
        private ComputeShader _compute;
        private Shader _depthShader;
        private Material _splashMaterial;
        private Camera _capture;
        private RenderTexture _depth;
        private ComputeBuffer _previous, _surfaces, _splashes, _normals, _counters;
        private GraphicsBuffer _args;
        private Mesh _mesh;
        private int _prepare, _resolve, _clear;
        private bool _reset = true;
        private float _retryAt, _captureRetryAt;
        private int _generation;
        private float _lastFade, _seaAltitude, _depthFar;
        private bool _hasWater;
        private int _taggedMask, _physicalMask;
        internal bool DepthRendered { get; private set; }
        internal string Status => _compute == null ? "missing-compute" :
            !DepthRendered ? "no-scene-depth" : _splashMaterial == null ? "missing-splash-shader" : "ready";

        internal void Reset() { _reset = true; DepthRendered = false; }

        private bool EnsureAssets()
        {
            bool ready = _compute != null;
            if (ready && _depthShader != null && _splashMaterial != null) return true;
            if (Time.realtimeSinceStartup < _retryAt) return ready;
            _retryAt = Time.realtimeSinceStartup + 5f;
            try
            {
                if (!ready)
                {
                    var asset = Mod.LoadVolkenAsset<ComputeShader>(AssetRoot + "RainCollision.compute");
                    if (asset == null)
                    {
                        Mod.Diag("RainCollision assets: missing RainCollision.compute; rebuild and reload the mod bundle");
                        return false;
                    }
                    _compute = UnityEngine.Object.Instantiate(asset);
                    _prepare = _compute.FindKernel("Prepare");
                    _resolve = _compute.FindKernel("Resolve");
                    _clear = _compute.FindKernel("Clear");
                }
                if (_depthShader == null)
                    _depthShader = Mod.LoadVolkenAsset<Shader>(AssetRoot + "RainCollisionDepth.shader");
                if (_splashMaterial == null)
                {
                    var splashShader = Mod.LoadVolkenAsset<Shader>(AssetRoot + "RainSplashes.shader");
                    if (splashShader != null && splashShader.isSupported)
                        _splashMaterial = new Material(splashShader) { hideFlags = HideFlags.HideAndDontSave };
                }
                Mod.Diag("RainCollision assets: compute={0} depthShader={1} splashShader={2} RFloat={3}",
                    _compute != null, _depthShader != null && _depthShader.isSupported,
                    _splashMaterial != null, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat));
                return true;
            }
            catch (Exception ex)
            {
                if (_compute != null) Destroy(_compute);
                _compute = null;
                Mod.LogThrottled("RainCollision.Assets", ex);
                return false;
            }
        }

        private void EnsureBuffers(int capacity)
        {
            if (_surfaces != null && _surfaces.count == capacity) return;
            ReleaseBuffers();
            _previous = new ComputeBuffer(capacity, 16);
            _surfaces = new ComputeBuffer(capacity, 16);
            _splashes = new ComputeBuffer(SplashCapacity, 16);
            _normals = new ComputeBuffer(SplashCapacity, 16);
            _counters = new ComputeBuffer(2, 4);
            _counters.SetData(new uint[2]);
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, 4);
            _args.SetData(new uint[] { 6, SplashCapacity, 0, 0, 0 });
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "RainSplashQuad" };
                _mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
                _mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _mesh.RecalculateBounds();
                _mesh.UploadMeshData(true);
            }
            _reset = true;
        }

        private void Capture(Camera source, Vector3 down, float radius)
        {
            DepthRendered = false;
            if (_depthShader == null || !_depthShader.isSupported ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ||
                Time.realtimeSinceStartup < _captureRetryAt) return;
            try
            {
                int resolution = RainParticles.CollisionResolution >= 384 ? 512 : 256;
                if (_depth == null || _depth.width != resolution)
                {
                    if (_depth != null) { _depth.Release(); Destroy(_depth); }
                    _depth = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    {
                        name = "RainCollisionDepth", filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp, antiAliasing = 1, hideFlags = HideFlags.HideAndDontSave
                    };
                    _depth.Create();
                }
                if (!_depth.IsCreated()) return;
                if (_capture == null)
                {
                    var go = new GameObject("VolkenRainCollisionCamera") { hideFlags = HideFlags.HideAndDontSave };
                    _capture = go.AddComponent<Camera>();
                    _capture.enabled = false;
                    _capture.orthographic = true;
                    _capture.allowHDR = false;
                    _capture.allowMSAA = false;
                    _capture.useOcclusionCulling = false;
                    _capture.depthTextureMode = DepthTextureMode.None;
                    _capture.clearFlags = CameraClearFlags.SolidColor;
                }
                float far = radius * 2f + 10f;
                _depthFar = far;
                _capture.backgroundColor = new Color(far, far, far, far);
                _capture.nearClipPlane = 0.1f;
                _capture.farClipPlane = far;
                _capture.orthographicSize = radius + 1f;
                _capture.aspect = 1f;
                _capture.targetTexture = _depth;
                Vector3 reference = Mathf.Abs(Vector3.Dot(down, Vector3.up)) < 0.95f ? Vector3.up : Vector3.forward;
                _capture.transform.SetPositionAndRotation(source.transform.position - down * (radius + 5f), Quaternion.LookRotation(down, reference));
                _taggedMask = RainParticles.StandaloneMode ? source.cullingMask : source.cullingMask & ~PhysicalLayers & ~ExcludedLayers;
                _physicalMask = RainParticles.StandaloneMode ? 0 : (source.cullingMask | (1 << TerrainLayer)) & PhysicalLayers;
                // Clear once, then preserve both color and depth for the physical-layer pass.
                // Other layers retain RenderType filtering, including alpha-cutout handling.
                _capture.clearFlags = CameraClearFlags.SolidColor;
                _capture.cullingMask = _taggedMask;
                _capture.RenderWithShader(_depthShader, "RenderType");
                if (_physicalMask != 0)
                {
                    _capture.clearFlags = CameraClearFlags.Nothing;
                    _capture.cullingMask = _physicalMask;
                    _capture.RenderWithShader(_depthShader, string.Empty);
                }
                var projection = GL.GetGPUProjectionMatrix(_capture.projectionMatrix, true);
                _compute.SetMatrix("_CollisionVP", projection * _capture.worldToCameraMatrix);
                _compute.SetMatrix("_CollisionView", _capture.worldToCameraMatrix);
                float step = 2f * _capture.orthographicSize / resolution;
                _compute.SetVector("_CollisionStepX", _capture.transform.right * step);
                bool flipY = SystemInfo.graphicsUVStartsAtTop;
                float signY = (projection.m11 < 0f ? -1f : 1f) * (flipY ? -1f : 1f);
                _compute.SetVector("_CollisionStepY", _capture.transform.up * (step * signY));
                _compute.SetInt("_DepthFlipY", flipY ? 1 : 0);
                _compute.SetInt("_DepthResolution", resolution);
                _compute.SetFloat("_DepthFar", far);
                // 仅成功渲染且矩阵上传后置位;空场景的 far 清屏值表示无表面。
                DepthRendered = true;
            }
            catch (Exception ex)
            {
                _captureRetryAt = Time.realtimeSinceStartup + 5f;
                Mod.LogThrottled("RainCollision.Capture", ex);
            }
        }

        internal bool Begin(Camera camera, ComputeBuffer positions, Material rainMaterial, Vector3 down,
            float radius, float dt, bool hasWater, float seaAltitude, float seaRadius, Vector3 seaUp)
        {
            rainMaterial.SetInt("_RainCollisionEnabled", 0);
            if (!EnsureAssets()) return false;
            EnsureBuffers(positions.count);
            _compute.SetInt("_Capacity", positions.count);
            _compute.SetInt("_SplashCapacity", SplashCapacity);
            _compute.SetFloat("_DeltaTime", dt);
            _compute.SetInt("_SplashesEnabled", RainParticles.SplashesEnabled && _splashMaterial != null ? 1 : 0);
            _compute.SetFloat("_SplashLifetime", Mathf.Clamp(RainParticles.SplashLifetime, 0.1f, 1f));
            Bind(_clear, positions);
            if (_reset)
            {
                _compute.Dispatch(_clear, Mathf.CeilToInt(Mathf.Max(positions.count, SplashCapacity) / 64f), 1, 1);
                _reset = false;
            }
            Bind(_prepare, positions);
            _compute.Dispatch(_prepare, Mathf.CeilToInt(Mathf.Max(positions.count, SplashCapacity) / 64f), 1, 1);
            Capture(camera, down, radius);
            _compute.SetInt("_DepthReady", DepthRendered ? 1 : 0);
            _compute.SetTexture(_resolve, "_CollisionDepth", _depth != null ? (Texture)_depth : Texture2D.whiteTexture);
            _compute.SetVector("_Down", down);
            _compute.SetVector("_DomainCenter", camera.transform.position);
            _compute.SetFloat("_DomainRadius", radius);
            _compute.SetFloat("_FallSpeed", RainParticles.FallSpeed);
            _compute.SetInt("_HasWater", hasWater && !float.IsNaN(seaAltitude) && !float.IsInfinity(seaAltitude) ? 1 : 0);
            _compute.SetFloat("_SeaAltitude", seaAltitude);
            _seaAltitude = seaAltitude;
            _hasWater = hasWater && !float.IsNaN(seaAltitude) && !float.IsInfinity(seaAltitude);
            _compute.SetFloat("_SeaRadius", seaRadius);
            _compute.SetVector("_SeaUp", seaUp);
            _compute.SetFloat("_SplashDistance", Mathf.Clamp(RainParticles.SplashDistance, 1f, 50f));
            _compute.SetFloat("_SplashDensity", Mathf.Clamp01(RainParticles.SplashDensity));
            _compute.SetInt("_MaxImpacts", MaxImpactsPerFrame);
            _compute.SetInt("_FrameSeed", Time.frameCount);
            _counters.SetData(Zero, 0, 1, 1);
            Bind(_resolve, positions);
            rainMaterial.SetBuffer("_RainCollisionData", _surfaces);
            rainMaterial.SetInt("_RainCollisionEnabled", 1);
            return true;
        }

        private void Bind(int kernel, ComputeBuffer positions)
        {
            _compute.SetBuffer(kernel, "_Positions", positions);
            _compute.SetBuffer(kernel, "_Previous", _previous);
            _compute.SetBuffer(kernel, "_Surfaces", _surfaces);
            _compute.SetBuffer(kernel, "_SplashPositions", _splashes);
            _compute.SetBuffer(kernel, "_SplashNormals", _normals);
            _compute.SetBuffer(kernel, "_Counters", _counters);
        }

        internal void Resolve() => _compute.Dispatch(_resolve, Mathf.CeilToInt(_surfaces.count / 64f), 1, 1);

        internal void Draw(Camera camera, float fade)
        {
            _lastFade = fade;
            if (!RainParticles.SplashesEnabled || _splashMaterial == null || _splashes == null) return;
            _splashMaterial.SetBuffer("_SplashPositions", _splashes);
            _splashMaterial.SetBuffer("_SplashNormals", _normals);
            _splashMaterial.SetFloat("_Lifetime", Mathf.Clamp(RainParticles.SplashLifetime, 0.1f, 1f));
            _splashMaterial.SetFloat("_Size", Mathf.Clamp(RainParticles.SplashSize, 0.03f, 0.5f));
            _splashMaterial.SetFloat("_Fade", fade);
            _splashMaterial.SetFloat("_MaxDistance", Mathf.Clamp(RainParticles.SplashDistance, 1f, 50f));
            var parameters = new RenderParams(_splashMaterial)
            {
                camera = camera, layer = 0, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                worldBounds = new Bounds(camera.transform.position, Vector3.one * (RainParticles.SplashDistance * 2f + 4f))
            };
            Graphics.RenderMeshIndirect(parameters, _mesh, _args);
        }

        // Requested by the diagnostics button; no automatic or synchronous GPU readback.
        internal void RequestDiagnostics(Camera camera)
        {
            Mod.Diag("RainCollision state: status={0} camera='{1}' depth={2} splashes={3} fade={4:F2} distance={5:F1}m density={6:F2} sea={7} ASL={8:F1}m",
                Status, camera.name, DepthRendered, RainParticles.SplashesEnabled, _lastFade,
                RainParticles.SplashDistance, RainParticles.SplashDensity, _hasWater, _seaAltitude);
            Mod.Diag("RainCollision capture: taggedMask=0x{0:X8} physicalMask=0x{1:X8} mode=physical-untagged-v2", _taggedMask, _physicalMask);
            if (_counters == null || !SystemInfo.supportsAsyncGPUReadback) return;
            int generation = _generation;
            string cameraName = camera.name;
            AsyncGPUReadback.Request(_counters, request =>
            {
                if (generation != _generation) return;
                if (request.hasError) { Mod.Diag("RainCollision counters: readback failed"); return; }
                var values = request.GetData<uint>();
                Mod.Diag("RainCollision impacts: camera='{0}' requestedThisFrame={1} poolCursor={2}", cameraName, values[1], values[0]);
            });
            AsyncGPUReadback.Request(_splashes, request =>
            {
                if (generation != _generation) return;
                if (request.hasError) { Mod.Diag("RainCollision splashes: readback failed"); return; }
                var values = request.GetData<Vector4>();
                int alive = 0;
                for (int i = 0; i < values.Length; i++) if (values[i].w >= 0f) alive++;
                Mod.Diag("RainCollision splashes: camera='{0}' alive={1}/{2}", cameraName, alive, SplashCapacity);
            });
            if (!DepthRendered || _depth == null) return;
            float far = _depthFar;
            AsyncGPUReadback.Request(_depth, 0, request =>
            {
                if (generation != _generation) return;
                if (request.hasError) { Mod.Diag("RainCollision depth: readback failed"); return; }
                var values = request.GetData<float>();
                int covered = 0;
                float nearest = far;
                for (int i = 0; i < values.Length; i++)
                    if (values[i] > 0f && values[i] < far - 0.05f) { covered++; nearest = Mathf.Min(nearest, values[i]); }
                Mod.Diag("RainCollision depth: camera='{0}' covered={1}/{2} nearest={3:F2}m far={4:F1}m",
                    cameraName, covered, values.Length, nearest, far);
            });
        }

        private void ReleaseBuffers()
        {
            _generation++; // Ignore diagnostic callbacks from a disposed or resized instance.
            _previous?.Release(); _previous = null;
            _surfaces?.Release(); _surfaces = null;
            _splashes?.Release(); _splashes = null;
            _normals?.Release(); _normals = null;
            _counters?.Release(); _counters = null;
            _args?.Release(); _args = null;
        }

        public void Dispose()
        {
            ReleaseBuffers();
            if (_capture != null) Destroy(_capture.gameObject);
            if (_depth != null) { _depth.Release(); Destroy(_depth); }
            if (_mesh != null) Destroy(_mesh);
            if (_splashMaterial != null) Destroy(_splashMaterial);
            if (_compute != null) Destroy(_compute);
        }

        private static void Destroy(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
