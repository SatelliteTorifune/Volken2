using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Audio;

namespace Volken.Weather
{
    
    public class LightningModule : MonoBehaviour
    {
        /// <summary>近雷素材路径模板(参数 = 序号 1..<see cref="NearClipCount"/>)。</summary>
        private const string NearThunderPathFormat = "Assets/Scripts/Volken/Weather/Audio/volkenThrunder-near-{0}.wav";

        /// <summary>远雷素材路径模板(参数 = 序号 1..<see cref="FarClipCount"/>)。</summary>
        private const string FarThunderPathFormat = "Assets/Scripts/Volken/Weather/Audio/volkenThrunder-far-{0}.wav";

        private const int NearClipCount = 4;
        private const int FarClipCount = 5;

        /// <summary>
        /// 同时可发声的雷声通道数。
        ///
        /// 【为什么不是 1 个 AudioSource】雷声素材最长约 16s,而 <c>lightning.minDelay</c>
        /// 下限是 0.05s —— 单个 AudioSource 上第二次 <c>PlayScheduled</c> 会**顶掉**第一条,
        /// 多个通道轮转,配合一个最小冷却:密度够高时最坏情况是"新雷抢占最旧通道"。
        /// </summary>
        private const int ThunderVoices = 4;

        /// <summary>两次雷声之间的最小间隔(秒),低于它就不播(防止极端配置把 4 个通道全打满)。</summary>
        private const float MinThunderCooldown = 0.6f;

        /// <summary>主干/分叉 shader(<c>Hidden/Volken/LightningBolt</c>)。</summary>
        private Shader _boltShader;

        /// <summary>落点闪光 shader(<c>Hidden/Volken/LightningFlash</c>)。见 <see cref="LoadFlashShader"/>。</summary>
        private Shader _flashShader;

        /// <summary>近雷素材(落点近时用:短促、起始即峰值)。</summary>
        private readonly List<AudioClip> _nearClips = new List<AudioClip>();

        /// <summary>远雷素材(落点远时用:延迟起峰、长隆隆)。</summary>
        private readonly List<AudioClip> _farClips = new List<AudioClip>();

        /// <summary>雷声发声通道池(轮转 + 冷却,见 <see cref="ThunderVoices"/>)。</summary>
        private AudioSource[] _thunderVoices;
        private int _nextVoice;
        private float _lastThunderTime = -999f;

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
            LoadFlashShader();
            LoadAudioAssets();
        }

        /// <summary>
        /// 载入落点闪光 shader。
        ///
        /// 【为什么是独立的第二个 shader】两者原来共用一个双 Pass shader,靠 Pass 名区分 ——
        /// 但**一个 Material 只用 Shader 的第一个匹配 Pass**,那两个 Pass 又没有 LightMode 标签,
        /// 结果闪光球一直在跑主干那套逻辑(它的 <c>_CoreWidth</c>/halo 完全没生效)。
        /// 拆成各含单一 Pass 的两个 shader 之后不存在"选错 Pass"的可能。
        ///
        /// 载入失败不致命:此时 <see cref="LightningBolt.Create"/> 会让闪光球退化用 bolt shader
        /// (画法不对但可见),而不是让材质变紫红。
        /// </summary>
        private void LoadFlashShader()
        {
            try
            {
                _flashShader = Mod.LoadVolkenAsset<Shader>(
                    "Assets/Scripts/Volken/Weather/LightningFlash.shader", false);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:LightningModule flash shader load error: " + ex.Message);
            }

            if (_flashShader == null)
            {
                try { _flashShader = Shader.Find("Hidden/Volken/LightningFlash"); }
                catch { }
            }

            if (_flashShader == null)
            {
                Mod.Log("Volken:LightningModule flash shader NOT FOUND — strike flash will fall back to the bolt shader " +
                        "(add LightningFlash.shader to ModData.asset._otherAssets and rebuild)");
            }
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
            //
            // 【2026-09-28】改成 ThunderVoices 个通道轮转,长期配置(rolloff/spread)在每次播放时
            // 按当前配置刷新(见 ConfigureVoice)—— 面板上调阈值/衰减要立刻听得出差别。
            try
            {
                _thunderVoices = new AudioSource[ThunderVoices];
                for (int i = 0; i < ThunderVoices; i++)
                {
                    var src = gameObject.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.loop = false;
                    src.spatialBlend = 1f;                  // 3D
                    src.rolloffMode = AudioRolloffMode.Logarithmic;
                    src.dopplerLevel = 0f;
                    src.bypassReverbZones = true;
                    _thunderVoices[i] = src;
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:LightningModule AudioSource creation failed: " + ex.Message);
            }

            LoadClipSet(NearThunderPathFormat, NearClipCount, _nearClips, "near");
            LoadClipSet(FarThunderPathFormat, FarClipCount, _farClips, "far");

            int total = _nearClips.Count + _farClips.Count;
            Mod.Log($"Volken:LightningModule loaded thunder clips: near={_nearClips.Count}/{NearClipCount} " +
                    $"far={_farClips.Count}/{FarClipCount} voices={(_thunderVoices != null ? _thunderVoices.Length : 0)}" +
                    (total == 0
                        ? " (SILENT — the audio is not in the asset bundle: check ModData.asset._otherAssets, then rebuild)"
                        : ""));
        }

        /// <summary>载入一组雷声素材(缺失 = 静默跳过,素材没打进 bundle 是预期情况,不刷红字)。</summary>
        private static void LoadClipSet(string pathFormat, int count, List<AudioClip> into, string label)
        {
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    string path = string.Format(pathFormat, i);
                    var clip = Mod.LoadVolkenAsset<AudioClip>(path, false);
                    if (clip == null) continue;
                    into.Add(clip);
                }
                catch (Exception ex)
                {
                    Mod.Log($"Volken:LightningModule {label} thunder clip {i} load failed: {ex.Message}");
                }
            }
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

                // 【2026-09-28】停用时把**在播的闪电**一起清掉:离开飞行场景时若有一道雷
                // 正在播,它自己会随场景卸载消失,但"根物体 + 硬性寿命兜底"之前,
                // 残留物有可能被带到下一个场景 —— 这是"闪电永久存在"最常见的入口。
                int before = LightningBolt.ActiveCount;
                LightningBolt.DestroyAll();

                Mod.Log($"Volken:LightningModule storm loop stopped (cleared {before} in-flight bolt(s))");
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

            // 【2026-09-28】bolt 不再挂到相机下面(旧实现见 LightningBolt 类注释):
            // 挂在 inactive 的相机下会让 bolt 自己在层级里也 inactive,
            // 进而在自己身上 StartCoroutine 静默失败 → 自毁链断掉 → 闪电永久残留。
            // 现在 bolt 是根物体,生命周期完全由自己负责。
            var bolt = LightningBolt.Create(_boltShader, _flashShader, cam);
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

            // 云底海拔(ASL):远雷的声源更接近"云底那一段通道",见 PlayThunderForStrike 的说明。
            // 取不到云层带时给 0 = 等价于"只用落点距离",不会算出一个荒唐的值。
            bool haveCloudBand = TryGetCloudBand(out float cloudBottom, out float cloudTop);
            float cloudBaseAsl = haveCloudBand ? cloudBottom : 0f;
            Camera observerCam = cam;
            // 观测者处的地表法线:声程分解要用它区分"垂直/水平"(world Y/Z 不能当水平用,参考系可能被旋转)
            Vector3 radialUp = GetRadialUp(camPos);

            bolt.OnBoltLanded = () =>
            {
                // 注意:回调里**不再**回传距离 —— 老代码传的是 bolt 自身长度(与玩家无关),
                // 距离必须在这里按"落点 ↔ 观测者"现算,见 PlayThunderForStrike。
                //
                // 观测者位置在**回调里**取而不是在 `CastBolt` 里捕获:bolt 的动画要跑约 0.2s,
                // 这期间相机可能已经移动了。取不到就退回 CastBolt 时的位置(有界、非致命)。
                Vector3 observerPos = camPos;
                try
                {
                    if (observerCam != null) observerPos = observerCam.transform.position;
                }
                catch { }

                PlayThunderForStrike(landing, observerPos, cloudBaseAsl, radialUp, cfg);
            };

            bolt.CastBolt(origin, landing);
            _boltCount++;
            // camDist 是**起手时**的落点距离,仅供参考;真正决定延迟/音色的是雷声那一刻现算的距离,
            // 由 PlayThunder 的 `thunder [...] dist=…` 那行打印。
            Mod.Log($"Volken:LightningModule bolt #{_boltCount} from {origin} to {landing} " +
                    $"(len={Vector3.Distance(origin, landing):F0}m strikeDist~{Vector3.Distance(camPos, landing):F0}m)");
        }

        /// <summary>
        /// 一道雷的**雷声**全流程:① 距离 → ② 声速定延迟 → ③ 阈值选 near/far → 发声。
        ///
        /// 【① 距离】<c>strikeDist</c> = 落点到**观测者**的距离。
        /// 观测者取相机:在 JNO 里近相机与飞船同参考系、相距只有几米,而 <b>AudioListener 就挂在它上面</b> ——
        /// 用相机位置能保证"延迟""音色阈值""3D 定位"三者用的是同一个原点,不会互相打架。
        ///
        /// 【声程而非直线距离】雷声由整条放电通道(云底 ↔ 落点)发出,远处观测者先听到的是
        /// 声程最短的那一段(通常是云底那一端)。所以按 <c>thunderSourceBlend</c> 在
        /// "落点距离"与"云底距离"之间取一个声程,默认各半。
        /// 实测量级(D=3 km、云底 2 km、Droo 340 m/s):地面观测者 8.8 s;观测者升到云底高度时
        /// 落点距离 3.6 km 而云底距离 3.0 km → 混合后 9.7 s(比纯落点距离短约 0.3 s)。
        /// 修正幅度不大但方向正确,且不花额外代价。
        ///
        /// 【② 声速】取 <c>ICraftFlightData.AtmosphereSample.SpeedOfSound</c>(游戏口径,与马赫数同源)。
        /// 真空 / 高度超出大气顶时它恒为 0 → 回退配置里的兜底值,绝不去除 0。
        ///
        /// 【③ 阈值】<c>strikeDist &lt;= thunderNearDistance</c> → 近雷素材组,否则远雷素材组。
        /// 组内随机一条;某一组为空时自动落到另一组(素材只打了一半也能出声)。
        /// </summary>
        private void PlayThunderForStrike(Vector3 strikePos, Vector3 observerPos, float cloudBaseAsl,
                                          Vector3 radialUp, VolkenWeatherConfig cfg)
        {
            var lcfg = cfg.lightning;

            // ---- ① 距离 / 声程 ----
            float strikeDist = Vector3.Distance(observerPos, strikePos);
            float pathDist = strikeDist;
            if (lcfg.thunderSourceBlend > 0f)
            {
                // 云底那一端的声程:沿**地表法线**分解,得到真正的垂直/水平分量。
                // ⚠️ 不能用 world Y/Z 当"水平" —— 参考系可能被旋转(行星坐标系的 Y 才是"上")。
                Vector3 toStrike = strikePos - observerPos;
                float vert = Mathf.Abs(Vector3.Dot(toStrike, radialUp));   // 高度差
                float horizSq = Mathf.Max(0f, toStrike.sqrMagnitude - vert * vert);
                float obsAsl = VolkenWeather.Instance?.CameraAltitudeAsl ?? 0f;
                float cloudBaseVert = Mathf.Abs(cloudBaseAsl - obsAsl);
                float cloudBaseDist = Mathf.Sqrt(horizSq + cloudBaseVert * cloudBaseVert);
                pathDist = Mathf.Lerp(strikeDist, Mathf.Min(strikeDist, cloudBaseDist),
                    Mathf.Clamp01(lcfg.thunderSourceBlend));
            }

            // ---- ② 声速 ----
            float c = GetSpeedOfSound();
            if (c <= 1f)
            {
                c = Mathf.Max(1f, lcfg.thunderFallbackSpeedOfSound);
            }

            // ---- 延迟 / 音量(0 = 回到原版:恒 0.05s + 恒音量) ----
            float at = Mathf.Clamp01(lcfg.thunderDistanceAttenuation);
            float delay = Mathf.Lerp(lcfg.thunderDelay, pathDist / c, at);
            float volume = lcfg.thunderVolume * Mathf.Lerp(1f, DistanceVolume(strikeDist, lcfg.thunderNearDistance), at);

            // ---- ③ near / far ----
            bool near = strikeDist <= lcfg.thunderNearDistance;

            PlayThunder(strikePos, volume, delay, near, strikeDist, pathDist, c);
        }

        /// <summary>
        /// 观测者(飞船)当前所在处的**声速**(米/秒);取不到 / 无物理大气 / 超出大气顶 → 返回 0。
        ///
        /// 数据源 = <c>ICraftFlightData.AtmosphereSample.SpeedOfSound</c>。它是 JNO 自己算马赫数
        /// 用的那个值(<c>DragPhysics</c>: <c>Mach = |v| / speedOfSound</c>),由行星大气的
        /// <c>MeanGamma / MeanMassPerMolecule / MeanSurfaceTemperature</c> 三者算出,**不随高度变化**。
        /// 因此这里读它而不是自己按温度算 —— 保证与游戏 HUD 完全一致。
        ///
        /// 实测量级:Droo 340、Cylero 233、Tydos 931 m/s(硬编码 343 在 Tydos 上要差 2.7 倍)。
        /// </summary>
        private static float GetSpeedOfSound()
        {
            try
            {
                var flightData = Game.Instance?.FlightScene?.CraftNode?.CraftScript?.FlightData;
                if (flightData == null) return 0f;

                // AtmosphereSample 是 struct(按值返回),没有空引用风险;
                // 无物理大气 / 高度 ≥ 大气顶时 SpeedOfSound 保持默认 0。
                return flightData.AtmosphereSample.SpeedOfSound;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 雷声音量随距离的额外衰减(只在 <c>thunderDistanceAttenuation &gt; 0</c> 时叠加)。
        ///
        /// 换算成"能量按 1/距离":这里给的是**线性音量**,所以用 <c>sqrt(near/d)</c> ——
        /// 若直接用 <c>near/d</c>,叠加 AudioSource 自带的对数 rolloff 会把远处的雷压到听不见。
        /// 阈值一半以内不衰减;之后缓慢收,最低留 0.25,免得远雷完全消失。
        /// </summary>
        private static float DistanceVolume(float distance, float nearDistance)
        {
            float inner = Mathf.Max(1f, nearDistance * 0.5f);   // 阈值一半以内视为"就在跟前"
            if (distance <= inner) return 1f;
            return Mathf.Clamp(Mathf.Sqrt(inner / distance), 0.25f, 1f);
        }

        /// <summary>
        /// 按当前配置刷新某个通道的 3D 参数。
        ///
        /// 【为什么远雷要把 spread 拉大】<c>spread</c> = 声源在 3D 空间里的"张角"。
        /// 近雷是一个点(炸响),远雷是**头顶一大片区域**在响 —— 60° 对远雷太"尖",
        /// 听上去像一个远处的小喇叭。这条对"真实感"的贡献比阈值本身更大。
        /// </summary>
        private static void ConfigureVoice(AudioSource src, bool near, float nearDistance)
        {
            src.minDistance = near ? Mathf.Max(10f, nearDistance * 0.15f) : nearDistance;
            src.maxDistance = Mathf.Max(src.minDistance * 4f, nearDistance * 12f);
            src.spread = near ? 60f : 160f;
        }

        private void PlayThunder(Vector3 position, float volume, float delay, bool near,
                                 float strikeDist, float pathDist, float speedOfSound)
        {
            int nearCount = _nearClips.Count;
            int farCount = _farClips.Count;
            if (nearCount + farCount == 0 || _thunderVoices == null || _thunderVoices.Length == 0) return;

            // 某一组为空时自动落到另一组:素材只打了一半也能出声,而不是静默
            if (near && nearCount == 0) near = false;
            else if (!near && farCount == 0) near = true;
            var clips = near ? _nearClips : _farClips;

            // 最小冷却:防止极端配置(minDelay 下限 0.05s)把通道池打成"每条都抢断前一条"
            if (Time.unscaledTime - _lastThunderTime < MinThunderCooldown)
            {
                Mod.Log($"Volken:LightningModule thunder skipped (cooldown {MinThunderCooldown:F1}s, " +
                        $"dist={strikeDist:F0}m)");
                return;
            }
            _lastThunderTime = Time.unscaledTime;

            var clip = clips[UnityEngine.Random.Range(0, clips.Count)];
            try
            {
                if (_nextVoice < 0 || _nextVoice >= _thunderVoices.Length) _nextVoice = 0;
                var src = _thunderVoices[_nextVoice];
                _nextVoice = (_nextVoice + 1) % _thunderVoices.Length;

                float nearDistance = VolkenWeather.Instance?.Config?.lightning?.thunderNearDistance ?? 2000f;
                ConfigureVoice(src, near, nearDistance);

                src.transform.position = position;
                src.clip = clip;
                src.volume = Mathf.Clamp01(volume);
                // PlayScheduled 定时播放:不占协程、时间点精确。dspTime 是**真实时间**,
                // 不受 Time.timeScale / 游戏倍速影响 —— 这正是"声音按真实秒到达"该有的行为。
                double startTime = AudioSettings.dspTime + Mathf.Max(0f, delay);
                src.PlayScheduled(startTime);

                Mod.Log($"Volken:LightningModule thunder [{(near ? "near" : "far")}] " +
                        $"dist={strikeDist:F0}m path={pathDist:F0}m c={speedOfSound:F0}m/s " +
                        $"delay={delay:F2}s vol={volume:F2} clip={clip.name}");
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
