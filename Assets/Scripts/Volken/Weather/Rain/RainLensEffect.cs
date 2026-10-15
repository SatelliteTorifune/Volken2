using System;
using Assets.Scripts;
using UnityEngine;

namespace Volken.Weather
{
    /// <summary>主视图镜头水珠;透明物体之后合成,不写共享雨状态或云历史。</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class RainLensEffect : MonoBehaviour
    {
        internal const string ShaderPath = "Assets/Scripts/Volken/Weather/Rain/Shader/RainLens.shader";
        private static RainLensEffect _current;
        private static float _nextCameraCheck;
        private Camera _camera;
        private RainLensSimulation _simulation;
        private Material _material;
        private Mesh _mesh;
        private RenderTexture _mask;
        private Vector3[] _vertices;
        private Color[] _colors;
        private float _lastTime, _nextShelterCheck, _nextAssetTry;
        private float _spawnRate, _environmentFade = 1f;
        private bool _sheltered;
        private int _lastTickFrame = -1;
        private static readonly int MaskId = Shader.PropertyToID("_DropMask");
        private static readonly int RefractionId = Shader.PropertyToID("_RefractionPx");
        private static readonly int BlurId = Shader.PropertyToID("_BlurPx");
        private const int ShelterLayers = (1 << 26) | (1 << 29) | (1 << 31);

        /// <summary>跟随当前游戏近相机,关闭或环境失效时不挂到备用 / 菜单相机。</summary>
        public static void SyncToCurrentView()
        {
            var weather = VolkenWeather.Instance;
            bool want = Game.Instance != null && Game.InFlightScene && weather != null && weather.IsActive &&
                weather.Config?.rainLens?.enabled == true;
            if (!want)
            {
                if (_current != null && _current.enabled) _current.enabled = false;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (_current != null && now < _nextCameraCheck)
            {
                _current.enabled = true;
                return;
            }
            _nextCameraCheck = now + 0.5f;
            var camera = Game.Instance.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
            if (camera == null) return;
            if (_current != null && _current._camera != camera)
            {
                _current.enabled = false;
                Destroy(_current);
            }
            var effect = camera.GetComponent<RainLensEffect>();
            _current = effect != null ? effect : camera.gameObject.AddComponent<RainLensEffect>();
            _current.enabled = true;
        }

        public static void ClearCurrent() { if (_current != null) _current.ClearState(); }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _simulation = new RainLensSimulation(unchecked((uint)GetInstanceID()));
        }

        private void OnEnable() { _lastTime = GamePause.Now; _lastTickFrame = -1; }

        private void ClearState()
        {
            _simulation?.Clear();
            _spawnRate = 0f;
            _sheltered = false;
            _nextShelterCheck = 0f;
            _environmentFade = 1f;
            _lastTime = GamePause.Now;
        }

        private void OnPreCull()
        {
            if (_lastTickFrame == Time.frameCount) return;
            _lastTickFrame = Time.frameCount;
            if (Game.Instance == null || !Game.InFlightScene) { ClearState(); enabled = false; return; }
            if (Game.Instance.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera != _camera)
            { ClearState(); enabled = false; return; }
            var weather = VolkenWeather.Instance;
            var settings = weather?.Config?.rainLens;
            if (weather == null || !weather.IsActive || settings == null || !settings.enabled)
            { ClearState(); enabled = false; return; }
            if (GamePause.IsPaused) { _lastTime = GamePause.Now; return; }
            float now = GamePause.Now;
            float dt = Mathf.Clamp(now - _lastTime, 0f, 0.1f);
            _lastTime = now;
            try { Simulate(dt, now, weather, settings); }
            catch (Exception ex) { ClearState(); Mod.LogThrottled("RainLens.Simulate", ex); }
        }

        private void Simulate(float dt, float now, VolkenWeather weather, VolkenWeatherConfig.RainLensSection settings)
        {
            var rain = weather.Config.rain;
            var node = Game.Instance.FlightScene?.CraftNode;
            var planet = node?.Parent?.PlanetData;
            if (node?.ReferenceFrame == null || planet == null || rain == null) { ClearState(); return; }
            Vector3d radial = (Vector3d)_camera.transform.position - node.ReferenceFrame.PlanetToFramePositiond(Vector3d.zero);
            float altitude = (float)(radial.magnitude - planet.Radius);
            Vector3 up = (Vector3)radial.normalized;
            if (!Finite(up) || !Finite(altitude) || up.sqrMagnitude < 0.5f) { ClearState(); return; }
            Vector3 velocity = node.CraftScript != null ? node.CraftScript.SurfaceVelocity : Vector3.zero;
            if (!Finite(velocity)) velocity = Vector3.zero;
            float speed = velocity.magnitude;
            float fast = RainLensSimulation.Smooth((speed - settings.speedThreshold) / Mathf.Max(1f, settings.speedThreshold));
            Vector3 down = -up;
            Vector3 relativeRain = down * rain.fallSpeed - velocity;
            Vector2 gravity = Project(down);
            Vector2 air = Project(relativeRain / Mathf.Max(1f, rain.fallSpeed + speed));
            _environmentFade = RainLensSimulation.EnvironmentFade(altitude, planet.HasWater, rain);

            float exposure = RainLensSimulation.Exposure(Vector3.Dot(_camera.transform.forward, up), velocity,
                _camera.transform.forward, settings.speedThreshold);
            if (settings.shelterCheck && rain.enabled && exposure > 0f && _environmentFade > 0f && now >= _nextShelterCheck)
            {
                _nextShelterCheck = now + 0.15f;
                Vector3 incoming = relativeRain.sqrMagnitude > 0.001f ? -relativeRain.normalized : up;
                Vector3 position = _camera.transform.position;
                // 从雨来向向镜头检测,也能命中包围镜头的机体外壳;不依赖可选的 GPU 雨碰撞。
                _sheltered = Physics.Linecast(position + incoming * 60f, position, ShelterLayers, QueryTriggerInteraction.Ignore);
            }
            if (!settings.shelterCheck) _sheltered = false;
            float density = Mathf.Clamp(rain.amount / 100000f * Mathf.Pow(50f / Mathf.Max(10f, rain.domainRadius), 3f), 0f, 3f);
            float targetRate = rain.enabled && !_sheltered
                ? settings.spawnRate * settings.intensity * density * exposure * _environmentFade : 0f;
            _spawnRate = Mathf.Lerp(_spawnRate, targetRate, 1f - Mathf.Exp(-10f * dt));
            if (!rain.enabled || _sheltered || exposure <= 0f || _environmentFade <= 0f) _spawnRate = 0f;
            _simulation.Tick(dt, _spawnRate, gravity, air, fast, _camera.aspect, settings);
        }

        private Vector2 Project(Vector3 vector) => new Vector2(Vector3.Dot(vector, _camera.transform.right), Vector3.Dot(vector, _camera.transform.up));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        private bool EnsureResources(int width, int height)
        {
            if (_material == null)
            {
                if (Time.realtimeSinceStartup < _nextAssetTry) return false;
                _nextAssetTry = Time.realtimeSinceStartup + 5f;
                var shader = Mod.LoadVolkenAsset<Shader>(ShaderPath);
                if (shader == null || !shader.isSupported) return false;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                Mod.Log("RainLens: shader={0} camera='{1}' capacity={2}", _material.GetFloat("_ShaderVer"), _camera.name, RainLensSimulation.Capacity);
            }
            if (_mesh == null)
            {
                int quads = RainLensSimulation.Capacity * 2;
                _vertices = new Vector3[quads * 4];
                _colors = new Color[quads * 4];
                var uv = new Vector2[quads * 4];
                var indices = new int[quads * 6];
                for (int i = 0; i < quads; i++)
                {
                    int v = i * 4, t = i * 6;
                    uv[v] = Vector2.zero; uv[v + 1] = Vector2.right; uv[v + 2] = Vector2.one; uv[v + 3] = Vector2.up;
                    indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
                    indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3;
                }
                _mesh = new Mesh { name = "VolkenRainLens", hideFlags = HideFlags.HideAndDontSave };
                _mesh.MarkDynamic();
                _mesh.vertices = _vertices; _mesh.uv = uv; _mesh.colors = _colors; _mesh.triangles = indices;
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            }
            float scale = Mathf.Min(1f, Mathf.Min(1024f / width, 512f / height));
            int w = Mathf.Max(1, Mathf.RoundToInt(width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            if (_mask != null && (_mask.width != w || _mask.height != h || !_mask.IsCreated())) ReleaseMask();
            if (_mask == null)
            {
                _mask = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "VolkenRainLensMask", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                if (!_mask.Create()) { ReleaseMask(); return false; }
            }
            return true;
        }

        private void BuildMesh(float aspect)
        {
            for (int i = 0; i < RainLensSimulation.Capacity; i++)
            {
                var drop = _simulation.drops[i];
                if (!drop.alive)
                {
                    for (int v = i * 8; v < i * 8 + 8; v++) { _vertices[v] = Vector3.zero; _colors[v] = Color.clear; }
                    continue;
                }
                Vector2 direction = drop.velocity.sqrMagnitude > 0.000001f ? drop.velocity.normalized : Vector2.down;
                float opacity = drop.Opacity * _environmentFade;
                float stretch = 1f + Mathf.Min(0.7f, drop.velocity.magnitude * 3f);
                SetQuad(i * 8, drop.position, direction, drop.radius, drop.radius * stretch, aspect, opacity, false);
                Vector2 tailCenter = drop.position - new Vector2(direction.x / aspect, direction.y) * (drop.trail * 0.5f);
                SetQuad(i * 8 + 4, tailCenter, direction, drop.radius * 0.40f, drop.trail * 0.5f, aspect,
                    opacity * Mathf.Clamp01(drop.trail / Mathf.Max(0.001f, drop.radius)) * 0.65f, true);
            }
            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
        }

        private void SetQuad(int start, Vector2 center, Vector2 direction, float width, float height, float aspect, float opacity, bool trail)
        {
            Vector2 across = new Vector2(direction.y, -direction.x) * width;
            Vector2 along = direction * height;
            Color data = new Color(direction.x, direction.y, opacity, trail ? 1f : 0f);
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 offset = across * (corner == 0 || corner == 3 ? -1f : 1f) + along * (corner < 2 ? -1f : 1f);
                _vertices[start + corner] = new Vector3((center.x + offset.x / aspect) * 2f - 1f, (center.y + offset.y) * 2f - 1f, 0f);
                _colors[start + corner] = data;
            }
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            var settings = VolkenWeather.Instance?.Config?.rainLens;
            if (Game.Instance == null || !Game.InFlightScene || VolkenWeather.Instance?.IsActive != true ||
                settings == null || !settings.enabled || settings.intensity <= 0f || _simulation.Count == 0 || _environmentFade <= 0.001f)
            { Graphics.Blit(source, destination); return; }
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (!EnsureResources(source.width, source.height)) { Graphics.Blit(source, destination); return; }
                BuildMesh((float)source.width / source.height);
                Graphics.SetRenderTarget(_mask);
                GL.Clear(false, true, new Color(0.5f, 0.5f, 0f, 0f));
                if (!_material.SetPass(0)) { Graphics.Blit(source, destination); return; }
                Graphics.DrawMeshNow(_mesh, Matrix4x4.identity);
                _material.SetTexture(MaskId, _mask);
                _material.SetFloat(RefractionId, settings.refraction);
                _material.SetFloat(BlurId, settings.blur);
                Graphics.Blit(source, destination, _material, 1);
            }
            catch (Exception ex) { Mod.LogThrottled("RainLens.Render", ex); Graphics.Blit(source, destination); }
            finally { RenderTexture.active = previous; }
        }

        private void ReleaseMask()
        {
            if (_mask == null) return;
            _mask.Release(); Destroy(_mask); _mask = null;
        }

        private void OnDisable()
        {
            ClearState(); ReleaseMask();
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            _mesh = null; _material = null;
            _vertices = null; _colors = null;
            _nextAssetTry = 0f;
        }

        private void OnDestroy() { if (_current == this) _current = null; }
    }
}
