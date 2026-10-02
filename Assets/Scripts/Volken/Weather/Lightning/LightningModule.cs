using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Audio;
using Volken.Clouds;

namespace Volken.Weather
{
    
    public class LightningModule : MonoBehaviour
    {
        private const string NearThunderPathFormat = "Assets/Scripts/Volken/Weather/Lightning/Audio/volkenThrunder-near-{0}.wav";
        private const string FarThunderPathFormat = "Assets/Scripts/Volken/Weather/Lightning/Audio/volkenThrunder-far-{0}.wav";

        private const int NearClipCount = 4;
        private const int FarClipCount = 5;

        /// 雷声通道数:同一个 AudioSource 上第二次 <c>PlayScheduled</c> 会**顶掉**前一条(素材最长约 16s),故多通道轮转。
        private const int ThunderVoices = 4;

        private const float MinThunderCooldown = 0.6f;   // 秒:两次雷声之间的最小间隔,低于它不播(防极端配置打满通道)

        private Shader _boltShader;

        private Shader _flashShader;

        private readonly List<AudioClip> _nearClips = new List<AudioClip>();   // 落点近:短促、起始即峰值
        private readonly List<AudioClip> _farClips = new List<AudioClip>();    // 落点远:延迟起峰、长隆隆

        private AudioSource[] _thunderVoices;
        private int _nextVoice;
        private float _lastThunderTime = -999f;

        private readonly double[] _voiceStartDsp = new double[ThunderVoices];   // 各通道的排程起点(0 = 空闲)
        private readonly float[] _voicePending = new float[ThunderVoices];      // 暂停时未到点的剩余延迟
        private readonly bool[] _voicePaused = new bool[ThunderVoices];
        private bool _paused;

        private bool _assetsLoaded;

        private Coroutine _stormRoutine;
        private bool _running;
        private int _boltCount;

        public int BoltCount => _boltCount;

        public float TimeToNextStrike { get; private set; }

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

        /// 落点闪光 shader 必须与主干分开:一个 Material 只用第一个匹配 Pass,共用双 Pass shader 时闪光球会一直跑主干那套逻辑。
        private void LoadFlashShader()
        {
            try
            {
                _flashShader = Mod.LoadVolkenAsset<Shader>(
                    "Assets/Scripts/Volken/Weather/Lightning/Shader/LightningFlash.shader", false);
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

            // 自建 AudioSource:mod 的 IModResourceLoader 读不到 mod bundle 里的音频,代价是不经过游戏混音组、
            // 不受"音效音量"滑块控制(音量单独由 LightningSection.thunderVolume 给)。rolloff/spread 每次播放时刷新。
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

        /// 载入一组素材;缺失 = 静默跳过(素材没打进 bundle 属预期情况,不刷红字)。
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

        /// 天气 / 行星变化时由 <c>VolkenWeather</c> 调用,按需起停雷暴循环。
        public void SetActive(bool active)
        {
            // 常驻宿主:不跑雷暴时把 Update 也停掉(本组件会长久留在宿主上,不停就在所有场景里每帧空转)
            enabled = active;

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

                // 连同还没响完的雷声一起停:Update 停了之后没人再兜底暂停/收尾,残留排程会在关闭雷电或离场后继续响
                StopThunderVoices();

                // Update 停表后没人再检测参考系重定位;清掉状态,下次启用时重新标定(否则把"停用那段时间的位移"误判成一次重定位)
                _paused = GamePause.IsPaused;
                ReleaseFrameGuard();

                // 停用时把在播的闪电一起清掉,否则残留物可能被带到下一个场景。
                int before = LightningBolt.ActiveCount;
                LightningBolt.DestroyAll();

                Mod.Log($"Volken:LightningModule storm loop stopped (cleared {before} in-flight bolt(s))");
            }
        }

        /// <summary>停掉全部雷声通道并清空排程 / 暂停状态(调用方随后会停掉 <c>Update</c>,所以必须在这里收干净)。</summary>
        private void StopThunderVoices()
        {
            _paused = GamePause.IsPaused;
            if (_thunderVoices == null) return;

            for (int i = 0; i < _thunderVoices.Length; i++)
            {
                try { if (_thunderVoices[i] != null) _thunderVoices[i].Stop(); } catch { }
                _voiceStartDsp[i] = 0.0;
                _voicePending[i] = 0f;
                _voicePaused[i] = false;
            }
        }

        // ---- 参考系重定位(浮动原点)防护 ----
        // SR2 会在"离帧中心 >5000m / 帧速 >1000m/s / 时间加速 / 表面锁定切换"时把整个世界平移 positionDelta。
        // 雷的线段(LineRenderer world space)、落点闪光球、点光源全部在**铸造那一刻**按世界坐标摆好,之后只播动画
        // (Grow→Flash→Fade 约 0.2s,硬寿命上限 3.5s)。世界一旦平移,这些坐标就停在一个固定的世界位置上不动,
        // 而飞船/相机已经跳走 —— 看上去就是"某处凭空挂着一束亮光/射灯",而不是一道雷劈下来。
        //
        // 判据照抄 RainParticles.DetectRecenterJump(那里已验证过):飞行器是"世界物体",换帧时它的**帧位置**整体跳变
        // (自己没动)。跳变 > max(100m, 自身本帧运动×3 + 30m) → 判为重定位;只比阈值,不比较 delta 的方向/符号。
        //
        // 为什么不订阅 IGameView.ReferenceFrameRecentered:RainParticles 的实测注释写明"本 mod 环境下该事件不触发,
        // 兜底才是主力"。这里同样以跳变检测为主,不新增一条不可靠的订阅链。
        private const float FrameJumpMinThreshold = 100f;   // 米:低于它一律不算重定位(避免把抖动/瞬移判成换帧)
        private const float FrameJumpMotionFactor = 3f;     // 自身运动的上限倍数(帧率抖动 + 加速度裕量)
        private const float FrameJumpMotionSlack = 30f;     // 米:静止时的固定裕量

        private Vector3 _lastCraftFramePos;
        private bool _hasLastCraftFramePos;

        /// <summary>每帧标定飞行器的帧位置;判到重定位就把纪元 +1 并清掉属于旧纪元的闪电。取不到飞行器则暂不判定(不猜)。</summary>
        private void BeginFrameGuard()
        {
            Vector3 craftFramePos;
            float selfSpeed;
            try
            {
                var cr = Game.Instance?.FlightScene?.CraftNode?.CraftScript;
                if (cr == null)
                {
                    // 取不到飞行器 → 本次不判定,并且**丢掉基准**:否则"飞行器消失一阵又在别处出现"会被当成一次重定位
                    ReleaseFrameGuard();
                    return;
                }
                craftFramePos = cr.FramePosition;
                selfSpeed = cr.FrameVelocity.magnitude;
            }
            catch { ReleaseFrameGuard(); return; }

            if (!_hasLastCraftFramePos)
            {
                _lastCraftFramePos = craftFramePos;
                _hasLastCraftFramePos = true;
                return;
            }

            Vector3 jump = craftFramePos - _lastCraftFramePos;
            _lastCraftFramePos = craftFramePos;

            float jumpMag = jump.magnitude;
            if (jumpMag < 0.01f) return;

            float selfMotion = selfSpeed * Mathf.Max(0f, Time.deltaTime);   // 本帧飞行器自身运动上限
            float threshold = Mathf.Max(FrameJumpMinThreshold, selfMotion * FrameJumpMotionFactor + FrameJumpMotionSlack);
            if (jumpMag <= threshold) return;

            int killed = LightningBolt.DestroyStale();   // 推进纪元 + 清掉旧纪元的雷
            Mod.Log($"Volken:LightningModule frame recentered (jump={jumpMag:F1}m threshold={threshold:F0}m " +
                    $"selfSpd={selfSpeed:F1}m/s dt={Time.deltaTime:F4}s) → epoch={LightningBolt.FrameIndex} {killed} bolt(s) dropped");
        }

        /// <summary>停止标定(停用/销毁时调):下次启用第一帧重新取基准,不要跨停用区间比对。</summary>
        private void ReleaseFrameGuard()
        {
            _hasLastCraftFramePos = false;
            _lastCraftFramePos = Vector3.zero;
        }

        /// <summary>暂停状态每帧检查一次:暂停时冻住雷声(在播的 Pause、未到点的停掉记剩余延迟),恢复时接着播/按剩余延迟重排。</summary>
        private void Update()
        {
            BeginFrameGuard();

            bool paused = GamePause.IsPaused;
            if (paused == _paused) return;
            _paused = paused;
            if (paused) SuspendThunder();
            else ResumeThunder();
        }

        private void SuspendThunder()
        {
            if (_thunderVoices == null) return;
            double now = AudioSettings.dspTime;
            for (int i = 0; i < _thunderVoices.Length; i++)
            {
                var src = _thunderVoices[i];
                if (src == null) continue;

                if (_voiceStartDsp[i] > now)   // 已排程、还没响 → 绝对 dsp 排程会在暂停期间抢跑,只能停掉
                {
                    _voicePending[i] = (float)(_voiceStartDsp[i] - now);
                    src.Stop();
                }
                else if (src.isPlaying)        // 正在响 → 原地暂停(无爆音)
                {
                    src.Pause();
                    _voicePaused[i] = true;
                }
            }
            Mod.Log("Volken:LightningModule thunder suspended (game paused)");
        }

        private void ResumeThunder()
        {
            if (_thunderVoices == null) return;
            double now = AudioSettings.dspTime;
            for (int i = 0; i < _thunderVoices.Length; i++)
            {
                var src = _thunderVoices[i];
                if (src == null) continue;

                if (_voicePaused[i])
                {
                    _voicePaused[i] = false;
                    src.UnPause();
                }
                else if (_voicePending[i] > 0f)   // 按"暂停前还剩多久"重排,不吞掉已经发生的落雷
                {
                    _voiceStartDsp[i] = now + _voicePending[i];
                    src.PlayScheduled(_voiceStartDsp[i]);
                    _voicePending[i] = 0f;
                }
            }
            Mod.Log("Volken:LightningModule thunder resumed (game unpaused)");
        }

        private IEnumerator StormLoop()
        {
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
                    if (!GamePause.IsPaused)   // 暂停期间不倒数(否则菜单里也会继续落雷)
                    {
                        t += Time.deltaTime;
                        TimeToNextStrike = Mathf.Max(0f, wait - t);
                    }
                    yield return null;
                }
                TimeToNextStrike = 0f;

                // 条件复核:等待期间玩家可能关掉了天气/雷电
                var weather = VolkenWeather.Instance;
                if (weather == null || !weather.IsActive) continue;
                var c = weather.Config;
                if (c == null || !c.lightning.enabled) continue;

                CastRandomBolt();
            }
        }

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

            // 源点:云层中高(取不到云层带则退回相机高度 +2000m)
            float sourceAlt = haveCloudBand
                ? Mathf.Lerp(cloudBottom, cloudTop, 0.5f)
                : weather.CameraAltitudeAsl + 2000f;
            Vector3 source = camPos + radial * (sourceAlt - weather.CameraAltitudeAsl);
            source += UnityEngine.Random.insideUnitSphere * cfg.lightning.spawnRange;

            Vector3 ground = GetGroundPosition(camPos, radial);
            Vector3 target = ground + UnityEngine.Random.insideUnitSphere * cfg.lightning.targetRange;
            // 落点别低于地面太多(否则闪电会插进地里)
            target += radial * Mathf.Max(0f, Vector3.Dot(ground - target, radial));

            CastBolt(source, target, cam);
        }

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

            // bolt 是根物体,不挂到相机下:挂在 inactive 的相机会让层级里的 bolt 也 inactive,自毁链随之断掉 → 永久残留。
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

            // 云底海拔(ASL):远雷的声源更接近"云底那一段通道";取不到云层带时给 0 = 只用落点距离。
            bool haveCloudBand = TryGetCloudBand(out float cloudBottom, out float cloudTop);
            float cloudBaseAsl = haveCloudBand ? cloudBottom : 0f;
            Camera observerCam = cam;
            // 观测者处的地表法线:声程分解要用它区分"垂直/水平"(world Y/Z 不能当水平用,参考系可能被旋转)
            Vector3 radialUp = GetRadialUp(camPos);

            bolt.OnBoltLanded = () =>
            {
                // 距离必须在这里按"落点 ↔ 观测者"现算:bolt 的 transform.position 是云里的起点,不是落点。
                // 观测者位置在**回调里**取:动画要跑约 0.2s,这期间相机可能已经移动(取不到就退回 camPos)。
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
            // 这里的距离是起手时的,仅供参考;真正决定延迟/音色的是雷声那一刻现算的距离(见 PlayThunder 日志)。
            Mod.Log($"Volken:LightningModule bolt #{_boltCount} from {origin} to {landing} " +
                    $"(len={Vector3.Distance(origin, landing):F0}m strikeDist~{Vector3.Distance(camPos, landing):F0}m)");
        }

        /// 雷声全流程:① 声程 = 按 <c>thunderSourceBlend</c> 在"落点距离"与"云底距离"之间取(观测者 = 相机,AudioListener 所在处);
        /// ② 声速取 <c>AtmosphereSample.SpeedOfSound</c>,真空 / 超大气顶时恒为 0 → **必须**回退 <c>thunderFallbackSpeedOfSound</c>;
        /// ③ <c>strikeDist &lt;= thunderNearDistance</c> 选近雷素材,组为空时自动落到另一组。
        private void PlayThunderForStrike(Vector3 strikePos, Vector3 observerPos, float cloudBaseAsl,
                                          Vector3 radialUp, VolkenWeatherConfig cfg)
        {
            var lcfg = cfg.lightning;

            // ---- ① 距离 / 声程 ----
            float strikeDist = Vector3.Distance(observerPos, strikePos);
            float pathDist = strikeDist;
            if (lcfg.thunderSourceBlend > 0f)
            {
                // 云底那一端的声程:沿**地表法线**分解,不能用 world Y/Z 当"水平"(参考系可能被旋转)。
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

            // ---- 延迟 / 音量(thunderDistanceAttenuation = 0 → 回到"恒 0.05s + 恒音量") ----
            float at = Mathf.Clamp01(lcfg.thunderDistanceAttenuation);
            float delay = Mathf.Lerp(lcfg.thunderDelay, pathDist / c, at);
            float volume = lcfg.thunderVolume * Mathf.Lerp(1f, DistanceVolume(strikeDist, lcfg.thunderNearDistance), at);

            // ---- ③ near / far ----
            bool near = strikeDist <= lcfg.thunderNearDistance;

            PlayThunder(strikePos, volume, delay, near, strikeDist, pathDist, c);
        }

        /// 观测者(飞船)当前所在处的声速(m/s);取不到 / 无物理大气 / 超出大气顶 → 返回 0(调用方兜底);用 <c>AtmosphereSample.SpeedOfSound</c>(与 HUD 同源,**不要自己按温度算**)。
        private static float GetSpeedOfSound()
        {
            try
            {
                var flightData = Game.Instance?.FlightScene?.CraftNode?.CraftScript?.FlightData;
                if (flightData == null) return 0f;

                // struct(按值返回),没有空引用风险;无物理大气 / 高度 ≥ 大气顶时保持默认 0。
                return flightData.AtmosphereSample.SpeedOfSound;
            }
            catch
            {
                return 0f;
            }
        }

        /// 雷声音量随距离的额外衰减(只在 <c>thunderDistanceAttenuation &gt; 0</c> 时叠加);用 <c>sqrt(near/d)</c> 而非 <c>near/d</c> —— 线性音量再叠 AudioSource 的对数 rolloff 会把远雷压到听不见。
        private static float DistanceVolume(float distance, float nearDistance)
        {
            float inner = Mathf.Max(1f, nearDistance * 0.5f);
            if (distance <= inner) return 1f;
            return Mathf.Clamp(Mathf.Sqrt(inner / distance), 0.25f, 1f);
        }

        /// 按当前配置刷新某个通道的 3D 参数。<c>spread</c> = 声源张角:近雷是一个点(60°),远雷是头顶一大片(160°)。
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

            if (GamePause.IsPaused)   // 暂停期间不新起雷声(手动触发/暂停中落雷都挡在这里)
            {
                Mod.Log($"Volken:LightningModule thunder skipped (game paused, dist={strikeDist:F0}m)");
                return;
            }

            if (near && nearCount == 0) near = false;
            else if (!near && farCount == 0) near = true;
            var clips = near ? _nearClips : _farClips;

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
                int voice = _nextVoice;
                var src = _thunderVoices[voice];
                _nextVoice = (_nextVoice + 1) % _thunderVoices.Length;

                float nearDistance = VolkenWeather.Instance?.Config?.lightning?.thunderNearDistance ?? 2000f;
                ConfigureVoice(src, near, nearDistance);

                src.transform.position = position;
                src.clip = clip;
                src.volume = Mathf.Clamp01(volume);
                // PlayScheduled 用 dspTime:真实时间,不受 Time.timeScale / 游戏倍速影响(声音按真实秒到达)。
                double startTime = AudioSettings.dspTime + Mathf.Max(0f, delay);
                src.PlayScheduled(startTime);
                _voiceStartDsp[voice] = startTime;   // 供暂停时判断"这条还没响"
                _voicePending[voice] = 0f;
                _voicePaused[voice] = false;

                Mod.Log($"Volken:LightningModule thunder [{(near ? "near" : "far")}] " +
                        $"dist={strikeDist:F0}m path={pathDist:F0}m c={speedOfSound:F0}m/s " +
                        $"delay={delay:F2}s vol={volume:F2} clip={clip.name}");
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:LightningModule PlayThunder failed: " + ex.Message);
            }
        }

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

        /// 相机正下方的地面世界坐标(行星按正球近似,忽略局部地形起伏)。
        /// **不要用 AGL 修正** —— 那是飞船所在点的地形高度,会把落雷点横向带偏。已知限制:山峰上的落雷可能插进山体。
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

        /// 取云层高度带 [底, 顶](米, ASL);层高/层厚的遍历统一走 <see cref="CloudConfig.TryGetBand"/> 这一份实现。
        private static bool TryGetCloudBand(out float bottom, out float top)
        {
            bottom = 0f;
            top = 0f;

            var cloudCfg = VolkenClouds.Instance?.MainLayer?.config;
            if (cloudCfg == null || !cloudCfg.enabled) return false;

            return cloudCfg.TryGetBand(out bottom, out top);
        }

        private void OnDestroy()
        {
            SetActive(false);
        }
    }
}
