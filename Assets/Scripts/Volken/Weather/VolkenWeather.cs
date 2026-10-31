using System;
using System.Collections.Generic;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using Volken.Clouds;
using Volken.Core;

namespace Volken.Weather
{
    /// <summary>
    /// 天气系统:按行星装载天气预设并驱动雨 / 雷电;雾由每相机 CloudRenderer 按本配置显式绘制。
    ///
    /// 与云的关系:**直接读对方 config,不造接口层**;但**读 ≠ 联动** —— 天气不修改云的任何参数。
    /// 行星切换订阅 <see cref="VolumetricClouds.PlanetChanged"/>,不自己监听场景事件。
    /// </summary>
    public class VolkenWeather
    {
        public static VolkenWeather Instance { get; private set; }

        /// <summary>该行星的天气配置被(重新)加载完成。子系统据此重建自己的参数。</summary>
        public event Action<string> PlanetConfigLoaded;

        /// <summary>当前行星名(空 = 尚未进入任何有天气的行星)。</summary>
        public string CurrentPlanet { get; private set; } = string.Empty;

        /// <summary>当前天气预设名(**与云预设名互相独立**,换它不动云)。</summary>
        public string CurrentConfigName { get; private set; } = VolkenWeatherConfig.DefaultConfigName;

        /// <summary>本行星可选的天气预设名(扫 <c>UserData/VolkenWeatherConfig/{行星}/</c>);与云的预设列表无关。</summary>
        public List<string> AvailableConfigs { get; private set; } =
            new List<string> { VolkenWeatherConfig.DefaultConfigName };

        /// <summary>当前内存里的天气参数(<c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c> 的独立实例)。面板改的是它,点"保存"才落盘。</summary>
        public VolkenWeatherConfig Config { get; private set; } = VolkenWeatherConfig.CreateDefault();

        /// <summary>当前环境是否允许天气运行;雨 / 雾 / 雷电另由各自开关控制。</summary>
        public bool IsActive { get; private set; }

        /// <summary>相机海拔(ASL,米),供雷电限高、起点计算及雾诊断读取。</summary>
        public float CameraAltitudeAsl { get; private set; }

        /// <summary>相机海拔(AGL,米)。取不到地形高度时退化为 ASL。</summary>
        public float CameraAltitudeAgl { get; private set; }

        public bool CameraSubmerged { get; private set; }

        /// <summary>遗留云层高度因子:云底以下为 1,云顶附近为 0;当前无外部消费者。</summary>
        public float CameraCloudFade { get; private set; }

        private bool _initialized;

        private GameObject _host;                   // 承载子系统 MonoBehaviour 的临时物体
        private WeatherTicker _ticker;
        private LightningModule _lightning;
        private Shader _boltShader;
        private bool _boltShaderTried;
        private bool _regionalSampleWarning;
        private RegionalWeatherMap _regionalMap;
        private float _regionalMapRetryAt;
        private bool _regionalMapWarning;

        /// <summary>初始化单例(幂等)。由 <c>Mod.OnModLoaded</c> 调用,必须在 <see cref="VolumetricClouds.Initialize"/> 之后。</summary>
        public static void Initialize()
        {
            if (Instance != null) return;
            Instance = new VolkenWeather();
            Instance.InitializeInternal();
        }

        private void InitializeInternal()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _host = new GameObject("VolkenWeather");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _ticker = _host.AddComponent<WeatherTicker>();
                RainAudio.Ensure(_host);   // 雨声:挂常驻 host(全局唯一,不随相机切换重建);它自己读 RainParticles 做门控
                _host.SetActive(true);
                // 常驻宿主 → 非飞行场景整体停表,别只在 Update 里早退(放最后:别让这一步的失败带掉上面的装配)
                _ticker.enabled = Game.InFlightScene;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: host creation failed: " + ex.Message);
            }

            if (Clouds.VolumetricClouds.Instance != null)
            {
                Clouds.VolumetricClouds.Instance.PlanetChanged += OnPlanetChanged;
            }
            else
            {
                Mod.Log("VolkenWeather: Clouds 未初始化 —— 天气不会跟随行星切换(初始化顺序错误)");
            }

            Mod.Log("VolkenWeather initialized");
        }

        // 行星切换(唯一入口:Clouds 的事件)

        /// <summary>行星环境变化(由 <see cref="VolumetricClouds.PlanetChanged"/> 广播)。</summary>
        private void OnPlanetChanged(PlanetEnvironment env)
        {
            try
            {
                // 唯一的启停点:相机指标只在飞行场景有意义,常驻宿主的每帧回调按同一个信号关掉
                if (_ticker != null) _ticker.enabled = env.InFlight;

                if (!env.InFlight)
                {
                    // 离开飞行场景 → 停掉天气(不做卸载,配置留着)
                    SetActive(false);
                    return;
                }

                ApplyPlanet(env.PlanetName, env.SupportsAtmosphereEffects);
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather.OnPlanetChanged ERROR: " + ex);
            }
        }

        /// <summary>装载该行星登记的天气预设。同行星重复调用不重载;换预设请调 <see cref="ApplyPreset"/>。</summary>
        public void ApplyPlanet(string planetName, bool hasAtmosphere)
        {
            if (string.IsNullOrEmpty(planetName) || !hasAtmosphere)
            {
                CurrentPlanet = planetName ?? string.Empty;
                SetActive(false);
                return;
            }

            // 同行星重复调用(OnSceneLoaded + SOI 会连着来)→ 不重载
            if (CurrentPlanet == planetName && IsActive)
            {
                return;
            }

            ApplyPreset(planetName, ResolvePresetName(planetName));
        }

        /// <summary>取这颗行星登记的**天气预设名**(独立于云层),取不到就用 Default。</summary>
        private static string ResolvePresetName(string planetName)
        {
            try
            {
                var list = Clouds.VolumetricClouds.Instance?.planetConfigList;
                var name = list?.GetWeatherConfigName(planetName);
                return string.IsNullOrEmpty(name) ? VolkenWeatherConfig.DefaultConfigName : name;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: resolve weather preset name failed for " + planetName + ": " + ex.Message);
                return VolkenWeatherConfig.DefaultConfigName;
            }
        }

        /// <summary>按天气预设名装载配置。进新行星 / 换预设都走这里。返回 false = 预设文件存在但损坏(已隔离为 <c>.broken</c>,
        /// 实际生效的是内存默认值),调用方据此提示"加载失败"而不是误报成功。</summary>
        public bool ApplyPreset(string planetName, string configName)
        {
            if (string.IsNullOrEmpty(planetName))
            {
                return false;
            }
            if (string.IsNullOrEmpty(configName))
            {
                configName = VolkenWeatherConfig.DefaultConfigName;
            }

            CurrentPlanet = planetName;
            CurrentConfigName = configName;
            RefreshPresetList(planetName);

            VolkenWeatherConfig cfg;
            bool loadFailed;
            try
            {
                cfg = VolkenWeatherConfig.LoadFromFile(planetName, configName, out loadFailed);
            }
            catch (Exception ex)
            {
                Mod.Log($"VolkenWeather: load preset '{configName}' for {planetName} failed: {ex.Message}");
                cfg = VolkenWeatherConfig.CreateDefault();
                loadFailed = true;
            }
            Config = cfg;

            SetActive(CanRunWeather(), refreshSchedule: true);
            RainParticles.ApplyConfig(cfg.rain);
            RainParticles.SyncToCurrentView();

            RainLensEffect.SyncToCurrentView();
            RainLensEffect.ClearCurrent();

            Mod.Log($"VolkenWeather: planet={planetName} preset={configName} active={IsActive} " +
                    $"rain={cfg.rain.enabled} fog={cfg.fog.enabled} lightning={cfg.lightning.enabled}" +
                    (loadFailed ? " (load failed -> quarantined)" : ""));

            try { PlanetConfigLoaded?.Invoke(planetName); }
            catch (Exception ex) { Mod.Log("VolkenWeather: PlanetConfigLoaded handler threw: " + ex.Message); }

            return !loadFailed;
        }

        // 天气预设的独立管理(与云层预设无关)

        /// <summary>重扫本行星的天气预设列表(面板下拉用)。当前预设一定在列表里。</summary>
        public void RefreshPresetList(string planetName)
        {
            var names = VolkenWeatherConfig.GetAllConfigNames(planetName);
            if (!string.IsNullOrEmpty(CurrentConfigName) && !names.Contains(CurrentConfigName))
            {
                names.Add(CurrentConfigName);
            }
            if (names.Count == 0)
            {
                names.Add(VolkenWeatherConfig.DefaultConfigName);
            }
            AvailableConfigs = names;
        }

        /// <summary>切换到另一个天气预设(只动天气,不碰云配置),并登记为该行星的天气预设。返回 false = 没切(名字空/同名/预设文件不存在)。</summary>
        public bool SwitchPreset(string configName)
        {
            if (string.IsNullOrEmpty(CurrentPlanet) || string.IsNullOrWhiteSpace(configName)) return false;
            if (configName == CurrentConfigName) return false;

            //必须校验文件存在:ApplyPreset → LoadFromFile 在文件缺失时会**就地新建一份默认 XML 并落盘**,
            // 然后 RegisterPresetName 又把行星清单里的预设名改写成它 —— 下拉里一条幽灵项(例如只有 Default 时)
            // 被点一下 = 静默造出垃圾预设 + 改掉行星绑定。新建预设请走"另存为新配置"。
            if (!VolkenWeatherConfig.Exists(CurrentPlanet, configName))
            {
                Mod.Log($"VolkenWeather: 预设 '{configName}' 在 {CurrentPlanet} 的天气预设目录里不存在 → 拒绝切换(避免新建默认文件并改写行星绑定)");
                return false;
            }

            bool loaded = ApplyPreset(CurrentPlanet, configName);
            RegisterPresetName();
            return loaded;
        }

        /// <summary>把当前天气参数另存为新预设(玩家给名字)并切到它。纯天气操作,不碰云。
        /// 名字非法或已存在时拒绝(后者请用"保存"),避免静默覆盖既有预设。</summary>
        public bool SaveAsNewConfig(string newName)
        {
            if (string.IsNullOrEmpty(CurrentPlanet) || Config == null || !ConfigFileNames.IsValid(newName))
            {
                return false;
            }

            // 另存为不覆盖既有预设:同名覆盖走"保存当前"
            if (VolkenWeatherConfig.Exists(CurrentPlanet, newName))
            {
                Mod.Log($"VolkenWeather: 天气预设 '{newName}' 已存在 → 另存为拒绝覆盖(请用保存当前配置)");
                return false;
            }

            if (!Config.SaveToFile(CurrentPlanet, newName)) return false;
            CurrentConfigName = newName;
            RefreshPresetList(CurrentPlanet);
            RegisterPresetName();
            return true;
        }

        /// <summary>把"当前用的天气预设名"记进行星清单(名字没变就不写盘)。</summary>
        private void RegisterPresetName()
        {
            try
            {
                var list = Clouds.VolumetricClouds.Instance?.planetConfigList;
                if (list == null || string.IsNullOrEmpty(CurrentPlanet)) return;
                if (list.GetWeatherConfigName(CurrentPlanet) == CurrentConfigName) return;
                list.SetWeatherConfig(CurrentPlanet, CurrentConfigName);
            }
            catch (Exception ex) { Mod.Log("VolkenWeather: register preset name failed: " + ex.Message); }
        }

        /// <summary>把当前天气参数落盘到本行星的天气预设文件,并确保清单里记着这个预设名。</summary>
        public bool SaveCurrentConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(CurrentPlanet) || Config == null)
                {
                    Mod.Log("VolkenWeather: SaveCurrentConfig 失败 —— 当前没有行星/配置");
                    return false;
                }
                if (!Config.SaveToFile(CurrentPlanet, CurrentConfigName)) return false;
                RegisterPresetName();
                return true;
            }
            catch (Exception ex)
            {
                Mod.Log("Failed to save current weather config: {0}", ex);
                return false;
            }
        }

        /// <summary>把内存里的天气参数恢复成默认(完全关闭),不落盘 —— 需玩家再点"保存"才写文件。</summary>
        public void ResetCurrentConfigToDefault()
        {
            Config?.CopyFrom(VolkenWeatherConfig.CreateDefault());
            RainParticles.ApplyConfig(Config?.rain);
            RainParticles.SyncToCurrentView();
            SetActive(CanRunWeather(), refreshSchedule: true);
            RainLensEffect.SyncToCurrentView();
            RainLensEffect.ClearCurrent();
        }

        // 天气不修改云参数;模块边界见 docs/architecture.md。

        /// <summary>按雷电开关与当前环境起停雷电;雷暴节奏由 <see cref="LightningModule"/> 自己管。</summary>
        private void UpdateLightning(bool refreshSchedule = false)
        {
            var cfg = Config;
            bool want = IsActive && cfg != null && cfg.lightning.enabled;

            if (!want)
            {
                _lightning?.SetActive(false);
                return;
            }

            if (_lightning == null)
            {
                if (_host == null) return;
                // 不用 ??= :Unity 的 Object 重载了 == ,??= 走的是纯 CLR null 判断,语义不同
                var existing = _host.GetComponent<LightningModule>();
                _lightning = existing != null ? existing : _host.AddComponent<LightningModule>();
                _lightning.Initialize(LoadBoltShader());
            }

            _lightning.SetActive(true, refreshSchedule);
        }

        /// <summary>载入闪电 shader;失败回退 <c>Shader.Find</c>(开发期 shader 可能已在内存里)。</summary>
        private Shader LoadBoltShader()
        {
            if (_boltShaderTried) return _boltShader;
            _boltShaderTried = true;

            try
            {
                _boltShader = Mod.LoadVolkenAsset<Shader>("Assets/Scripts/Volken/Weather/Lightning/Shader/LightningBolt.shader");
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: bolt shader load error: " + ex.Message);
            }

            if (_boltShader == null)
            {
                try { _boltShader = Shader.Find("Hidden/Volken/LightningBolt"); }
                catch { }
            }

            if (_boltShader == null)
            {
                Mod.Log("VolkenWeather: bolt shader NOT FOUND — lightning will be invisible");
            }
            return _boltShader;
        }

        /// <summary>开发接口:手动落雷,要求配置启用;模块未激活或海拔闸门拦下时返回 false。</summary>
        public bool TriggerLightning()
        {
            if (_lightning == null)
            {
                Mod.Log("VolkenWeather: TriggerLightning ignored — lightning module not active " +
                        "(need active planet environment + lightning.enabled)");
                return false;
            }
            return _lightning.CastRandomBolt();
        }

        /// <summary>供开发代码检查的雷电模块,可能为 null。</summary>
        public LightningModule Lightning => _lightning;

        private void SetActive(bool active, bool refreshSchedule = false)
        {
            if (IsActive == active && !refreshSchedule) return;
            IsActive = active;
            RainParticles.SyncToCurrentView();
            if (!active)
            {
                RainLensEffect.ClearCurrent();
                RainLensEffect.SyncToCurrentView();
            }
            UpdateLightning(refreshSchedule);
        }

        internal bool CanRunWeather()
        {
            var environment = Clouds.VolumetricClouds.Instance?.CurrentPlanet;
            return Config != null && !string.IsNullOrEmpty(CurrentPlanet) && Game.InFlightScene &&
                environment.HasValue && environment.Value.SupportsAtmosphereEffects &&
                environment.Value.PlanetName == CurrentPlanet;
        }

        /// <summary>重算环境激活态并按独立开关同步雷电启停。</summary>
        public void RefreshActiveState()
        {
            SetActive(CanRunWeather());
            UpdateLightning();
            Mod.Log($"VolkenWeather: state refreshed → active={IsActive}");
        }

        /// <summary>由 <c>WeatherTicker</c> 每帧驱动。独立于任何渲染器:相机被销毁/替换后天气仍继续跑。</summary>
        public void Tick(float deltaTime)
        {
            SetActive(CanRunWeather());
            UpdateCameraMetrics(deltaTime);

            // 雨挂在场景相机上(随 Flight 场景卸载就没了)→ 每个飞行场景都要按配置重挂;不新增 SceneLoaded 订阅者,由这条每帧门控承担
            RainParticles.SyncToCurrentView();

            RainLensEffect.SyncToCurrentView();

            // 子系统各自按自己的配置跑(雷暴节奏由 LightningModule 自己管),这里不喂。
            if (!IsActive) return;

            var cfg = Config;
            if (cfg == null)
            {
                SetActive(false);
            }
        }

        /// <summary>帧空间位置转到行星固连坐标；只返回地区倍率，不改变效果开关。</summary>
        public RegionalWeatherSample SampleRegionalWeather(Vector3 framePosition)
        {
            var regions = Config?.Regions;
            if (regions == null || !regions.enabled || RainParticles.StandaloneMode || !Game.InFlightScene)
            {
                _regionalSampleWarning = false;
                return RegionalWeatherSample.Identity;
            }
            try
            {
                var flight = Game.Instance?.FlightScene;
                var craft = flight?.CraftNode;
                var planet = craft?.Parent;
                if (craft?.ReferenceFrame != null && planet?.PlanetData != null && flight.FlightState != null)
                {
                    Vector3d inertial = craft.ReferenceFrame.FrameToPlanetPosition(framePosition);
                    Vector3d surface = planet.PlanetVectorToSurfaceVector(inertial);
                    if (RegionalWeather.TrySample(regions, CurrentPlanet, surface.x, surface.y, surface.z,
                        planet.PlanetData.Radius, flight.FlightState.Time, out var sample, GetRegionalMap()))
                    {
                        _regionalSampleWarning = false;
                        return sample;
                    }
                }
            }
            catch (Exception)
            {
                // 场景切换可能先卸载参考帧；走同一恒等降级，不保存失效坐标。
            }
            if (!_regionalSampleWarning)
            {
                _regionalSampleWarning = true;
                Mod.Log("VolkenWeather: invalid regional sampling context; using global weather until recovery.");
            }
            return RegionalWeatherSample.Identity;
        }

        private RegionalWeatherMap GetRegionalMap()
        {
            if (Config?.Regions?.distribution != 1) return null;
            if (_regionalMap != null) return _regionalMap;
            if (Time.realtimeSinceStartup < _regionalMapRetryAt) return null;
            _regionalMapRetryAt = Time.realtimeSinceStartup + 30f;
            try
            {
                var texture = Mod.LoadVolkenAsset<Texture2D>(RegionalWeatherMap.AssetPath, false);
#if UNITY_EDITOR
                if (texture == null) texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(RegionalWeatherMap.AssetPath);
#endif
                if (texture == null || !texture.isReadable || texture.width < 2 || texture.height < 2 ||
                    (long)texture.width * texture.height > 16777216)
                    throw new InvalidOperationException("Missing, unreadable or oversized regional coverage texture.");
                var pixels = texture.GetPixels32();
                var coverage = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) coverage[i] = pixels[i].r;
                _regionalMap = new RegionalWeatherMap(texture.width, texture.height, coverage);
                _regionalMapWarning = false;
                return _regionalMap;
            }
            catch (Exception ex)
            {
                if (!_regionalMapWarning)
                {
                    _regionalMapWarning = true;
                    Mod.Log("VolkenWeather: regional map unavailable; using procedural distribution: " + ex.Message);
                }
                return null;
            }
        }

        /// <summary>雨声独立查询主 NearCamera，避免渲染顺序或 PIP 覆盖输入。</summary>
        public RegionalWeatherSample SampleMainRegionalWeather()
        {
            if (Config?.Regions == null || !Config.Regions.enabled || RainParticles.StandaloneMode || !Game.InFlightScene)
                return RegionalWeatherSample.Identity;
            var camera = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
            return camera != null ? SampleRegionalWeather(camera.transform.position) : RegionalWeatherSample.Identity;
        }

        private void UpdateCameraMetrics(float deltaTime)
        {
            try
            {
                var gameCam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera;
                if (gameCam == null) return;
                var near = gameCam.NearCamera;
                if (near == null) return;

                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                var planetNode = craftNode?.Parent;
                if (craftNode?.ReferenceFrame == null || planetNode?.PlanetData == null) return;

                Vector3 camPos = near.transform.position;
                Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                double radius = planetNode.PlanetData.Radius;

                float asl = (camPos - planetCenter).magnitude - (float)radius;
                CameraAltitudeAsl = asl;

                // AGL:优先用游戏给的"地形之上高度",它已经处理了山脉/水面
                float agl = asl;
                try
                {
                    double nodeAgl = craftNode.AltitudeAgl;
                    // 相机与飞船可能不同高(自由视角/舱外),用差值修正
                    Vector3 craftPos = craftNode.ReferenceFrame.PlanetToFramePosition(craftNode.Position);
                    agl = (float)nodeAgl + (camPos - craftPos).y;
                }
                catch { /* 取不到就用 ASL 近似 */ }
                CameraAltitudeAgl = agl;

                bool hasWater = false;
                try { hasWater = planetNode.PlanetData.HasWater; } catch { }
                CameraSubmerged = hasWater && asl < 0f;

                // 云内淡化:直接读云的 config 取高度带。
                CameraCloudFade = ComputeCameraCloudFade(asl);
            }
            catch (Exception ex)
            {
                // 每帧调用,不刷日志
                if (Time.frameCount % 600 == 0)
                    Mod.Log("VolkenWeather.UpdateCameraMetrics: " + ex.Message);
            }
        }

        /// <summary>按高度带计算遗留因子;无有效云层时返回 1。</summary>
        private float ComputeCameraCloudFade(float asl)
        {
            try
            {
                var cloudCfg = Clouds.VolumetricClouds.Instance?.MainLayer?.config;
                if (cloudCfg == null || !cloudCfg.enabled) return 1f;

                float bottom, top;
                if (!cloudCfg.TryGetBand(out bottom, out top)) return 1f;

                float fadeTop = top - 400f;
                if (fadeTop <= bottom) return 1f;

                return 1f - Mathf.InverseLerp(bottom, fadeTop, asl);
            }
            catch
            {
                return 1f;
            }
        }
    }

    /// <summary>驱动 <see cref="VolkenWeather.Tick"/> 的极简 MonoBehaviour —— 让天气不依赖任何场景相机,同时 VolkenWeather 保持纯逻辑。</summary>
    public class WeatherTicker : MonoBehaviour
    {
        private void Update()
        {
            var w = VolkenWeather.Instance;
            if (w == null) return;

            // 双保险(写法同 RainAudio.Update):场景门控靠 SceneLoaded 事件链,而别家 mod 的处理器抛异常会静默跳过
            // 后续订阅者(本仓库有同源事故)→ 按 Game.InFlightScene 当场兜底:非飞行场景的每帧回调一律不跑。
            if (Game.Instance == null || !Game.InFlightScene) return;

            w.Tick(Time.deltaTime);
        }
    }
}
