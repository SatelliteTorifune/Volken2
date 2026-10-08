using System;
using System.Collections.Generic;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using Volken.Clouds;

namespace Volken.Weather
{
    /// <summary>
    /// 天气系统:按行星装载天气预设,并托管其下的子系统(雨 / 雷电 / 雾占位)。
    ///
    /// 与云的关系:**直接读对方 config,不造接口层**;但**读 ≠ 联动** —— 天气不修改云的任何参数。
    /// 行星切换订阅 <see cref="VolkenClouds.PlanetChanged"/>,不自己监听场景事件。
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

        /// <summary>天气系统当前是否实际在跑(有配置 + 启用 + 在飞行场景 + 有大气)。</summary>
        public bool IsActive { get; private set; }

        /// <summary>相机海拔(ASL,米)。每帧刷新,雨/雾的高度判定共用。</summary>
        public float CameraAltitudeAsl { get; private set; }

        /// <summary>相机海拔(AGL,米)。取不到地形高度时退化为 ASL。</summary>
        public float CameraAltitudeAgl { get; private set; }

        public bool CameraSubmerged { get; private set; }

        /// <summary>局部太阳时(0~24,12 ≈ 正午,6 ≈ 日出,18 ≈ 日落)。见 <see cref="ComputeLocalSolarHour"/>;黎明雾用它判定窗口。</summary>
        public float LocalSolarHour { get; private set; } = 12f;

        /// <summary>相机正在云层中/云层高度附近的淡化因子(0=云外, 1=云内)。</summary>
        public float CameraCloudFade { get; private set; }

        private bool _initialized;

        private GameObject _host;                   // 承载子系统 MonoBehaviour 的临时物体
        private WeatherTicker _ticker;
        private LightningModule _lightning;
        private Shader _boltShader;
        private bool _boltShaderTried;

        // 上一帧相机位置,用于推算玩家/相机速度(雨丝对齐需要)
        private Vector3 _lastCamPos;
        private float _lastCamTime = -1f;
        private Vector3 _smoothedCamVelocity;

        /// <summary>相机速度(米/秒,平滑后)。雨丝拉伸方向需要 "玩家速度 − 相机速度"。</summary>
        public Vector3 CameraVelocity { get; private set; }

        /// <summary>初始化单例(幂等)。由 <c>Mod.OnModLoaded</c> 调用,必须在 <see cref="VolkenClouds.Initialize"/> 之后。</summary>
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

            if (VolkenClouds.Instance != null)
            {
                VolkenClouds.Instance.PlanetChanged += OnPlanetChanged;
            }
            else
            {
                Mod.Log("VolkenWeather: VolkenClouds 未初始化 —— 天气不会跟随行星切换(初始化顺序错误)");
            }

            Mod.Log("VolkenWeather initialized");
        }

        // 行星切换(唯一入口:VolkenClouds 的事件)

        /// <summary>行星环境变化(由 <see cref="VolkenClouds.PlanetChanged"/> 广播)。</summary>
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
                var list = VolkenClouds.Instance?.planetConfigList;
                var name = list?.GetWeatherConfigName(planetName);
                return string.IsNullOrEmpty(name) ? VolkenWeatherConfig.DefaultConfigName : name;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: resolve weather preset name failed for " + planetName + ": " + ex.Message);
                return VolkenWeatherConfig.DefaultConfigName;
            }
        }

        /// <summary>按天气预设名装载配置。进新行星 / 换预设都走这里。</summary>
        public void ApplyPreset(string planetName, string configName)
        {
            if (string.IsNullOrEmpty(planetName))
            {
                return;
            }
            if (string.IsNullOrEmpty(configName))
            {
                configName = VolkenWeatherConfig.DefaultConfigName;
            }

            CurrentPlanet = planetName;
            CurrentConfigName = configName;
            RefreshPresetList(planetName);

            VolkenWeatherConfig cfg;
            try
            {
                cfg = VolkenWeatherConfig.LoadFromFile(planetName, configName);
            }
            catch (Exception ex)
            {
                Mod.Log($"VolkenWeather: load preset '{configName}' for {planetName} failed: {ex.Message}");
                cfg = VolkenWeatherConfig.CreateDefault();
            }
            Config = cfg;

            // 预设里的 overall.enabled 开才真的跑
            SetActive(cfg.overall.enabled);

            Mod.Log($"VolkenWeather: planet={planetName} preset={configName} cfgEnabled={cfg.overall.enabled} " +
                    $"active={IsActive}");

            try { PlanetConfigLoaded?.Invoke(planetName); }
            catch (Exception ex) { Mod.Log("VolkenWeather: PlanetConfigLoaded handler threw: " + ex.Message); }

            UpdateLightning();
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

            ApplyPreset(CurrentPlanet, configName);
            RegisterPresetName();
            return true;
        }

        /// <summary>把当前天气参数另存为新预设(玩家给名字)并切到它。纯天气操作,不碰云。</summary>
        public bool SaveAsNewConfig(string newName)
        {
            if (string.IsNullOrEmpty(CurrentPlanet) || Config == null || string.IsNullOrWhiteSpace(newName))
            {
                return false;
            }

            Config.SaveToFile(CurrentPlanet, newName);
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
                var list = VolkenClouds.Instance?.planetConfigList;
                if (list == null || string.IsNullOrEmpty(CurrentPlanet)) return;
                if (list.GetWeatherConfigName(CurrentPlanet) == CurrentConfigName) return;
                list.SetWeatherConfig(CurrentPlanet, CurrentConfigName);
            }
            catch (Exception ex) { Mod.Log("VolkenWeather: register preset name failed: " + ex.Message); }
        }

        /// <summary>把当前天气参数落盘到本行星的天气预设文件,并确保清单里记着这个预设名。</summary>
        public void SaveCurrentConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(CurrentPlanet) || Config == null)
                {
                    Mod.Log("VolkenWeather: SaveCurrentConfig 失败 —— 当前没有行星/配置");
                    return;
                }
                Config.SaveToFile(CurrentPlanet, CurrentConfigName);
                RegisterPresetName();
            }
            catch (Exception ex) { Mod.Log("VolkenWeather: SaveCurrentConfig error: " + ex.Message); }
        }

        /// <summary>把内存里的天气参数恢复成默认(完全关闭),不落盘 —— 需玩家再点"保存"才写文件。</summary>
        public void ResetCurrentConfigToDefault()
        {
            Config?.CopyFrom(VolkenWeatherConfig.CreateDefault());
            SetActive(Config?.overall?.enabled ?? false);
            UpdateLightning();
        }

        // 【不做联动】天气不修改云的任何参数。要做的话见 docs/archive/weather-cloud-decoupling-2026-10-01.md §3.6。

        /// <summary>按当前配置/行星起停雷电(只管总开关 + 行星启停;雷暴节奏由 <see cref="LightningModule"/> 自己管)。</summary>
        private void UpdateLightning()
        {
            var cfg = Config;
            bool want = IsActive && cfg != null && cfg.overall.enabled && cfg.lightning.enabled;

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

            _lightning.SetActive(true);
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

        /// <summary>手动劈一道雷(dev 命令 / UI)。要求配置启用。返回 false = 没落雷(模块未激活 / 海拔闸门拦下)。</summary>
        public bool TriggerLightning()
        {
            if (_lightning == null)
            {
                Mod.Log("VolkenWeather: TriggerLightning ignored — lightning module not active " +
                        "(need enabled planet config + lightning.enabled)");
                return false;
            }
            return _lightning.CastRandomBolt();
        }

        /// <summary>雷电模块(诊断/UI 用,可能为 null)。</summary>
        public LightningModule Lightning => _lightning;

        private void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            UpdateLightning();
        }

        /// <summary>玩家在面板上拨动"启用天气 / 启用雷电"时调用,重算激活态并同步雷电启停。</summary>
        public void RefreshActiveState()
        {
            var cfg = Config;
            bool want = cfg != null && cfg.overall.enabled;
            SetActive(want);
            UpdateLightning();
            Mod.Log($"VolkenWeather: enabled toggled → active={IsActive}");
        }

        /// <summary>由 <c>WeatherTicker</c> 每帧驱动。独立于任何渲染器:相机被销毁/替换后天气仍继续跑。</summary>
        public void Tick(float deltaTime)
        {
            UpdateCameraMetrics(deltaTime);

            // 雨挂在场景相机上(随 Flight 场景卸载就没了)→ 每个飞行场景都要按配置重挂;不新增 SceneLoaded 订阅者,由这条每帧门控承担
            RainParticles.SyncToCurrentView();

            // 子系统各自按自己的配置跑(雷暴节奏由 LightningModule 自己管),这里不喂。
            if (!IsActive) return;

            var cfg = Config;
            if (cfg == null || !cfg.overall.enabled)
            {
                SetActive(false);
            }
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

                // 相机速度(平滑):雨丝拉伸方向需要 "玩家速度 − 相机速度"
                float now = Time.realtimeSinceStartup;
                if (_lastCamTime > 0f)
                {
                    float dt = Mathf.Max(1e-3f, now - _lastCamTime);
                    Vector3 inst = (camPos - _lastCamPos) / dt;
                    _smoothedCamVelocity = Vector3.Lerp(_smoothedCamVelocity, inst, 0.25f);
                }
                _lastCamPos = camPos;
                _lastCamTime = now;
                CameraVelocity = _smoothedCamVelocity;

                LocalSolarHour = ComputeLocalSolarHour(camPos, planetCenter);

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

        /// <summary>
        /// 由太阳方向与地表法线夹角现算局部太阳时(0~24),仅供黎明雾特例用。
        /// **局限**:分不出日出侧 6 点与日落侧 18 点(行星自转轴朝向在 SR2 不可靠),故黎明雾两侧对等触发。
        /// </summary>
        private float ComputeLocalSolarHour(Vector3 camPos, Vector3 planetCenter)
        {
            try
            {
                var sun = Game.Instance?.FlightScene?.ViewManager?.GameView?.SunLight;
                if (sun == null) return LocalSolarHour;

                Vector3 toSun = -sun.transform.forward;
                Vector3 up = camPos - planetCenter;
                if (up.sqrMagnitude < 1e-6f) return LocalSolarHour;
                up.Normalize();

                float cosAngle = Mathf.Clamp(Vector3.Dot(up, toSun), -1f, 1f);
                // dot=+1 → 12 时;dot=−1 → 0 时
                float hour = Mathf.Acos(cosAngle) * Mathf.Rad2Deg / 15f;
                if (hour > 12f) hour = 24f - hour;

                return Mathf.Repeat(hour, 24f);
            }
            catch
            {
                return LocalSolarHour;
            }
        }

        /// <summary>相机在云层内的淡化因子(云底以下 = 1,云顶附近 = 0)。没有云 → 1。仅供状态显示与闪电起点。</summary>
        private float ComputeCameraCloudFade(float asl)
        {
            try
            {
                var cloudCfg = VolkenClouds.Instance?.MainLayer?.config;
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
