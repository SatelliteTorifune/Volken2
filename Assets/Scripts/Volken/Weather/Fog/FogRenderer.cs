using System;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;
using Volken.Clouds;

namespace Volken.Weather
{
    /// <summary>Per-camera fog, called explicitly by the opaque image-effect chain.</summary>
    public sealed class FogRenderer : IDisposable
    {
        private const string Root = "Assets/Scripts/Volken/Weather/Fog/Shader/";
        private static Texture3D _noise;
        private readonly Camera _camera;
        private Material _material;
        private ComputeShader _compute;
        private RenderTexture _volume, _heightLookup;
        private const int LookupWidth = 513, LookupHeight = 257;
        private int _lookupPass = -1;
        private int _kernel, _slices;
        private float _retry;
        private string _state;
        private bool _volumeActive;
        public bool Active { get; private set; }
        public string Status => _state ?? "not-rendered";
        public static string MainStatus { get; private set; } = "not-rendered";

        public FogRenderer(Camera camera)
        {
            _camera = camera;
        }

        public static void ClearGlobals()
        {
            Shader.SetGlobalFloat("_VolkenFogEnabled",0);
            Shader.SetGlobalFloat("_VolkenFogLookupEnabled",0);
        }

        public void BlockFrame(string reason)
        {
            Active = false;
            ClearGlobals();
            bool main = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera == _camera;
            State(reason,main);
        }

        private bool State(string reason, bool main)
        {
            if (_state != reason)
            {
                _state = reason;
            }
            if (main) MainStatus = reason;
            return Active;
        }

        public bool Prepare(int width, int height)
        {
            Active = false;
            ClearGlobals();
            bool main = false;
            try
            {
                if (Game.Instance == null || !Game.InFlightScene) return State("not-in-flight", false);
                var gameView = Game.Instance.FlightScene.ViewManager.GameView;
                main = gameView.GameCamera?.NearCamera == _camera;
                var weather = VolkenWeather.Instance;
                var cfg = weather?.Config?.fog;
                if (cfg == null || !cfg.enabled) { ReleaseVolume(); ReleaseLookup(); return State("disabled", main); }
                if (!weather.IsActive || !Clouds.VolumetricClouds.Instance.CurrentPlanet.SupportsAtmosphereEffects)
                    return State("weather-or-atmosphere-disabled", main);
                if (!main && !ModSettings.Instance.ExtraCameraFog.Value) { ReleaseVolume(); ReleaseLookup(); return State("extra-camera-disabled", false); }
                if (_camera.orthographic) return State("orthographic-camera-unsupported", main);
                bool thin = cfg.heightEnabled && cfg.density > 0;
                bool volume = cfg.volumetricEnabled && cfg.volumeDensity > 0;
                if ((!thin && !volume) || cfg.maxOpacity <= 0) { ReleaseVolume(); ReleaseLookup(); return State("zero-density-or-opacity", main); }
                if (!EnsureAssets(volume)) return State("fog-shader-missing-or-unsupported", main);

                var craft = Game.Instance.FlightScene.CraftNode;
                var frame = craft?.ReferenceFrame;
                var planet = craft?.Parent?.PlanetData;
                if (frame == null || planet == null) return State("planet-frame-not-ready", main);
                Vector3d planetPosition = frame.FrameToPlanetPosition(_camera.transform.position);
                double radius = planet.Radius;
                double altitude = planetPosition.magnitude-radius;
                if (double.IsNaN(altitude) || double.IsInfinity(altitude) || radius <= 0) return State("invalid-planet-position", main);
                if (planet.HasWater && altitude < -0.5) return State("underwater", main);
                double fogTop = Math.Max(0,cfg.baseHeight+cfg.height/cfg.heightFalloff*12);
                if (altitude > fogTop+cfg.maxDistance)
                {
                    ReleaseVolume(); ReleaseLookup();
                    return State("outside-fog-reach",main);
                }
                Vector3 radialUp = frame.PlanetToFrameVector(planetPosition.normalized).normalized;
                Matrix4x4 rotation = Matrix4x4.identity;
                Vector3d bx = frame.FrameToPlanetVector(Vector3.right);
                Vector3d by = frame.FrameToPlanetVector(Vector3.up);
                Vector3d bz = frame.FrameToPlanetVector(Vector3.forward);
                rotation.SetColumn(0,new Vector4((float)bx.x,(float)bx.y,(float)bx.z,0));
                rotation.SetColumn(1,new Vector4((float)by.x,(float)by.y,(float)by.z,0));
                rotation.SetColumn(2,new Vector4((float)bz.x,(float)bz.y,(float)bz.z,0));

                var sun = gameView.SunLight;
                Vector3 toSun = sun != null ? -sun.transform.forward : radialUp;
                float elevation = Vector3.Dot(radialUp,toSun);
                float dawn = cfg.dawnFog ? 1f + (1f-Mathf.SmoothStep(0,1,Mathf.Abs(elevation)/0.22f)) : 1f;
                Vector3 cloudLight = Volken.Clouds.CloudSceneLighting.Factors(_camera);
                Color ambient = Volken.Clouds.CloudSceneLighting.AttenuateAmbient(Volken.Clouds.CloudSceneLighting.AmbientBaseline, cloudLight.z);
                Color sunColor = Volken.Clouds.CloudSceneLighting.SunColorBaseline(sun);
                bool sunEnabled = sun != null && sun.isActiveAndEnabled;
                float sunIntensity = Volken.Clouds.CloudSceneLighting.SunIntensityBaseline(sun) * cloudLight.y;
                // JNO stores ParentPlanetOcclusion in SunLight.color.a.
                float sunVisibility = sun != null ? Mathf.Clamp01(sunColor.a) : 0;
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                var lighting = FogLighting.Evaluate(ambient, new Color(cfg.colorR,cfg.colorG,cfg.colorB,1),
                    cfg.colorBlend, sunColor, sunIntensity, sunEnabled, sunVisibility, elevation, linear);

                _volumeActive = volume && _compute != null && SystemInfo.supportsComputeShaders &&
                    SystemInfo.supports3DTextures && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf);
                if (volume && !_volumeActive && !thin) return State("volume-unavailable-no-height-fallback", main);
                if (_volumeActive) { ReleaseLookup(); EnsureVolume(width,height,main ? cfg.quality : 0); }
                else ReleaseVolume();

                // Fog starts from texture UV, unlike cloud raymarch's raster clip position.
                // The render-target Y flip must not be applied a second time here.
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(_camera.projectionMatrix,false);
                SetMatrix("_VolkenFogProjection",projection);
                SetMatrix("_VolkenFogInvProjection",projection.inverse);
                SetMatrix("_VolkenFogCameraToWorld",_camera.cameraToWorldMatrix);
                SetMatrix("_VolkenFogWorldToCamera",_camera.worldToCameraMatrix);
                SetMatrix("_VolkenFogPlanetRotation",rotation);
                Vector3 origin = _camera.transform.position;
                SetVector("_VolkenFogOrigin",new Vector4(origin.x,origin.y,origin.z,(float)altitude));
                SetVector("_VolkenFogUpRadius",new Vector4(radialUp.x,radialUp.y,radialUp.z,(float)radius));
                SetVector("_VolkenFogHeight",new Vector4(cfg.baseHeight,cfg.height/cfg.heightFalloff,thin ? cfg.density*dawn : 0,volume ? cfg.volumeDensity*dawn : 0));
                SetVector("_VolkenFogRange",new Vector4(cfg.startDistance,cfg.maxDistance,cfg.maxOpacity,0));
                SetVector("_VolkenFogAmbient",lighting.Ambient);
                SetVector("_VolkenFogDirect",lighting.Direct);
                SetVector("_VolkenFogSun",new Vector4(toSun.x,toSun.y,toSun.z,0));
                SetVector("_VolkenFogLight",new Vector4(cfg.anisotropy,0,0,0));
                double time = GamePause.Now;
                double period = cfg.noiseScale*32.0;
                double angle = cfg.windDirection*Math.PI/180;
                SetVector("_VolkenFogNoiseOffset",new Vector4(
                    Wrap(planetPosition.x-time*cfg.windSpeed*Math.Cos(angle),period),
                    Wrap(planetPosition.y,period),Wrap(planetPosition.z-time*cfg.windSpeed*Math.Sin(angle),period),0));
                SetVector("_VolkenFogShape",new Vector4(cfg.noiseScale,cfg.coverage,cfg.contrast,0));
                if (_volumeActive)
                {
                    SetVector("_VolkenFogGrid",new Vector4(_volume.width,_volume.height,_slices,0));
                    _compute.SetTexture(_kernel,"_FogOutput",_volume);
                    _compute.SetTexture(_kernel,"_FogNoise",Noise());
                    UnityEngine.Profiling.Profiler.BeginSample("Volken.Fog.Integrate");
                    try { _compute.Dispatch(_kernel,Mathf.CeilToInt(_volume.width/8f),Mathf.CeilToInt(_volume.height/8f),1); }
                    finally { UnityEngine.Profiling.Profiler.EndSample(); }
                    Shader.SetGlobalTexture("_VolkenFogVolume",_volume);
                }
                if (!_volumeActive && _lookupPass >= 0 && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
                {
                    EnsureLookup();
                    Shader.SetGlobalVector("_VolkenFogLookupSize",new Vector4(LookupWidth,LookupHeight,0,0));
                    UnityEngine.Profiling.Profiler.BeginSample("Volken.Fog.HeightLookup");
                    try { Graphics.Blit(Texture2D.blackTexture,_heightLookup,_material,_lookupPass); }
                    finally { UnityEngine.Profiling.Profiler.EndSample(); }
                    Shader.SetGlobalTexture("_VolkenFogLookup",_heightLookup);
                    Shader.SetGlobalFloat("_VolkenFogLookupEnabled",1);
                }
                Shader.SetGlobalFloat("_VolkenFogVolumeEnabled",_volumeActive ? 1 : 0);
                Shader.SetGlobalFloat("_VolkenFogEnabled",1);
                _material.SetFloat("_FogDebug",cfg.debugMode);
                Active = true;
                State(volume && !_volumeActive ? "height-only(volume-unavailable)" : _volumeActive ? (thin ? "height+volume" : "volume") : "height",main);
                return true;
            }
            catch (Exception ex)
            {
                Active = false;
                ClearGlobals();
                Mod.Log("Fog.Prepare failed: {0}", ex);
                return State("prepare-error",main);
            }
        }

        private bool EnsureAssets(bool volume)
        {
            if (_material != null && (!volume || _compute != null)) return true;
            if (Time.realtimeSinceStartup < _retry) return _material != null;
            _retry = Time.realtimeSinceStartup+5f;
            if (_material == null)
            {
                var shader = Mod.LoadVolkenAsset<Shader>(Root+"HeightFog.shader",false);
                if (shader != null && shader.isSupported)
                {
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _lookupPass = _material.FindPass("HeightLookup");
                }
            }
            if (volume && _compute == null && SystemInfo.supportsComputeShaders)
            {
                var compute = Mod.LoadVolkenAsset<ComputeShader>(Root+"VolumetricFog.compute",false);
                if (compute != null) { _compute = UnityEngine.Object.Instantiate(compute); _kernel = _compute.FindKernel("Integrate"); }
            }
            if (_material == null)
            {
                Mod.Log("Fog shader missing or unsupported; rebuild and reload the mod bundle");
            }
            if (volume && _compute == null)
            {
                Mod.Log("Fog volume unavailable: asset='{0}VolumetricFog.compute' supportsCompute={1} heightFallback={2}",Root,SystemInfo.supportsComputeShaders,_material != null);
                _retry = Time.realtimeSinceStartup+30f;
            }
            return _material != null;
        }

        private void EnsureLookup()
        {
            if (_heightLookup != null && _heightLookup.IsCreated()) return;
            ReleaseLookup();
            _heightLookup = new RenderTexture(LookupWidth,LookupHeight,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
            {
                name = "Volken Height Lookup " + _camera.GetInstanceID(), filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
            };
            if (!_heightLookup.Create()) throw new InvalidOperationException("Fog height lookup creation failed");
        }

        private void EnsureVolume(int width, int height, int quality)
        {
            int capW = quality == 0 ? 160 : quality == 1 ? 240 : 320;
            int capH = quality == 0 ? 90 : quality == 1 ? 135 : 180;
            int slices = quality == 0 ? 32 : quality == 1 ? 48 : 64;
            int w = Mathf.Clamp(Mathf.CeilToInt(width/8f),1,capW), h = Mathf.Clamp(Mathf.CeilToInt(height/8f),1,capH);
            if (_volume != null && _volume.width == w && _volume.height == h && _slices == slices && _volume.IsCreated()) return;
            ReleaseVolume();
            _slices = slices;
            _volume = new RenderTexture(w,h,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)
            {
                name = "Volken Fog " + _camera.GetInstanceID(), dimension = TextureDimension.Tex3D,
                volumeDepth = slices+1, enableRandomWrite = true, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
            };
            if (!_volume.Create()) throw new InvalidOperationException("Fog volume creation failed");
        }

        private static Texture3D Noise()
        {
            if (_noise != null) return _noise;
            byte[] bytes = new byte[32*32*32];
            var random = new System.Random(19790427);
            random.NextBytes(bytes);
            _noise = new Texture3D(32,32,32,TextureFormat.R8,false) { name="Volken Fog Noise", wrapMode=TextureWrapMode.Repeat, filterMode=FilterMode.Bilinear, hideFlags=HideFlags.HideAndDontSave };
            _noise.SetPixelData(bytes,0);
            _noise.Apply(false,true);
            return _noise;
        }

        private static float Wrap(double value, double period) => (float)((value%period+period)%period);
        private void SetVector(string name, Vector4 value) { Shader.SetGlobalVector(name,value); if (_volumeActive) _compute.SetVector(name,value); }
        private void SetMatrix(string name, Matrix4x4 value) { Shader.SetGlobalMatrix(name,value); if (_volumeActive) _compute.SetMatrix(name,value); }

        public void Render(RenderTexture source, RenderTexture destination, RenderTexture depth)
        {
            if (!Active || depth == null) { Graphics.Blit(source,destination); return; }
            _material.SetTexture("_FogSceneDepth",depth);
            UnityEngine.Profiling.Profiler.BeginSample("Volken.Fog.Composite");
            try { Graphics.Blit(source,destination,_material,0); }
            finally { UnityEngine.Profiling.Profiler.EndSample(); }
        }

        private void ReleaseLookup()
        {
            if (_heightLookup == null) return;
            _heightLookup.Release();
            UnityEngine.Object.Destroy(_heightLookup);
            _heightLookup = null;
        }

        private void ReleaseVolume()
        {
            if (_volume == null) return;
            _volume.Release();
            UnityEngine.Object.Destroy(_volume);
            _volume = null;
        }

        public void Dispose()
        {
            ReleaseVolume();
            ReleaseLookup();
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_compute != null) UnityEngine.Object.Destroy(_compute);
            Active = false;
        }
    }
}
