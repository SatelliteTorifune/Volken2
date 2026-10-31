using System;
using Assets.Scripts;
using ModApi;
using ModApi.Ui.Inspector;
using UnityEngine;

namespace Volken.Weather
{
    
    /// <summary>天气根组提供预设管理、地区分布和各效果参数。</summary>
    /// <remarks>面板修改当前天气配置实例;保存才落盘,效果参数由各子系统独立消费。</remarks>
    public static class WeatherUserInterface
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

            BuildConfigControls(root, weather);
            BuildRegionsGroup(root, weather);
            BuildRainGroup(root, weather);
            BuildFogGroup(root, weather);
            BuildLightningGroup(root, weather);

            inspector.AddGroup(root);
        }

        /// <summary>把当前配置、保存、另存为、加载与重置直接加入天气根组。</summary>
        private static void BuildConfigControls(GroupModel group, VolkenWeather weather)
        {
            group.Add(new TextModel(Locale.GetString("Volken.UI.CurrentConfig"),
                () => string.IsNullOrEmpty(weather.CurrentConfigName) ? "—" : weather.CurrentConfigName));

            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.SaveCurrentConfig"), _ =>
            {
                try
                {
                    bool saved = weather.SaveCurrentConfig();
                    Game.Instance.FlightScene.FlightSceneUI.ShowMessage(
                        Locale.GetString(saved ? "Volken.UI.WeatherConfigSaved" : "Volken.UI.ErrorSavingConfig"));
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

                            if (!weather.SaveAsNewConfig(name))
                            {
                                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.ErrorSavingNewConfig"));
                                return;
                            }

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


        }
        private static void BuildRegionsGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherRegions"));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RegionsEnabled"),
                () => weather.Config?.Regions?.enabled ?? false,
                v => ApplyRegions(r => r.enabled = v)));
            var distributions = new System.Collections.Generic.List<string>
            {
                Locale.GetString("Volken.UI.RegionsProcedural"),
                Locale.GetString("Volken.UI.RegionsTexture")
            };
            group.Add(new DropdownModel(Locale.GetString("Volken.UI.RegionsDistribution"),
                () => distributions[Mathf.Clamp(weather.Config?.Regions?.distribution ?? 0, 0, 1)],
                value => { int index = distributions.IndexOf(value); if (index >= 0) ApplyRegions(r => r.distribution = index); },
                distributions));
            AddSlider(group, "Volken.UI.RegionsMapLongitude", () => weather.Config?.Regions?.mapLongitudeOffset ?? 0f,
                v => ApplyRegions(r => r.mapLongitudeOffset = v), -180f, 180f, 1);
            AddSlider(group, "Volken.UI.RegionsSeed", () => weather.Config?.Regions?.seed ?? 1337,
                v => ApplyRegions(r => r.seed = Mathf.RoundToInt(v)), 0f, 999999f, 0, true);
            AddSlider(group, "Volken.UI.RegionsCoverage", () => weather.Config?.Regions?.coverage ?? 0.5f,
                v => ApplyRegions(r => r.coverage = v), 0f, 1f, 2);
            AddSlider(group, "Volken.UI.RegionsCellSize", () => weather.Config?.Regions?.cellSizeKm ?? 150f,
                v => ApplyRegions(r => r.cellSizeKm = v), 1f, 2000f, 0);
            AddSlider(group, "Volken.UI.RegionsEdgeSoftness", () => weather.Config?.Regions?.edgeSoftness ?? 0.2f,
                v => ApplyRegions(r => r.edgeSoftness = v), 0.01f, 0.5f, 2);
            AddSlider(group, "Volken.UI.RegionsWindSpeed", () => weather.Config?.Regions?.windSpeed ?? 20f,
                v => ApplyRegions(r => r.windSpeed = v), -200f, 200f, 1);
            AddSlider(group, "Volken.UI.RegionsStormFraction", () => weather.Config?.Regions?.stormFraction ?? 0.3f,
                v => ApplyRegions(r => r.stormFraction = v), 0f, 1f, 2);
            parent.Add(group);
        }

        private static void ApplyRegions(Action<VolkenWeatherConfig.RegionalSection> mutate)
        {
            var config = VolkenWeather.Instance?.Config;
            if (config == null) return;
            config.EnsureSections();
            mutate(config.Regions);
            config.Regions.Clamp();
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

            AddSlider(group, "Volken.UI.RainTransparency",
                () => weather.Config?.rain?.transparency ?? 0f,
                v => ApplyRain(c => c.rain.transparency = v), 0f, 1f, 2);

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainStreamMode"),
                () => weather.Config?.rain?.streamMode ?? true,
                v => ApplyRain(c => c.rain.streamMode = v)));

            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainCollision"),
                () => weather.Config?.rain?.collisionEnabled ?? false,
                v => ApplyRain(c => { c.rain.collisionEnabled = v; if (!v) c.rain.splashesEnabled = false; })));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainCollisionHighPrecision"),
                () => (weather.Config?.rain?.collisionResolution ?? 256) == 512,
                v => ApplyRain(c => c.rain.collisionResolution = v ? 512 : 256)));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainSplashes"),
                () => weather.Config?.rain?.splashesEnabled ?? false,
                v => ApplyRain(c => { c.rain.splashesEnabled = v; if (v) c.rain.collisionEnabled = true; })));
            AddSlider(group, "Volken.UI.RainSplashDistance",
                () => weather.Config?.rain?.splashDistance ?? 25f,
                v => ApplyRain(c => c.rain.splashDistance = v), 1f, 50f, 0);
            AddSlider(group, "Volken.UI.RainSplashLifetime",
                () => weather.Config?.rain?.splashLifetime ?? 0.35f,
                v => ApplyRain(c => c.rain.splashLifetime = v), 0.1f, 1f, 2);
            AddSlider(group, "Volken.UI.RainSplashSize",
                () => weather.Config?.rain?.splashSize ?? 0.18f,
                v => ApplyRain(c => c.rain.splashSize = v), 0.03f, 0.5f, 2);
            AddSlider(group, "Volken.UI.RainSplashDensity",
                () => weather.Config?.rain?.splashDensity ?? 0.35f,
                v => ApplyRain(c => c.rain.splashDensity = v), 0f, 1f, 2);
            

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

            BuildRainLensGroup(group, weather);
            parent.Add(group);
        }

        private static void BuildRainLensGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.RainLens"));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainLensEnabled"),
                () => weather.Config?.rainLens?.enabled ?? false, v =>
                {
                    Set(c => c.rainLens.enabled = v);
                    RainLensEffect.SyncToCurrentView();
                }));
            AddSlider(group, "Volken.UI.RainLensIntensity", () => weather.Config?.rainLens?.intensity ?? 1f,
                v => Set(c => c.rainLens.intensity = v), 0f, 3f, 2);
            AddSlider(group, "Volken.UI.RainLensSpawnRate", () => weather.Config?.rainLens?.spawnRate ?? 18f,
                v => Set(c => c.rainLens.spawnRate = v), 0f, 40f, 0);
            AddSlider(group, "Volken.UI.RainLensDropSize", () => (weather.Config?.rainLens?.dropSize ?? 0.018f) * 100f,
                v => Set(c => c.rainLens.dropSize = v / 100f), 0.3f, 3.5f, 2);
            AddSlider(group, "Volken.UI.RainLensLifetime", () => weather.Config?.rainLens?.lifetime ?? 6f,
                v => Set(c => c.rainLens.lifetime = v), 0.5f, 20f, 1);
            AddSlider(group, "Volken.UI.RainLensRefraction", () => weather.Config?.rainLens?.refraction ?? 14f,
                v => Set(c => c.rainLens.refraction = v), 0f, 30f, 1);
            AddSlider(group, "Volken.UI.RainLensBlur", () => weather.Config?.rainLens?.blur ?? 1.8f,
                v => Set(c => c.rainLens.blur = v), 0f, 4f, 1);
            AddSlider(group, "Volken.UI.RainLensSpeedThreshold", () => weather.Config?.rainLens?.speedThreshold ?? 40f,
                v => Set(c => c.rainLens.speedThreshold = v), 1f, 500f, 0);
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.RainLensShelterCheck"),
                () => weather.Config?.rainLens?.shelterCheck ?? true,
                v => Set(c => c.rainLens.shelterCheck = v)));
            parent.Add(group);
        }

        private static void ApplyRain(Action<VolkenWeatherConfig> mutate)
        {
            var cfg = VolkenWeather.Instance?.Config;
            if (cfg == null || cfg.rain == null) return;
            mutate(cfg);
            RainParticles.ApplyConfig(cfg.rain);
        }

        // 雾:薄雾与体积密度可以独立启用,参数沿用天气预设的保存路径。
        private static void BuildFogGroup(GroupModel parent, VolkenWeather weather)
        {
            var group = new GroupModel(Locale.GetString("Volken.UI.WeatherFog"));
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.FogPresetThin"), _ => FogPreset(weather, true, false)));
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.FogPresetVolume"), _ => FogPreset(weather, false, true)));
            group.Add(new TextButtonModel(Locale.GetString("Volken.UI.FogPresetBoth"), _ => FogPreset(weather, true, true)));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.FogEnabled"),
                () => weather.Config?.fog?.enabled ?? false, v => ApplyFog(f => f.enabled = v)));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.FogHeightEnabled"),
                () => weather.Config?.fog?.heightEnabled ?? false, v => ApplyFog(f => f.heightEnabled = v)));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.FogVolumeEnabled"),
                () => weather.Config?.fog?.volumetricEnabled ?? false, v => ApplyFog(f => f.volumetricEnabled = v)));
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.FogDawnFog"),
                () => weather.Config?.fog?.dawnFog ?? false, v => ApplyFog(f => f.dawnFog = v)));
            AddFogSlider(group, weather, "FogDensity", f => f.density, (f,v) => f.density=v, 0, 0.01f, 5);
            AddFogSlider(group, weather, "FogVolumeDensity", f => f.volumeDensity, (f,v) => f.volumeDensity=v, 0, 0.02f, 5);
            AddFogSlider(group, weather, "FogBaseHeight", f => f.baseHeight, (f,v) => f.baseHeight=v, -500, 20000, 0);
            AddFogSlider(group, weather, "FogHeight", f => f.height, (f,v) => f.height=v, 1, 10000, 0);
            AddFogSlider(group, weather, "FogHeightFalloff", f => f.heightFalloff, (f,v) => f.heightFalloff=v, 0.01f, 5, 2);
            AddFogSlider(group, weather, "FogMaxOpacity", f => f.maxOpacity, (f,v) => f.maxOpacity=v, 0, 1, 2);
            AddFogSlider(group, weather, "FogStartDistance", f => f.startDistance, (f,v) => f.startDistance=v, 0, 20000, 0);
            AddFogSlider(group, weather, "FogMaxDistance", f => f.maxDistance, (f,v) => f.maxDistance=v, 100, 100000, 0);
            var quality = new SliderModel(Locale.GetString("Volken.UI.FogQuality"),
                () => weather.Config?.fog?.quality ?? 0, v => ApplyFog(f => f.quality = (int)v), 0, 2, true);
            quality.ValueFormatter = value => Locale.GetString(value < 1 ? "Volken.UI.QualityLow"
                : value < 2 ? "Volken.UI.QualityMedium" : "Volken.UI.QualityHigh");
            group.Add(quality);
            AddFogSlider(group, weather, "FogNoiseScale", f => f.noiseScale, (f,v) => f.noiseScale=v, 20, 5000, 0);
            AddFogSlider(group, weather, "FogCoverage", f => f.coverage, (f,v) => f.coverage=v, 0.01f, 1, 2);
            AddFogSlider(group, weather, "FogContrast", f => f.contrast, (f,v) => f.contrast=v, 0.2f, 5, 2);
            AddFogSlider(group, weather, "FogWindSpeed", f => f.windSpeed, (f,v) => f.windSpeed=v, 0, 100, 1);
            AddFogSlider(group, weather, "FogWindDirection", f => f.windDirection, (f,v) => f.windDirection=v, 0, 360, 0);
            AddFogSlider(group, weather, "FogAnisotropy", f => f.anisotropy, (f,v) => f.anisotropy=v, -0.8f, 0.8f, 2);
            AddFogSlider(group, weather, "FogColorBlend", f => f.colorBlend, (f,v) => f.colorBlend=v, 0, 1, 2);
            AddFogSlider(group, weather, "FogColorR", f => f.colorR, (f,v) => f.colorR=v, 0, 1, 2);
            AddFogSlider(group, weather, "FogColorG", f => f.colorG, (f,v) => f.colorG=v, 0, 1, 2);
            AddFogSlider(group, weather, "FogColorB", f => f.colorB, (f,v) => f.colorB=v, 0, 1, 2);
            parent.Add(group);
        }

        private static void ApplyFog(Action<VolkenWeatherConfig.FogSection> mutate)
        {
            var weather = VolkenWeather.Instance;
            if (weather?.Config?.fog == null) return;
            mutate(weather.Config.fog);
            weather.Config.ClampAll();
            // FogRenderer consumes this configuration on the next render; rain/thunder state is independent.
        }

        private static void AddFogSlider(GroupModel group, VolkenWeather weather, string key,
            Func<VolkenWeatherConfig.FogSection,float> getter, Action<VolkenWeatherConfig.FogSection,float> setter,
            float min, float max, int decimals, bool integer = false)
        {
            AddSlider(group, "Volken.UI."+key, () => weather.Config?.fog != null ? getter(weather.Config.fog) : 0,
                v => ApplyFog(f => setter(f,v)), min, max, decimals, integer);
        }

        private static void FogPreset(VolkenWeather weather, bool thin, bool volume)
        {
            if (weather.Config?.fog == null) return;
            ApplyFog(f =>
            {
                f.enabled=true; f.heightEnabled=thin; f.volumetricEnabled=volume;
                f.baseHeight=0; f.height=250; f.heightFalloff=1; f.density=0.0003f; f.volumeDensity=0.0015f;
                f.startDistance=0; f.maxDistance=30000; f.maxOpacity=0.95f; f.quality=1;
                f.noiseScale=800; f.coverage=0.65f; f.contrast=1.5f; f.windSpeed=5; f.windDirection=45;
                f.colorR=0.72f; f.colorG=0.78f; f.colorB=0.85f; f.colorBlend=0.5f;
                f.anisotropy=0.35f; f.dawnFog=false; f.debugMode=0;
            });
            Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Volken.UI.FogPresetApplied"));
        }

        // 雷

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
                () => weather.Config?.lightning?.lightIntensity ?? 1f,
                v => Set(c => c.lightning.lightIntensity = v), 0f, 50f, 1);
            AddSlider(group, "Volken.UI.LightningLightRange",
                () => weather.Config?.lightning?.lightRange ?? 8000f,
                v => Set(c => c.lightning.lightRange = v), 100f, 50000f, 0);
            group.Add(new ToggleModel(Locale.GetString("Volken.UI.LightningCloudLightingEnabled"),
                () => weather.Config?.lightning?.cloudLightingEnabled ?? false,
                v => Set(c => c.lightning.cloudLightingEnabled = v)));
            AddSlider(group, "Volken.UI.LightningCloudLightIntensity",
                () => weather.Config?.lightning?.cloudLightIntensity ?? 8f,
                v => Set(c => c.lightning.cloudLightIntensity = v), 0f, 100f, 1);
            AddSlider(group, "Volken.UI.LightningCloudLightRange",
                () => weather.Config?.lightning?.cloudLightRange ?? 3000f,
                v => Set(c => c.lightning.cloudLightRange = v), 100f, 30000f, 0);
            AddSlider(group, "Volken.UI.LightningCloudLightSamples",
                () => weather.Config?.lightning?.cloudLightSamples ?? 8,
                v => Set(c => c.lightning.cloudLightSamples = Mathf.RoundToInt(v)), 2f, 16f, 0, true);
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

    }
}
