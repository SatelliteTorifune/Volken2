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

    /// <summary>本 mod 的 GameMod 单例。</summary>
    public partial class Mod : ModApi.Mods.GameMod
    {
        /// <summary>私有构造:禁止外部 new。</summary>
        private Mod() : base()
        {
        }

        public int frontRenderQueue = 3000;
        public int backRenderQueue = 3000;
        
        //f 2000 2500 2501
        //b 2500-2501
        
        public static Mod Instance { get; } = GetModInstance<Mod>();

        private GameObject VolkenUI;
        public GameObject ForceSettingScriptLoadGameObject;
        
        /// <summary>mod 版本(取自 ModInfo.Version)。</summary>
        public Version ModVersion { get; private set; }
        
        public override void OnModLoaded()
        {
            base.OnModInitialized();
            var harmony = new Harmony("com.SatelliteTorifune.Volken");
            harmony.PatchAll();
            
            //PlanetRingsZWriteFix.Apply(harmony);
            //PlanetRingsShaderPatch.Apply(harmony);
            
            SetUpGo<VolkenUserInterface>(ref VolkenUI,"VolkenUI");
            SetUpGo<ForceSetting>(ref ForceSettingScriptLoadGameObject,"ForceSettingObject");
            
            // 初始化顺序是硬要求:VolkenClouds 负责解析行星环境并广播 PlanetChanged,
            // 必须先于 VolkenWeather(后者订阅它)。
            VolkenClouds.Initialize();
            VolkenWeather.Initialize();
            // 注:性能剖析器(VolkenProfiler)不再由正式代码启动 —— 见 VolkenTests/TestsBootstrap.cs 自发注册
            RegisterCommands();

            Game.Instance.Settings.Game.Flight.GroundClouds.Value = true;

            // 本地版本 = ModInfo.Version(System.Version,如 0.6)
            this.ModVersion = this.ModInfo.Version;

            // 更新检查(本地模式:LatestVersionUrl 未配置时仅打日志,不弹窗)
            new ModUpdater().CheckForUpdate();
        }

        private void SetUpGo<T>(ref GameObject GO,string name) where T : MonoBehaviour
        {

            GO = new GameObject(name);
            GO.AddComponent<T>();
            GameObject.DontDestroyOnLoad(GO);
            GO.SetActive(true);
        }
        private void RegisterCommands()
        {
            DevConsoleApi.RegisterCommand<int>("frs",i=>this.frontRenderQueue=i);
            DevConsoleApi.RegisterCommand<int>("brs",i=>this.backRenderQueue=i);
            DevConsoleApi.RegisterCommand("VolkenForceRefresh",ForceRefresh);

            DevConsoleApi.RegisterCommand("volkenWeather",WeatherStatus);
            DevConsoleApi.RegisterCommand("volkenAssets",LogVolkenAssets);

            // 测试/开发工具的命令由 VolkenTests/TestsBootstrap.cs 自发注册 —— 正式代码对该文件夹零引用。

            // 雨:没有控制台指令,全部旋钮在天气面板「雨」分组
        }

        /// <summary>打印天气系统当前状态(行星 / 配置 / 雷电 / 相机)。</summary>
        private void WeatherStatus()
        {
            var w = VolkenWeather.Instance;
            if (w == null) { Diag("WeatherStatus: VolkenWeather 未初始化"); return; }
            var c = w.Config;
            Diag("WeatherStatus: planet={0} active={1} cfgEnabled={2} lightningCfg={3} boltCount={4} nextStrike={5:F1}s " +
                 "camAsl={6:F0}m agl={7:F0}m cloudFade={8:F2} solarHour={9:F1} submerged={10}",
                w.CurrentPlanet, w.IsActive, c?.overall?.enabled, c?.lightning?.enabled,
                w.Lightning?.BoltCount ?? 0,
                w.Lightning?.TimeToNextStrike ?? 0f,
                w.CameraAltitudeAsl, w.CameraAltitudeAgl, w.CameraCloudFade, w.LocalSolarHour, w.CameraSubmerged);
            Diag("WeatherStatus.preset: planet={0} weatherPreset={1} (独立于云层预设)", w.CurrentPlanet, w.CurrentConfigName);
            Diag("WeatherStatus.paths: clouds={0}  weather={1}",
                Volken.Core.PlanetConfigList.GetConfigPath(Volken.Core.PlanetConfigList.DefaultListName),
                string.IsNullOrEmpty(w.CurrentPlanet)
                    ? "—"
                    : VolkenWeatherConfig.GetConfigPath(w.CurrentPlanet, w.CurrentConfigName));

            // 资源台账:一眼看出"哪个资源没打进 bundle"
            LogVolkenAssets();
        }
        
        /// <summary>手动劈一道雷(需要该行星配置里 lightning.enabled = true)。</summary>
        private void TriggerLightning()
        {
            var w = VolkenWeather.Instance;
            if (w == null) { Log("VolkenWeather: not initialized"); return; }
            w.TriggerLightning();
            Log("VolkenWeather: manual bolt requested");
        }
        
        /// <summary>开发命令 <c>VolkenForceRefresh</c>:强制重跑一次行星解析。</summary>
        private void ForceRefresh()
        {
            if (!Game.InFlightScene) return;

            Volken.Clouds.VolkenClouds.Initialize();
            VolkenWeather.Initialize();

            Volken.Clouds.VolkenClouds.Instance?.OnFlightSceneLoaded();
            Log("VolkenForceRefresh: 已让 VolkenClouds 重新解析行星环境");;
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

        /// <summary>始终输出的诊断日志(不受 <c>DevMode</c> 影响)。排查时在 Player.log 里搜 <c>VolkenDiag</c>。</summary>
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

        // 每帧路径上的异常必须节流,否则 Player.log 被冲爆、看不到第一现场。
        private static readonly System.Collections.Generic.Dictionary<string, float> _lastThrottledLog =
            new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>节流日志(同一 key 5 秒一次)。</summary>
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

        /// <summary>资源加载台账(dev 命令 <c>volkenAssets</c>)。新增资产不会自动进包,缺失 = 功能静默失效。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _assetLedger =
            new System.Collections.Generic.Dictionary<string, string>();

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
        /// 从 bundle 载入资源;**用 <c>LoadAsset&lt;T&gt;</c> 而不是 <c>Load&lt;T&gt;</c>**(后者读不到 mod bundle)。
        /// 失败记日志并返回 null。
        /// </summary>
        /// <param name="path">工程内路径(移动资产必须同步改这里)。</param>
        /// <param name="required">true = 缺失时打日志(默认)。</param>
        public static T LoadVolkenAsset<T>(string path, bool required = true) where T : UnityEngine.Object
        {
            // 编辑器(含 Play 预览)先用 AssetDatabase:别先碰 Instance.ResourceLoader,
            // 否则可能触发游戏侧半初始化,刷出一堆无关报错。路径格式两边一致,共用同一常量。
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
