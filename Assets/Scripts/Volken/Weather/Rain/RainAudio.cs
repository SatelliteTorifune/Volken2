using System;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;

namespace Volken.Weather
{
    /// <summary>雨声:两组循环素材(light/heavy)按雨量等功率交叉淡化,整体淡入淡出;输入取 <see cref="RainParticles"/> 静态字段,不自己读配置。</summary>
    ///  循环靠两个 AudioSource 乒乓 + <c>PlayScheduled</c> 提前排段:只在**已停止**的那条上改 clip(改正在播的会立刻切歌 = 爆音);时间轴用 <c>AudioSettings.dspTime</c>。
    ///  淡变只算**目标值**、<c>MoveTowards</c> 单调推进 —— 不在每帧路径里重置进度,否则淡入永远回 0。
    public class RainAudio : MonoBehaviour
    {
        private const string LightPathFormat = "Assets/Scripts/Volken/Weather/Rain/Audio/volkenRain-light-{0}.wav";
        private const string HeavyPathFormat = "Assets/Scripts/Volken/Weather/Rain/Audio/volkenRain-heavy-{0}.wav";
        private const int ClipsPerSet = 3;

        private const float ScheduleLead = 0.25f;   // 下一段的排程提前量(秒;太小会因掉帧断音)
        private const float StartLead = 0.05f;      // 起步排程提前量(秒;避免 dspTime 已过 = 起播咔哒声)
        private const float VoiceStopMix = 0.001f;  // 低于它停源(省 CPU)
        private const float VoiceStartMix = 0.004f; // 高于它才起(与上一行构成迟滞,防边界反复起停)
        private const float IntensityAmountScale = 200000f;   // 雨量归一化基准:出厂 10 万粒 × 强度 1.0 = 0.5
        private const int MaxLoadAttempts = 3;                // 素材加载重试上限(本组件在 OnModLoaded 就建,一次失败不该永久静音)
        private const float LoadRetryInterval = 1.0f;         // 重试间隔(秒)

        public static bool Enabled = true;          // 雨声总开关(关掉只是不响,雨照下)
        public static float FadeInTime = 2.0f;      // 淡入时长(秒)
        public static float FadeOutTime = 1.2f;     // 淡出时长(秒)
        public static float IntensitySmooth = 1.5f; // 雨量平滑速率(每秒;太小则拉滑块听不出变化)
        public static float IntensityGain = 0.4f;   // 小雨相对暴雨的音量落差(0 = 一样响;0.4 = 小雨只有 60%)
        public static int RepeatsPerClip = 3;       // 每条素材连播几遍再换下一条(素材本身是循环,同条之间**无缝**续播,不淡化)
        public static float CrossfadeTime = 2.0f;   // 换素材的交叉淡化时长(秒;实际会被压到较短素材的 40% 以内)

        public static RainAudio Instance { get; private set; }

        public static bool AssetsReady;
        public static string AssetsStatus = "未加载";

        public static float LastMaster;             // 主音量(已含淡变包络与配置音量)
        public static float LastFade;               // 淡变包络(0..1)
        public static float LastIntensity;          // 当前 light→heavy 混合(0..1)
        public static float LastTargetIntensity;
        public static float LastLightVolume;
        public static float LastHeavyVolume;
        public static bool LastGateOpen;            // 本帧"该不该有雨声"
        public static bool LastPaused;              // 本帧游戏是否暂停(暂停时雨声一起停)
        public static int SegmentCount;             // 累计排程段数(连播 + 换素材)
        public static int ClipChanges;              // 累计换素材次数(每次带一次交叉淡化)
        public static int StopCount;

        /// <summary>一组素材的乒乓状态:两条 AudioSource 交替,<see cref="ActiveIndex"/> 在播、<see cref="PendingIndex"/> 已排待播。</summary>
        private sealed class LoopGroup
        {
            public readonly List<AudioClip> Clips = new List<AudioClip>();
            public AudioSource A;
            public AudioSource B;

            public int ActiveIndex;        // 正在播的那条(0 = A,1 = B)
            public int PendingIndex = -1;  // 已排程、尚未开播的那条
            public double PendingStart;
            public double PendingLength;
            public double SegmentStart;    // 正在播的那段窗口(dsp 时间 + 秒)
            public double SegmentLength;
            public int OutgoingIndex = -1; // 交叉淡化中正在退场的那条
            public double FadeStart;
            public double FadeDuration;
            public bool Running;           // 是否已排过程
            public bool ActivePaused;      // 暂停时被 Pause 住的两条(恢复只 UnPause 这两条)
            public bool OutgoingPaused;
            public int LastClipIndex = -1; // 上次用的素材下标(轮换时不重复)
            public int RepeatsLeft;        // 当前素材还要再排几遍

            public AudioSource SourceAt(int index) { return index == 0 ? A : (index == 1 ? B : null); }
            public static int Other(int index) { return 1 - index; }
        }

        private readonly LoopGroup _light = new LoopGroup();
        private readonly LoopGroup _heavy = new LoopGroup();

        private int _loadAttempts;
        private float _nextLoadAttemptTime;
        private bool _preloadKicked;
        private bool _paused;
        private double _pauseDsp;               // 进入暂停时的 dsp 时间(恢复时把排程整体顺延)
        private float _fade;                    // 0..1 淡入淡出包络
        private float _intensity = 0.5f;        // 当前 light→heavy 混合
        private bool _lastGateOpen;
        private int _transitions;

        /// <summary>把雨声挂到常驻物体上(游戏 = VolkenWeather 的 DontDestroyOnLoad host;编辑器预览 = 预览台);已存在则复用。</summary>
        public static RainAudio Ensure(GameObject host)
        {
            if (Instance != null) return Instance;      // 只允许一份:多处挂载时后到的自毁(见 Awake)
            if (host == null) return null;
            var existing = host.GetComponent<RainAudio>();
            if (existing != null) return existing;
            return host.AddComponent<RainAudio>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            LoadAssets();
            CreateVoices();
        }

        private void OnDisable()
        {
            StopGroup(_light);
            StopGroup(_heavy);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            StopGroup(_light);
            StopGroup(_heavy);
        }

        /// <summary>是否 6 条素材都到位(缺任何一条都还值得再试一次)。</summary>
        private bool AllClipsLoaded
        {
            get { return _light.Clips.Count >= ClipsPerSet && _heavy.Clips.Count >= ClipsPerSet; }
        }

        private void LoadAssets()
        {
            if (AllClipsLoaded) return;
            if (_loadAttempts >= MaxLoadAttempts) return;

            float now = Time.realtimeSinceStartup;
            if (_loadAttempts > 0 && now < _nextLoadAttemptTime) return;   // 重试之间留间隔,别每帧砸加载器
            _loadAttempts++;
            _nextLoadAttemptTime = now + LoadRetryInterval;

            // 重试时重建列表,避免把上一轮的条目累加两遍
            _light.Clips.Clear();
            _heavy.Clips.Clear();
            LoadClipSet(LightPathFormat, _light.Clips, "light");
            LoadClipSet(HeavyPathFormat, _heavy.Clips, "heavy");

            AssetsReady = _light.Clips.Count > 0 || _heavy.Clips.Count > 0;
            AssetsStatus = string.Format("light={0}/{1} heavy={2}/{3}",
                _light.Clips.Count, ClipsPerSet, _heavy.Clips.Count, ClipsPerSet);

            if (AllClipsLoaded)
            {
                Mod.Log(string.Format("Volken:RainAudio assets ready: {0} (第 {1} 次尝试)", AssetsStatus, _loadAttempts));
            }
            else if (_loadAttempts >= MaxLoadAttempts)
            {
                AssetsStatus += "(试了 " + MaxLoadAttempts + " 次)";
                Mod.Log("Volken:RainAudio 没加载到全部雨声素材 —— " +
                        "检查 ModData.asset._otherAssets 是否含 6 个 volkenRain-*.wav,然后重建 asset bundle;当前: " + AssetsStatus);
            }
            else
            {
                Mod.Diag("Volken:RainAudio 素材未齐({0},第 {1} 次尝试)→ {2}s 后重试", AssetsStatus, _loadAttempts, LoadRetryInterval);
            }
        }

        /// <summary>载入一组素材;单条缺失 = 静默跳过(素材没打进 bundle 属预期情况,不刷红字)。</summary>
        private static void LoadClipSet(string pathFormat, List<AudioClip> into, string label)
        {
            for (int i = 1; i <= ClipsPerSet; i++)
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
                    Mod.Log(string.Format("Volken:RainAudio {0} clip {1} load failed: {2}", label, i, ex.Message));
                }
            }
        }

        private void CreateVoices()
        {
            try
            {
                _light.A = AddVoice();
                _light.B = AddVoice();
                _heavy.A = AddVoice();
                _heavy.B = AddVoice();
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainAudio AudioSource creation failed: " + ex.Message);
            }
        }

        /// <summary>环境底噪:2D、无多普勒、不过混响区;循环靠乒乓排程而不是 <c>loop=true</c>(见类注释)。</summary>
        private AudioSource AddVoice()
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;              // 2D:雨是环境底噪,不随相机位置/朝向变化
            src.dopplerLevel = 0f;
            src.bypassEffects = true;
            src.bypassReverbZones = true;
            src.volume = 0f;
            return src;
        }

        private void Update()
        {
            try
            {
                // 常驻宿主:飞行外只有"还在淡出"或"素材还没加载完"才需要每帧跑 → 其余时间整体停表;编辑器预览台(独立模式)不受影响
                if (!RainParticles.StandaloneMode && !Game.InFlightScene && _fade <= 0f
                    && (_loadAttempts >= MaxLoadAttempts || AllClipsLoaded)) return;

                Tick(Time.unscaledDeltaTime);
            }
            catch (Exception ex)
            {
                Mod.LogThrottled("RainAudio", ex);
            }
        }

        /// <summary>雨量 → light/heavy 混合系数(0 = 小雨,1 = 暴雨)。出厂 10 万粒 × 强度 1.0 → 0.5(各半);20 万粒或强度 2 → 1.0。</summary>
        public static float ComputeIntensity()
        {
            float amountFactor = RainParticles.Capacity / IntensityAmountScale;
            return Mathf.Clamp01(RainParticles.Strength * amountFactor);
        }

        /// <summary>暂停一组:待播的那条必须停(绝对 dsp 排程会在暂停期间抢跑);在播的两条 Pause 住,恢复后接着播/接着淡。</summary>
        private static void SuspendGroup(LoopGroup g)
        {
            if (!g.Running) return;

            if (g.PendingIndex >= 0) { StopVoice(g, g.PendingIndex); g.PendingIndex = -1; }

            try
            {
                var active = g.SourceAt(g.ActiveIndex);
                if (active == null || !active.isPlaying) { StopGroup(g); return; }   // 还在 StartLead 窗口里没开播 → 停掉,恢复时重排
                active.Pause();
                g.ActivePaused = true;
            }
            catch { StopGroup(g); return; }

            if (g.OutgoingIndex >= 0)
            {
                try
                {
                    var outgoing = g.SourceAt(g.OutgoingIndex);
                    if (outgoing != null && outgoing.isPlaying) { outgoing.Pause(); g.OutgoingPaused = true; }
                }
                catch { }
            }
        }

        /// <summary>恢复一组:暂停期间 dsp 时钟照走,所以把排程时间整体顺延,再 UnPause 之前暂停的那两条。</summary>
        private static void ResumeGroup(LoopGroup g, double pausedFor)
        {
            if (!g.Running) return;
            g.SegmentStart += pausedFor;
            if (g.PendingIndex >= 0) g.PendingStart += pausedFor;
            if (g.OutgoingIndex >= 0) g.FadeStart += pausedFor;

            if (g.ActivePaused)
            {
                g.ActivePaused = false;
                try { var a = g.SourceAt(g.ActiveIndex); if (a != null) a.UnPause(); } catch { }
            }
            if (g.OutgoingPaused)
            {
                g.OutgoingPaused = false;
                try { var o = g.SourceAt(g.OutgoingIndex); if (o != null) o.UnPause(); } catch { }
            }
        }

        private void Tick(float dt)
        {
            if (!AllClipsLoaded) LoadAssets();     // 资产晚到/缺失:有限次重试(Awake 那一刻资源加载器可能还没就绪)
            if (!AssetsReady) return;
            if (dt < 0f) dt = 0f;

            // ---- 游戏暂停 → 雨声跟着停(包络与排程都冻结) ----
            bool paused = GamePause.IsPaused;
            if (paused != _paused)
            {
                _paused = paused;
                LastPaused = paused;
                if (paused)
                {
                    _pauseDsp = AudioSettings.dspTime;
                    SuspendGroup(_light);
                    SuspendGroup(_heavy);
                }
                else
                {
                    double pausedFor = Math.Max(0.0, AudioSettings.dspTime - _pauseDsp);
                    ResumeGroup(_light, pausedFor);
                    ResumeGroup(_heavy, pausedFor);
                }
                Mod.Log("RainAudio: {0}", paused ? "游戏暂停 → 雨声暂停" : "游戏恢复 → 雨声继续");
            }
            if (paused) return;

            float now = Time.realtimeSinceStartup;

            // ---- 门控:该不该有雨声 ----
            bool gateOpen = Enabled
                            && RainParticles.Enabled
                            && RainParticles.Volume > VoiceStopMix;

            float altFade = 1f;
            if (gateOpen)
            {
                altFade = Mathf.Clamp01(RainParticles.LastAltitudeFade * RainParticles.LastWaterFade);   // 高度 + 水下闸门:雨被压掉时声音一起走
                if (altFade <= 0.001f) gateOpen = false;

                //  不要读一次就缓存 —— 离场要停、回场要能再起;独立模式(编辑器预览)没有天气系统,跳过
                if (gateOpen && !RainParticles.StandaloneMode)
                {
                    var w = VolkenWeather.Instance;
                    if (w != null && !w.IsActive) gateOpen = false;
                }
            }

            if (gateOpen != _lastGateOpen)
            {
                _lastGateOpen = gateOpen;
                _transitions++;
                Mod.Log("RainAudio: 门控 {0} (enabled={1} rainEnabled={2} rainVol={3:F2} altFade={4:F3} → 目标包络 {5})",
                    gateOpen ? "开 → 淡入" : "关 → 淡出", Enabled, RainParticles.Enabled,
                    RainParticles.Volume, altFade, gateOpen ? 1f : 0f);
            }

            // 首次出声时拉起素材数据(preloadAudioData=0 不会自动预载;不拉会吃掉第一声;只在要出声时拉以省内存)
            if (gateOpen && !_preloadKicked)
            {
                _preloadKicked = true;
                int started = PreloadGroup(_light) + PreloadGroup(_heavy);
                if (started > 0) Mod.Log("RainAudio: 首次出声 → 拉起 {0} 条素材数据(preloadAudioData=0)", started);
            }

            // ---- 目标值:只算目标,进度单调推进 ----
            float targetFade = gateOpen ? 1f : 0f;
            float fadeDuration = targetFade > _fade ? FadeInTime : FadeOutTime;
            _fade = Mathf.MoveTowards(_fade, targetFade, dt / Mathf.Max(0.05f, fadeDuration));

            float targetIntensity = ComputeIntensity();
            _intensity = Mathf.MoveTowards(_intensity, targetIntensity, Mathf.Max(0.01f, IntensitySmooth) * dt);

            // ---- 混音:等功率交叉淡化 ----
            float volumeCfg = Mathf.Clamp01(RainParticles.Volume);
            float gain = Mathf.Lerp(1f - Mathf.Clamp01(IntensityGain), 1f, _intensity);
            float master = _fade * volumeCfg * gain;

            float lightMix = Mathf.Cos(_intensity * Mathf.PI * 0.5f);
            float heavyMix = Mathf.Sin(_intensity * Mathf.PI * 0.5f);

            // 只有一组素材 → 铺满,不留半边空
            if (_heavy.Clips.Count == 0) { lightMix = 1f; heavyMix = 0f; }
            else if (_light.Clips.Count == 0) { lightMix = 0f; heavyMix = 1f; }

            TickGroup(_light, master * lightMix);
            TickGroup(_heavy, master * heavyMix);

            LastGateOpen = gateOpen;   // 以下为诊断快照
            LastFade = _fade;
            LastMaster = master;
            LastIntensity = _intensity;
            LastTargetIntensity = targetIntensity;
            LastLightVolume = master * lightMix;
            LastHeavyVolume = master * heavyMix;

        }

        /// <summary>推进一组素材的排程与音量:同一素材连播 <see cref="RepeatsPerClip"/> 遍(无缝),换素材时交叉淡化。</summary>
        private void TickGroup(LoopGroup g, float volume)
        {
            if (g.A == null || g.B == null) return;
            if (g.Clips.Count == 0) { StopGroup(g); return; }

            float v = Mathf.Clamp01(volume);
            bool wantRunning = v > (g.Running ? VoiceStopMix : VoiceStartMix);   // 迟滞见两个阈值常量
            if (!wantRunning)
            {
                StopGroup(g);
                return;
            }

            double now = AudioSettings.dspTime;

            if (!g.Running) StartSegment(g, now + StartLead);

            // 待播段到点 → 交接成"正在播"(音量前一帧就写好了,交接本身不出声)
            if (g.PendingIndex >= 0 && now >= g.PendingStart)
            {
                g.ActiveIndex = g.PendingIndex;
                g.SegmentStart = g.PendingStart;
                g.SegmentLength = g.PendingLength;
                g.PendingIndex = -1;
            }

            SetGroupVolumes(g, v, now);
            if (!g.Running) return;

            double boundary = g.SegmentStart + g.SegmentLength;

            // 排下一段的时机:换素材要整段交叉淡化,必须提前 CrossfadeTime 就动手(否则淡化被压成一小截)
            double lead = g.RepeatsLeft > 0 ? ScheduleLead : Math.Max(ScheduleLead, (double)CrossfadeTime + 0.05);

            // 当前段已开始、离结束不足一个 lead、且没有待播段 → 排下一段(同素材连播,或换素材淡化)
            if (g.PendingIndex < 0 && now >= g.SegmentStart && boundary - now <= lead)
            {
                if (g.RepeatsLeft > 0) ScheduleRepeat(g, boundary);
                else ScheduleClipChange(g, boundary);
                return;
            }

            // 掉帧把排程窗口整个错过 → 重来(宁可一次极短断音,也不要永久静音)
            if (g.PendingIndex < 0 && now > boundary + 0.1)
            {
                Mod.Diag("RainAudio: 排程掉队(now={0:F2}s boundary={1:F2}s)→ 重启段", now, boundary);
                StartSegment(g, now + StartLead);
            }
        }

        private static int RepeatBudget { get { return Mathf.Max(1, RepeatsPerClip) - 1; } }

        /// <summary>两条声源的音量:正在播的按交叉淡化权重,待播的先写好(它一开播就是正确音量),其余 0。</summary>
        private static void SetGroupVolumes(LoopGroup g, float v, double now)
        {
            float inWeight = 1f, outWeight = 0f;
            if (g.OutgoingIndex >= 0)
            {
                double t = (now - g.FadeStart) / Math.Max(0.01, g.FadeDuration);
                if (t >= 1.0) { StopVoice(g, g.OutgoingIndex); g.OutgoingIndex = -1; }
                else if (t > 0.0) { inWeight = Mathf.Sin((float)t * Mathf.PI * 0.5f); outWeight = Mathf.Cos((float)t * Mathf.PI * 0.5f); }
                else { inWeight = 0f; outWeight = 1f; }
            }

            for (int i = 0; i < 2; i++)
            {
                float w = g.OutgoingIndex == i ? outWeight
                        : (i == g.ActiveIndex || i == g.PendingIndex) ? inWeight
                        : 0f;
                SetVoiceVolume(g, i, v * w);
            }
        }

        private void StartSegment(LoopGroup g, double startDsp)
        {
            var clip = PickClip(g);
            if (clip == null) { StopGroup(g); return; }

            var src = g.SourceAt(g.ActiveIndex);
            var other = g.SourceAt(LoopGroup.Other(g.ActiveIndex));
            if (other != null && other.isPlaying) other.Stop();   // 乒乓的另一条必须先停 —— 改正在播的 clip = 切歌
            if (src.isPlaying) src.Stop();

            src.clip = clip;
            src.PlayScheduled(startDsp);

            g.PendingIndex = -1;
            g.OutgoingIndex = -1;
            g.ActivePaused = false;
            g.OutgoingPaused = false;
            g.SegmentStart = startDsp;
            g.SegmentLength = Mathf.Max(0.05f, clip.length);
            g.RepeatsLeft = RepeatBudget;
            g.Running = true;
            SegmentCount++;
        }

        /// <summary>同一条素材再排一遍:素材本身是循环,首尾相接即可,**不做淡化**(淡化反而出现音量凹陷)。</summary>
        private void ScheduleRepeat(LoopGroup g, double boundary)
        {
            var active = g.SourceAt(g.ActiveIndex);
            var clip = active != null ? active.clip : null;
            if (clip == null) { StartSegment(g, boundary); return; }

            int idx = LoopGroup.Other(g.ActiveIndex);
            var src = g.SourceAt(idx);
            if (src.isPlaying) src.Stop();
            src.clip = clip;
            src.PlayScheduled(boundary);

            g.PendingIndex = idx;
            g.PendingStart = boundary;
            g.PendingLength = Mathf.Max(0.05f, clip.length);
            g.RepeatsLeft--;
            SegmentCount++;
        }

        /// <summary>换素材:新素材提前 <see cref="CrossfadeTime"/> 秒起播,旧素材同时等功率淡出。</summary>
        private void ScheduleClipChange(LoopGroup g, double boundary)
        {
            var clip = PickClip(g);
            if (clip == null) { StopGroup(g); return; }

            double newLength = Mathf.Max(0.05f, clip.length);
            double fade = Mathf.Clamp(CrossfadeTime, 0.05f, (float)Math.Min(g.SegmentLength, newLength) * 0.4f);
            double start = boundary - fade;
            double now = AudioSettings.dspTime;
            if (start < now + 0.01) start = now + 0.01;          // 掉帧兜底:别把新段排到过去
            if (boundary - start < 0.05) start = boundary - 0.05;

            int idx = LoopGroup.Other(g.ActiveIndex);
            var src = g.SourceAt(idx);
            if (src.isPlaying) src.Stop();
            src.clip = clip;
            src.PlayScheduled(start);

            g.PendingIndex = idx;
            g.PendingStart = start;
            g.PendingLength = newLength;
            g.OutgoingIndex = g.ActiveIndex;      // 旧的那条退场
            g.FadeStart = start;
            g.FadeDuration = Math.Max(0.05, boundary - start);
            g.RepeatsLeft = RepeatBudget;
            SegmentCount++;
            ClipChanges++;
        }

        private static void SetVoiceVolume(LoopGroup g, int index, float volume)
        {
            var src = g.SourceAt(index);
            if (src != null) src.volume = Mathf.Clamp01(volume);
        }

        private static void StopVoice(LoopGroup g, int index)
        {
            var src = g.SourceAt(index);
            try { if (src != null) src.Stop(); } catch { }
        }

        private AudioClip PickClip(LoopGroup g)
        {
            var list = g.Clips;
            if (list.Count == 0) return null;
            int idx = 0;
            if (list.Count > 1)
            {
                idx = UnityEngine.Random.Range(0, list.Count);
                if (idx == g.LastClipIndex) idx = (idx + 1) % list.Count;
            }
            g.LastClipIndex = idx;
            var clip = list[idx];
            EnsureClipLoaded(clip);
            return clip;
        }

        /// <summary>请求把一组素材的音频数据载进内存;返回本次真正发起加载的条数。</summary>
        private static int PreloadGroup(LoopGroup g)
        {
            int started = 0;
            for (int i = 0; i < g.Clips.Count; i++)
            {
                var c = g.Clips[i];
                try
                {
                    if (c == null || c.loadState == AudioDataLoadState.Loaded) continue;
                    if (c.LoadAudioData()) started++;
                }
                catch { }
            }
            return started;
        }

        private static void EnsureClipLoaded(AudioClip clip)
        {
            try
            {
                if (clip == null || clip.loadState == AudioDataLoadState.Loaded) return;
                clip.LoadAudioData();
            }
            catch { }
        }

        private static void StopGroup(LoopGroup g)
        {
            if (!g.Running) return;
            try
            {
                if (g.A != null) g.A.Stop();
                if (g.B != null) g.B.Stop();
            }
            catch { }
            g.Running = false;
            g.PendingIndex = -1;
            g.OutgoingIndex = -1;
            g.ActivePaused = false;
            g.OutgoingPaused = false;
            StopCount++;
        }

        /// <summary>面板状态行(紧凑:数值 + 短标签)。</summary>
        public static string StatsLine()
        {
            if (!AssetsReady) return "雨声未加载 — " + AssetsStatus + "(检查 bundle 里的 6 个 wav)";
            if (!Enabled) return "雨声已关闭(音频总开关)";
            if (LastPaused) return "雨声已暂停(游戏暂停中)";
            if (LastFade <= 0.001f) return string.Format("雨声静音  vol {0:F2}  ({1})", LastMaster, AssetsStatus);

            return string.Format(
                "vol {0:F2}  小雨 {1:F2} / 暴雨 {2:F2}  混合 {3:F2}  淡入淡出 {4:F2}  {5}",
                LastMaster, LastLightVolume, LastHeavyVolume, LastIntensity, LastFade, AssetsStatus);
        }

        /// <summary>把完整状态写进 Player.log。</summary>
        public static void DiagStatus()
        {
            try
            {
                Mod.Diag("RainAudio STATUS: enabled={0} paused={1} assets='{2}' gate={3} fade={4:F2} master={5:F2} mix={6:F2}(target {7:F2}) light={8:F2} heavy={9:F2} segments={10} clipChanges={11} stops={12}",
                    Enabled, LastPaused, AssetsStatus, LastGateOpen, LastFade, LastMaster, LastIntensity,
                    LastTargetIntensity, LastLightVolume, LastHeavyVolume, SegmentCount, ClipChanges, StopCount);
                Mod.Diag("RainAudio input: RainParticles enabled={0} volume={1:F2} strength={2:F2} capacity={3} altFade={4:F3} standalone={5} | fadeIn={6:F1}s fadeOut={7:F1}s smooth={8:F1}/s gain={9:F2} repeats={10} crossfade={11:F1}s",
                    RainParticles.Enabled, RainParticles.Volume, RainParticles.Strength, RainParticles.Capacity,
                    RainParticles.LastAltitudeFade, RainParticles.StandaloneMode,
                    FadeInTime, FadeOutTime, IntensitySmooth, IntensityGain, RepeatsPerClip, CrossfadeTime);
                Mod.Diag("RainAudio ui: {0}", StatsLine());
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainAudio.DiagStatus ERROR: " + ex.Message);
            }
        }
    }
}
