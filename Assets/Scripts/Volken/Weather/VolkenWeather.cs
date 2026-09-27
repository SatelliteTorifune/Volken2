using System;
using System.Collections.Generic;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;

namespace Volken.Weather
{
    /// <summary>
    /// Volken 天气系统单例 —— 天气状态机与各子系统(雨/雾/雷电)的唯一驱动源。
    ///
    /// 【从 SP2 移植的是什么】移植的是 **SP2 <c>VolumetricEnvironment</c> 的纯逻辑**:
    ///   - 天气值标度与"淡变到目标值"的推进方式;
    ///   - 随机天气规则(<c>PickRandomWeather</c>,含晴后必转云、雨/暴后必转晴的取值偏置);
    ///   - 黎明起雾特例;
    ///   - 天气值变化事件。
    ///
    /// 【刻意不移植的是什么】Enviro 预设混合(<c>LerpWeatherTypes</c>)、光照/雾反射混合、
    /// <c>IsDarkOutside</c>/<c>Brightness</c> 夜间光照 —— Volken 有自己的云/天空栈,
    /// 引入 Enviro 预设混合会与 <c>Clouds.shader</c> 合成链冲突(计划 §3 已决策不引入 Enviro3)。
    ///
    /// 【SR2 与原版的差异,以及本实现的对策】
    ///   1. SP2 有 <c>FlightSceneScript.Instance.Environment.WeatherValue</c> 这个全局环境对象,
    ///      SR2 没有 → **天气状态由本单例持有**,云/雾/雨/闪电全部以它为唯一事实源。
    ///   2. SP2 的 <c>TimeOfDay</c>/<c>LengthOfDay</c> 来自 Enviro 时间模块,SR2 没有时间概念 →
    ///      由**太阳方向与地表法线的夹角**现算一个局部太阳时(见 <see cref="ComputeLocalSolarHour"/>),
    ///      供"黎明起雾"使用。精度足够(这是个氛围特例,不是天文计算)。
    ///   3. SP2 的风来自 <c>WindManager</c>,SR2 没有 → 风沿用 <c>CloudConfig.windSpeed/windDirection</c>
    ///      (云系统已经在用同一套参数,雨复用可保证云雨同向)。
    ///
    /// 【挂载方式】不依赖任何场景物体:随 <c>Volken</c> 生命周期,由 <c>Mod.OnModLoaded</c> 初始化,
    /// 由 <c>Volken.Core.VolkenMod.OnSceneLoaded</c>/<c>Volken.Core.VolkenMod.OnPlayerChangedSoi</c> 通知行星切换,内部自建临时
    /// GameObject 承载需要 MonoBehaviour 的子系统(闪电/雨)。
    /// </summary>
    public class VolkenWeather
    {
        public static VolkenWeather Instance { get; private set; }

        // ================= 事件 =================

        /// <summary>天气值发生变化(旧值, 新值)。各子系统订阅此事件决定启停/强度。</summary>
        public event Action<float, float> WeatherValueChanged;

        /// <summary>该行星的天气配置被(重新)加载完成。子系统据此重建自己的参数。</summary>
        public event Action<string> PlanetConfigLoaded;

        // ================= 状态 =================

        /// <summary>当前行星名(空 = 尚未进入任何有天气的行星)。</summary>
        public string CurrentPlanet { get; private set; } = string.Empty;

        /// <summary>
        /// 当前使用的**天气预设名**。**与云层预设完全独立** —— 存在
        /// <c>&lt;PlanetConfig WeatherConfigName&gt;</c> 上,与 <c>CloudConfigName</c> 各不相干,
        /// 所以可以单独换天气预设而不动云(反之亦然)。
        /// </summary>
        public string CurrentConfigName { get; private set; } = VolkenWeatherConfig.DefaultConfigName;

        /// <summary>
        /// 本行星可选的**天气预设名**(扫 <c>UserData/VolkenWeatherConfig/{行星}/</c>)。
        /// 面板那个"加载配置"下拉直接用这一份 —— 与云的预设列表没有关系。
        /// </summary>
        public List<string> AvailableConfigs { get; private set; } =
            new List<string> { VolkenWeatherConfig.DefaultConfigName };

        /// <summary>
        /// 当前内存里的天气参数 —— 从 <c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>
        /// 读出来的**独立实例**(存法与 <see cref="Volken.Clouds.CloudConfig"/> 一致)。
        ///
        /// 【2026-10 改动】原先天气是内联在 <c>PlanetConfigList.xml</c> 记录里的
        /// (一颗行星一套、不随预设走);现在按预设名存成独立文件,预设名与云层共用,
        /// 换预设时云与天气一起换。面板改的是这个实例,点"保存当前配置"才落盘到该预设文件。
        /// </summary>
        public VolkenWeatherConfig Config { get; private set; } = VolkenWeatherConfig.CreateDefault();

        /// <summary>当前天气值(连续标度 0~<see cref="VolkenWeatherConfig.OverallSection.maxWeatherValue"/>,见类注释)。</summary>
        public float WeatherValue { get; private set; } = VolkenWeatherConfig.DefaultWeatherValue;

        /// <summary>正在淡变到的目标天气值。</summary>
        public float TargetWeatherValue { get; private set; } = VolkenWeatherConfig.DefaultWeatherValue;

        /// <summary>天气系统当前是否实际在跑(有配置 + 启用 + 在飞行场景 + 有大气)。</summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// 天气值的**描述性**文字(只用于日志/UI 显示)。
        ///
        /// ⚠️ 这不是"天气档位" —— Volken **没有** SP2 那种全局预设系统
        /// (那个 <c>WeatherTypes</c> 已按用户要求删除)。这里只是把连续值切成几段好读的标签,
        /// **不驱动任何逻辑**;各子系统各自看自己的阈值字段
        /// (<see cref="VolkenWeatherConfig.RainSection.triggerValue"/> /
        /// <see cref="VolkenWeatherConfig.LightningSection.stormValue"/>)。
        /// </summary>
        public string WeatherName => DescribeWeatherValue(WeatherValue);

        /// <summary>把连续天气值切成可读标签(纯显示,无逻辑含义)。</summary>
        public static string DescribeWeatherValue(float v)
        {
            if (v < -0.5f) return "Foggy";
            if (v < 0.1f) return "Clear";
            if (v < 0.5f) return "Few";
            if (v < 1.0f) return "Broken";
            if (v < 2.0f) return "Overcast";
            if (v < 2.4f) return "Rainy";
            if (v < 2.6f) return "Stormy";
            return "Heavy";
        }

        /// <summary>相机海拔(ASL,米)。每帧刷新,雨/雾的高度判定共用。</summary>
        public float CameraAltitudeAsl { get; private set; }

        /// <summary>相机海拔(AGL,米)。取不到地形高度时退化为 ASL。</summary>
        public float CameraAltitudeAgl { get; private set; }

        /// <summary>相机是否在水下。</summary>
        public bool CameraSubmerged { get; private set; }

        /// <summary>局部太阳时(0~24,12 ≈ 正午,6 ≈ 日出,18 ≈ 日落)。见 <see cref="ComputeLocalSolarHour"/>。</summary>
        public float LocalSolarHour { get; private set; } = 12f;

        /// <summary>相机正在云层中/云层高度附近的淡化因子(0=云外, 1=云内)。雨在云内应渐隐。</summary>
        public float CameraCloudFade { get; private set; }

        // ================= 内部状态 =================

        private float _timeSinceWeatherChange;      // 距上次随机天气经过的游戏时间
        private float _timeSinceWeatherUpdate;      // 距上次淡变步进的游戏时间
        private float _targetWeatherDuration = 600f;
        private bool _isFoggyDawn;
        private bool _initialized;
        private bool _subscribedToSoi;

        private GameObject _host;                   // 承载子系统 MonoBehaviour 的临时物体
        private LightningModule _lightning;
        private Shader _boltShader;
        private bool _boltShaderTried;

        // 上一帧相机位置,用于推算玩家/相机速度(雨丝对齐需要)
        private Vector3 _lastCamPos;
        private float _lastCamTime = -1f;
        private Vector3 _smoothedCamVelocity;

        /// <summary>相机速度(米/秒,平滑后)。雨丝拉伸方向需要 "玩家速度 − 相机速度"。</summary>
        public Vector3 CameraVelocity { get; private set; }

        // ================= 初始化 =================

        /// <summary>
        /// 初始化单例(幂等)。由 <c>Mod.OnModLoaded</c> 调用一次。
        /// 只建立宿主物体与订阅场景事件,不加载任何行星配置(那要等进飞行场景)。
        /// </summary>
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
                _host.AddComponent<WeatherTicker>();
                _host.SetActive(true);
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: host creation failed: " + ex.Message);
            }

            try
            {
                Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: SceneLoaded subscribe failed: " + ex.Message);
            }

            TrySubscribeSoi();
            Mod.Log("VolkenWeather initialized");
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
                // 加载期 FlightScene 可能尚不可用,由 OnSceneLoaded 再试(与 Volken 同策略)
            }
        }

        // ================= 行星切换 =================

        private void OnSceneLoaded(object sender, ModApi.Scenes.Events.SceneEventArgs e)
        {
            if (e.Scene != "Flight")
            {
                // 离开飞行场景 → 停掉天气(不做卸载,配置留着)
                SetActive(false);
                return;
            }

            bool hasAtmo = false;
            string planet = null;
            try
            {
                var planetNode = Game.Instance?.FlightScene?.CraftNode?.Parent;
                if (planetNode != null)
                {
                    planet = planetNode.Name;
                    hasAtmo = planetNode.PlanetData != null && planetNode.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
                }
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather.OnSceneLoaded planet lookup failed: " + ex.Message);
            }

            ApplyPlanet(planet, hasAtmo);
            TrySubscribeSoi();
        }

        private void OnPlayerChangedSoi(ICraftNode craftNode, IPlanetNode newParent)
        {
            try
            {
                if (newParent == null) return;

                // 恒星(没有 parent 的天体)没有大气,天气直接停
                bool hasAtmo = newParent.PlanetData != null
                    && newParent.PlanetData.AtmosphereData.HasPhysicsAtmosphere;
                ApplyPlanet(newParent.Name, hasAtmo);
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather.OnPlayerChangedSoi ERROR: " + ex);
            }
        }

        /// <summary>
        /// 应用某个行星的天气配置。无大气 → 停用;有大气 → 取该行星登记的**预设名**
        /// (与云层同一个名字,规则见 <c>Volken.Core.VolkenMod.OnSceneLoaded</c>),
        /// 再读 <c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>,不存在则建默认(关闭)并落盘。
        ///
        /// 同行星重复调用(OnSceneLoaded + SOI 会连着来)→ 不重载,避免把状态机进度冲掉。
        /// **换预设请直接调 <see cref="ApplyPreset"/>(会被这里的去重挡住)**。
        /// </summary>
        public void ApplyPlanet(string planetName, bool hasAtmosphere)
        {
            if (string.IsNullOrEmpty(planetName) || !hasAtmosphere)
            {
                CurrentPlanet = planetName ?? string.Empty;
                SetActive(false);
                return;
            }

            // 同行星重复调用(OnSceneLoaded + SOI 会连着来)→ 不重载,避免把状态机进度冲掉
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
                var list = Volken.Core.VolkenMod.Instance?.planetConfigList;
                var name = list?.GetWeatherConfigName(planetName);
                return string.IsNullOrEmpty(name) ? VolkenWeatherConfig.DefaultConfigName : name;
            }
            catch (Exception ex)
            {
                Mod.Log("VolkenWeather: resolve weather preset name failed for " + planetName + ": " + ex.Message);
                return VolkenWeatherConfig.DefaultConfigName;
            }
        }

        /// <summary>
        /// 按**天气预设名**装载配置(与云层预设无关)。进新行星、换天气预设都走这里。
        /// 每次都重新读文件,并把状态机重置到该配置的初值。
        /// </summary>
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

            // 重置状态机到该配置的初值(换行星/换预设 = 天气从头开始)
            WeatherValue = cfg.overall.initialWeatherValue;
            TargetWeatherValue = cfg.overall.initialWeatherValue;
            _targetWeatherDuration = UnityEngine.Random.Range(cfg.overall.minDuration, cfg.overall.maxDuration);
            _timeSinceWeatherChange = 0f;
            _timeSinceWeatherUpdate = 0f;
            _isFoggyDawn = false;

            // 预设里的 overall.enabled 开才真的跑
            SetActive(cfg.overall.enabled);

            Mod.Log($"VolkenWeather: planet={planetName} preset={configName} cfgEnabled={cfg.overall.enabled} " +
                    $"active={IsActive} dynamic={cfg.overall.dynamicWeather} " +
                    $"value={WeatherValue:F2}({WeatherName})");

            try { PlanetConfigLoaded?.Invoke(planetName); }
            catch (Exception ex) { Mod.Log("VolkenWeather: PlanetConfigLoaded handler threw: " + ex.Message); }

            UpdateLightning();

            RaiseWeatherValueChanged(WeatherValue, WeatherValue);
        }

        // ================= 天气预设的独立管理(与云层预设无关) =================

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

        /// <summary>
        /// 切换到另一个**天气预设** —— 只换天气,**完全不碰云层配置**;
        /// 并把它登记为这颗行星以后使用的天气预设。
        /// </summary>
        public void SwitchPreset(string configName)
        {
            if (string.IsNullOrEmpty(CurrentPlanet) || string.IsNullOrWhiteSpace(configName)) return;
            if (configName == CurrentConfigName) return;

            ApplyPreset(CurrentPlanet, configName);
            RegisterPresetName();
        }

        /// <summary>
        /// 把当前天气参数**另存为一个新预设**(玩家给名字)并切到它。
        /// 纯天气操作:不新建、不修改任何云层配置。
        /// </summary>
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
                var list = Volken.Core.VolkenMod.Instance?.planetConfigList;
                if (list == null || string.IsNullOrEmpty(CurrentPlanet)) return;
                if (list.GetWeatherConfigName(CurrentPlanet) == CurrentConfigName) return;
                list.SetWeatherConfig(CurrentPlanet, CurrentConfigName);
            }
            catch (Exception ex) { Mod.Log("VolkenWeather: register preset name failed: " + ex.Message); }
        }

        /// <summary>
        /// 把当前内存里的天气参数**落盘**到本行星当前天气预设的文件
        /// (<c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>),并确保清单里记着这个预设名。
        /// **只动天气这一份,不碰云的任何配置。**
        /// </summary>
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

        /// <summary>
        /// 把内存里的天气参数恢复成默认(**完全关闭**),不落盘 ——
        /// 与云层"重置为默认"同语义:需要玩家再点"保存"才写文件。
        /// </summary>
        public void ResetCurrentConfigToDefault()
        {
            Config?.CopyFrom(VolkenWeatherConfig.CreateDefault());
            SetActive(Config?.overall?.enabled ?? false);
            UpdateLightning();
            RaiseWeatherValueChanged(WeatherValue, WeatherValue);
        }

        /// <summary>
        /// 当前是否处于"雨天"区间(天气值 &gt; 该行星配置里的
        /// <see cref="VolkenWeatherConfig.RainSection.triggerValue"/>,默认 2.25)。
        ///
        /// 【2026-09-27 起:无消费者】雨系统(雨丝/雨声/雾)已按用户要求**整体移除**,
        /// 只保留雷电。这个属性留着是因为:
        ///   1. 它是天气标度里一个有意义的分段(日志/UI/诊断仍用);
        ///   2. 将来重做雨系统时,这是最自然的触发判据。
        /// 它**不驱动任何渲染** —— 别误以为"改了它会有雨"。
        /// </summary>
        public bool IsRaining
        {
            get
            {
                float trigger = Config?.rain?.triggerValue ?? 2.25f;
                return WeatherValue > trigger;
            }
        }

        // ================= 天气 → 云层 =================
        //
        // 【明确不做联动 —— 2026-09-27 用户决定】
        // 云层的**厚度/覆盖度/浓度/颜色/风速全部由云自己的配置(CloudConfig / 云面板滑块)决定**,
        // 天气系统一概不碰。
        //
        // 原因:联动(尤其是"天气值 → 云覆盖度/云厚度")会让玩家在云面板上调好的云形
        // 被天气悄悄改掉 —— 调参结果不稳定,而且很难判断"看到的是我调的,还是天气改的"。
        // 云的观感归云,雨的观感归雨,两者互不干涉。
        //
        // 历史:曾经实现过 ApplyCloudCoupling(覆盖度 / 云色暗化 / 风速,从基线纯函数重算,
        // 增益默认 0)。代码与 UI 已全部移除。**不要再加回来** —— 如果将来真要联动,
        // 必须做成面板里可以明确关掉、且默认关的显式选项,而不是隐式行为。

        /// <summary>
        /// 按当前配置/天气值起停雷电。天气值本身每帧都在 <see cref="Tick"/> 里推进,
        /// 但雷电模块自己会在等待间隔里复核天气值(与 SP2 的 lightningStorm 复核同构),
        /// 因此这里只需要处理"总开关 + 行星启停"这两件粗粒度的事。
        /// </summary>
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

        /// <summary>
        /// 载入闪电 shader。与云 shader 同一套加载约定(<c>Mod.LoadVolkenAsset</c>),
        /// 失败时回退 <c>Shader.Find</c>(开发期 shader 可能已在内存里)。
        /// </summary>
        private Shader LoadBoltShader()
        {
            if (_boltShaderTried) return _boltShader;
            _boltShaderTried = true;

            try
            {
                _boltShader = Mod.LoadVolkenAsset<Shader>("Assets/Scripts/Volken/Weather/LightningBolt.shader");
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

        /// <summary>
        /// 手动劈一道雷(dev 命令 / UI 按钮)。无视天气值阈值,但要求配置启用。
        /// </summary>
        public void TriggerLightning()
        {
            if (_lightning == null)
            {
                Mod.Log("VolkenWeather: TriggerLightning ignored — lightning module not active " +
                        "(need enabled planet config + lightning.enabled)");
                return;
            }
            _lightning.CastRandomBolt();
        }

        /// <summary>雷电模块(诊断/UI 用,可能为 null)。</summary>
        public LightningModule Lightning => _lightning;

        private void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            UpdateLightning();
            try { WeatherValueChanged?.Invoke(WeatherValue, WeatherValue); }
            catch (Exception ex) { Mod.Log("VolkenWeather: activation handler threw: " + ex.Message); }
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

        /// <summary>外部(UI/dev 命令)强制设置天气值,立即生效不走淡变。</summary>
        public void ForceWeatherValue(float value)
        {
            float old = WeatherValue;
            WeatherValue = value;
            TargetWeatherValue = value;
            _timeSinceWeatherChange = 0f;
            RaiseWeatherValueChanged(old, value);
        }

        /// <summary>外部(UI/dev 命令)设置目标天气值,由状态机淡变过去。</summary>
        public void SetTargetWeatherValue(float value, float duration = -1f)
        {
            TargetWeatherValue = value;
            if (duration > 0f) _targetWeatherDuration = duration;
            _timeSinceWeatherChange = 0f;
        }

        // ================= 每帧推进 =================

        /// <summary>
        /// 由场景中某个活跃 MonoBehaviour 的 Update 驱动(见 <c>WeatherTicker</c>)。
        /// 独立于 CloudRenderer:天气系统在相机被销毁/替换后仍应继续跑。
        /// </summary>
        public void Tick(float deltaTime)
        {
            UpdateCameraMetrics(deltaTime);

            // 【2026-09-27】雨/雾的每帧推进已在用户要求下**整体移除**,只保留雷电。
            // 雷暴循环由 LightningModule 自己的协程驱动,不需要这里每帧喂。

            if (!IsActive)
            {
                return;
            }

            var cfg = Config;
            if (cfg == null || !cfg.overall.enabled)
            {
                SetActive(false);
                return;
            }

            // 时间加速:SR2 的时间加速通过 TimeManager.DeltaTime 体现,天气跟着快进
            float dt = deltaTime * Mathf.Max(0f, cfg.overall.timeScaleFollow);
            if (dt <= 0f) return;

            if (cfg.overall.dynamicWeather)
            {
                float diff = Mathf.Abs(WeatherValue - TargetWeatherValue);
                if (diff > 0.03f)
                {
                    // 与 SP2 一致:每 updateInterval 步进一次,步长 = 经历时间 × 淡变速度
                    _timeSinceWeatherUpdate += dt;
                    if (_timeSinceWeatherUpdate > cfg.overall.updateInterval)
                    {
                        float old = WeatherValue;
                        WeatherValue = Mathf.MoveTowards(WeatherValue, TargetWeatherValue,
                            _timeSinceWeatherUpdate * cfg.overall.fadeSpeed);
                        _timeSinceWeatherUpdate = 0f;
                        if (!Mathf.Approximately(old, WeatherValue))
                        {
                            RaiseWeatherValueChanged(old, WeatherValue);
                        }
                    }
                }
                else
                {
                    _timeSinceWeatherChange += dt;
                    if (_timeSinceWeatherChange > _targetWeatherDuration)
                    {
                        PickRandomWeather();
                    }
                }
            }
            else
            {
                // 固定天气:只保证值对齐,不随机
                if (!Mathf.Approximately(WeatherValue, cfg.overall.fixedWeatherValue))
                {
                    float old = WeatherValue;
                    WeatherValue = cfg.overall.fixedWeatherValue;
                    TargetWeatherValue = cfg.overall.fixedWeatherValue;
                    RaiseWeatherValueChanged(old, WeatherValue);
                }
            }
        }

        /// <summary>
        /// 随机挑一个天气目标(移植 SP2 <c>VolumetricEnvironment.PickRandomWeather</c> 的取值规则):
        ///   - 黎明特例:局部太阳时 5~6 点、当前天气 &lt; 0.5、本黎明还没起过雾 → 目标取 [-0.7, -0.4](雾),
        ///     持续时长 = 剩下的天亮时间 − 淡变所需时间(即"雾到天亮就散");
        ///   - 常规:上一次目标 ≤ 0.2(晴/少云)→ 下限抬到 0.4(**晴后必转云**);
        ///           上一次目标 ≥ 2.0(雨/暴)→ 上限压到 1.5(**雨天后必转晴**);
        ///           其余 → 全区间 [0, 3]。
        /// </summary>
        private void PickRandomWeather()
        {
            var cfg = Config;
            _timeSinceWeatherChange = 0f;

            // === 黎明起雾特例 ===
            bool isDawnWindow = LocalSolarHour > 5f && LocalSolarHour < 6f;
            if (cfg.overall.foggyDawn && isDawnWindow && WeatherValue < 0.5f && !_isFoggyDawn)
            {
                float fogTarget = UnityEngine.Random.Range(-0.7f, -0.4f);
                // 天亮还剩多少游戏秒:1 小时 = 24 分之一个昼夜 = LengthOfDay/24 游戏秒
                float lengthOfDay = EstimateLengthOfDaySeconds();
                float secondsPerHour = lengthOfDay / 24f;
                float hoursUntilDawnEnds = 6f - LocalSolarHour;
                float fadeSeconds = Mathf.Abs(fogTarget - WeatherValue) / Mathf.Max(1e-5f, cfg.overall.fadeSpeed);
                float duration = hoursUntilDawnEnds * secondsPerHour - fadeSeconds;

                _isFoggyDawn = true;
                TargetWeatherValue = fogTarget;
                _targetWeatherDuration = Mathf.Max(0f, duration);

                Mod.Log($"VolkenWeather: foggy dawn → target={fogTarget:F2} duration={_targetWeatherDuration:F0}s");
                return;
            }

            if (!isDawnWindow)
            {
                _isFoggyDawn = false;
            }

            // === 常规取值(带偏置) ===
            // 上限来自配置(OverallSection.maxWeatherValue,默认 3),不再是 SP2 的硬编码 [0,3]。
            // 下限的"晴后必转云"偏置按上限的比例给(0.4/3 ≈ 13%),这样调上限不会让偏置失真。
            float ceiling = Mathf.Max(0.1f, cfg.overall.maxWeatherValue);
            float min = TargetWeatherValue <= 0.2f ? ceiling * 0.133f : 0f;
            float max = TargetWeatherValue >= 2.0f ? Mathf.Min(1.5f, ceiling) : ceiling;
            if (max <= min) max = Mathf.Min(ceiling, min + 0.1f);
            TargetWeatherValue = UnityEngine.Random.Range(min, max);
            _targetWeatherDuration = UnityEngine.Random.Range(cfg.overall.minDuration, cfg.overall.maxDuration);

            Mod.Log($"VolkenWeather: picked weather target={TargetWeatherValue:F2}({DescribeWeatherValue(TargetWeatherValue)}) " +
                    $"duration={_targetWeatherDuration:F0}s range=[{min:F1},{max:F1}]");
        }

        private void RaiseWeatherValueChanged(float oldValue, float newValue)
        {
            try { WeatherValueChanged?.Invoke(oldValue, newValue); }
            catch (Exception ex) { Mod.Log("VolkenWeather: WeatherValueChanged handler threw: " + ex); }
        }

        // ================= 相机指标 =================

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

                // 云内淡化:相机海拔落在云层高度带内 → 1(与 SP2 CameraCloudFadeVal 同义)
                CameraCloudFade = ComputeCameraCloudFade(planetNode, asl);
            }
            catch (Exception ex)
            {
                // 每帧调用,不刷日志
                if (Time.frameCount % 600 == 0)
                    Mod.Log("VolkenWeather.UpdateCameraMetrics: " + ex.Message);
            }
        }

        /// <summary>
        /// 由太阳方向与地表法线的夹角现算局部太阳时(0~24)。
        ///
        /// SR2 没有 TimeOfDay,但太阳光方向是现成的
        /// (<c>IGameView.SunLight</c>,<c>forward</c> = 太阳射向场景的方向,与
        /// <c>CloudRenderer.SetLayerDynamicProperties</c> 里 <c>lightDir</c> 的用法一致)。
        /// 把"太阳方向 · 地表法线"映射成时角:
        ///   dot = +1(正对太阳)→ 12 时;dot = 0(晨昏线)→ 6 / 18 时;dot = −1(背对)→ 0 / 24 时。
        ///
        /// **已知局限(刻意,可接受)**:无法区分"日出侧的 6 点"与"日落侧的 18 点" ——
        /// 行星自转轴朝向在 SR2 侧没有可靠来源(本体轴没有公开字段,用本地"北"推断会随飞行翻转),
        /// 硬猜会得到一个**随机**的日/夜半边。因此本实现把 0~12 一律当"上行(日出侧)"、
        /// 12~24 当"下行",即 **黎明雾特效在日出与日落两侧对等触发**。
        /// 后果:雾会在黄昏也起一次;对氛围特例无伤,且行为稳定可预测 —— 比"随机翻面"好。
        ///
        /// 另:它给的是"相机所在经度的局部太阳时",不含行星公转/极昼极夜;纯粹给
        /// <see cref="PickRandomWeather"/> 的黎明特例用,不参与任何物理计算。
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

        /// <summary>
        /// 相机在云层内的淡化因子(移植 SP2 <c>CameraCloudFadeVal</c>):
        /// <c>1 − InverseLerp(云底, 云顶−400, 相机ASL)</c> —— 云底以下 = 1(完全可见),
        /// 云顶附近 = 0(雨在云内渐隐,避免云里看见雨丝)。
        /// 云层高度取自 Volken 主层的 <c>layerHeights</c>,因此雨和云永远同层。
        /// </summary>
        private float ComputeCameraCloudFade(IPlanetNode planetNode, float asl)
        {
            try
            {
                var main = Volken.Core.VolkenMod.Instance?.MainLayer;
                var cloudCfg = main?.config;
                if (cloudCfg == null || !cloudCfg.enabled) return 1f;

                // layerHeights/layerSpreads 是 Vector4,取实际启用的层里最低底与最高顶
                float bottom = float.MaxValue, top = float.MinValue;
                var heights = cloudCfg.layerHeights;
                var spreads = cloudCfg.layerSpreads;
                var strengths = cloudCfg.layerStrengths;
                for (int i = 0; i < 4; i++)
                {
                    float strength = i == 0 ? strengths.x : i == 1 ? strengths.y : i == 2 ? strengths.z : strengths.w;
                    if (strength <= 0f) continue;
                    float h = i == 0 ? heights.x : i == 1 ? heights.y : i == 2 ? heights.z : heights.w;
                    float sp = i == 0 ? spreads.x : i == 1 ? spreads.y : i == 2 ? spreads.z : spreads.w;
                    bottom = Mathf.Min(bottom, h - sp);
                    top = Mathf.Max(top, h + sp);
                }
                if (bottom > top) return 1f;

                float fadeTop = top - 400f;
                if (fadeTop <= bottom) return 1f;
                return 1f - Mathf.InverseLerp(bottom, fadeTop, asl);
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// 估算行星昼夜长度(游戏秒)。SR2 没有现成字段,用行星自转角速度反推:
        /// 昼夜 = 2π/|ω| 秒。取不到 → 回退 3600 秒(仅影响黎明雾的持续时长估算)。
        /// </summary>
        private float EstimateLengthOfDaySeconds()
        {
            try
            {
                var pd = Game.Instance?.FlightScene?.CraftNode?.Parent?.PlanetData;
                if (pd != null)
                {
                    double w = Math.Abs(pd.AngularVelocity);
                    if (w > 1e-9) return (float)(2.0 * Math.PI / w);
                }
            }
            catch { }
            return 3600f;
        }
    }

    /// <summary>
    /// 驱动 <see cref="VolkenWeather.Tick"/> 的极简 MonoBehaviour。
    /// 单独一个小类,是为了让天气系统不依赖任何具体场景相机(相机被销毁/替换时天气仍在跑),
    /// 同时又不需要 VolkenWeather 自己继承 MonoBehaviour(保持纯逻辑好测试)。
    /// </summary>
    public class WeatherTicker : MonoBehaviour
    {
        private void Update()
        {
            var w = VolkenWeather.Instance;
            if (w == null) return;
            w.Tick(Time.deltaTime);
        }
    }
}
