using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;
/*
    云渲染主流程:合并远近相机深度 → 逐层低清 raymarch(+ 云面深度/MV 的 MRT)→ MV 膨胀 → 全清时序上采样 → 逐层合成。
    ⚠️ 每个 CloudRenderer 实例 = 一台相机,每相机状态全在 CloudLayerView;farDepthSource = 这台相机的远相机。
    ⚠️ 风/自转是全球共享量,每帧只能推进一次(AdvanceGlobalCloudState),多相机下推两次 = 云速翻倍。
    ⚠️ [ImageEffectOpaque] 必须保留:云要在不透明之后、透明之前合成,否则会盖住水面/玻璃/UI。
*/

namespace Volken.Clouds
{
    
    public class CloudRenderer : MonoBehaviour
    {
        // 本相机独享的共享深度 RT
        private RenderTexture combinedDepthTex;
        private RenderTexture lowResDepthTex;
        private Camera cam;
        private static Mesh _fullscreenTriangle; // 全屏三角形(一次构建,所有相机复用)

        // 本相机的远相机深度源(主视角 = 游戏 FarCamera;PIP = 与克隆主相机共享 targetTexture 的更低 depth 相机)
        public FarCameraScript farDepthSource;

        private readonly Dictionary<CloudLayer, CloudLayerView> _views = new Dictionary<CloudLayer, CloudLayerView>();

        // 本相机本帧的线性场景深度(远+近合并后的 RFloat,单位 = LinearEyeDepth 米);雨软粒子等消费者直接取
        public RenderTexture LinearSceneDepth => combinedDepthTex;

        // 全球风/自转推进去重:同一帧只推进一次(所有相机共享同一全局云状态;推进两次 = 云速翻倍)
        private static int _lastGlobalAdvanceFrame = -1;

        private const float kTssFreshBlend = 0.5f;  // 非新鲜格的本帧混合权重 lerp(历史, 本帧, 该值);越大越追运动、降噪越弱

        public CloudRenderer()
        {
            cam = GetComponent<Camera>();
            CloudRenderManualRefresh();

            // 切天体 → 本相机历史作废。只订阅唯一的行星事件源:自己监听 PlayerChangedSoi 会漏退订(销毁后实例仍被事件链引用)
            if (VolkenClouds.Instance != null)
            {
                VolkenClouds.Instance.PlanetChanged += OnPlanetChanged;
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
            // 额外相机不会被游戏开 depthTextureMode,而 Clouds.shader 的 NearDepth pass 要采样 _CameraDepthTexture
            if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;
            TryResolveFarDepthSource();
        }

        private void OnPlanetChanged(PlanetEnvironment env)
        {
            // 不要回写 layer.config.enabled 做大气门控(会污染玩家预设);环境抑制由 VolkenClouds 统一置位
            foreach (var view in _views.Values)
            {
                view.frameNumber = 0;
                view.prevCloudAngle = float.NaN; // 云转角相位作废,首帧回退世界空间重投影
            }
        }

        // 浮动原点重置(离帧中心 >5000m / 帧速 >1000m/s / 时间加速 / 表面锁定切换)时世界坐标整体平移,重投影错位
        private void OnReferenceFrameRecentered(ModApi.Flight.GameView.IReferenceFrame referenceFrame, Vector3d positionDelta, Vector3d velocityDelta)
        {
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
                    kv.Value.currentResolutionScale = kv.Key.config.resolutionScale;
                }
            }
        }

        private void BuildViews()
        {
            if (Volken.Clouds.VolkenClouds.Instance == null) return;
            var stale = _views.Keys.Where(k => !Volken.Clouds.VolkenClouds.Instance.layers.Contains(k)).ToList();
            foreach (var k in stale)
            {
                _views[k].ReleaseRenderTextures();
                _views.Remove(k);
            }
            foreach (var layer in Volken.Clouds.VolkenClouds.Instance.layers)
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

            if (combinedDepthTex != null && combinedDepthTex.IsCreated())
                combinedDepthTex.Release();
            combinedDepthTex = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat);
            combinedDepthTex.Create();
        }

        private void EnsureLowResDepthTex(Vector2Int targetSize)
        {
            if (lowResDepthTex != null &&
                lowResDepthTex.width == targetSize.x &&
                lowResDepthTex.height == targetSize.y)
                return;

            if (lowResDepthTex != null && lowResDepthTex.IsCreated())
                lowResDepthTex.Release();

            lowResDepthTex = new RenderTexture(Mathf.Max(1, targetSize.x), Mathf.Max(1, targetSize.y), 0, RenderTextureFormat.RFloat);
            lowResDepthTex.Create();
        }

        private void ReleaseAllRenderTextures()
        {
            if (combinedDepthTex != null && combinedDepthTex.IsCreated())
                combinedDepthTex.Release();
            if (lowResDepthTex != null && lowResDepthTex.IsCreated())
                lowResDepthTex.Release();

            foreach (var view in _views.Values)
            {
                view?.ReleaseRenderTextures();
            }
        }

        public void SetAllLayersShaderProperties()
        {
            try
            {
                foreach (var layer in Volken.Clouds.VolkenClouds.Instance.layers)
                {
                    if (layer?.config == null || layer.material == null) continue;

                    layer.SetStaticShaderProperties();
                }
            }
            catch (Exception)
            {
                Mod.Log("Volken:CloudRenderer.SetAllLayersShaderProperties" + Environment.StackTrace);
            }
        }

        /// <summary>解析本实例的远深度源:与 cam 共享 targetTexture 的更低 depth 相机(PIP 克隆相机,优先子相机),否则回退游戏 FarCamera。</summary>
        public void TryResolveFarDepthSource()
        {
            if (farDepthSource != null) return;
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
                    foreach (Camera other in UnityEngine.Object.FindObjectsOfType<Camera>())
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
                if (Volken.Clouds.VolkenClouds.Instance?.farCam != null) farDepthSource = Volken.Clouds.VolkenClouds.Instance.farCam;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:CloudRenderer.TryResolveFarDepthSource ERROR: " + ex.Message);
            }
        }

        /// <summary>全球风/自转推进:每帧只执行一次。放 SetLayerDynamicProperties(每相机每帧调用)会让多相机云速成倍加速。</summary>
        private static void AdvanceGlobalCloudState()
        {
            if (_lastGlobalAdvanceFrame == Time.frameCount) return;
            _lastGlobalAdvanceFrame = Time.frameCount;

            if (Volken.Clouds.VolkenClouds.Instance == null) return;
            var craftNode = Game.Instance?.FlightScene?.CraftNode;
            if (craftNode == null || craftNode.ReferenceFrame == null || craftNode.CraftScript == null) return;
            float deltaTime = (float)Game.Instance.FlightScene.TimeManager.DeltaTime;

            foreach (var layer in Volken.Clouds.VolkenClouds.Instance.layers)
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

        public void SetLayerDynamicProperties(CloudLayer layer, CloudLayerView view, int renderW, int renderH)
        {
            if (layer?.config == null || layer.material == null || view == null) return;

            var craftNode = Game.Instance.FlightScene.CraftNode;
            Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
            var sun = Game.Instance.FlightScene.ViewManager.GameView.SunLight;

            var mat = layer.material;
            mat.SetFloat("currentRotation", layer.accumulatedRotation);
            mat.SetFloat("maxDepth", 0.9f * (farDepthSource != null ? farDepthSource.maxFarDepth : cam.farClipPlane));
            mat.SetVector("sphereCenter", planetCenter);
            mat.SetVector("lightDir", sun.transform.forward);
            mat.SetVector("cloudOffset", layer.runningOffset);
            float time = (float)Game.Instance.GameState.GetCurrentTime();
            mat.SetVector("blueNoiseOffset", new Vector2(
                Mathf.PerlinNoise(time * 0.5f + layer.layerIndex * 0.3f, 0f) * 2f - 1f,
                Mathf.PerlinNoise(0f, time * 0.5f + layer.layerIndex * 0.3f) * 2f - 1f
            ));
            // ⚠️ 重投影必须在云空间做:云自身在动(自转 + 风平移),纯世界空间 prevViewProj 会采到旧位置的云
            // → 运动残影,并在残影边缘形成水平割裂线。φ = 自转累积角 + 风平移折算的经度角(2π·runningOffset.x)。
            float cloudPhi = layer.accumulatedRotation + 2.0f * Mathf.PI * layer.runningOffset.x;
            float dPhi = float.IsNaN(view.prevCloudAngle) ? 0.0f : cloudPhi - view.prevCloudAngle;
            mat.SetMatrix("reprojMat", view.prevViewProjMat * BuildCloudSpaceRepro(dPhi, planetCenter));
            view.prevCloudAngle = cloudPhi;
            // 观察射线用相机 transform 轴构造。⚠️ 不要用 cameraToWorldMatrix 第 2 列当 fwd —— Unity 视图约定里那是 -forward,会反向。
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

            mat.SetFloat("surfaceRadius", (float)Game.Instance.FlightScene.CraftNode.Parent.PlanetData.Radius);

            // 参考系 → 星球本体系旋转(随飞行/轨道每帧变化),供游戏自带云 cubemap 采样
            mat.SetTexture("StockCloudCube", StockCloudMap.Current);
            var referenceFrame = craftNode.ReferenceFrame;
            if (referenceFrame != null)
            {
                Matrix4x4 bodyFromFrame = new Matrix4x4();
                var bx = referenceFrame.FrameToPlanetVector(Vector3.right);
                var by = referenceFrame.FrameToPlanetVector(Vector3.up);
                var bz = referenceFrame.FrameToPlanetVector(Vector3.forward);
                bodyFromFrame.m00 = (float)bx.x; bodyFromFrame.m10 = (float)bx.y; bodyFromFrame.m20 = (float)bx.z;
                bodyFromFrame.m01 = (float)by.x; bodyFromFrame.m11 = (float)by.y; bodyFromFrame.m21 = (float)by.z;
                bodyFromFrame.m02 = (float)bz.x; bodyFromFrame.m12 = (float)bz.y; bodyFromFrame.m22 = (float)bz.z;
                bodyFromFrame.m33 = 1f;
                mat.SetMatrix("planetToBody", bodyFromFrame);
            }

            // 每像素每帧都有本帧 raymarch 数据(不依赖滞后的上一帧),故不需要运动门控
            var tcfg = layer.config;
            int upX = Mathf.Max(1, tcfg.upscaleX);
            int upY = Mathf.Max(1, tcfg.upscaleY);
            int totalCells = upX * upY;

            mat.SetFloat("_TssBlend", kTssFreshBlend);
            mat.SetFloat("historyBlend", tcfg.historyBlend);

            if (tcfg.useTemporalUpscale)
            {
                if (view.temporalSequence == null || view.temporalSequence.Length != totalCells)
                {
                    view.temporalSequence = UpscalingPixelSequence.FindOptimalSamplingSequence(upX, upY);
                    view.frameNumber = 0;   // 格网变化 → 历史相位作废 → 冷启动
                }
                int cell = view.temporalSequence[view.frameNumber % totalCells];
                mat.SetVector("_SampleCell", new Vector2(cell % upX, cell / upX));
                mat.SetVector("_Upscale", new Vector2(upX, upY));
                mat.SetFloat("_UseTemporal", 1f);
                view.frameNumber++;
            }
            else
            {
                mat.SetVector("_SampleCell", Vector2.zero);
                mat.SetVector("_Upscale", new Vector2(upX, upY));
                mat.SetFloat("_UseTemporal", 0f);
            }
            mat.SetVector("_LowResSize", new Vector2(
                view.cloudTex != null ? view.cloudTex.width : 1f,
                view.cloudTex != null ? view.cloudTex.height : 1f));

            // ⚠️ 必须用 GL.GetGPUProjectionMatrix(cam.projectionMatrix, true),不能用逻辑投影:Clouds 顶点着色器
            // 用光栅化 clip 重建射线,D3D 下 GPU clip 与逻辑投影 Y 约定相反 → 历史采错行 → 云带边缘镜像鬼影(割裂线)。
            view.prevViewProjMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
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

        private float _lastOrbitDiagLogTime = -999f;
        private Vector3 _lastDiagCamPos;
        private float _lastDiagCamTime = -999f;

        // 2s 节流诊断:相机海拔/速度、每层淡入、体积云是否运行、RT 尺寸,用于量化 2D 与体积云的"范围/速度"差异
        private void LogOrbitDiagnostics(List<CloudLayerView> activeViews, float camAlt, int orbitPass)
        {
            bool anyOrbit = false;
            foreach (var view in activeViews)
                if (view.layer.config != null && view.layer.config.useOrbitClouds) { anyOrbit = true; break; }
            if (!anyOrbit) return;

            if (Time.realtimeSinceStartup - _lastOrbitDiagLogTime < 2f) return;
            _lastOrbitDiagLogTime = Time.realtimeSinceStartup;

            // 相机速度(2s 窗口平均,米/秒)
            float camSpeed = 0f;
            if (_lastDiagCamTime > 0f)
            {
                float dt = Mathf.Max(1e-3f, Time.realtimeSinceStartup - _lastDiagCamTime);
                camSpeed = Vector3.Distance(cam.transform.position, _lastDiagCamPos) / dt;
            }
            _lastDiagCamPos = cam.transform.position;
            _lastDiagCamTime = Time.realtimeSinceStartup;

            // 角速度对比:行星自转(rad/s)vs 云场旋转率(globalRotationAngular×5e-4)。两者不一致 = 云相对地表漂移
            double planetSpin = 0.0;
            try
            {
                planetSpin = Game.Instance.FlightScene.CraftNode.Parent.PlanetData.AngularVelocity;
            }
            catch { }

            var sb = new System.Text.StringBuilder();
            sb.Append("Volken:OrbitDiag camAlt=").Append(camAlt.ToString("F0")).Append("m speed=").Append(camSpeed.ToString("F1")).Append("m/s orbitPass=").Append(orbitPass >= 0 ? "yes" : "missing");
            sb.Append(" planetSpin=").Append(planetSpin.ToString("G4")).Append("rad/s");
            if (lowResDepthTex != null)
                sb.Append(" lowDepth=").Append(lowResDepthTex.width).Append('x').Append(lowResDepthTex.height);
            LogProjectionCheck(sb);
            foreach (var view in activeViews)
            {
                var c = view.layer.config;
                sb.Append(" | L").Append(view.layer.layerIndex)
                  .Append(" fade=").Append(view.orbitFade.ToString("F3"))
                  .Append(" volRun=").Append(view.orbitFade < 0.999f ? 1 : 0)
                  .Append(" orbitRT=").Append(view.orbitCloudTex != null ? view.orbitCloudTex.width + "x" + view.orbitCloudTex.height : "null");
                if (c == null) continue;
                sb.Append(" rot=").Append(view.layer.accumulatedRotation.ToString("F3"))
                  .Append(" off=").Append(view.layer.runningOffset.ToString("F2"))
                  .Append(" wind=").Append(c.windSpeed.ToString("F2")).Append('@').Append(c.windDirection.ToString("F0"))
                  .Append(" globRot=").Append(c.globalRotationAngular.ToString("F2"))
                  .Append(" rotRate=").Append((c.globalRotationAngular * 5e-4f).ToString("G4")).Append("rad/s")
                  .Append(" tss=").Append(c.useTemporalUpscale ? 1 : 0)
                  .Append(" histBlend=").Append(c.historyBlend.ToString("F2"))
                  .Append(" resScale=").Append(c.resolutionScale.ToString("F2"))
                  .Append(" h=").Append(c.layerHeights)
                  .Append(" sp=").Append(c.layerSpreads)
                  .Append(" w=").Append(c.layerStrengths)
                  .Append(" dens=").Append(c.density.ToString("F4"))
                  .Append(" abs=").Append(c.absorption.ToString("F4"))
                  .Append(" cov=").Append(c.coverage.ToString("F3"))
                  .Append(" maxH=").Append(c.maxCloudHeight.ToString("F0"))
                  .Append(" step=").Append(c.stepSize.ToString("F0"))
                  .Append(" comp=").Append(c.compositeMode)
                  .Append(" stock=").Append(c.useStockCloudMap ? 1 : 0)
                  .Append(" stockDensityScale=").Append(c.stockDensityScale.ToString("F2"))
                  .Append(" fadeBand=").Append(c.orbitTransitionStartAltitude.ToString("F0")).Append('-').Append(c.orbitTransitionEndAltitude.ToString("F0"))
                  .Append(" boost=").Append(c.orbitDensityBoost.ToString("F2"))
                  .Append(" bright=").Append(c.orbitBrightness.ToString("F2"));
            }
            Mod.Log(sb.ToString());
        }

        private bool _projChecked;

        // 投影一致性校验(每会话一次):CPU 端用"显式相机轴"与"相机投影矩阵"两条路径重建 viewDir,量最大角差
        private void LogProjectionCheck(System.Text.StringBuilder sb)
        {
            if (cam == null) return;
            sb.Append(" fov=").Append(cam.fieldOfView.ToString("F2"))
              .Append(" aspect=").Append(cam.aspect.ToString("F4"))
              .Append(" ortho=").Append(cam.orthographic ? 1 : 0);
            if (_projChecked) return;
            _projChecked = true;
            try
            {
                float tanFovV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float aspect = cam.aspect;
                Matrix4x4 invProj = cam.projectionMatrix.inverse;
                Matrix4x4 camToWorld = cam.cameraToWorldMatrix;
                Vector3 fwd = cam.transform.forward, right = cam.transform.right, up = cam.transform.up;
                float maxDeg = 0f;
                for (int gy = 0; gy <= 8; gy++)
                for (int gx = 0; gx <= 8; gx++)
                {
                    float ndcX = gx / 4f - 1f, ndcY = gy / 4f - 1f;
                    Vector3 vExplicit = (fwd + right * (ndcX * tanFovV * aspect) - up * (ndcY * tanFovV)).normalized;
                    // 与 2D 旧 shader 的重建一致:clip=(ndc,0,-1) → invProj → CameraToWorld
                    Vector4 unproj = invProj * new Vector4(ndcX, ndcY, 0f, -1f);
                    Vector3 vMatrix = camToWorld.MultiplyVector(new Vector3(unproj.x, unproj.y, unproj.z)).normalized;
                    float deg = Mathf.Rad2Deg * Mathf.Acos(Mathf.Clamp(Vector3.Dot(vExplicit, vMatrix), -1f, 1f));
                    if (deg > maxDeg) maxDeg = deg;
                }
                Mod.Log("Volken:ProjCheck maxViewDirDelta=" + maxDeg.ToString("F4") + "deg (显式轴 vs 逆投影矩阵;≈0 = 两路径一致)");
            }
            catch (Exception e) { Mod.Log("Volken:ProjCheck ERROR " + e.Message); }
        }

        private float _lastCoverageLogTime = -999f;

        // 覆盖探针(2s 节流,任一层的 orbitDebugMode>0 才启用):GPU 回读 cloudTex / orbitCloudTex,量屏幕覆盖比例
        private void LogCloudCoverage(List<CloudLayerView> activeViews, float camAlt, Vector3 planetCenter, float surfaceRadius)
        {
            try { if (ModSettings.Instance == null || !ModSettings.Instance.DevMode) return; }
            catch { return; }

            bool anyDebug = false;
            foreach (var view in activeViews)
                if (view.layer.config != null && view.layer.config.orbitDebugMode > 0.5f) { anyDebug = true; break; }
            if (!anyDebug) return;
            if (cam == null) return;

            if (Time.realtimeSinceStartup - _lastCoverageLogTime < 2f) return;
            _lastCoverageLogTime = Time.realtimeSinceStartup;

            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float focalPx = (cam.pixelHeight * 0.5f) / Mathf.Tan(Mathf.Max(halfFov, 1e-4f));
            float dist = Mathf.Max(camAlt + surfaceRadius, surfaceRadius * 1.001f);
            float limbPx = focalPx * Mathf.Tan(Mathf.Asin(Mathf.Clamp(surfaceRadius / dist, 0f, 0.999f)));
            Vector2 planetScreen = cam.WorldToScreenPoint(planetCenter);   // 左下原点,像素

            foreach (var view in activeViews)
            {
                var layer = view.layer;
                if (layer.config == null || !layer.config.useOrbitClouds) continue;
                if (view.orbitCloudTex == null) continue;
                var sb = new System.Text.StringBuilder();
                sb.Append("Volken:Coverage L").Append(layer.layerIndex)
                  .Append(" alt=").Append(camAlt.ToString("F0"))
                  .Append(" fade=").Append(view.orbitFade.ToString("F3"))
                  .Append(" pCtr=").Append(planetScreen.x.ToString("F0")).Append(',').Append(planetScreen.y.ToString("F0"))
                  .Append(" limb=").Append(limbPx.ToString("F0")).Append("px");
                // 2D 轨道云 / 体积云:仅在淡入因子说明本帧确实渲染过时才量(否则 RT 是陈旧帧)
                if (view.orbitFade > 0.001f)
                    MeasureCloudRT(view.orbitCloudTex, planetScreen, limbPx, "2d", sb);
                else
                    sb.Append(" 2d=skip");
                if (view.cloudTex != null && view.orbitFade < 0.999f)
                    MeasureCloudRT(view.cloudTex, planetScreen, limbPx, "vol", sb);
                else
                    sb.Append(" vol=skip");
                Mod.Log(sb.ToString());
            }
        }

        private static void MeasureCloudRT(RenderTexture rt, Vector2 planetScreenPx, float limbPx, string tag, System.Text.StringBuilder sb)
        {
            var prev = RenderTexture.active;
            var tmp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tmp.Apply();
            RenderTexture.active = prev;

            var px = tmp.GetPixels32();
            int total = px.Length;
            float cx = planetScreenPx.x * rt.width / (float)Screen.width;
            float cy = planetScreenPx.y * rt.height / (float)Screen.height;
            float limbRt = limbPx * rt.width / (float)Screen.width;
            int count03 = 0, count15 = 0;
            float maxR03 = 0f, maxR15 = 0f;
            for (int i = 0; i < total; i++)
            {
                var c = px[i];
                float lum = (c.r + c.g + c.b) / 765f;
                if (lum <= 0.03f) continue;
                int x = i % rt.width, y = i / rt.width;
                float dx = x - cx, dy = y - cy;
                float rad = Mathf.Sqrt(dx * dx + dy * dy);
                count03++;
                if (rad > maxR03) maxR03 = rad;
                if (lum > 0.15f)
                {
                    count15++;
                    if (rad > maxR15) maxR15 = rad;
                }
            }
            if (Application.isPlaying) UnityEngine.Object.DestroyImmediate(tmp);
            else UnityEngine.Object.Destroy(tmp);

            sb.Append(' ').Append(tag).Append("=").Append((count03 * 100f / total).ToString("F1")).Append('%')
              .Append(" R03=").Append(maxR03.ToString("F0")).Append("px")
              .Append(" R15=").Append(maxR15.ToString("F0")).Append("px")
              .Append(" ring03=").Append((maxR03 - limbRt).ToString("F1")).Append("px")
              .Append(" ring15=").Append((maxR15 - limbRt).ToString("F1")).Append("px");
        }

        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            try
            {
                TryResolveFarDepthSource();
                RenderTexture farDepthTex = farDepthSource != null ? farDepthSource.farDepthTex : null;
                if (farDepthTex == null)
                {
                    Graphics.Blit(source, destination);
                    return;
                }

                var activeLayers = Volken.Clouds.VolkenClouds.Instance.ActiveLayers.ToList();
                if (activeLayers.Count == 0)
                {
                    Graphics.Blit(source, destination);
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
                        view.currentTemporal != (tss ? 1 : 0) ||
                        view.currentUpX != upX || view.currentUpY != upY;
                    if (needsCreate)
                    {
                        view.ReleaseRenderTextures();
                        view.currentResolutionScale = layer.config.resolutionScale;
                        view.CreateRenderTextures(renderW, renderH);
                    }
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
                int nearDepthPass = matRef.FindPass("NearDepth");
                int downsamplePass = matRef.FindPass("DownsampleDepth");
                Graphics.Blit(farDepthTex, combinedDepthTex, matRef, nearDepthPass);
                Graphics.Blit(combinedDepthTex, lowResDepthTex, matRef, downsamplePass);

                int orbitPass = matRef.FindPass("OrbitClouds");
                float camAlt = ComputeCameraAltitude();
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    view.orbitFade = ComputeOrbitFade(layer.config, camAlt);
                    bool orbitOnly = view.orbitFade >= 0.999f;
                    if (orbitOnly && !view.orbitOnlyLastFrame)   // 进入纯 2D 的瞬间清历史,防切回体积云时残影
                        view.ClearHistory();
                    view.orbitOnlyLastFrame = orbitOnly;
                }
                var activeViews = activeLayers.Select(l => GetView(l)).Where(v => v != null).ToList();
                LogOrbitDiagnostics(activeViews, camAlt, orbitPass);

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
                    // reprojMat / _CamPos / _SampleCell 等每相机每层各自设置(风/自转已全局推进一次)
                    SetLayerDynamicProperties(layer, view, renderW, renderH);
                    if (view.orbitFade >= 0.999f) continue;   // 高空纯 2D:跳过体积 raymarch(性能大头)

                    layer.material.SetTexture("DepthTex", lowResDepthTex);   // Clouds pass 地面遮挡用
                    // MRT: cloudTex(RGBA) + cloudDepthTex(RFloat 云面距离) + cloudMVTex(RG 本帧运动矢量)
                    var mrt = new RenderBuffer[] { view.cloudTex.colorBuffer, view.cloudDepthTex.colorBuffer, view.cloudMVTex.colorBuffer };
                    Graphics.SetRenderTarget(mrt, view.cloudTex.depthBuffer);
                    layer.material.SetPass(cloudsPass);
                    Graphics.DrawMeshNow(_fullscreenTriangle, Matrix4x4.identity);

                    // 本帧 MV 膨胀 3 次 → cloudMVDilatedTex,供同帧 Upscale(无 1 帧滞后)
                    int dilatePass = layer.material.FindPass("DilateMV");
                    if (dilatePass >= 0)
                    {
                        var mvTmp1 = RenderTexture.GetTemporary(view.cloudMVTex.width, view.cloudMVTex.height, 0, view.cloudMVTex.format);
                        var mvTmp2 = RenderTexture.GetTemporary(view.cloudMVTex.width, view.cloudMVTex.height, 0, view.cloudMVTex.format);
                        Graphics.Blit(view.cloudMVTex, mvTmp1, layer.material, dilatePass);
                        Graphics.Blit(mvTmp1, mvTmp2, layer.material, dilatePass);
                        Graphics.Blit(mvTmp2, view.cloudMVDilatedTex, layer.material, dilatePass);
                        RenderTexture.ReleaseTemporary(mvTmp1);
                        RenderTexture.ReleaseTemporary(mvTmp2);
                    }
                }

                // 轨道云(2D 壳着色):每层渲染到 view.orbitCloudTex(淡入因子>0 才做;pass 缺失则降级为纯体积云)
                if (orbitPass >= 0)
                {
                    foreach (var layer in activeLayers)
                    {
                        var view = GetView(layer);
                        if (view == null) continue;
                        if (view.orbitFade <= 0.001f) continue;
                        // Blit 的 source 不被 OrbitClouds pass 采样(纯壳着色),仅作为合法非空输入
                        Graphics.Blit(lowResDepthTex, view.orbitCloudTex, layer.material, orbitPass);
                    }
                }

                // 逐层上采样。⚠️ 不要改成 MRT + DrawMeshNow:双 MRT(0 深度)在该路径上不渲染 → upscaled 恒黑 → 看不到云。
                int upscalePass = matRef.FindPass("Upscale");
                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    if (view.orbitFade >= 0.999f) continue;   // 高空纯 2D:体积时序链路整条跳过

                    var mat = layer.material;
                    mat.SetTexture("CloudDepthTex", view.cloudDepthTex);
                    mat.SetTexture("CloudMVDilatedTex", view.cloudMVDilatedTex);
                    mat.SetTexture("CombinedDepthTex", combinedDepthTex);
                    mat.SetTexture("HistoryTex", view.historyTex);
                    mat.SetTexture("HistoryDepthTex", view.historyDepthTex);
                    mat.SetTexture("HistoryCloudDepthTex", view.historyCloudDepthTex);
                    Graphics.Blit(view.cloudTex, view.upscaledCloudTex, mat, upscalePass);

                    // 时序写回:上采样结果 → 历史(下一帧在 Upscale 里按 MV 重投影采样);云面距离历史供下一帧 cloudGate 校验
                    Graphics.Blit(view.upscaledCloudTex, view.historyTex);
                    Graphics.Blit(combinedDepthTex, view.historyDepthTex);
                    Graphics.Blit(view.cloudDepthTex, view.historyCloudDepthTex);
                }

                // 逐层链式合成,每层按其 compositeMode 混合
                int compositePass = matRef.FindPass("Composite");
                RenderTexture result = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                Graphics.Blit(source, result);

                foreach (var layer in activeLayers)
                {
                    var view = GetView(layer);
                    if (view == null) continue;
                    matRef.SetTexture("UpscaledCloudTex", view.upscaledCloudTex);
                    matRef.SetTexture("OrbitCloudTex", view.orbitCloudTex);
                    matRef.SetTexture("SceneDepthTex", combinedDepthTex);
                    matRef.SetFloat("_CompositeMode",
                        layer.config.compositeMode == CompositeMode.Standard ? 1.0f : 0.0f);
                    // 交叉淡入因子:orbit pass 不可用 → 强制 0(纯体积云,等同未开启本特性)
                    matRef.SetFloat("_OrbitFade", orbitPass >= 0 ? view.orbitFade : 0f);

                    var temp = RenderTexture.GetTemporary(renderW, renderH, 0, source.format);
                    Graphics.Blit(result, temp, matRef, compositePass);
                    RenderTexture.ReleaseTemporary(result);
                    result = temp;
                }

                // 覆盖探针(仅 DevMode + orbitDebugMode>0 时生效):量 2D/体积云的屏幕范围差
                try
                {
                    var probeCraft = Game.Instance.FlightScene.CraftNode;
                    if (probeCraft != null && probeCraft.ReferenceFrame != null && probeCraft.Parent != null)
                    {
                        Vector3 probeCenter = probeCraft.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                        float probeRadius = (float)probeCraft.Parent.PlanetData.Radius;
                        LogCloudCoverage(activeViews, camAlt, probeCenter, probeRadius);
                    }
                }
                catch { }

                Graphics.Blit(result, destination);
                RenderTexture.ReleaseTemporary(result);
            }
            catch (Exception e)
            {
                Mod.Log("Volken:CloudRenderer.OnRenderImage ERROR: " + e);
                try { Graphics.Blit(source, destination); } catch { }
            }
        }

        private void OnDestroy()
        {
            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered -= OnReferenceFrameRecentered;
            }
            catch { }

            // 退订行星事件(必须成对退订,否则销毁后实例仍被事件链引用)
            if (VolkenClouds.Instance != null)
            {
                VolkenClouds.Instance.PlanetChanged -= OnPlanetChanged;
            }

            ReleaseAllRenderTextures();
        }

    }

}
