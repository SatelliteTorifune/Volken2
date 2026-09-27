using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Audio;

namespace Volken.Weather
{
    
    /// <summary>
    /// 随机闪电生成 + 雷声 —— 移植自 SP2 的 <c>Enviro.EnviroLightningModule</c>。
    ///
    /// 【原版触发链】
    ///   <c>lightningStorm = true</c> → <c>UpdateModule()</c> 累计间隔 →
    ///   <c>CastLightningBoltRandom()</c>(源点 = 云层中高 <c>(云底+云顶)/2</c> 加随机偏移,
    ///   落点 = 地面加随机偏移)→ 实例化 lightning prefab → <c>CastBolt(from, to)</c> →
    ///   延迟 0.05s → <c>PlayRandomThunderSFX()</c>(从 <c>thunderClips</c> 里随机播一条)。
    ///
    /// 【SR2 侧的差异与对策】
    ///   1. <c>lightningStorm</c> 在 SP2 是天气预设里的布尔开关;SR2 没有预设系统 →
    ///      直接用 <c>lightning.stormValue</c>(默认 2.5,可调)表达同一语义,
    ///      再加一个 <c>VolkenWeatherConfig.LightningSection.enabled</c> 总开关。
    ///   2. 雷声音频:SP2 从 <c>EnviroAudioModule.thunderClips</c> 随机;这里从 mod bundle 里
    ///      载入 <c>enviro_thunder_1~5.ogg</c>(自 sp2d4 素材工程拷入),缺失时静默降级为无雷声。
    ///   3. 云层中高:SP2 读 Enviro 的 <c>bottomCloudsHeight/topCloudsHeight</c>;这里读
    ///      Volken 主层的 <c>layerHeights/layerSpreads/layerStrengths</c> —— 保证雷从**真正的云**里出来。
    ///
    /// 【雷声延迟】原版固定 0.05s(它把"光线先到、声音后到"忽略了)。
    /// 本实现给 <c>thunderDistanceAttenuation</c>(0~1):0 = 恒 0.05s(原版行为),
    /// 1 = 按真实声速 343 m/s 延迟。用 <c>AudioSource.PlayScheduled</c> 做定时,不占协程。
    /// </summary>
    public class LightningModule : MonoBehaviour
    {
        private const string ThunderPathFormat = "Assets/Scripts/Volken/Weather/Audio/enviro_thunder_{0}.ogg";
        private const int ThunderClipCount = 5;
        private const float SpeedOfSound = 343f;

        private Shader _boltShader;
        private readonly List<AudioClip> _thunderClips = new List<AudioClip>();
        private AudioSource _audioSource;
        private bool _assetsLoaded;

        private Coroutine _stormRoutine;
        private bool _running;
        private int _boltCount;

        /// <summary>已生成的闪电数(诊断用)。</summary>
        public int BoltCount => _boltCount;

        /// <summary>距下一次雷击的剩余秒数(诊断/UI 用)。</summary>
        public float TimeToNextStrike { get; private set; }

        // ================= 生命周期 =================

        public void Initialize(Shader boltShader)
        {
            _boltShader = boltShader;
            if (_boltShader == null)
            {
                try { _boltShader = Shader.Find("Hidden/Volken/LightningBolt"); }
                catch { }
            }
            LoadAudioAssets();
        }

        private void LoadAudioAssets()
        {
            if (_assetsLoaded) return;
            _assetsLoaded = true;

            // 自建 AudioSource 播放雷声(而不是走 Game.Instance.AudioPlayer):
            //   - mod 自己的加载器 IModResourceLoader 只暴露 LoadAsset<T>,**没有** LoadAudio
            //     (LoadAudio 是游戏内置 IResourceLoader 上的方法,它走 Resources.Load,
            //     读不到 mod bundle 里的素材);
            //   - AudioPlayer.PlaySound 吃 ModApi.Audio.AudioFile,而 AudioFile 一旦 AudioClip
            //     为空,内部同样会走游戏的 LoadAudio 去 Resources 里找 —— 够不到 mod bundle。
            // 自建 AudioSource 的代价:不经过游戏的音效混音组,不受"音效音量"滑块控制,
            // 因此音量在 VolkenWeatherConfig.LightningSection.thunderVolume 里单独给(默认 0.65)。
            try
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.loop = false;
                _audioSource.spatialBlend = 1f;                  // 3D
                _audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                _audioSource.minDistance = 200f;
                _audioSource.maxDistance = 60000f;
                _audioSource.dopplerLevel = 0f;
                _audioSource.spread = 60f;
                _audioSource.bypassReverbZones = true;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:LightningModule AudioSource creation failed: " + ex.Message);
            }

            for (int i = 1; i <= ThunderClipCount; i++)
            {
                try
                {
                    string path = string.Format(ThunderPathFormat, i);
                    // required: false —— 素材没打进 bundle 是预期情况,不要刷红字
                    var clip = Mod.LoadVolkenAsset<AudioClip>(path, false);
                    if (clip == null) continue;
                    _thunderClips.Add(clip);
                }
                catch (Exception ex)
                {
                    Mod.Log($"Volken:LightningModule thunder clip {i} load failed: {ex.Message}");
                }
            }

            Mod.Log($"Volken:LightningModule loaded {_thunderClips.Count}/{ThunderClipCount} thunder clips" +
                    (_thunderClips.Count == 0 ? " (silent — check that the audio is in the asset bundle)" : ""));
        }

        /// <summary>天气/行星变化时由 <c>VolkenWeather</c> 调用,按需起停雷暴循环。</summary>
        public void SetActive(bool active)
        {
            if (active == _running) return;
            _running = active;

            if (active)
            {
                if (!_assetsLoaded) LoadAudioAssets();
                _stormRoutine = StartCoroutine(StormLoop());
                Mod.Log("Volken:LightningModule storm loop started");
            }
            else
            {
                if (_stormRoutine != null)
                {
                    StopCoroutine(_stormRoutine);
                    _stormRoutine = null;
                }
                TimeToNextStrike = 0f;
                Mod.Log("Volken:LightningModule storm loop stopped");
            }
        }

        private IEnumerator StormLoop()
        {
            // 起手先随机等一段,避免刚进雷暴就劈
            while (true)
            {
                var cfg = VolkenWeather.Instance?.Config;
                float minDelay = cfg != null ? cfg.lightning.minDelay : 6f;
                float maxDelay = cfg != null ? cfg.lightning.maxDelay : 45f;

                float wait = UnityEngine.Random.Range(minDelay, maxDelay);
                TimeToNextStrike = wait;

                float t = 0f;
                while (t < wait)
                {
                    t += Time.deltaTime;
                    TimeToNextStrike = Mathf.Max(0f, wait - t);
                    yield return null;
                }
                TimeToNextStrike = 0f;

                // 条件复核:天气可能在等待期间转晴了(原版也每帧复核 lightningStorm)
                var weather = VolkenWeather.Instance;
                if (weather == null || !weather.IsActive) continue;
                var c = weather.Config;
                if (c == null || !c.lightning.enabled) continue;
                if (weather.WeatherValue < c.lightning.stormValue) continue;

                CastRandomBolt();
            }
        }

        // ================= 落雷 =================

        /// <summary>
        /// 随机落一道雷(对应 SP2 <c>CastLightningBoltRandom</c>)。
        /// 源点 = 云层中高 ± randomSpawnRange;落点 = 地面 ± randomTargetRange。
        /// </summary>
        public void CastRandomBolt()
        {
            var weather = VolkenWeather.Instance;
            var cfg = weather?.Config;
            if (cfg == null) return;

            if (_boltShader == null)
            {
                Mod.Log("Volken:LightningModule: no bolt shader — cannot cast");
                return;
            }

            Camera cam = null;
            try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
            catch { }
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            bool haveCloudBand = TryGetCloudBand(out float cloudBottom, out float cloudTop);
            Vector3 radial = GetRadialUp(camPos);

            // === 源点:云层中高 ===
            float sourceAlt = haveCloudBand
                ? Mathf.Lerp(cloudBottom, cloudTop, 0.5f)
                : weather.CameraAltitudeAsl + 2000f;
            Vector3 source = camPos + radial * (sourceAlt - weather.CameraAltitudeAsl);
            source += UnityEngine.Random.insideUnitSphere * cfg.lightning.spawnRange;

            // === 落点:地面 ===
            Vector3 ground = GetGroundPosition(camPos, radial);
            Vector3 target = ground + UnityEngine.Random.insideUnitSphere * cfg.lightning.targetRange;
            // 落点别低于地面太多(否则闪电会插进地里)
            target += radial * Mathf.Max(0f, Vector3.Dot(ground - target, radial));

            CastBolt(source, target, cam);
        }

        /// <summary>
        /// 在指定的世界坐标之间劈一道雷(dev 命令 / 手动触发用)。
        /// </summary>
        public void CastBolt(Vector3 from, Vector3 to, Camera cam = null)
        {
            var cfg = VolkenWeather.Instance?.Config;
            if (cfg == null || _boltShader == null) return;

            if (cam == null)
            {
                try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
                catch { }
                if (cam == null) cam = Camera.main;
            }

            Transform parent = null;
            try { parent = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera?.transform; }
            catch { }

            var bolt = LightningBolt.Create(parent, _boltShader, cam);
            bolt.flashIntensity = cfg.lightning.flashIntensity;
            bolt.flashPlaneIntensity = cfg.lightning.flashIntensity * 0.4f;
            bolt.arcs = cfg.lightning.arcs;
            bolt.inaccuracy = cfg.lightning.inaccuracy;
            bolt.splits = cfg.lightning.splits;
            bolt.width = cfg.lightning.width;
            bolt.baseIntensity = cfg.lightning.intensity;
            bolt.lightIntensity = cfg.lightning.lightIntensity;
            bolt.lightRange = cfg.lightning.lightRange;

            Vector3 origin = from;
            Vector3 landing = to;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;

            bolt.OnBoltLanded = distance =>
            {
                // 雷声:延迟 = lerp(原版 0.05s, 真实声速传播, thunderDistanceAttenuation)
                float at = Mathf.Clamp01(cfg.lightning.thunderDistanceAttenuation);
                float delay = Mathf.Lerp(cfg.lightning.thunderDelay, distance / SpeedOfSound, at);
                float volume = cfg.lightning.thunderVolume * Mathf.Lerp(1f, DistanceVolume(distance), at);
                PlayThunder(landing, volume, delay);
            };

            bolt.CastBolt(origin, landing);
            _boltCount++;
            Mod.Log($"Volken:LightningModule bolt #{_boltCount} from {origin} to {landing} " +
                    $"(len={Vector3.Distance(origin, landing):F0}m camDist={Vector3.Distance(camPos, landing):F0}m)");
        }

        /// <summary>
        /// 雷声音量随距离衰减(只在 <c>thunderDistanceAttenuation &gt; 0</c> 时叠加,作为额外的听觉距离感)。
        /// 1000m 内不衰减;之后按 1/距离 收,最低留 0.25 免得远处雷完全听不见。
        /// </summary>
        private static float DistanceVolume(float distance)
        {
            const float near = 1000f;
            if (distance <= near) return 1f;
            return Mathf.Clamp(near / distance, 0.25f, 1f);
        }

        private void PlayThunder(Vector3 position, float volume, float delay)
        {
            if (_thunderClips.Count == 0 || _audioSource == null) return;

            var clip = _thunderClips[UnityEngine.Random.Range(0, _thunderClips.Count)];
            try
            {
                _audioSource.transform.position = position;
                _audioSource.clip = clip;
                _audioSource.volume = Mathf.Clamp01(volume);
                // PlayScheduled 定时播放:不占协程,时间点精确(与 SP2 的"延迟 0.05s 后播雷声"等价,
                // 本实现还额外支持按声速传播的真实延迟)。
                double startTime = AudioSettings.dspTime + Mathf.Max(0f, delay);
                _audioSource.PlayScheduled(startTime);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:LightningModule PlayThunder failed: " + ex.Message);
            }
        }

        // ================= 几何辅助 =================

        /// <summary>相机所在处的地表外法线(单位向量)。取不到时回退世界 Y 轴。</summary>
        private static Vector3 GetRadialUp(Vector3 camPos)
        {
            try
            {
                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                if (craftNode?.ReferenceFrame != null)
                {
                    Vector3 center = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                    Vector3 up = camPos - center;
                    if (up.sqrMagnitude > 1e-6f) return up.normalized;
                }
            }
            catch { }
            return Vector3.up;
        }

        /// <summary>
        /// 相机正下方的地面世界坐标。
        /// 用行星半径 + 相机正下方方向近似(即把行星当作正球、忽略局部地形起伏)——
        /// JNO ModApi 没有"该点地形高度"的直接查询,而 AGL 给的是**飞船**所在点的地形高度,
        /// 用它去修正相机下方的地面会把落雷点横向带偏。宁可差一个山顶高度,也不要偏几百米。
        /// 已知限制:山峰上的落雷可能插进山体(方案 A 的可接受损失,二期可换 Raycast)。
        /// </summary>
        private static Vector3 GetGroundPosition(Vector3 camPos, Vector3 radialUp)
        {
            try
            {
                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                if (craftNode?.ReferenceFrame != null && craftNode.Parent?.PlanetData != null)
                {
                    Vector3 center = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                    double radius = craftNode.Parent.PlanetData.Radius;
                    return center + radialUp * (float)radius;
                }
            }
            catch { }

            return camPos - radialUp * 1000f;
        }

        /// <summary>
        /// 取 Volken 主层的云层高度带 [底, 顶](米, ASL)。与
        /// <c>VolkenWeather.ComputeCameraCloudFade</c> 同一套数据源,保证雷从真正的云里出来。
        /// </summary>
        private static bool TryGetCloudBand(out float bottom, out float top)
        {
            bottom = 0f;
            top = 0f;
            try
            {
                var cloudCfg = Volken.Core.VolkenMod.Instance?.MainLayer?.config;
                if (cloudCfg == null || !cloudCfg.enabled) return false;

                float lo = float.MaxValue, hi = float.MinValue;
                var heights = cloudCfg.layerHeights;
                var spreads = cloudCfg.layerSpreads;
                var strengths = cloudCfg.layerStrengths;
                for (int i = 0; i < 4; i++)
                {
                    float strength = i == 0 ? strengths.x : i == 1 ? strengths.y : i == 2 ? strengths.z : strengths.w;
                    if (strength <= 0f) continue;
                    float h = i == 0 ? heights.x : i == 1 ? heights.y : i == 2 ? heights.z : heights.w;
                    float sp = i == 0 ? spreads.x : i == 1 ? spreads.y : i == 2 ? spreads.z : spreads.w;
                    lo = Mathf.Min(lo, h - sp);
                    hi = Mathf.Max(hi, h + sp);
                }
                if (lo > hi) return false;
                bottom = lo;
                top = hi;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnDestroy()
        {
            SetActive(false);
        }
    }
}
