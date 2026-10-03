using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using ModApi;
using ModApi.Craft;
using ModApi.Flight.Sim;
using ModApi.Scenes.Events;
using UnityEngine;
using Volken.Core;

namespace Volken.Clouds
{
    /// <summary>
    /// 云系统:持有全部云层、按行星装载云预设、装配云渲染器,并且是全工程唯一解析"当前行星环境"的地方。
    /// 与天气的关系:直接读对方 config,不要造接口层。Mod.OnModLoaded 里本类必须先于 VolkenWeather 初始化。
    /// </summary>
    public class VolkenClouds
    {
        public static VolkenClouds Instance { get; private set; }

        /// <summary>全部云层(Layer 0 = Main,Layer 1..n = Extra)。</summary>
        public List<CloudLayer> layers = new List<CloudLayer>();

        public CloudLayer MainLayer => layers.Count > 0 ? layers[0] : null;

        // 本帧真正参与渲染的云层。所有渲染/反射路径都要用它,不要直接看 config.enabled(漏掉环境抑制)
        public IEnumerable<CloudLayer> ActiveLayers => layers.Where(IsActiveLayer);

        /// <summary>把本帧渲染层填进调用方自带的缓冲;渲染 / 反射路径每帧都取一次,别再 LINQ + 临时 List。</summary>
        public void FillActiveLayers(List<CloudLayer> into)
        {
            into.Clear();
            for (int i = 0; i < layers.Count; i++)
            {
                if (IsActiveLayer(layers[i])) into.Add(layers[i]);
            }
        }

        private static bool IsActiveLayer(CloudLayer l) =>
            l != null && l.config != null && l.config.enabled && !l.EnvironmentSuppressed;

        public CloudRenderer cloudRenderer;
        public FarCameraScript farCam;

        public const string BlueNoisePath = "Assets/Resources/Volken/BlueNoise.png";
        public const string PerlinFullRough = "Assets/Resources/Volken/DragNoise.png";
        public const string PerlinFullSoft = "Assets/Resources/Volken/flareNoise.png";
        public const string PerlinHalfRough = "Assets/Resources/Volken/PerlinHalfRough.png";
        public const string PerlinHalfSoft = "Assets/Resources/Volken/Noise.png";

        public static string GetNoiseMapPath()
        {
            switch (ModSettings.Instance.NoiseMapIndex)
            {
                case 1: return BlueNoisePath;
                case 2: return PerlinFullRough;
                case 3: return PerlinFullSoft;
                case 4: return PerlinHalfRough;
                case 5: return PerlinHalfSoft;
                default: return PerlinFullRough;
            }
        }

        /// <summary>本行星可选的云预设名(扫 <c>UserData/VolkenConfig/{行星}/</c>)。</summary>
        public List<string> _availableConfigs = new List<string>();

        private Shader _cloudShader;

        // 行星环境(全工程唯一来源)

        public event Action<PlanetEnvironment> PlanetChanged;   // 行星环境变化(进/出飞行场景、切 SOI、换行星);天气订阅它

        public PlanetEnvironment CurrentPlanet { get; private set; } = PlanetEnvironment.Empty;   // 当前行星环境快照

        public string CurrentPlanetName => CurrentPlanet.PlanetName;   // 空 = 不在任何有 SOI 的天体上

        /// <summary>行星 → 预设名 映射(文件 <c>UserData/VolkenConfig/PlanetConfigList.xml</c>);云与天气各记各的,互相独立。</summary>
        public PlanetConfigList planetConfigList;

        private bool _subscribedToSoi;

        /// <summary>初始化单例(幂等)。必须先于 <c>VolkenWeather.Initialize</c>(天气要订阅 <see cref="PlanetChanged"/>)。</summary>
        public static void Initialize()
        {
            if (Instance != null) return;
            Instance = new VolkenClouds();
        }

        private VolkenClouds()
        {
            try
            {
                _cloudShader = Mod.Instance.ResourceLoader.LoadAsset<Shader>("Assets/Scripts/Volken/Clouds/Shader/Clouds.shader");
            }
            catch (Exception ex) { Mod.Log("VolkenClouds: Cloud shader load error: " + ex); }

            if (_cloudShader == null)
            {
                try { _cloudShader = Shader.Find("Hidden/Clouds"); }
                catch (Exception ex) { Mod.Log("VolkenClouds: Shader.Find fallback error: " + ex); }
            }

            InitializeLayers();
            ReloadPlanetConfigList();

            try
            {
                Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenClouds: SceneLoaded subscribe failed: " + ex.Message);
            }

            TrySubscribeSoi();
            Mod.Log("VolkenClouds initialized");
        }

        private void InitializeLayers()
        {
            var main = new CloudLayer
            {
                layerIndex = 0, displayName = Locale.GetString("Volken.VolkenCloud.MainLayerName"),
                noise = new CloudNoise(seed: UnityEngine.Random.Range(1, 99999)),
            };
            main.material = new Material(_cloudShader);
            main.config = CloudConfig.CreateDefault();
            main.currentConfigName = "Default";
            main.runningOffset = main.config.offset;
            layers.Add(main);

            var extra1 = new CloudLayer
            {
                layerIndex = 1, displayName = Locale.GetString("Volken.VolkenCloud.ExtraLayerName"),
                noise = new CloudNoise(seed: UnityEngine.Random.Range(1, 99999)),
            };
            extra1.material = new Material(_cloudShader);
            extra1.config = CreateExtraDefaultConfig();
            extra1.currentConfigName = "ExtraDefault";
            extra1.runningOffset = extra1.config.offset;
            layers.Add(extra1);

            //add more Extra here
            foreach (var layer in layers)
            {
                layer.GenerateNoiseTextures();
                layer.SetStaticShaderProperties();
            }
        }

        private static CloudConfig CreateExtraDefaultConfig()
        {
            return new CloudConfig
            {
                compositeMode = CompositeMode.Additive,
                enabled = false,
                density = 0.01f, absorption = 0.3f, ambientLight = 0.1f, coverage = 0.1f,
                shapeScale = 15000f, detailScale = 10000f, detailStrength = 0.5f,
                phaseParameters = new Vector4(0.75f, -0.75f, 0.5f, 0.5f),
                offset = new Vector3(0.3f, 0.6f, 0.2f),
                windSpeed = 0.0005f, windDirection = 45f, globalRotationAngular = 0.03f,
                scatterStrength = 0.1f, atmoBlendFactor = 1.0f,
                cloudColor = new Color(0.9f, 0.9f, 1.0f, 1f),
                layerHeights = new Vector4(15000f, 25000f, 0f, 0f),
                layerSpreads = new Vector4(5000f, 8000f, 1f, 1f),
                layerStrengths = new Vector4(1.0f, 1.5f, 0f, 0f),
                maxCloudHeight = 35000f, resolutionScale = 0.3f,
                stepSize = 400f, stepSizeFalloff = 1.0f, numLightSamplePoints = 10,
                blueNoiseStrength = 0f, depthThreshold = 0.5f,
                historyBlend = 0f, historyDepthThreshold = 0.05f,
                scatterPower = 1.5f, multiScatterBlend = 0.1f, ambientScatterStrength = 0.3f,
                customWavelengths = new Vector3(680f, 550f, 450f),
                silverLiningIntensity = 1.0f, forwardScatteringBias = 0.7f,
                nearThreshold = 100000f,
            };
        }

        private void TrySubscribeSoi()
        {
            if (_subscribedToSoi) return;
            try
            {
                Game.Instance.FlightScene.PlayerChangedSoi -= OnPlayerChangedSoi;
                Game.Instance.FlightScene.PlayerChangedSoi += OnPlayerChangedSoi;
                _subscribedToSoi = true;
            }
            catch
            {
                // 加载期 FlightScene 可能尚不可用,由 OnSceneLoaded 再试
            }
        }

        private void OnSceneLoaded(object sender, SceneEventArgs e)
        {
            // 每次场景加载都重读清单(面板可能刚改过)
            ReloadPlanetConfigList();

            if (e.Scene != "Flight")
            {
                ApplyEnvironment(PlanetEnvironment.Empty);   // 离开飞行场景:通知订阅者"不在飞行"
                return;
            }

            ResolveAndApply();
            TrySubscribeSoi();
        }

        private void OnPlayerChangedSoi(ICraftNode craftNode, IPlanetNode newParent)
        {
            ResolveAndApply();
        }

        /// <summary>开发命令 <c>VolkenForceRefresh</c> 用:立刻重解析一次行星环境。</summary>
        public void OnFlightSceneLoaded()
        {
            ReloadPlanetConfigList();
            ResolveAndApply();
        }

        /// <summary>从盘上重新读一遍行星预设清单(场景加载 / 面板改动前调用)。</summary>
        public void ReloadPlanetConfigList()
        {
            planetConfigList = PlanetConfigList.LoadFromFile(PlanetConfigList.DefaultListName);
        }

        private void ResolveAndApply()
        {
            ApplyEnvironment(ResolvePlanetEnvironment());
        }

        /// <summary>解析当前行星环境。全工程唯一读 <c>Game.Instance</c> 行星信息的地方,其它模块不要自己再算一遍。</summary>
        private PlanetEnvironment ResolvePlanetEnvironment()
        {
            var env = PlanetEnvironment.Empty;
            env.InFlight = true;

            try
            {
                var planetNode = Game.Instance?.FlightScene?.CraftNode?.Parent;
                if (planetNode == null)
                {
                    // 没有 SOI(或加载期):不是天体
                    return env;
                }

                // 恒星没有 parent → 不是"天体"(云/天气都不该在恒星上跑)
                env.IsCelestial = planetNode.Parent != null;
                env.PlanetName = planetNode.Name;

                var pd = planetNode.PlanetData;
                if (pd != null)
                {
                    try { env.HasAtmosphere = pd.AtmosphereData != null && pd.AtmosphereData.HasPhysicsAtmosphere; }
                    catch { env.HasAtmosphere = false; }
                    try { env.HasWater = pd.HasWater; }
                    catch { env.HasWater = false; }
                }
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenClouds: resolve planet failed: " + ex.Message);
            }

            return env;
        }

        private void ApplyEnvironment(PlanetEnvironment env)
        {
            CurrentPlanet = env;

            ApplyWaterSetting(env);
            ApplyPlanetToClouds(env);       // 云自己先装配完

            try { PlanetChanged?.Invoke(env); }   // 再广播给天气等订阅者
            catch (Exception ex) { Mod.Log("VolkenClouds: PlanetChanged handler threw: " + ex); }
        }

        // 按"该行星有没有水"切水体强制设置。刻意放在这里而不是云装配里:它是行星级判断,与云/天气都无关。
        private static void ApplyWaterSetting(PlanetEnvironment env)
        {
            try
            {
                Mod.Instance?.ForceSettingScriptLoadGameObject?.SetActive(env.InFlight && env.HasWater);
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenClouds: water setting toggle failed: " + ex.Message);
            }
        }

        /// <summary>行星环境变化时装配云:无大气/绕恒星 → 抑制全部云层;有大气 → 装预设、挂渲染器、载自带云图。</summary>
        private void ApplyPlanetToClouds(PlanetEnvironment env)
        {
            try
            {
                if (!env.InFlight)
                {
                    // 离开飞行场景:不动任何云配置(动它会写坏玩家预设,见 SetEnvironmentSuppressed 说明)
                    return;
                }

                RefreshConfigList();

                if (!env.SupportsAtmosphereEffects)
                {
                    SetEnvironmentSuppressed(true);
                    StockCloudMap.Release();

                    // 绕恒星时不动预设名;绕"无大气的行星"时把当前预设名归零
                    if (env.IsCelestial && MainLayer != null)
                    {
                        MainLayer.currentConfigName = "Default";
                    }

                    VolkenUserInterface.Instance?.RebuildInspectorPanel();
                    Mod.Log($"VolkenClouds: 无云环境({env}) → 全部云层已关闭");
                    return;
                }

                ApplyPlanetPresets(env.PlanetName);
                SetEnvironmentSuppressed(false);

                var planetNode = ResolvePlanetNode();
                if (planetNode != null) StockCloudMap.LoadFor(planetNode);

                EnsureRenderers();

                VolkenUserInterface.Instance?.RebuildInspectorPanel();
                Mod.Log($"VolkenClouds: 已装配云 planet={env.PlanetName} preset={MainLayer?.currentConfigName} " +
                        $"layers={layers.Count} renderer={(cloudRenderer != null)}");
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenClouds.ApplyPlanetToClouds ERROR: " + ex);
            }
        }

        /// <summary>按行星装载 Main / Extra 两层的云预设(预设名记在 <see cref="planetConfigList"/>)。</summary>
        private void ApplyPlanetPresets(string planet)
        {
            var list = planetConfigList;
            if (list == null || string.IsNullOrEmpty(planet)) return;

            var main = MainLayer;
            if (main == null) return;

            if (_availableConfigs.Count > 0)
            {
                if (!list.ExistsInConfig(planet))
                {
                    main.currentConfigName = _availableConfigs[0];
                    list.AddConfig(planet, main.currentConfigName);
                }
                else
                {
                    main.currentConfigName = list.GetConfigName(planet, 0);
                    if (!_availableConfigs.Contains(main.currentConfigName))
                    {
                        main.currentConfigName = _availableConfigs[0];
                        list.SetConfig(planet, main.currentConfigName, 0);
                    }
                }

                main.config = CloudConfig.LoadFromFile(planet, main.currentConfigName);
            }
            else
            {
                main.currentConfigName = "Default";
                main.config = CloudConfig.CreateDefault();
                main.config.SaveToFile(planet, main.currentConfigName);
                _availableConfigs.Add(main.currentConfigName);

                if (!list.ExistsInConfig(planet)) list.AddConfig(planet, main.currentConfigName);
                else list.SetConfig(planet, main.currentConfigName, 0);
            }

            var extra = layers.Count > 1 ? layers[1] : null;   // Extra 层(Layer 1)按行星记的预设名单独装
            if (extra != null)
            {
                string extraCfgName = list.ExistsInConfig(planet) ? list.GetConfigName(planet, 1) : null;
                if (!string.IsNullOrEmpty(extraCfgName))
                {
                    try
                    {
                        var loaded = CloudConfig.LoadFromFile(planet, extraCfgName);
                        extra.config.CopyFrom(loaded);
                        extra.currentConfigName = extraCfgName;
                    }
                    catch (Exception ex) { Mod.Log("VolkenClouds: Error loading extra config: " + ex); }
                }
            }
        }

        // 按环境设/撤"运行时抑制"。不要回写 CloudConfig.enabled:那是玩家预设本体的字段,回写会在保存时污染预设。
        private void SetEnvironmentSuppressed(bool suppressed)
        {
            foreach (var layer in layers)
            {
                if (layer != null) layer.EnvironmentSuppressed = suppressed;
            }
        }

        private static IPlanetNode ResolvePlanetNode()
        {
            try { return Game.Instance?.FlightScene?.CraftNode?.Parent; }
            catch { return null; }
        }

        /// <summary>确保近/远相机上各挂一个云渲染器(幂等),并配对远深度源。</summary>
        private void EnsureRenderers()
        {
            var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;

            cloudRenderer = GetOrAdd<CloudRenderer>(gameCam.NearCamera.gameObject);
            farCam = GetOrAdd<FarCameraScript>(gameCam.FarCamera.gameObject);

            if (cloudRenderer != null && farCam != null)
            {
                cloudRenderer.farDepthSource = farCam;
            }
        }

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        /// <summary>重扫本行星的云预设列表(<see cref="CurrentPlanetName"/> 目录下的 *.xml)。</summary>
        public void RefreshConfigList()
        {
            try
            {
                string planet = CurrentPlanetName;
                if (string.IsNullOrEmpty(planet))
                {
                    _availableConfigs = new List<string> { "Default" };
                    return;
                }

                _availableConfigs = CloudConfig.GetAllConfigNames(planet);
                if (_availableConfigs.Count == 0) _availableConfigs.Add("Default");

                var nm = MainLayer?.currentConfigName ?? "Default";
                if (!_availableConfigs.Contains(nm)) _availableConfigs.Add(nm);
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenClouds: Error refreshing config list: " + ex);
                _availableConfigs = new List<string> { "Default" };
            }
        }

        public void AddConfig(string cfg)
        {
            _availableConfigs.Add(cfg);
            Mod.Log($"VolkenClouds: Added config {cfg}, now has {_availableConfigs.Count} configs");
        }

        /// <summary>面板改动云参数后调用:把新参数推给 shader。</summary>
        public void ValueChanged()
        {
            cloudRenderer?.SetAllLayersShaderProperties();
        }
    }
}
