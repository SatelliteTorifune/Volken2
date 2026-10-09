using System;
using System.Collections.Generic;
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
        private static readonly HashSet<FogRenderer> Instances = new HashSet<FogRenderer>();
        private static Texture3D _noise;
        private readonly Camera _camera;
        private Material _material;
        private ComputeShader _compute;
        private RenderTexture _volume, _heightLookup;
        private const int LookupWidth = 513, LookupHeight = 257;
        private int _lookupPass = -1, _lookupBuilds, _allocations, _perfFrames, _lastPerfFrame = -1;
        private float _perfSeconds, _perfMaxFrame, _nextPerfLog;
        private double _prepareCpuMs, _compositeCpuMs;
        private string _perfState;
        private readonly FrameTiming[] _frameTiming = new FrameTiming[1];
        private bool _assetStateLogged, _loggedComputeAvailable, _loggedShaderAvailable, _trackTimings;
        private int _kernel, _slices, _draws;
        private float _retry, _nextLog;
        private string _state;
        private bool _volumeActive;
        private bool _diagnoseNextDraw;
        private int _resourceVersion;
        public bool Active { get; private set; }
        public string Status => _state ?? "not-rendered";
        public static string MainStatus { get; private set; } = "not-rendered";

        public FogRenderer(Camera camera)
        {
            _camera = camera;
            Instances.Add(this);
            Mod.Diag("Fog attached: camera='{0}' id={1}", camera.name, camera.GetInstanceID());
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
                Mod.Diag("Fog state: camera='{0}' {1} -> {2}", _camera.name, _state ?? "new", reason);
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
            long prepareStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (Game.Instance == null || !Game.InFlightScene) return State("not-in-flight", false);
                var gameView = Game.Instance.FlightScene.ViewManager.GameView;
                main = gameView.GameCamera?.NearCamera == _camera;
                var weather = VolkenWeather.Instance;
                var cfg = weather?.Config?.fog;
                if (cfg == null || !cfg.enabled) { ReleaseVolume(); ReleaseLookup(); return State("disabled", main); }
                if (!weather.IsActive || !VolkenClouds.Instance.CurrentPlanet.SupportsAtmosphereEffects)
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
                Color ambient = RenderSettings.ambientLight;
                Color sunColor = sun != null ? sun.color : Color.black;
                bool sunEnabled = sun != null && sun.isActiveAndEnabled;
                float sunIntensity = sun != null ? sun.intensity : 0;
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
                    _lookupBuilds++;
                    Shader.SetGlobalTexture("_VolkenFogLookup",_heightLookup);
                    Shader.SetGlobalFloat("_VolkenFogLookupEnabled",1);
                }
                Shader.SetGlobalFloat("_VolkenFogVolumeEnabled",_volumeActive ? 1 : 0);
                Shader.SetGlobalFloat("_VolkenFogEnabled",1);
                _material.SetFloat("_FogDebug",cfg.debugMode);
                Active = true;
                _draws++;
                State(volume && !_volumeActive ? "height-only(volume-unavailable)" : _volumeActive ? (thin ? "height+volume" : "volume") : "height",main);
                if (cfg.diagnostics && Time.realtimeSinceStartup >= _nextLog)
                {
                    _nextLog = Time.realtimeSinceStartup+5f;
                    Mod.Diag("Fog path: camera='{0}' integration={1} lookupBuilds={2} allocations={3}",_camera.name,_volumeActive ? "volume-grid" : _heightLookup != null ? "height-lookup-513x257" : "height-analytic-fallback",_lookupBuilds,_allocations);
                    Mod.Diag("Fog render: camera='{0}' mode={1} size={2}x{3} altitude={4:F1}m H={5:F1} density={6:F6}/{7:F6} max={8:F0}m opacity={9:F2} dawn={10:F2} draws={11} grid={12} debug={13}",
                        _camera.name,Status,width,height,altitude,cfg.height/cfg.heightFalloff,cfg.density,cfg.volumeDensity,cfg.maxDistance,cfg.maxOpacity,dawn,_draws,
                        _volume != null ? _volume.width+"x"+_volume.height+"x"+(_slices+1) : "none",cfg.debugMode);
                    Mod.Diag("Fog lighting: camera='{0}' colorSpace={1} ambientMode={2} sunElevation={3:F2}deg daylight={4:F4} sunEnabled={5} sunIntensity={6:F4} sunVisibility={7:F4} ambientRaw={8} ambientWorking={9} sunRaw={10} ambientScatter={11} sunScatter={12}",
                        _camera.name,QualitySettings.activeColorSpace,RenderSettings.ambientMode,
                        Mathf.Asin(Mathf.Clamp(elevation,-1,1))*Mathf.Rad2Deg,lighting.Daylight,
                        sunEnabled,sunIntensity,sunVisibility,ambient.ToString("F5"),
                        FogLighting.WorkingColor(ambient,linear).ToString("F5"),sunColor.ToString("F5"),
                        lighting.Ambient.ToString("F5"),lighting.Direct.ToString("F5"));
                }
                return true;
            }
            catch (Exception ex)
            {
                Active = false;
                ClearGlobals();
                Mod.LogThrottled("Fog.Prepare",ex);
                return State("prepare-error",main);
            }
            finally
            {
                _trackTimings = main && (VolkenWeather.Instance?.Config?.fog?.diagnostics ?? false);
                if (_trackTimings)
                    RecordFrameTiming(ElapsedMs(prepareStart));
                else { _perfState = null; _perfFrames = 0; }
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
            if (!_assetStateLogged || _loggedComputeAvailable != (_compute != null) || _loggedShaderAvailable != (_material != null))
            {
                _assetStateLogged = true;
                _loggedComputeAvailable = _compute != null;
                _loggedShaderAvailable = _material != null;
                Mod.Diag("Fog assets: camera='{0}' shader={1} compute={2} supportsCompute={3} supports3D={4}",_camera.name,_material!=null,_compute!=null,SystemInfo.supportsComputeShaders,SystemInfo.supports3DTextures);
                if (_material != null) Mod.Diag("Fog build: code=3 shader={0} api={1} colorSpace={2} nativeWater=unadapted reflectionFog=unadapted",
                    _material.HasProperty("_FogBuild") ? _material.GetFloat("_FogBuild") : -1,SystemInfo.graphicsDeviceType,QualitySettings.activeColorSpace);
            }
            if (volume && _compute == null)
            {
                Mod.Diag("Fog volume unavailable: camera='{0}' asset='{1}VolumetricFog.compute' retrySeconds=30 heightFallback={2}",_camera.name,Root,_material != null);
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
            _allocations++;
            Mod.Diag("Fog allocate: camera='{0}' heightLookup={1}x{2} memory~{3:F2}MiB",_camera.name,LookupWidth,LookupHeight,LookupWidth*LookupHeight*4.0/1048576);
        }

        private static double ElapsedMs(long start) => (System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;

        private void RecordFrameTiming(double prepareMs)
        {
            if (_lastPerfFrame == Time.frameCount) return;
            _lastPerfFrame = Time.frameCount;
            if (FrameTimingManager.IsFeatureEnabled()) FrameTimingManager.CaptureFrameTimings();
            if (_perfState != Status)
            {
                WriteFrameTiming();
                _perfState = Status;
                _perfFrames = 0; _perfSeconds = 0; _perfMaxFrame = 0; _prepareCpuMs = 0; _compositeCpuMs = 0;
                _nextPerfLog = Time.realtimeSinceStartup+5;
                return;
            }
            float seconds = Time.unscaledDeltaTime;
            if (seconds <= 0) return;
            _perfFrames++; _perfSeconds += seconds; _perfMaxFrame = Mathf.Max(_perfMaxFrame,seconds);
            _prepareCpuMs += prepareMs;
            if (Time.realtimeSinceStartup < _nextPerfLog) return;
            WriteFrameTiming();
            _perfFrames = 0; _perfSeconds = 0; _perfMaxFrame = 0; _prepareCpuMs = 0; _compositeCpuMs = 0;
            _nextPerfLog = Time.realtimeSinceStartup+5;
        }

        private void WriteFrameTiming()
        {
            if (_perfFrames < 10 || _perfSeconds <= 0) return;
            double gpu = -1, cpu = -1;
            if (FrameTimingManager.IsFeatureEnabled() && FrameTimingManager.GetLatestTimings(1,_frameTiming) > 0)
            {
                if (_frameTiming[0].gpuFrameTime > 0) gpu = _frameTiming[0].gpuFrameTime;
                if (_frameTiming[0].cpuMainThreadFrameTime > 0) cpu = _frameTiming[0].cpuMainThreadFrameTime;
            }
            Mod.Diag("Fog perf: camera='{0}' mode={1} frames={2} seconds={3:F2} avgFrameMs={4:F2} maxFrameMs={5:F2} fps={6:F1} prepareSubmitCpuMs={7:F3} compositeSubmitCpuMs={8:F3} allocations={9} (whole-frame timing; submit CPU is not GPU time)",
                _camera.name,_perfState,_perfFrames,_perfSeconds,_perfSeconds*1000/_perfFrames,_perfMaxFrame*1000,_perfFrames/_perfSeconds,_prepareCpuMs/_perfFrames,_compositeCpuMs/_perfFrames,_allocations);
            Mod.Diag("Fog frame timing: latestGpuFrameMs={0:F3} latestCpuMainMs={1:F3} (-1=unavailable; whole game frame, not isolated fog)",gpu,cpu);
            var weather = VolkenWeather.Instance;
            var rain = weather?.Config?.rain;
            Mod.Diag("Fog context: rainEnabled={0} rainCapacity={1} collision={2} splashes={3} strikesTotal={4} fov={5:F1} altitude={6:F1}m activeBolts={7} fogEditsTotal={8}",
                RainParticles.Enabled,RainParticles.Capacity,rain?.collisionEnabled ?? false,rain?.splashesEnabled ?? false,
                weather?.Lightning?.BoltCount ?? 0,_camera.fieldOfView,weather?.CameraAltitudeAsl ?? 0,
                LightningBolt.ActiveCount,WeatherPanel.FogEditCount);
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
            _allocations++;
            Mod.Diag("Fog allocate: camera='{0}' grid={1}x{2}x{3} memory~{4:F2}MiB",_camera.name,w,h,slices+1,w*h*(slices+1)*8.0/1048576);
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
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Graphics.Blit(source,destination,_material,0); }
            finally { if (_trackTimings) _compositeCpuMs += ElapsedMs(start); UnityEngine.Profiling.Profiler.EndSample(); }
            if (_diagnoseNextDraw)
            {
                _diagnoseNextDraw = false;
                try { InspectGpu(depth); }
                catch (Exception ex) { Mod.LogThrottled("Fog.GpuDiagnostics",ex); }
            }
        }

        public static void DiagnoseAll()
        {
            Mod.Diag("Fog diagnostics: instances={0} graphics={1} device='{2}'",Instances.Count,SystemInfo.graphicsDeviceType,SystemInfo.graphicsDeviceName);
            foreach (var item in Instances)
            {
                Mod.Diag("Fog snapshot: camera='{0}' state={1} active={2} draws={3} volume={4}",item._camera != null ? item._camera.name : "destroyed",item.Status,item.Active,item._draws,item._volume != null);
                item._nextLog = 0;
                item._diagnoseNextDraw = true;
            }
        }

        private void InspectGpu(RenderTexture depth)
        {
            if (!SystemInfo.supportsAsyncGPUReadback) { Mod.Diag("Fog GPU: async readback unsupported"); return; }
            string cameraName = _camera.name;
            int version = _resourceVersion;
            int width = Math.Min(9,depth.width), height = Math.Min(9,depth.height);
            Mod.Diag("Fog GPU request: camera='{0}' depth={1}x{2} format={3} volume={4}",cameraName,depth.width,depth.height,depth.format,_volumeActive);
            AsyncGPUReadback.Request(depth,0,(depth.width-width)/2,width,(depth.height-height)/2,height,0,1,TextureFormat.RFloat,request =>
            {
                if (!Instances.Contains(this) || version != _resourceVersion) return;
                if (request.hasError) { Mod.Diag("Fog GPU depth: camera='{0}' readback-error",cameraName); return; }
                var data = request.GetData<float>();
                float min = float.MaxValue, max = 0; int invalid = 0;
                for (int i=0;i<data.Length;i++)
                {
                    float d=data[i];
                    if (float.IsNaN(d) || float.IsInfinity(d) || d<0) invalid++;
                    else { min=Math.Min(min,d); max=Math.Max(max,d); }
                }
                Mod.Diag("Fog GPU depth: camera='{0}' samples={1} center={2:F2}m range={3:F2}..{4:F2}m invalid={5}",cameraName,data.Length,data[data.Length/2],min,max,invalid);
            });
            if (!_volumeActive || _volume == null) return;
            AsyncGPUReadback.Request(_volume,0,_volume.width/2,1,_volume.height/2,1,0,_slices+1,TextureFormat.RGBAFloat,request =>
            {
                if (!Instances.Contains(this) || version != _resourceVersion) return;
                if (request.hasError) { Mod.Diag("Fog GPU volume: camera='{0}' readback-error",cameraName); return; }
                float min=1, max=0, maxS=0, previous=1; int invalid=0, nonMonotonic=0;
                for (int i=0;i<request.layerCount;i++)
                {
                    Color c=request.GetData<Color>(i)[0];
                    if (float.IsNaN(c.r+c.g+c.b+c.a) || float.IsInfinity(c.r+c.g+c.b+c.a) || c.a<0 || c.a>1) invalid++;
                    min=Math.Min(min,c.a); max=Math.Max(max,c.a); maxS=Math.Max(maxS,Math.Max(c.r,Math.Max(c.g,c.b)));
                    if (i>0 && c.a>previous+0.002f) nonMonotonic++;
                    previous=c.a;
                }
                Mod.Diag("Fog GPU volume: camera='{0}' slices={1} T={2:F4}..{3:F4} lastT={4:F4} maxS={5:F4} invalid={6} nonMonotonic={7}",cameraName,request.layerCount,min,max,previous,maxS,invalid,nonMonotonic);
            });
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
            _resourceVersion++;
            _volume.Release();
            UnityEngine.Object.Destroy(_volume);
            _volume = null;
        }

        public void Dispose()
        {
            Mod.Diag("Fog release: camera='{0}' draws={1}",_camera != null ? _camera.name : "destroyed",_draws);
            WriteFrameTiming();
            ReleaseVolume();
            ReleaseLookup();
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_compute != null) UnityEngine.Object.Destroy(_compute);
            Instances.Remove(this);
            Active = false;
        }
    }
}
