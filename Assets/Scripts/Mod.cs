using System.IO;
using Assets.Packages.DevConsole;
using Assets.Scripts.Flight.UI;
using HarmonyLib;
using ModApi.Scenes.Events;

namespace Assets.Scripts
{
    using Volken.Core;
    using Volken.Clouds;
    using Volken.Weather;
    using Volken.Water;
    using System;
    using UnityEngine;

    /// <summary>
    /// A singleton object representing this mod that is instantiated and initialize when the mod is loaded.
    /// </summary>
    public partial class Mod : ModApi.Mods.GameMod
    {
        /// <summary>
        /// Prevents a default instance of the <see cref="Mod"/> class from being created.
        /// </summary>
        private Mod() : base()
        {
        }

        public int frontRenderQueue = 3000;
        public int backRenderQueue = 3000;
        
        //f 2000 2500 2501
        //b 2500-2501
        
        /// <summary>
        /// Gets the singleton instance of the mod object.
        /// </summary>
        /// <value>The singleton instance of the mod object.</value>
        public static Mod Instance { get; } = GetModInstance<Mod>();

        private GameObject VolkenUI;
        public GameObject ForceSettingScriptLoadGameObject;
        
        /// <summary>
        /// Gets the mod version as reported by the mod manifest (ModInfo.Version), e.g. 0.6.
        /// </summary>
        public Version ModVersion { get; private set; }
        
        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();
            //PlanetRingsZWriteFix.Apply(harmony);
            //PlanetRingsShaderPatch.Apply(harmony);
            VolkenUI=new GameObject("VolkenUI");
            VolkenUI.AddComponent<VolkenUserInterface>();
            GameObject.DontDestroyOnLoad(VolkenUI);
            VolkenUI.SetActive(true);
            ForceSettingScriptLoadGameObject=new GameObject("ForceSettingObject");
            ForceSettingScriptLoadGameObject.AddComponent<ForceSetting>();
            GameObject.DontDestroyOnLoad(ForceSettingScriptLoadGameObject);
            ForceSettingScriptLoadGameObject.SetActive(false);
            Volken.Core.VolkenMod.Initialize();
            VolkenWeather.Initialize();
            // 注:性能剖析器(VolkenProfiler)不再由正式代码启动 —— 见 VolkenTests/TestsBootstrap.cs 自发注册
            RegisterCommands();

            Game.Instance.Settings.Game.Flight.GroundClouds.Value = true;

            // 本地版本 = ModInfo.Version(System.Version,如 0.6)
            this.ModVersion = this.ModInfo.Version;

            // 更新检查(本地模式:LatestVersionUrl 未配置时仅打日志,不弹窗)
            new ModUpdater().CheckForUpdate();
        }
        
        private void RegisterCommands()
        {
            DevConsoleApi.RegisterCommand<int>("frs",i=>this.frontRenderQueue=i);
            DevConsoleApi.RegisterCommand<int>("brs",i=>this.backRenderQueue=i);
            DevConsoleApi.RegisterCommand("VolkenForceRefresh",ForceRefresh);

            // === 天气(阶段 1:闪电先行,让"劈一道雷"能一键验证) ===
            DevConsoleApi.RegisterCommand("volkenWeather",WeatherStatus);
            DevConsoleApi.RegisterCommand<float>("volkenSetWeather",SetWeatherValue);
            DevConsoleApi.RegisterCommand("volkenBolt",TriggerLightning);
            DevConsoleApi.RegisterCommand("volkenAssets",LogVolkenAssets);

            // === 测试/开发工具的命令注册已移出正式代码 ===
            //   `volkenRainAxis*`(Phase 1 构轴探针)与 `VolkenProfiler*`(性能剖析)现在由
            //   `Assets/Scripts/VolkenTests/TestsBootstrap.cs` **自发注册** —— 正式代码对测试文件夹
            //   **零引用**,删掉整个 VolkenTests 文件夹 mod 仍能编译运行(见该文件夹 README.md)。

            // === 雨(阶段 3):**没有控制台指令** —— 全部旋钮已集成到天气面板「雨」分组 ===
            //   面板:启用雨 / 立即切换雨 / 实时状态行 / 把状态写入日志 / 粒子数量 / 域半径 / 下落速度 /
            //   雨丝长宽 / 拉伸 / 域边界淡出 / 软粒子 / 尾淡 / 亮度 / 朝向模式 / 出域镜像重生 / 海拔上限与带宽 /
            //   触发阈值 / 开发:等距排自检。
            //   (2026-09-29 用户要求:删除 volkenRainP2* 系列指令,避免"调试靠敲控制台"。)
        }

        /// <summary>打印天气系统当前状态(行星/配置/天气值/雷电)。
        /// 注:雨/雾已在 2026-09-27 按用户要求整体移除,故不再有 rain/fog 行。</summary>
        private void WeatherStatus()
        {
            var w = VolkenWeather.Instance;
            if (w == null) { Diag("WeatherStatus: VolkenWeather 未初始化"); return; }
            var c = w.Config;
            Diag("WeatherStatus: planet={0} active={1} value={2:F3}({3}) target={4:F3} " +
                 "cfgEnabled={5} dynamic={6} lightningCfg={7} boltCount={8} nextStrike={9:F1}s " +
                 "camAsl={10:F0}m agl={11:F0}m cloudFade={12:F2} solarHour={13:F1} submerged={14}",
                w.CurrentPlanet, w.IsActive, w.WeatherValue, w.WeatherName, w.TargetWeatherValue,
                c?.overall?.enabled, c?.overall?.dynamicWeather, c?.lightning?.enabled,
                w.Lightning?.BoltCount ?? 0,
                w.Lightning?.TimeToNextStrike ?? 0f,
                w.CameraAltitudeAsl, w.CameraAltitudeAgl, w.CameraCloudFade, w.LocalSolarHour, w.CameraSubmerged);
            Diag("WeatherStatus.preset: planet={0} weatherPreset={1} (独立于云层预设)", w.CurrentPlanet, w.CurrentConfigName);
            Diag("WeatherStatus.paths: clouds={0}  weather={1}",
                Volken.Core.PlanetConfigList.GetConfigPath(Volken.Core.VolkenMod.CloudConfigListName),
                string.IsNullOrEmpty(w.CurrentPlanet)
                    ? "—"
                    : VolkenWeatherConfig.GetConfigPath(w.CurrentPlanet, w.CurrentConfigName));

            // 资源台账:一眼看出"哪个资源没打进 bundle"
            LogVolkenAssets();
        }

        /// <summary>强制设置天气值(立即生效,不走淡变)。</summary>
        private void SetWeatherValue(float value)
        {
            VolkenWeather.Instance?.ForceWeatherValue(value);
            Log($"VolkenWeather: forced value = {value}");
        }

        /// <summary>手动劈一道雷(需要该行星配置里 lightning.enabled = true)。</summary>
        private void TriggerLightning()
        {
            var w = VolkenWeather.Instance;
            if (w == null) { Log("VolkenWeather: not initialized"); return; }
            w.TriggerLightning();
            Log("VolkenWeather: manual bolt requested");
        }
        
        private void ForceRefresh()
        {
            if (!Game.InFlightScene)
            {
                return;
            }
            if (Volken.Core.VolkenMod.Instance==null)
            {
                Volken.Core.VolkenMod.Initialize();
                Volken.Core.VolkenMod.Instance?.OnFlightSceneLoaded();
                Log("force refresh called");
            }

            if (Volken.Core.VolkenMod.Instance!=null)
            { 
                Volken.Core.VolkenMod.Initialize();
                Volken.Core.VolkenMod.Instance?.OnFlightSceneLoaded();
                Log("Volken is still alive");
            }
        }
        #region LOG
        public static void Log(string format, params object[] args)
        {
            try
            {
                if (ModSettings.Instance == null || !ModSettings.Instance.DevMode) return;
                Debug.unityLogger.LogFormat(LogType.Log, "[Volken]"+format, args);
            }
            catch
            {
                Debug.Log("什么叫做他妈的Log报错了??????");
            }
        }
        #endregion

        #region DIAG LOG (always on)

        /// <summary>诊断日志的前缀。用它在 Player.log 里一眼过滤出天气系统的排查信息。</summary>
        public const string DiagTag = "[VolkenDiag]";

        /// <summary>
        /// **始终输出**的诊断日志 —— 不受 <c>ModSettings.DevMode</c> 影响。
        ///
        /// 与 <see cref="Log"/> 的区别(很关键,踩过):
        /// <c>Log</c> 在 <c>DevMode = false</c> 时**直接 return**,所以"看不到雨"
        /// 的时候很可能连一行提示都没有,排查从"读日志"退化成"猜"。
        /// 天气系统里所有"某某资源没加载 / 某某环节没跑"的判定都用这个通道。
        /// 用法:排查雨的问题时,先在 Player.log 里搜 <c>VolkenDiag</c>。
        /// </summary>
        public static void Diag(string format, params object[] args)
        {
            try
            {
                Debug.unityLogger.LogFormat(LogType.Log, DiagTag + format, args);
            }
            catch
            {
                Debug.Log(DiagTag + "(diag format failed)");
            }
        }

        #endregion

        #region THROTTLED LOG

        // 每帧路径上的异常节流:同一个 key 5 秒内只打一条。
        // 存在意义:OnRenderImage / Tick 里的异常如果不节流,一帧就是几十上百条,
        // Player.log 会被冲爆,反而看不到第一现场。
        private static readonly System.Collections.Generic.Dictionary<string, float> _lastThrottledLog =
            new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>
        /// 节流日志(同一 key 5 秒内只输出一次)。
        /// 走 <see cref="Diag"/> 通道:渲染/Tick 路径上的异常**必须**能看见,
        /// 否则 DevMode 关着的时候一个每帧抛的异常会完全静默。
        /// </summary>
        public static void LogThrottled(string key, Exception ex)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_lastThrottledLog.TryGetValue(key, out float last) && now - last < 5f) return;
                _lastThrottledLog[key] = now;
            }
            catch { }
            Diag($"[{key}] {ex}");
        }

        #endregion

        #region ASSET LOADING

        /// <summary>
        /// 资源加载台账:每次 <see cref="LoadVolkenAsset{T}"/> 的结果都记在这里。
        ///
        /// 为什么需要它:本 mod 的资源全部走"显式资产清单"打包
        /// (<c>ModAssetBundles\…\volken.manifest</c>),**新增的 shader/compute/纹理/音频
        /// 不会自动进包**。缺失时的症状是"功能静默失效 + 日志里一句话",很容易被当成代码 bug。
        /// 有了这张表,一条 <c>volkenWeather</c> 就能直接看出是哪个资源没打进去。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _assetLedger =
            new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>打印资源加载台账(dev 命令 / 诊断用)。</summary>
        public static void LogVolkenAssets()
        {
            if (_assetLedger.Count == 0)
            {
                Diag("AssetLedger: (还没有请求过任何资源)");
                return;
            }
            var sb = new System.Text.StringBuilder("AssetLedger:");
            foreach (var kv in _assetLedger)
            {
                sb.Append("\n  ").Append(kv.Value.PadRight(20)).Append(kv.Key);
            }
            Diag(sb.ToString());
        }

        /// <summary>
        /// 从本 mod 的 asset bundle 里载入一个资源(带空值/异常护栏)。
        ///
        /// 为什么要集中成一个方法:Volken 的各子系统(云 / 天气)都要按
        /// <c>"Assets/Scripts/Volken/..."</c> 的**工程内路径**取 shader / compute / 纹理 / 音频,
        /// 而资源缺失(bundle 没重新打包)是最常见的开发期故障。这里统一处理:
        /// 抛异常 → 记日志 + 返回 null,调用方只需判空,不必各自包 try/catch。
        /// </summary>
        /// <typeparam name="T">资源类型(Shader / ComputeShader / Texture2D / GameObject ...)。</typeparam>
        /// <param name="path">工程内资源路径,如 <c>Assets/Scripts/Volken/Weather/LightningBolt.shader</c>。</param>
        /// <param name="required">true = 缺失时打日志(默认);false = 静默(可选素材用)。</param>
        /// <remarks>
        /// 用 <c>LoadAsset&lt;T&gt;</c> 而**不是** <c>Load&lt;T&gt;</c>:
        /// <c>IModResourceLoader</c>(mod 自己的加载器)只暴露 <c>LoadAsset&lt;T&gt;</c>;
        /// <c>Load&lt;T&gt;(path, logErrors)</c> 在 <c>IResourceLoader</c>(游戏内置加载器)上,
        /// <c>IModResourceLoader</c> 不继承它。云 shader 的既有加载处
        /// (VolkenMod.cs / CloudNoise.cs)用的都是 <c>LoadAsset&lt;T&gt;</c>。
        /// </remarks>
        public static T LoadVolkenAsset<T>(string path, bool required = true) where T : UnityEngine.Object
        {
            // ① 编辑器(含 Play 预览):**先用 AssetDatabase** ——
            //   ⚠️ 不要先碰 `Instance.ResourceLoader`:编辑器里访问 Mod.Instance 可能触发游戏侧半初始化,
            //   刷出一堆无关报错(CelestialDatabase / SceneManager prefab 找不到之类)。
            //   路径格式与游戏内一致(工程相对路径),所以两边共用同一个常量。
#if UNITY_EDITOR
            try
            {
                var editorAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
                if (editorAsset != null)
                {
                    _assetLedger[path] = "ok(editor)";
                    return editorAsset;
                }
            }
            catch
            {
            }
#endif

            // ② 游戏内:走 mod 资源加载器(sr2-mod 包里的 asset bundle)
            try
            {
                var asset = Instance.ResourceLoader.LoadAsset<T>(path);
                if (asset != null)
                {
                    _assetLedger[path] = "ok";
                    return asset;
                }
            }
            catch
            {
            }

            if (required) Log($"LoadVolkenAsset: '{path}' not found (rebuild the asset bundle?)");
            _assetLedger[path] = required ? "MISSING(required)" : "missing(optional)";
            return null;
        }

        #endregion
    }
}