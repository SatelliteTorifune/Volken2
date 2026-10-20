using System;
using System.Collections.Generic;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;
/*
    云渲染主流程:合并远近相机深度 → 逐层低清 raymarch(+ 云面深度/MV 的 MRT)→ 全清时序上采样 → 逐层合成。
     每个 CloudRenderer 实例 = 一台相机,场景深度由相机持有,云历史由 CloudLayerView 持有。
     风/自转是全球共享量,每帧只能推进一次(AdvanceGlobalCloudState),多相机下推两次 = 云速翻倍。
     [ImageEffectOpaque] 必须保留:云要在不透明之后、透明之前合成,否则会盖住水面/玻璃/UI。
*/

namespace Volken.Clouds
{

    public class CloudRenderer : MonoBehaviour
    {
        // 本相机独享的共享深度 RT
        private RenderTexture combinedDepthTex;
        private RenderTexture lowResDepthTex;
        private RenderTexture historyDepthTex;
        private long _historyDepthVersion;
        private Camera cam;
        private Material _sceneDepthMaterial;
        private Volken.Weather.FogRenderer _fog;
        private readonly CloudSunOcclusion _sunOcclusion = new CloudSunOcclusion();
        private readonly CloudGroundShadows _groundShadows = new CloudGroundShadows();
        internal Vector3 SunOcclusionFactors => SunOcclusionAllowed ? _sunOcclusion.Factors : Vector3.one;
        internal string SunOcclusionState => SunOcclusionAllowed ? _sunOcclusion.State : "inactive";
        internal string GroundShadowState => SunOcclusionAllowed ? _groundShadows.State : "inactive";
        internal CloudGroundShadows.View PrepareGroundShadows(Camera camera, Light sun) =>
            SunOcclusionAllowed ? _groundShadows.Prepare(camera, sun) : null;
        private bool SunOcclusionAllowed => Game.InFlightScene && (cam == Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera ||
            ModSettings.Instance != null && ModSettings.Instance.ExtraCameraClouds.Value);

        internal void PrepareSunOcclusion()
        {
            if (!SunOcclusionAllowed) return;
            _sunOcclusion.Tick(cam);
        }
        private static Mesh _fullscreenTriangle; // 全屏三角形(一次构建,所有相机复用)

        // 本相机的远相机深度源(主视角 = 游戏 FarCamera;PIP = 与克隆主相机共享 targetTexture 的更低 depth 相机)
        public FarCameraScript farDepthSource;

        private float _nextFarDepthSearchTime = -1f;   // 秒:解析失败后的下次重试时刻(见 TryResolveFarDepthSource)

        private readonly Dictionary<CloudLayer, CloudLayerView> _views = new Dictionary<CloudLayer, CloudLayerView>();

        // 每帧复用的扫描缓冲:渲染路径每相机每帧都跑,不许在这里 new List / LINQ
        private readonly List<CloudLayer> _activeLayers = new List<CloudLayer>();
        private readonly List<CloudLayer> _staleViewKeys = new List<CloudLayer>();
        private readonly List<CloudLayerView> _activeViews = new List<CloudLayerView>();

        // 本相机本帧的线性场景深度(远+近合并后的 RFloat,单位 = LinearEyeDepth 米);雨软粒子等消费者直接取
        public RenderTexture LinearSceneDepth => combinedDepthTex;

        // 全球风/自转推进去重:同一帧只推进一次(所有相机共享同一全局云状态;推进两次 = 云速翻倍)
        private static int _lastGlobalAdvanceFrame = -1;

        private const float kTssFreshBlend = 0.5f;  // 静止时本帧权重;运动时 shader 增大该权重。

        public CloudRenderer()
        {
            cam = GetComponent<Camera>();
            CloudRenderManualRefresh();

            // 切天体 → 本相机历史作废。只订阅唯一的行星事件源:自己监听 PlayerChangedSoi 会漏退订(销毁后实例仍被事件链引用)
            if (VolumetricClouds.Instance != null)
            {
                VolumetricClouds.Instance.PlanetChanged += OnPlanetChanged;
            }

            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered += OnReferenceFrameRecentered;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer cannot subscribe ReferenceFrameRecentered: " + ex.Message);
            }
        }

        private void OnEnable()
        {
            if (cam == null) cam = GetComponent<Camera>();
            // 额外相机不会被游戏开 depthTextureMode,而 Clouds.shader 的 NearDepth pass 要采样 _CameraDepthTexture
            if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;
            TryResolveFarDepthSource();
            CloudSceneLighting.Register(this);
        }

        private void OnPreRender() => Volken.Weather.FogRenderer.ClearGlobals();
        private void OnPostRender() => Volken.Weather.FogRenderer.ClearGlobals();
        private void OnDisable()
        {
            CloudSceneLighting.Unregister(this);
            _sunOcclusion.Dispose();
            _groundShadows.Dispose();
            foreach (var view in _views.Values) view.ClearHistory();
            Volken.Weather.FogRenderer.ClearGlobals();
            _fog?.Dispose();
            _fog = null;
        }

        private void OnPlanetChanged(PlanetEnvironment env)
        {
            _sunOcclusion.Invalidate();
            _groundShadows.Dispose();
            // 不要回写 layer.config.enabled 做大气门控(会污染玩家预设);环境抑制由 Clouds 统一置位
            foreach (var view in _views.Values)
            {
                view.ClearHistory();
            }
        }

        // 浮动原点重置(离帧中心 >5000m / 帧速 >1000m/s / 时间加速 / 表面锁定切换)时世界坐标整体平移,重投影错位
        private void OnReferenceFrameRecentered(ModApi.Flight.GameView.IReferenceFrame referenceFrame, Vector3d positionDelta, Vector3d velocityDelta)
        {
            if (!CloudLayerView.HasRecenterMotion(positionDelta.magnitude, velocityDelta.magnitude)) return;
            _sunOcclusion.RequestRefresh();
            try
            {
                Mod.Log("Volken:CloudRenderer frame recentered Δ=" + positionDelta.magnitude.ToString("F1") + "m — TSS history cleared");
                foreach (var view in _views.Values)
                {
                    view.frameNumber = 0;
                    view.ClearHistory();
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer OnReferenceFrameRecentered ERROR: " + ex.Message);
            }
        }

        public void CloudRenderManualRefresh()
        {
            BuildViews();
            CreateSharedRenderTextures();
            SetAllLayersShaderProperties();
            foreach (var kv in _views)
            {
                if (kv.Key != null && kv.Key.config != null)
                {
                    kv.Value.ClearHistory();
                }
            }
        }

        private void BuildViews()
        {
            var clouds = Volken.Clouds.VolumetricClouds.Instance;
            if (clouds == null) return;

            // 先收集再删,避免边遍历 _views.Keys 边 Remove(不用 LINQ:本方法每帧被调)
            _staleViewKeys.Clear();
            foreach (var key in _views.Keys)
            {
                if (!clouds.layers.Contains(key)) _staleViewKeys.Add(key);
            }
            foreach (var k in _staleViewKeys)
            {
                _views[k].ReleaseRenderTextures();
                _views.Remove(k);
            }
            foreach (var layer in clouds.layers)
            {
                if (layer != null && !_views.ContainsKey(layer)) _views[layer] = new CloudLayerView(layer);
            }
        }

        private CloudLayerView GetView(CloudLayer layer)
        {
            CloudLayerView view;
            _views.TryGetValue(layer, out view);
            return view;
        }

        private void CreateSharedRenderTextures()
        {
            var res = Screen.currentResolution;
            EnsureDepthTextures(Mathf.Max(1, res.width), Mathf.Max(1, res.height));
            EnsureLowResDepthTex(new Vector2Int(Mathf.Max(1, res.width / 2), Mathf.Max(1, res.height / 2)));
        }

        // 按本相机输出尺寸创建全清深度纹理(不要写死 Screen.currentResolution)
        private void EnsureDepthTextures(int renderW, int renderH)
        {
            if (combinedDepthTex != null && combinedDepthTex.IsCreated() &&
                combinedDepthTex.width == renderW && combinedDepthTex.height == renderH)
                return;

            if (combinedDepthTex != null)
            {
                combinedDepthTex.Release();
                Destroy(combinedDepthTex);
            }
            combinedDepthTex = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat);
            combinedDepthTex.Create();
        }

        private void EnsureLowResDepthTex(Vector2Int targetSize)
        {
            if (lowResDepthTex != null &&
                lowResDepthTex.IsCreated() &&
                lowResDepthTex.width == targetSize.x &&
                lowResDepthTex.height == targetSize.y)
                return;

            if (lowResDepthTex != null)
            {
                lowResDepthTex.Release();
                Destroy(lowResDepthTex);
            }

            lowResDepthTex = new RenderTexture(Mathf.Max(1, targetSize.x), Mathf.Max(1, targetSize.y), 0, RenderTextureFormat.RFloat);
            lowResDepthTex.Create();
        }

        private static bool NeedsTemporalHistory(CloudLayerView view) =>
            view.orbitFade < 0.999f && (view.layer.config.useTemporalUpscale || view.layer.config.historyBlend > 0f);

        private void PrepareCloudHistory(int renderW, int renderH)
        {
            bool needsHistory = false;
            foreach (var view in _activeViews)
                if (NeedsTemporalHistory(view)) { needsHistory = true; break; }

            if (needsHistory && (historyDepthTex == null || !historyDepthTex.IsCreated() ||
                historyDepthTex.width != renderW || historyDepthTex.height != renderH))
            {
                if (historyDepthTex != null)
                {
                    historyDepthTex.Release();
                    Destroy(historyDepthTex);
                }
                historyDepthTex = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat);
                historyDepthTex.name = "CloudSceneDepthHistory";
                historyDepthTex.Create();
                _historyDepthVersion++;
            }

            foreach (var view in _activeViews)
            {
                if (NeedsTemporalHistory(view)) view.PrepareHistory(_historyDepthVersion);
                else view.historyDepthVersion = -1;
            }
        }

        // 所有层先读取上一快照,再一次性写回本相机深度;云距离保持原全清 Point 格点。
        private void UpscaleClouds(Material matRef)
        {
            int upscalePass = matRef.FindPass("Upscale");
            bool writeHistory = false;
            foreach (var view in _activeViews)
            {
                if (view.orbitFade >= 0.999f) continue;
                var mat = view.layer.material;
                mat.SetTexture("CloudDepthTex", view.cloudDepthTex);
                mat.SetTexture("CloudMVTex", view.cloudMVTex);
                mat.SetTexture("CombinedDepthTex", combinedDepthTex);
                mat.SetTexture("HistoryTex", view.historyTex);
                mat.SetTexture("HistoryDepthTex", view.historyDepthVersion == _historyDepthVersion && historyDepthTex != null
                    ? (Texture)historyDepthTex : Texture2D.blackTexture);
                mat.SetTexture("HistoryCloudDepthTex", view.historyCloudDepthTex);
                Graphics.Blit(view.cloudTex, view.upscaledCloudTex, mat, upscalePass);
                if (NeedsTemporalHistory(view))
                {
                    Graphics.Blit(view.cloudDepthTex, view.historyCloudDepthTex);
                    writeHistory = true;
                }
            }
            if (!writeHistory) return;
            Graphics.Blit(combinedDepthTex, historyDepthTex);
            _historyDepthVersion++;
            foreach (var view in _activeViews)
                if (NeedsTemporalHistory(view)) view.historyDepthVersion = _historyDepthVersion;
        }

        private void InvalidateCloudHistory()
        {
            _historyDepthVersion++;
            foreach (var view in _views.Values) view.ClearHistory();
        }

        private void ReleaseAllRenderTextures()
        {
            if (combinedDepthTex != null)
            {
                combinedDepthTex.Release();
                Destroy(combinedDepthTex);
                combinedDepthTex = null;
            }
            if (lowResDepthTex != null)
            {
                lowResDepthTex.Release();
                Destroy(lowResDepthTex);
                lowResDepthTex = null;
            }
            if (historyDepthTex != null)
            {
                historyDepthTex.Release();
                Destroy(historyDepthTex);
                historyDepthTex = null;
            }
            _historyDepthVersion++;

            foreach (var view in _views.Values)
            {
                view?.ReleaseRenderTextures();
            }
        }

        public void SetAllLayersShaderProperties()
        {
            try
            {
                foreach (var layer in Volken.Clouds.VolumetricClouds.Instance.layers)
                {
                    if (layer?.config == null || layer.material == null) continue;

                    layer.SetStaticShaderProperties();
                }
            }
            catch (Exception ex)
            {
                Mod.Log("CloudRenderer.ShaderProperties failed: {0}", ex);
            }
        }

        /// <summary>解析本实例的远深度源:与 cam 共享 targetTexture 的更低 depth 相机(PIP 克隆相机,优先子相机),否则回退游戏 FarCamera。</summary>
        public void TryResolveFarDepthSource()
        {
            if (farDepthSource != null) return;

            // OnRenderImage 每帧都会调到这里:解析不出来时(该相机没有可用的远深度相机)把重试压到 1s 一次,
            // 否则每帧都在做 GetComponentsInChildren + 全场景找相机
            if (Time.realtimeSinceStartup < _nextFarDepthSearchTime) return;
            _nextFarDepthSearchTime = Time.realtimeSinceStartup + 1f;

            try
            {
                Camera spaceCam = null;
                if (cam != null)
                {
                    foreach (Camera child in GetComponentsInChildren<Camera>(true))
                    {
                        if (child == cam) continue;
                        if (child.targetTexture == cam.targetTexture && child.depth < cam.depth)
                        {
                            spaceCam = child;
                            break;
                        }
                    }
                }
                if (spaceCam == null && cam != null && cam.targetTexture != null)
                {
                    foreach (Camera other in Camera.allCameras)
                    {
                        if (other == null || other == cam) continue;
                        if (other.targetTexture == cam.targetTexture && other.depth < cam.depth)
                        {
                            spaceCam = other;
                            break;
                        }
                    }
                }
                if (spaceCam != null)
                {
                    var fcs = spaceCam.GetComponent<FarCameraScript>();
                    if (fcs == null) fcs = spaceCam.gameObject.AddComponent<FarCameraScript>();
                    farDepthSource = fcs;
                    return;
                }
                var mainCamera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
                if (cam == mainCamera && Volken.Clouds.VolumetricClouds.Instance?.farCam != null)
                    farDepthSource = Volken.Clouds.VolumetricClouds.Instance.farCam;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer.TryResolveFarDepthSource ERROR: " + ex.Message);
            }
        }

        /// <summary>全球风/自转推进:每帧只执行一次。放 SetLayerDynamicProperties(每相机每帧调用)会让多相机云速成倍加速。</summary>
        internal static void AdvanceGlobalCloudState()
        {
            if (_lastGlobalAdvanceFrame == Time.frameCount) return;
            _lastGlobalAdvanceFrame = Time.frameCount;

            if (Volken.Clouds.VolumetricClouds.Instance == null) return;
            var craftNode = Game.Instance?.FlightScene?.CraftNode;
            if (craftNode == null || craftNode.ReferenceFrame == null || craftNode.CraftScript == null) return;
            float deltaTime = (float)Game.Instance.FlightScene.TimeManager.DeltaTime;

            foreach (var layer in Volken.Clouds.VolumetricClouds.Instance.layers)
            {
                if (layer?.config == null) continue;

                Vector3 north = craftNode.ReferenceFrame.PlanetToFrameVector(craftNode.CraftScript.FlightData.North);
                Vector3 east = craftNode.ReferenceFrame.PlanetToFrameVector(craftNode.CraftScript.FlightData.East);
                float rad = Mathf.Deg2Rad * layer.config.windDirection;
                Vector3 windDir = Mathf.Cos(rad) * north + Mathf.Sin(rad) * east;
                float speedFactor = GetWindSpeedFactor(layer.config.windDirection);

                // 累加到 runningOffset,不要直接改 config.offset(会污染序列化预设)
                layer.runningOffset += layer.config.windSpeed * 0.1f * speedFactor * deltaTime * windDir;
                layer.runningOffset.x -= Mathf.Floor(layer.runningOffset.x);
                layer.runningOffset.y -= Mathf.Floor(layer.runningOffset.y);
                layer.runningOffset.z -= Mathf.Floor(layer.runningOffset.z);

                layer.accumulatedRotation += layer.config.globalRotationAngular * 5e-4f * deltaTime;
            }
        }

        internal static RenderTextureFormat CloudColorFormat => RenderTextureFormat.ARGB32;
        internal static bool CloudLightningEnabled =>
            Volken.Weather.VolkenWeather.Instance?.Config?.lightning?.cloudLightingEnabled == true &&
            SystemInfo.supportedRenderTargetCount >= 4;

        // 游戏环境色按当前颜色空间转换一次,不额外施加太阳高度或亮度下限。
        internal static Vector4 CloudAmbientColor(Color ambient, bool linear)
        {
            float r = float.IsNaN(ambient.r) || float.IsInfinity(ambient.r) ? 0f : Mathf.Max(0f, ambient.r);
            float g = float.IsNaN(ambient.g) || float.IsInfinity(ambient.g) ? 0f : Mathf.Max(0f, ambient.g);
            float b = float.IsNaN(ambient.b) || float.IsInfinity(ambient.b) ? 0f : Mathf.Max(0f, ambient.b);
            var color = new Color(r, g, b, 1f);
            if (linear) color = color.linear;
            return new Vector4(color.r, color.g, color.b, 0f);
        }

        internal static void SetCloudAmbientProperties(Material mat) =>
            mat.SetVector("_CloudAmbientColor", CloudAmbientColor(CloudSceneLighting.AmbientBaseline,
                QualitySettings.activeColorSpace == ColorSpace.Linear));

        internal static void SetCloudWorldProperties(Material mat, CloudLayer layer)
        {
            var node = Game.Instance.FlightScene.CraftNode;
            var frame = node.ReferenceFrame;
            mat.SetVector("sphereCenter", frame.PlanetToFramePosition(Vector3d.zero));
            mat.SetFloat("surfaceRadius", (float)node.Parent.PlanetData.Radius);
            var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;
            mat.SetVector("lightDir", sun.transform.forward);
            Color sunColor = CloudSceneLighting.SunColorBaseline(sun);
            var solar = CloudAmbientColor(sunColor, QualitySettings.activeColorSpace == ColorSpace.Linear);
            float amount = sun.isActiveAndEnabled ? CloudSceneLighting.SunIntensityBaseline(sun) * Mathf.Clamp01(sunColor.a) : 0f;
            mat.SetVector("_CloudSolarColor", solar * amount);
            mat.SetFloat("currentRotation", layer.accumulatedRotation);
            mat.SetVector("cloudOffset", layer.runningOffset);
            mat.SetTexture("StockCloudCube", StockCloudMap.Current);
            var bx = frame.FrameToPlanetVector(Vector3.right);
            var by = frame.FrameToPlanetVector(Vector3.up);
            var bz = frame.FrameToPlanetVector(Vector3.forward);
            var rotation = Matrix4x4.identity;
            rotation.SetColumn(0, new Vector4((float)bx.x, (float)bx.y, (float)bx.z, 0));
            rotation.SetColumn(1, new Vector4((float)by.x, (float)by.y, (float)by.z, 0));
            rotation.SetColumn(2, new Vector4((float)bz.x, (float)bz.y, (float)bz.z, 0));
            mat.SetMatrix("planetToBody", rotation);
        }

        internal static Vector4 SetCloudLightningProperties(Material mat, bool reflection)
        {
            Vector4 positionRange = Vector4.zero, colorIntensity = Vector4.zero;
            var weather = Volken.Weather.VolkenWeather.Instance;
            var cfg = weather?.Config?.lightning;
            int samples = 0;
            if (Game.InFlightScene && weather?.IsActive == true && cfg != null && cfg.enabled &&
                cfg.cloudLightingEnabled && !Volken.Weather.GamePause.IsPaused)
            {
                float intensity = Mathf.Clamp(cfg.cloudLightIntensity, 0f, 100f);
                float range = Mathf.Clamp(cfg.cloudLightRange, 1f, 30000f);
                if (!float.IsNaN(range) && !float.IsInfinity(range) &&
                    Volken.Weather.LightningBolt.TryGetCloudLight(intensity, range, out positionRange, out colorIntensity))
                    samples = reflection ? Mathf.Clamp(cfg.cloudLightSamples, 2, 4) : Mathf.Clamp(cfg.cloudLightSamples, 2, 16);
            }
            mat.SetVector("_LightningPositionRange", positionRange);
            mat.SetVector("_LightningColorIntensity", colorIntensity);
            mat.SetInt("_LightningSamples", samples);
            return positionRange;
        }

        public void SetLayerDynamicProperties(CloudLayer layer, CloudLayerView view, int renderW, int renderH)
        {
            if (layer?.config == null || layer.material == null || view == null) return;

            var craftNode = Game.Instance.FlightScene.CraftNode;
            Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            var mat = layer.material;
            SetCloudWorldProperties(mat, layer);
            SetCloudAmbientProperties(mat);
            SetCloudLightningProperties(mat, false);
            if (!view.currentLightningEnabled) mat.SetInt("_LightningSamples", 0);
            mat.SetFloat("maxDepth", 0.9f * Mathf.Max(cam.farClipPlane, farDepthSource != null ? farDepthSource.maxFarDepth : 0f));
            float time = (float)Game.Instance.GameState.GetCurrentTime();
            mat.SetVector("blueNoiseOffset", new Vector2(
                Mathf.PerlinNoise(time * 0.5f + layer.layerIndex * 0.3f, 0f) * 2f - 1f,
                Mathf.PerlinNoise(0f, time * 0.5f + layer.layerIndex * 0.3f) * 2f - 1f
            ));
            //  重投影必须在云空间做:云自身在动(自转 + 风平移),纯世界空间 prevViewProj 会采到旧位置的云
            // → 运动残影,并在残影边缘形成水平割裂线。φ = 自转累积角 + 风平移折算的经度角(2π·runningOffset.x)。
            float cloudPhi = layer.accumulatedRotation + 2.0f * Mathf.PI * layer.runningOffset.x;
            float dPhi = float.IsNaN(view.prevCloudAngle) ? 0.0f : cloudPhi - view.prevCloudAngle;
            var cloudReprojection = BuildCloudSpaceRepro(dPhi, planetCenter);
            mat.SetMatrix("reprojMat", view.prevViewProjMat * cloudReprojection);
            mat.SetMatrix("_HistoryCloudViewMat", view.prevWorldToCameraMat * cloudReprojection);
            view.prevCloudAngle = cloudPhi;
            // 观察射线用相机 transform 轴构造。 不要用 cameraToWorldMatrix 第 2 列当 fwd —— Unity 视图约定里那是 -forward,会反向。
            mat.SetVector("_CamPos", cam.transform.position);
            mat.SetFloat("_ReflectionMode", 0f);
            mat.SetVector("_CamFwd", cam.transform.forward);
            mat.SetVector("_CamRight", cam.transform.right);
            mat.SetVector("_CamUp", cam.transform.up);
            mat.SetFloat("_TanHalfFovV", Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // 以本相机渲染目标的宽高比为准:额外相机的 targetTexture 宽高比才是它的真实视场
            mat.SetFloat("_Aspect", renderW / (float)Mathf.Max(1, renderH));
            mat.SetVector("clipPlanes", new Vector2(cam.nearClipPlane, cam.farClipPlane));

            mat.SetFloat("_NearThreshold", layer.config.nearThreshold);

            mat.SetVector("blueNoiseScale",
                layer.config.resolutionScale * new Vector2(renderW, renderH) / 512.0f);

            // 每像素每帧都有本帧 raymarch 数据(不依赖滞后的上一帧),故不需要运动门控
            var tcfg = layer.config;
            mat.SetFloat("_TssBlend", kTssFreshBlend);
            mat.SetFloat("historyBlend", tcfg.historyBlend);
            mat.SetFloat("_UseTemporal", tcfg.useTemporalUpscale ? 1f : 0f);

            //  必须用 GL.GetGPUProjectionMatrix(cam.projectionMatrix, true),不能用逻辑投影:Clouds 顶点着色器
            // 用光栅化 clip 重建射线,D3D 下 GPU clip 与逻辑投影 Y 约定相反 → 历史采错行 → 云带边缘镜像鬼影(割裂线)。
            view.prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            view.prevWorldToCameraMat = cam.worldToCameraMatrix;
        }

        /// <summary>构造"绕行星中心 C 的 Y 轴旋转 +dPhi"的仿射矩阵;旋转约定与 shader SampleDensity 的 R_y(+θ) 一致。</summary>
        private static Matrix4x4 BuildCloudSpaceRepro(float dPhi, Vector3 center)
        {
            float ca = Mathf.Cos(dPhi);
            float sa = Mathf.Sin(dPhi);
            var R = new Matrix4x4();
            R.m00 = ca; R.m02 = -sa;
            R.m11 = 1f;
            R.m20 = sa; R.m22 = ca;
            R.m33 = 1f;
            // 平移项 t = C − R·C(使变换为绕 C 旋转)
            Vector3 rC = new Vector3(
                R.m00 * center.x + R.m02 * center.z,
                center.y,
                R.m20 * center.x + R.m22 * center.z);
            R.m03 = center.x - rC.x;
            R.m13 = center.y - rC.y;
            R.m23 = center.z - rC.z;
            return R;
        }

        private static float GetWindSpeedFactor(float directionDeg)
        {
            float angle = directionDeg % 360f;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;

            float absAngle = Mathf.Abs(angle);
            if (absAngle < 45f || absAngle > 135f) return 2.0f;
            if (absAngle > 60f && absAngle < 120f) return 1.0f;
            float t = Mathf.InverseLerp(45f, 60f, absAngle);
            return Mathf.Lerp(2.0f, 1.0f, t);
        }

        // 相机海拔 = |camPos − 行星中心| − 半径(米);任何异常回退 0(低空 → 纯体积云)
        private float ComputeCameraAltitude()
        {
            try
            {
                var craftNode = Game.Instance.FlightScene.CraftNode;
                if (craftNode == null || craftNode.ReferenceFrame == null || craftNode.Parent == null)
                    return 0f;
                Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                float surfaceRadius = (float)craftNode.Parent.PlanetData.Radius;
                return (cam.transform.position - planetCenter).magnitude - surfaceRadius;
            }
            catch
            {
                return 0f;
            }
        }

        // 海拔淡入因子:0 = 纯体积云,1 = 纯 2D 轨道云;useOrbitClouds 关闭 → 恒 0。smoothstep 保证过渡带两端导数为 0。
        private static float ComputeOrbitFade(CloudConfig cfg, float camAlt)
        {
            if (cfg == null || !cfg.useOrbitClouds) return 0f;
            float start = Mathf.Max(0f, cfg.orbitTransitionStartAltitude);
            float end = Mathf.Max(start + 1f, cfg.orbitTransitionEndAltitude);
            float t = Mathf.Clamp01(Mathf.InverseLerp(start, end, camAlt));
            return t * t * (3f - 2f * t); // smoothstep
        }

        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            RenderTexture result = null, compositeTemp = null;
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                if (cam == null) cam = GetComponent<Camera>();
                if (_fog == null) _fog = new Volken.Weather.FogRenderer(cam);
                TryResolveFarDepthSource();
                RenderTexture farDepthTex = farDepthSource != null ? farDepthSource.farDepthTex : null;
                if (farDepthTex != null && !farDepthTex.IsCreated()) farDepthTex = null;

                // 本帧渲染层走复用缓冲(判据仍是 Clouds 那一份,不在这里另写一遍)
                Volken.Clouds.VolumetricClouds.Instance.FillActiveLayers(_activeLayers);
                var activeLayers = _activeLayers;
                var gameCamera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera;
                if (cam != gameCamera?.NearCamera && !ModSettings.Instance.ExtraCameraClouds.Value) activeLayers.Clear();
                if (_sceneDepthMaterial == null) _sceneDepthMaterial = SceneDepth.CreateMaterial();
                if (_sceneDepthMaterial == null)
                {
                    _fog.BlockFrame("scene-depth-shader-missing");
                    Graphics.Blit(source, destination);
                    return;
                }
                bool fogActive = _fog.Prepare(source.width,source.height);
                EnsureDepthTextures(source.width,source.height);
                _sceneDepthMaterial.SetFloat("_HasFarDepth",farDepthTex != null ? 1 : 0);
                _sceneDepthMaterial.SetFloat("_DepthFarClip",Mathf.Max(cam.farClipPlane,farDepthSource != null ? farDepthSource.maxFarDepth : 0f));
                Graphics.Blit(farDepthTex != null ? (Texture)farDepthTex : Texture2D.whiteTexture,combinedDepthTex,_sceneDepthMaterial,1);
                if (activeLayers.Count == 0)
                {
                    _activeViews.Clear();
                    _fog.Render(source,destination,combinedDepthTex);
                    return;
                }

                BuildViews();
                int renderW = Mathf.Max(1, source.width);
                int renderH = Mathf.Max(1, source.height);

                // 分辨率 / TSS 开关 / 格网变化都会改变 cloudRes 与历史尺寸 → 重建 RT(按本相机输出尺寸)
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    bool tss = layer.config.useTemporalUpscale;
                    int upX = Mathf.Max(1, layer.config.upscaleX);
                    int upY = Mathf.Max(1, layer.config.upscaleY);
                    float orbitRes = Mathf.Clamp(layer.config.orbitResolutionScale, 0.1f, 1f);
                    bool needsCreate = !view.IsCreated ||
                        view.currentW != renderW || view.currentH != renderH ||
                        Mathf.Abs(view.currentResolutionScale - layer.config.resolutionScale) > 0.001f ||
                        Mathf.Abs(view.currentOrbitRes - orbitRes) > 0.001f ||
                        view.currentColorFormat != CloudColorFormat ||
                        view.currentLightningEnabled != CloudLightningEnabled ||
                        view.currentTemporal != (tss ? 1 : 0) ||
                        view.currentUpX != upX || view.currentUpY != upY;
                    if (needsCreate)
                    {
                        view.ReleaseRenderTextures();
                        view.currentResolutionScale = layer.config.resolutionScale;
                        view.CreateRenderTextures(renderW, renderH);
                    }
                    view.PrepareFrame(Time.frameCount);
                }

                int maxLowW = 1, maxLowH = 1;
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view != null && view.cloudTex != null)
                    {
                        maxLowW = Mathf.Max(maxLowW, view.cloudTex.width);
                        maxLowH = Mathf.Max(maxLowH, view.cloudTex.height);
                    }
                }
                EnsureDepthTextures(renderW, renderH);
                EnsureLowResDepthTex(new Vector2Int(maxLowW, maxLowH));

                var matRef = activeLayers[0].material; // 深度 pass 用哪层的材质都一样
                int downsamplePass = matRef.FindPass("DownsampleDepth");
                Graphics.Blit(combinedDepthTex, lowResDepthTex, matRef, downsamplePass);

                int orbitPass = matRef.FindPass("OrbitClouds");
                float camAlt = ComputeCameraAltitude();
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    float fade = orbitPass >= 0 ? ComputeOrbitFade(layer.config, camAlt) : 0f;
                    view.orbitFade = fade <= 0.001f ? 0f : fade >= 0.999f ? 1f : fade;
                    bool orbitOnly = view.orbitFade >= 0.999f;
                    if (orbitOnly && !view.orbitOnlyLastFrame)   // 进入纯 2D 的瞬间清历史,防切回体积云时残影
                        view.ClearHistory();
                    view.orbitOnlyLastFrame = orbitOnly;
                }
                _activeViews.Clear();
                for (int i = 0; i < activeLayers.Count; i++)
                {
                    var v = GetView(activeLayers[i]);
                    if (v != null) _activeViews.Add(v);
                }
                PrepareCloudHistory(renderW, renderH);

                AdvanceGlobalCloudState();   // 全球风/自转:本帧只推进一次

                int cloudsPass = matRef.FindPass("Clouds");
                if (_fullscreenTriangle == null)
                {
                    _fullscreenTriangle = new Mesh();
                    _fullscreenTriangle.vertices = new Vector3[] {
                        new Vector3(-1f, -1f, 0f),
                        new Vector3( 3f, -1f, 0f),
                        new Vector3(-1f,  3f, 0f),
                    };
                    _fullscreenTriangle.uv = new Vector2[] {
                        new Vector2(0f, 0f),
                        new Vector2(2f, 0f),
                        new Vector2(0f, 2f),
                    };
                    _fullscreenTriangle.triangles = new int[] { 0, 1, 2 };
                    _fullscreenTriangle.UploadMeshData(true);
                }

                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    // 重投影与相机参数按每相机每层设置,风 / 自转已全局推进一次。
                    SetLayerDynamicProperties(layer, view, renderW, renderH);
                    if (view.orbitFade >= 0.999f) continue;   // 高空纯 2D:跳过体积 raymarch(性能大头)

                    layer.material.SetTexture("DepthTex", lowResDepthTex);   // Clouds pass 地面遮挡用
                    if (view.currentLightningEnabled) view.lightningCache.Prepare(layer.material);
                    // MRT:颜色 + 云面距离 + 运动数据(RG=UV 位移,B=上一相机云距离×1e-5)。
                    Graphics.SetRenderTarget(view.currentLightningEnabled ? view.lightningTargets : view.cloudTargets, view.cloudTex.depthBuffer);
                    layer.material.SetPass(cloudsPass);
                    Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);
                }

                // 轨道云(2D 壳着色):每层渲染到 view.orbitCloudTex(淡入因子>0 才做;pass 缺失则降级为纯体积云)
                if (orbitPass >= 0)
                {
                    foreach (var layer in activeLayers)
                    {
                        var view = GetView(layer);
                        if (view == null) continue;
                        if (view.orbitFade <= 0.001f) continue;
                        // 纯轨道云跳过体积绘制,TSS 重建后必须独立绑定本帧深度。
                        layer.material.SetTexture("DepthTex", combinedDepthTex);
                        // Blit 的 source 不被 OrbitClouds pass 采样(纯壳着色),仅作为合法非空输入
                        Graphics.Blit(combinedDepthTex, view.orbitCloudTex, layer.material, orbitPass);
                    }
                }

                UpscaleClouds(matRef);

                // 逐层链式合成,每层按其 compositeMode 混合
                if (_activeViews.Count == 0)
                {
                    _fog.Render(source, destination, combinedDepthTex);
                    return;
                }
                int compositePass = matRef.FindPass("Composite");
                RenderTexture compositeSource = source;
                if (fogActive)
                {
                    result = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                    _fog.Render(source, result, combinedDepthTex);
                    compositeSource = result;
                }

                var compositeDescriptor = new RenderTextureDescriptor(renderW, renderH, source.format, 0)
                {
                    sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear
                };
                bool canWriteDestination = destination == null
                    ? renderW == Screen.width && renderH == Screen.height
                    : destination.width == renderW && destination.height == renderH &&
                      destination.graphicsFormat == compositeDescriptor.graphicsFormat && destination.antiAliasing == 1;
                for (int i = 0; i < _activeViews.Count; i++)
                {
                    var view = _activeViews[i];
                    var layer = view.layer;
                    var compositeMat = layer.material;
                    compositeMat.SetTexture("UpscaledCloudTex", view.upscaledCloudTex);
                    compositeMat.SetTexture("CloudLightningTex", view.lightningTex != null && view.orbitFade < 0.999f && compositeMat.GetInt("_LightningSamples") >= 2
                        ? (Texture)view.lightningTex : Texture2D.blackTexture);
                    compositeMat.SetTexture("OrbitCloudTex", view.orbitCloudTex);
                    compositeMat.SetTexture("SceneDepthTex", combinedDepthTex);
                    compositeMat.SetTexture("FogCloudDepthTex", view.cloudDepthTex);
                    compositeMat.SetFloat("_VolkenFogComposite", fogActive ? 1 : 0);
                    float fogBottom, fogTop;
                    compositeMat.SetFloat("_FogOrbitAltitude",layer.config.TryGetBand(out fogBottom,out fogTop) ? (fogBottom+fogTop)*0.5f : layer.config.maxCloudHeight);
                    compositeMat.SetFloat("_CompositeMode",
                        layer.config.compositeMode == CompositeMode.Standard ? 1.0f : 0.0f);
                    // 交叉淡入因子:orbit pass 不可用 → 强制 0(纯体积云,等同未开启本特性)
                    compositeMat.SetFloat("_OrbitFade", view.orbitFade);

                    // 仅同尺寸、同编码的最终目标可直写;别名目标仍经临时纹理,保留缩放与格式转换顺序。
                    bool directOutput = i == _activeViews.Count - 1 && canWriteDestination && compositeSource != destination;
                    if (!directOutput) compositeTemp = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                    Graphics.Blit(compositeSource, directOutput ? destination : compositeTemp, compositeMat, compositePass);
                    if (result != null) RenderTexture.ReleaseTemporary(result);
                    result = compositeTemp;
                    compositeTemp = null;
                    compositeSource = result;
                }

                if (result != null) Graphics.Blit(result, destination);
            }
            catch (Exception e)
            {
                Mod.Log("CloudRenderer.Render failed: {0}", e);
                Volken.Weather.FogRenderer.ClearGlobals();
                InvalidateCloudHistory();
                try { Graphics.Blit(source, destination); } catch { }
            }
            finally
            {
                if (compositeTemp != null) RenderTexture.ReleaseTemporary(compositeTemp);
                if (result != null) RenderTexture.ReleaseTemporary(result);
                RenderTexture.active = previousTarget;
            }
        }

        private void OnDestroy()
        {
            CloudSceneLighting.Unregister(this);
            _sunOcclusion.Dispose();
            _groundShadows.Dispose();
            _fog?.Dispose();
            _fog = null;
            if (_sceneDepthMaterial != null) Destroy(_sceneDepthMaterial);
            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered -= OnReferenceFrameRecentered;
            }
            catch { }

            // 退订行星事件(必须成对退订,否则销毁后实例仍被事件链引用)
            if (VolumetricClouds.Instance != null)
            {
                VolumetricClouds.Instance.PlanetChanged -= OnPlanetChanged;
            }

            ReleaseAllRenderTextures();
        }

    }

}
