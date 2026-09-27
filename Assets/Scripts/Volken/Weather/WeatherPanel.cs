using System;
using Assets.Scripts;
using ModApi;
using ModApi.Ui.Inspector;
using UnityEngine;

namespace Volken.Weather
{
    
    /// <summary>
    /// 天气面板 —— 加到 Volken 检查器里的一组控件。
    ///
    /// 【分组结构:与 <see cref="VolkenWeatherConfig"/> 的节点一一对应】
    ///   天气(根,已并入"总体")
    ///     ├─ 状态(只读:行星 / 天气值 / 运行状态 / 海拔 / 太阳时)
    ///     ├─ 总体          —— 总开关 / 动态天气 / 天气节奏(直接挂在根组,无独立子组)
    ///     ├─ 配置管理       —— 天气**自己的**预设(独立于云层):当前配置 / 保存 / 另存为新配置 / 加载配置(下拉) / 重置
    ///     ├─ 云层联动       —— 机制窗口(**整组禁用**,字段占位,默认全 0 = 恒等)
    ///     ├─ 雨             —— 占位(**整组禁用**,实现已移除)
    ///     ├─ 雾             —— 占位(**整组禁用**,实现已移除)
    ///     └─ 雷             —— 唯一可调的子系统
    ///
    /// 【设计取向(与云设置面板一致)】
    ///   - 面板直接操作**清单里那条记录上的天气参数实例本身**(不是副本),
    ///     改完点"保存当前配置"才落盘 —— 与云配置管理完全一样的语义;
    ///   - 滑块的生效时机是**下一帧**(<c>VolkenWeather.Tick</c> 每帧读 Config),调参实时;
    ///   - 雨/雾两组是**只读占位**:控件照结构铺出来但禁用,避免"改了没反应"的困惑。
    ///
    /// 【为什么不做成独立面板】Volken 已经有 <c>IInspectorPanel</c>,再开一个会得到两个
    /// 浮动窗口互相遮挡;天气与云也该在同一处调,才是"一套观感参数"。
    /// </summary>
    public static class WeatherPanel
    {
        /// <summary>往检查器里追加天气分组(无参数:换配置不需要重建面板)。</summary>
        public static void Build(InspectorModel inspector)
        {
            if (inspector == null) return;

            var root = new GroupModel(Locale.GetString("Volken.UI.Weather"));

            var weather = VolkenWeather.Instance;
            if (weather == null)
            {
                root.Add(new TextModel(Locale.GetString("Volken.UI.WeatherUnavailable"), () => "—"));
                inspector.AddGroup(root);
                return;
            }

            BuildStatusGroup(root, weather);
            BuildOverallGroup(root, weather);
            BuildConfigManagementGroup(root, weather);
            BuildCloudLinkageGroup(root, weather);
            BuildRainGroup(root, weather);
            BuildFogGroup(root, weather);
            BuildLightningGroup(root, weather);

            inspector.AddGroup(root);
        }

        // ======================= 状态(只读) =======================

        private static void BuildStatusGroup(GroupModel parent, VolkenWeather weather)
        {
            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherPlanet"),
                () => string.IsNullOrEmpty(weather.CurrentPlanet) ? "—" : weather.CurrentPlanet));

            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherValue"),
                () => $"{weather.WeatherValue:F2} ({weather.WeatherName})"));

            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherActive"),
                () => weather.IsActive
                    ? Locale.GetString("Volken.UI.WeatherActiveOn")
                    : Locale.GetString("Volken.UI.WeatherActiveOff")));

            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherAltitude"),
                () => $"{weather.CameraAltitudeAsl:F0} m ASL / {weather.CameraAltitudeAgl:F0} m AGL" +
                      $"   solar {weather.LocalSolarHour:F1}h   cloudFade {weather.CameraCloudFade:F2}"));
        }

        // ======================= 总体(并入"天气"根组) =======================

        private static void BuildOverallGroup(GroupModel parent, VolkenWeather weather)
        {
            // 行星级总开关(在 UI 里调,保存进 PlanetConfigList.xml 的 <Overall enabled>)
            parent.Add(new ToggleModel(Locale.GetString("Volken.UI.WeatherEnabled"),
                () => weather.Config?.overall?.enabled ?? false, v =>
                {
                    if (weather.Config?.overall == null) return;
                    weather.Config.overall.enabled = v;
                    weather.RefreshActiveState();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString(v ? "Volken.UI.WeatherEnabledOn" : "Volken.UI.WeatherEnabledOff"));
                }));

            parent.Add(new ToggleModel(Locale.GetString("Volken.UI.WeatherDynamic"),
                () => weather.Config?.overall?.dynamicWeather ?? false, v =>
                {
                    if (weather.Config?.overall == null) return;
                    weather.Config.overall.dynamicWeather = v;
                    if (!v) weather.SetTargetWeatherValue(weather.Config.overall.fixedWeatherValue);
                }));

            // 手动天气值(dev/调参用):直接强制,不走随机状态机
            // 范围 = [0-1, 配置的取值上限] —— 原先写死 SP2 的 [Foggy, Heavy] = [-1, 2.75]
            float ceiling = Mathf.Max(0.5f, weather.Config?.overall?.maxWeatherValue ?? 3f);
            var manual = new SliderModel(Locale.GetString("Volken.UI.WeatherManualValue"),
                () => VolkenWeather.Instance?.WeatherValue ?? 0f,
                v => VolkenWeather.Instance?.ForceWeatherValue(v),
                -1f, ceiling, false);
            manual.ValueFormatter = f => $"{f:F2} ({VolkenWeather.DescribeWeatherValue(f)})";
            parent.Add(manual);

            // ---- 天气节奏(同属"总体":状态机的时间常数) ----
            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherTiming"), () => "—"));
            AddSlider(parent, "Volken.UI.WeatherMinDuration",
                () => weather.Config?.overall?.minDuration ?? 480f,
                v => Set(c => c.overall.minDuration = v), 10f, 3600f, 0);
            AddSlider(parent, "Volken.UI.WeatherMaxDuration",
                () => weather.Config?.overall?.maxDuration ?? 960f,
                v => Set(c => c.overall.maxDuration = v), 10f, 7200f, 0);
            AddSlider(parent, "Volken.UI.WeatherFadeSpeed",
                () => weather.Config?.overall?.fadeSpeed ?? 0.001f,
                v => Set(c => c.overall.fadeSpeed = v), 0.0001f, 0.02f, 5);
            AddSlider(parent, "Volken.UI.WeatherTimeScaleFollow",
                () => weather.Config?.overall?.timeScaleFollow ?? 1f,
                v => Set(c => c.overall.timeScaleFollow = v), 0f, 10f, 2);

            parent.Add(new ToggleModel(Locale.GetString("Volken.UI.WeatherFoggyDawn"),
                () => weather.Config?.overall?.foggyDawn ?? false,
                v => Set(c => c.overall.foggyDawn = v)));
        }

        // ======================= 配置管理(布局与云层配置管理一致) =======================

        /// <summary>
        /// 天气的"配置管理"。**布局与云层配置管理一致,但两边完全独立**:
        /// 当前配置(只读) → 保存 → 另存为新配置 → 加载配置(下拉) → 重置为默认。
        ///
        /// 天气预设是**自己的一套名字**(<c>PlanetConfig.WeatherConfigName</c>,文件在
        /// <c>UserData/VolkenWeatherConfig/{行星}/{预设}.xml</c>),与云的预设名/预设列表
        /// **互不干扰** —— 这里换天气预设不会动云,反之亦然(就像两个云层各自的预设)。
        /// 下拉列表来自天气自己的目录(<see cref="VolkenWeather.AvailableConfigs"/>)。
        /// 没有"试试另一个配置":那是云层专有的(它会生成一套随机的云参数)。
        /// </summary>
        private static void BuildConfigManagementGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.ConfigManagement"));

            // 只读:当前天气预设名(独立于云层预设名)
            group.Add(new TextModel(Locale.GetString("Volken.UI.CurrentConfig"),
                () => string.IsNullOrEmpty(weather.CurrentConfigName) ? "—" : weather.CurrentConfigName));

            // 保存:把当前内存里的天气参数写回**本预设的天气文件**
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.SaveCurrentConfig"), _ =>
            {
                try
                {
                    weather.SaveCurrentConfig();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.WeatherConfigSaved"));
                }
                catch (Exception ex)
                {
                    Mod.Log("WeatherPanel: save weather config failed: " + ex);
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString("Volken.UI.ErrorSavingConfig"));
                }
            }));

            // 另存为新配置:问名字 → 写成新的天气预设 + 切过去(纯天气操作,不建任何云配置)
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.SaveAsNewConfig"), _ =>
            {
                try
                {
                    var dialog = Game.Instance.UserInterface.CreateInputDialog();
                    dialog.MessageText = Locale.GetString("Volken.UI.EnterNewConfigName");
                    dialog.InputText = Locale.GetString("Volken.UI.DefaultConfigName");
                    dialog.OkayClicked += (inputDialog) =>
                    {
                        try
                        {
                            string name = inputDialog.InputText;
                            if (string.IsNullOrWhiteSpace(name)) return;

                            if (!weather.SaveAsNewConfig(name)) return;

                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                string.Format(Locale.GetString("Volken.UI.ConfigSavedAs"),
                                    Locale.GetString("Volken.UI.Weather"), name));
                            // 预设列表多了一条 → 重建面板让下拉看到它
                            Core.VolkenUserInterface.Instance?.RebuildInspectorPanel();
                        }
                        catch (Exception ex)
                        {
                            Mod.Log("WeatherPanel: save-as failed: " + ex);
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                Locale.GetString("Volken.UI.ErrorSavingNewConfig"));
                        }
                        finally
                        {
                            // 输入对话框**不会自动关闭**,必须自己关(与云面板那两个对话框同一写法)
                            inputDialog?.Close();
                        }
                    };
                }
                catch (Exception ex)
                {
                    Mod.Log("WeatherPanel: create save dialog failed: " + ex);
                }
            }));

            // 加载配置(下拉):列表来自**天气自己的目录**,只换天气、不碰云
            group.Add(new DropdownModel(
                Locale.GetString("Volken.UI.LoadConfig"),
                () => string.IsNullOrEmpty(weather.CurrentConfigName) ? "Default" : weather.CurrentConfigName,
                newConfig =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(newConfig)) return;
                        if (newConfig == weather.CurrentConfigName) return;

                        weather.SwitchPreset(newConfig);

                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            string.Format(Locale.GetString("Volken.UI.ConfigLoaded"), newConfig));
                    }
                    catch (Exception ex)
                    {
                        Mod.Log("WeatherPanel: load weather preset failed: " + ex);
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                            Locale.GetString("Volken.UI.ErrorLoadingConfig"));
                    }
                },
                weather.AvailableConfigs));

            // 重置:只改内存(需再点"保存"才落盘),与云层"重置为默认"同语义
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.ResetCurrentToDefault"), _ =>
            {
                weather.ResetCurrentConfigToDefault();
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                    Locale.GetString("Volken.UI.WeatherResetToDefault"));
            }));

            parent.Add(group);
        }
        // ======================= 云层联动(占位) =======================

        /// <summary>
        /// 天气 → 云 的联动机制窗口。**整组禁用**:字段与分组结构保留(默认 0 = 恒等),
        /// 但当前实现里没有消费者(2026-09-27 决定"天气不联动云层",理由见 <see cref="VolkenWeather"/>)。
        /// </summary>
        private static void BuildCloudLinkageGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherCloudLinkage"));

            group.Add(new TextModel(Locale.GetString("Volken.UI.WeatherNotImplemented"), () => "—"));

            AddDisabledToggle(group, "Volken.UI.CloudLinkageEnabled",
                () => weather.Config?.cloudLinkage?.enabled ?? false);
            AddDisabledSlider(group, "Volken.UI.CloudLinkageCoverage", 0f, 1f, 2,
                () => weather.Config?.cloudLinkage?.coverageGain ?? 0f);
            AddDisabledSlider(group, "Volken.UI.CloudLinkageDarken", 0f, 1f, 2,
                () => weather.Config?.cloudLinkage?.darkenGain ?? 0f);
            AddDisabledSlider(group, "Volken.UI.CloudLinkageWind", 0f, 1f, 2,
                () => weather.Config?.cloudLinkage?.windGain ?? 0f);

            parent.Add(group);
        }

        // ======================= 雨(占位) =======================

        /// <summary>
        /// 雨的参数组 —— **整组禁用**。雨实现已于 2026-09-27 整体移除
        /// (见 docs/Volken-天气雨雾移植失败教训-2026-09-27.md),这些字段没有消费者。
        /// 保留分组是为了:① 结构完整;② 重做雨时参数位与默认值都还在。
        /// </summary>
        private static void BuildRainGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherRain"));

            group.Add(new TextModel(Locale.GetString("Volken.UI.WeatherRainRemoved"), () => "—"));

            AddDisabledToggle(group, "Volken.UI.RainEnabled",
                () => weather.Config?.rain?.enabled ?? false);
            AddDisabledSlider(group, "Volken.UI.RainTriggerValue", 0f, 10f, 2,
                () => weather.Config?.rain?.triggerValue ?? 2.25f);
            AddDisabledSlider(group, "Volken.UI.RainAmount", 0f, 200000f, 0,
                () => weather.Config?.rain?.amount ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainDomainRadius", 10f, 400f, 0,
                () => weather.Config?.rain?.domainRadius ?? 0f);
            AddDisabledToggle(group, "Volken.UI.RainAdaptiveDomain",
                () => weather.Config?.rain?.adaptiveDomain ?? false);
            AddDisabledSlider(group, "Volken.UI.RainFallSpeed", 1f, 60f, 1,
                () => weather.Config?.rain?.fallSpeed ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainStrength", 0f, 4f, 2,
                () => weather.Config?.rain?.strength ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainWindInfluence", 0f, 1f, 2,
                () => weather.Config?.rain?.windInfluence ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainStreakLength", 0.1f, 20f, 2,
                () => weather.Config?.rain?.streakLength ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainStreakWidth", 0.001f, 1f, 3,
                () => weather.Config?.rain?.streakWidth ?? 0f);
            AddDisabledSlider(group, "Volken.UI.RainVolume", 0f, 1f, 2,
                () => weather.Config?.rain?.volume ?? 0f);

            parent.Add(group);
        }

        // ======================= 雾(占位) =======================

        /// <summary>雾的参数组 —— **整组禁用**,理由同 <see cref="BuildRainGroup"/>。</summary>
        private static void BuildFogGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherFog"));

            group.Add(new TextModel(Locale.GetString("Volken.UI.WeatherFogRemoved"), () => "—"));

            AddDisabledToggle(group, "Volken.UI.FogEnabled",
                () => weather.Config?.fog?.enabled ?? false);
            AddDisabledSlider(group, "Volken.UI.FogBaseHeight", -500f, 20000f, 0,
                () => weather.Config?.fog?.baseHeight ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogHeight", 1f, 20000f, 0,
                () => weather.Config?.fog?.height ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogDensity", 0f, 0.1f, 5,
                () => weather.Config?.fog?.density ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogHeightFalloff", 0.001f, 5f, 3,
                () => weather.Config?.fog?.heightFalloff ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogMaxOpacity", 0f, 1f, 2,
                () => weather.Config?.fog?.maxOpacity ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogStartDistance", 0f, 20000f, 0,
                () => weather.Config?.fog?.startDistance ?? 0f);
            AddDisabledSlider(group, "Volken.UI.FogColorBlend", 0f, 1f, 2,
                () => weather.Config?.fog?.colorBlend ?? 0f);

            parent.Add(group);
        }

        // ======================= 雷(唯一可调) =======================

        private static void BuildLightningGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherLightning"));

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.LightningEnabled"),
                () => weather.Config?.lightning?.enabled ?? false, v =>
                {
                    if (weather.Config?.lightning == null) return;
                    weather.Config.lightning.enabled = v;
                    weather.RefreshActiveState();
                }));

            // 立即劈一道(唯一有副作用的按钮;方便不用等 6~45 秒的随机间隔)
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.LightningTriggerNow"), _ =>
            {
                VolkenWeather.Instance?.TriggerLightning();
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                    Locale.GetString("Volken.UI.LightningTriggered"));
            }));

            group.Add(new TextModel(Locale.GetString("Volken.UI.LightningStats"),
                () =>
                {
                    var lm = weather.Lightning;
                    if (lm == null) return Locale.GetString("Volken.UI.LightningInactive");
                    float storm = weather.Config?.lightning?.stormValue ?? 2.5f;
                    return $"bolts {lm.BoltCount}   next {lm.TimeToNextStrike:F1}s   " +
                           $"value {weather.WeatherValue:F2} (needs ≥ {storm:F2})";
                }));

            // 触发阈值:与"逐项设置"的取向一致,做成可调(原先写死 SP2 的 Stormy = 2.5)
            AddSlider(group, "Volken.UI.LightningStormValue",
                () => weather.Config?.lightning?.stormValue ?? 2.5f,
                v => Set(c => c.lightning.stormValue = v), 0f, 10f, 2);

            AddSlider(group, "Volken.UI.LightningMinDelay",
                () => weather.Config?.lightning?.minDelay ?? 6f,
                v => Set(c => c.lightning.minDelay = v), 0.5f, 60f, 1);
            AddSlider(group, "Volken.UI.LightningMaxDelay",
                () => weather.Config?.lightning?.maxDelay ?? 45f,
                v => Set(c => c.lightning.maxDelay = v), 1f, 300f, 1);
            AddSlider(group, "Volken.UI.LightningTargetRange",
                () => weather.Config?.lightning?.targetRange ?? 3000f,
                v => Set(c => c.lightning.targetRange = v), 10f, 30000f, 0);
            AddSlider(group, "Volken.UI.LightningSpawnRange",
                () => weather.Config?.lightning?.spawnRange ?? 4000f,
                v => Set(c => c.lightning.spawnRange = v), 10f, 30000f, 0);
            AddSlider(group, "Volken.UI.LightningArcs",
                () => weather.Config?.lightning?.arcs ?? 20,
                v => Set(c => c.lightning.arcs = Mathf.RoundToInt(v)), 4f, 64f, 0, true);
            AddSlider(group, "Volken.UI.LightningInaccuracy",
                () => weather.Config?.lightning?.inaccuracy ?? 0.5f,
                v => Set(c => c.lightning.inaccuracy = v), 0f, 1.5f, 2);
            AddSlider(group, "Volken.UI.LightningSplits",
                () => weather.Config?.lightning?.splits ?? 4,
                v => Set(c => c.lightning.splits = Mathf.RoundToInt(v)), 0f, 8f, 0, true);
            AddSlider(group, "Volken.UI.LightningWidth",
                () => weather.Config?.lightning?.width ?? 10f,
                v => Set(c => c.lightning.width = v), 0.5f, 100f, 1);
            AddSlider(group, "Volken.UI.LightningFlashIntensity",
                () => weather.Config?.lightning?.flashIntensity ?? 50f,
                v => Set(c => c.lightning.flashIntensity = v), 1f, 200f, 0);
            AddSlider(group, "Volken.UI.LightningLightIntensity",
                () => weather.Config?.lightning?.lightIntensity ?? 8f,
                v => Set(c => c.lightning.lightIntensity = v), 0f, 50f, 1);
            AddSlider(group, "Volken.UI.LightningLightRange",
                () => weather.Config?.lightning?.lightRange ?? 8000f,
                v => Set(c => c.lightning.lightRange = v), 100f, 50000f, 0);
            AddSlider(group, "Volken.UI.ThunderVolume",
                () => weather.Config?.lightning?.thunderVolume ?? 0.65f,
                v => Set(c => c.lightning.thunderVolume = v), 0f, 1f, 2);
            AddSlider(group, "Volken.UI.ThunderDelay",
                () => weather.Config?.lightning?.thunderDelay ?? 0.05f,
                v => Set(c => c.lightning.thunderDelay = v), 0f, 5f, 2);
            AddSlider(group, "Volken.UI.ThunderDistanceAttenuation",
                () => weather.Config?.lightning?.thunderDistanceAttenuation ?? 0.6f,
                v => Set(c => c.lightning.thunderDistanceAttenuation = v), 0f, 1f, 2);

            parent.Add(group);
        }

        // ======================= 小工具 =======================

        /// <summary>把 setter 收敛成"对当前配置实例的某个字段赋值",统一处理 null。</summary>
        private static void Set(Action<VolkenWeatherConfig> mutate)
        {
            var cfg = VolkenWeather.Instance?.Config;
            if (cfg == null) return;
            mutate(cfg);
        }

        private static void AddSlider(GroupModel group, string localeKey,
            Func<float> getter, Action<float> setter,
            float min, float max, int decimals, bool isInteger = false)
        {
            var model = new SliderModel(Locale.GetString(localeKey), getter, s => setter(s), min, max, isInteger);
            model.ValueFormatter = f => f.ToString("n" + Mathf.Max(0, decimals));
            group.Add(model);
        }

        /// <summary>禁用滑块(雨/雾/云层联动的占位组用)。</summary>
        private static void AddDisabledSlider(GroupModel group, string localeKey,
            float min, float max, int decimals, Func<float> getter)
        {
            var model = new SliderModel(Locale.GetString(localeKey), getter, _ => { }, min, max, false)
            {
                Enabled = false,
            };
            model.ValueFormatter = f => f.ToString("n" + Mathf.Max(0, decimals));
            group.Add(model);
        }

        /// <summary>禁用开关(占位组用)。</summary>
        private static void AddDisabledToggle(GroupModel group, string localeKey, Func<bool> getter)
        {
            group.Add(new ToggleModel(Locale.GetString(localeKey), getter, _ => { })
            {
                Enabled = false,
            });
        }
    }
}
