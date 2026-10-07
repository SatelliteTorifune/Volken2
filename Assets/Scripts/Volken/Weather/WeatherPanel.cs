using System;
using Assets.Scripts;
using ModApi;
using ModApi.Ui.Inspector;
using UnityEngine;

namespace Volken.Weather
{
    
    /// <summary>天气面板,追加到 Volken 检查器里(不另开浮动窗口,否则两个面板会互相遮挡);分组 = 状态(只读)/ 总体(总开关、天气值、节奏)/ 配置管理(天气**自己的**预设,与云互不干扰)/ 雨 / 雾 / 雷,
    /// 与 <see cref="VolkenWeatherConfig"/> 的 XML 节点一一对应。</summary>
    /// <remarks>面板直接改**清单里那条记录上的参数实例本身**(不是副本),点"保存当前配置"才落盘;滑块下一帧生效(<see cref="VolkenWeather.Tick"/> 每帧读 Config)。</remarks>
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
            BuildRainGroup(root, weather);
            BuildFogGroup(root, weather);
            BuildLightningGroup(root, weather);

            inspector.AddGroup(root);
        }

        // 状态(只读)

        private static void BuildStatusGroup(GroupModel parent, VolkenWeather weather)
        {
            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherPlanet"),
                () => string.IsNullOrEmpty(weather.CurrentPlanet) ? "—" : weather.CurrentPlanet));

            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherActive"),
                () => weather.IsActive
                    ? Locale.GetString("Volken.UI.WeatherActiveOn")
                    : Locale.GetString("Volken.UI.WeatherActiveOff")));

            parent.Add(new TextModel(Locale.GetString("Volken.UI.WeatherAltitude"),
                () => $"{weather.CameraAltitudeAsl:F0} m ASL / {weather.CameraAltitudeAgl:F0} m AGL" +
                      $"   solar {weather.LocalSolarHour:F1}h   cloudFade {weather.CameraCloudFade:F2}"));
        }

        // 总体(并入"天气"根组)

        private static void BuildOverallGroup(GroupModel parent, VolkenWeather weather)
        {
            parent.Add(new ToggleModel(Locale.GetString("Volken.UI.WeatherEnabled"),
                () => weather.Config?.overall?.enabled ?? false, v =>
                {
                    if (weather.Config?.overall == null) return;
                    weather.Config.overall.enabled = v;
                    weather.RefreshActiveState();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString(v ? "Volken.UI.WeatherEnabledOn" : "Volken.UI.WeatherEnabledOff"));
                }));
        }

        // 配置管理(布局与云层配置管理一致)

        /// <summary>天气的"配置管理":当前配置(只读) → 保存 → 另存为新配置 → 加载配置(下拉) → 重置;用的是天气**自己**的预设名与目录,与云的预设列表互不干扰。</summary>
        private static void BuildConfigManagementGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.ConfigManagement"));

            group.Add(new TextModel(Locale.GetString("Volken.UI.CurrentConfig"),
                () => string.IsNullOrEmpty(weather.CurrentConfigName) ? "—" : weather.CurrentConfigName));

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

            // 另存为新配置:问名字 → 写新的天气预设并切过去(纯天气操作,不建任何云配置)
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

                        // 预设文件不存在时 SwitchPreset 会拒绝(否则会就地新建一份 Default 并改掉行星绑定)
                        if (!weather.SwitchPreset(newConfig))
                        {
                            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                                Locale.GetString("Volken.UI.ErrorLoadingConfig"));
                            return;
                        }

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

            // 重置:只改内存(需再点"保存"才落盘)
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.ResetCurrentToDefault"), _ =>
            {
                weather.ResetCurrentConfigToDefault();
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                    Locale.GetString("Volken.UI.WeatherResetToDefault"));
            }));

            parent.Add(group);
        }
        // 雨(实时控制)

        /// <summary>雨参数组:改动立刻推给 <see cref="RainParticles.ApplyConfig"/>(容量/雨丝长宽会触发重建);持续型参数由 RainParticles 每帧读静态字段。</summary>
        private static void BuildRainGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherRain"));

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainEnabled"),
                () => weather.Config?.rain?.enabled ?? false, v =>
                {
                    Set(c => c.rain.enabled = v);
                    RainParticles.ApplyConfig(VolkenWeather.Instance?.Config?.rain);
                    RainParticles.SetEnabled(v ? 1 : 0);   // 内含 AttachToCurrentView
                }));

            group.Add(new TextModel(Locale.GetString("Volken.UI.RainStats"),
                () => RainParticles.StatsLine()));

            // 雨声状态(素材是否进包 / 淡变包络 / 小雨↔暴雨混音)
            group.Add(new TextModel(Locale.GetString("Volken.UI.RainAudioStats"),
                () => RainAudio.StatsLine()));

            // 立即开关(调试用:不用等天气值到阈值)
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.RainToggleNow"), _ =>
            {
                bool now = !(VolkenWeather.Instance?.Config?.rain?.enabled ?? false);
                Set(c => c.rain.enabled = now);
                RainParticles.ApplyConfig(VolkenWeather.Instance?.Config?.rain);
                RainParticles.SetEnabled(now ? 1 : 0);
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                    Locale.GetString(now ? "Volken.UI.RainOnMsg" : "Volken.UI.RainOffMsg"));
            }));

            // 密度(粒子数):SP2 出厂 100000@R50 = 0.191/m³,EVE 参考 0.139/m³
            AddSlider(group, "Volken.UI.RainAmount",
                () => weather.Config?.rain?.amount ?? 20000f,
                v => ApplyRain(c => c.rain.amount = v), 1000f, 200000f, 0, true);

            // 雨声:强度(与粒子数一起决定选哪组音效)+ 雨声音量
            AddSlider(group, "Volken.UI.RainStrength",
                () => weather.Config?.rain?.strength ?? 1f,
                v => ApplyRain(c => c.rain.strength = v), 0f, 4f, 2);
            AddSlider(group, "Volken.UI.RainVolume",
                () => weather.Config?.rain?.volume ?? 0.5f,
                v => ApplyRain(c => c.rain.volume = v), 0f, 1f, 2);

            AddSlider(group, "Volken.UI.RainDomainRadius",
                () => weather.Config?.rain?.domainRadius ?? 50f,
                v => ApplyRain(c => c.rain.domainRadius = v), 10f, 400f, 0);

            AddSlider(group, "Volken.UI.RainFallSpeed",
                () => weather.Config?.rain?.fallSpeed ?? 15f,
                v => ApplyRain(c => c.rain.fallSpeed = v), 0.5f, 60f, 1);

            AddSlider(group, "Volken.UI.RainStreakLength",
                () => weather.Config?.rain?.streakLength ?? 2.5f,
                v => ApplyRain(c => c.rain.streakLength = v), 0.1f, 20f, 2);

            AddSlider(group, "Volken.UI.RainStreakWidth",
                () => weather.Config?.rain?.streakWidth ?? 0.1f,
                v => ApplyRain(c => c.rain.streakWidth = v), 0.005f, 0.6f, 3);

            AddSlider(group, "Volken.UI.RainStretch",
                () => weather.Config?.rain?.stretchAmount ?? 0.045f,
                v => ApplyRain(c => c.rain.stretchAmount = v), 0f, 0.3f, 3);

            // 拉伸上限:雨丝最长 = 长度 × 本值(治"太长像面条")
            AddSlider(group, "Volken.UI.RainStretchLimit",
                () => weather.Config?.rain?.stretchLimit ?? 3.5f,
                v => ApplyRain(c => c.rain.stretchLimit = v), 1f, 12f, 2);

            AddSlider(group, "Volken.UI.RainEdgeFade",
                () => weather.Config?.rain?.edgeFade ?? 0.2f,
                v => ApplyRain(c => c.rain.edgeFade = v), 0f, 0.5f, 2);

            AddSlider(group, "Volken.UI.RainSoftParticles",
                () => weather.Config?.rain?.softParticles ?? 1f,
                v => ApplyRain(c => c.rain.softParticles = v), 0f, 3f, 2);

            AddSlider(group, "Volken.UI.RainTailFalloff",
                () => weather.Config?.rain?.tailFalloff ?? 0.9f,
                v => ApplyRain(c => c.rain.tailFalloff = v), 0f, 1f, 2);

            AddSlider(group, "Volken.UI.RainBrightness",
                () => weather.Config?.rain?.brightness ?? 0f,
                v => ApplyRain(c => c.rain.brightness = v), 0f, 3f, 2);

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainStreamMode"),
                () => weather.Config?.rain?.streamMode ?? true,
                v => ApplyRain(c => c.rain.streamMode = v)));

            // 出域处置(SP2 风格 = 镜像重生:从边界进入,近旁不爆闪)
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainRespawnMirror"),
                () => weather.Config?.rain?.respawnMirror ?? true,
                v => ApplyRain(c => c.rain.respawnMirror = v)));

            // 海拔闸门(必需:JNO 镜头能缩到整颗星球,不限制会在太空下雨)
            AddSlider(group, "Volken.UI.RainCeilingAltitude",
                () => weather.Config?.rain?.ceilingAltitude ?? 0f,
                v => ApplyRain(c => c.rain.ceilingAltitude = v), 0f, 200000f, 0, true);
            AddSlider(group, "Volken.UI.RainCeilingBand",
                () => weather.Config?.rain?.ceilingBand ?? 0.4f,
                v => ApplyRain(c => c.rain.ceilingBand = v), 0.05f, 1f, 2);

            // 水下闸门(高度闸门只管上半边:潜进水里时雨会照样从头顶落下来)
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainUnderwaterGate"),
                () => weather.Config?.rain?.underwaterGate ?? true,
                v => ApplyRain(c => c.rain.underwaterGate = v)));
            AddSlider(group, "Volken.UI.RainUnderwaterFade",
                () => weather.Config?.rain?.underwaterFade ?? 2f,
                v => ApplyRain(c => c.rain.underwaterFade = v), 0.1f, 20f, 1);

            // 纵深线索:整片雨丝等长/等亮/平行是"像一层平面"的主因
            AddSlider(group, "Volken.UI.RainDistanceFade",
                () => weather.Config?.rain?.distanceFade ?? 0.35f,
                v => ApplyRain(c => c.rain.distanceFade = v), 0f, 1f, 2);
            AddSlider(group, "Volken.UI.RainStreakVariation",
                () => weather.Config?.rain?.streakVariation ?? 0.5f,
                v => ApplyRain(c => c.rain.streakVariation = v), 0f, 1f, 2);

            // 域半径随相机速度自适应(快相机时固定半径会被整片回收 → 没有视差)
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainAdaptiveDomain"),
                () => weather.Config?.rain?.adaptiveDomain ?? true,
                v => ApplyRain(c => c.rain.adaptiveDomain = v)));

            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.RainDumpLog"), _ =>
            {
                RainParticles.DiagStatus();   // 完整状态写进 Player.log(方便发日志排查)
                RainAudio.DiagStatus();       // 雨声状态
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.RainDumped"));
            }));

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainRowTest"),
                () => RainParticles.TestRow, v =>
                {
                    RainParticles.TestRow = v;
                    Mod.Diag("RainParticles: testRow = {0} (阶段2.3 等距排自检:相机前 30m 一排 3m 间距,应见 6~7 根竖条)", v);
                }));

            parent.Add(group);
        }

        private static void ApplyRain(Action<VolkenWeatherConfig> mutate)
        {
            var cfg = VolkenWeather.Instance?.Config;
            if (cfg == null || cfg.rain == null) return;
            mutate(cfg);
            RainParticles.ApplyConfig(cfg.rain);
        }

        // 雾(占位)

        /// <summary>雾的参数组 —— **整组禁用**(雾未落地)。</summary>
        private static void BuildFogGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherFog"));

            group.Add(new TextModel(Locale.GetString("Volken.UI.WeatherFogRemoved"), () => "—"));

            AddDisabledToggle(group, "Volken.UI.FogEnabled",
                () => weather.Config?.fog?.enabled ?? false);
            AddDisabledToggle(group, "Volken.UI.FogDawnFog",
                () => weather.Config?.fog?.dawnFog ?? false);
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

        // 雷(唯一可调)

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

            // 立即劈一道(唯一有副作用的按钮;省得等 6~45 秒的随机间隔)
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.LightningTriggerNow"), _ =>
            {
                bool struck = VolkenWeather.Instance?.TriggerLightning() ?? false;
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString(
                    struck ? "Volken.UI.LightningTriggered" : "Volken.UI.LightningTriggerBlocked"));
            }));

            group.Add(new TextModel(Locale.GetString("Volken.UI.LightningStats"),
                () =>
                {
                    var lm = weather.Lightning;
                    if (lm == null) return Locale.GetString("Volken.UI.LightningInactive");
                    return $"bolts {lm.BoltCount}   next {lm.TimeToNextStrike:F1}s";
                }));

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

            // 海拔闸门(必需:JNO 镜头能缩到整颗星球,不限制会在太空里劈雷)
            AddSlider(group, "Volken.UI.LightningCeilingAltitude",
                () => weather.Config?.lightning?.ceilingAltitude ?? 12000f,
                v => Set(c => c.lightning.ceilingAltitude = v), 0f, 200000f, 0, true);
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
                () => weather.Config?.lightning?.thunderDistanceAttenuation ?? 1f,
                v => Set(c => c.lightning.thunderDistanceAttenuation = v), 0f, 1f, 2);

            // 雷声:距离 → 声速延迟 → near/far 阈值
            // 阈值决定"听哪一组",声速决定"延迟多久",混合比例决定远雷要不要按云底那段缩短。
            AddSlider(group, "Volken.UI.ThunderNearDistance",
                () => weather.Config?.lightning?.thunderNearDistance ?? 2000f,
                v => Set(c => c.lightning.thunderNearDistance = v), 100f, 20000f, 0);
            AddSlider(group, "Volken.UI.ThunderFallbackSpeedOfSound",
                () => weather.Config?.lightning?.thunderFallbackSpeedOfSound ?? 343f,
                v => Set(c => c.lightning.thunderFallbackSpeedOfSound = v), 50f, 1500f, 0);
            AddSlider(group, "Volken.UI.ThunderSourceBlend",
                () => weather.Config?.lightning?.thunderSourceBlend ?? 0.5f,
                v => Set(c => c.lightning.thunderSourceBlend = v), 0f, 1f, 2);

            parent.Add(group);
        }

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

        private static void AddDisabledToggle(GroupModel group, string localeKey, Func<bool> getter)
        {
            group.Add(new ToggleModel(Locale.GetString(localeKey), getter, _ => { })
            {
                Enabled = false,
            });
        }
    }
}
