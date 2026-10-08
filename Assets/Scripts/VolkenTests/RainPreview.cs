using System;
using System.Collections.Generic;
using UnityEngine;
using Volken.Core;
using Volken.Weather;

namespace Volken.Tests
{
    /// <summary>
    /// 编辑器内预览台(不打包、不进游戏):跑的就是正式那份 compute/shader/驱动代码。
    /// **测不到**:软粒子(需云的深度图)、换帧重定位、真实海拔、天气面板与配置持久化。
    /// </summary>
    [AddComponentMenu("Volken/Rain Preview (编辑器预览台)")]
    public class RainPreview : MonoBehaviour
    {
        /// <summary>PlayerPrefs 键:下次 Play 是否自动生成预览台(仅编辑器生效)。</summary>
        private const string AutoStartPref = "Volken.RainPreview.AutoStart";

        /// <summary>PlayerPrefs 键:记住上次调好的参数。</summary>
        private const string ParamsPref = "Volken.RainPreview.Params";

        [Header("相机与场景")]
        [Tooltip("相机;留空则用本物体的 Camera / 主相机 / 自动新建一个")]
        public Camera targetCamera;

        [Tooltip("Play 时自动搭地面 + 参照方块(判断尺度/遮挡用)")]
        public bool buildEnvironment = true;

        [Header("自由飞行")]
        [Tooltip("基础飞行速度(米/秒);滚轮可调")]
        public float moveSpeed = 40f;
        [Tooltip("按住 Shift 的加速倍率")]
        public float boostMultiplier = 5f;
        [Tooltip("鼠标视角灵敏度")]
        public float lookSensitivity = 2.2f;

        [Header("雨参数(与游戏内天气面板同一批字段)")]
        public bool rainEnabled = true;
        public float amount = 100000f;
        public float domainRadius = 50f;
        public float fallSpeed = 15f;
        public float streakLength = 2.5f;
        public float streakWidth = 0.1f;
        public float stretchAmount = 0.045f;
        public float stretchLimit = 3.5f;
        public float edgeFade = 0.2f;
        public float softParticles = 1f;
        public float tailFalloff = 0.9f;
        public float brightness = 0f;
        [Range(0f, 1f)] public float transparency = 0f;
        public bool streamMode = true;
        public bool respawnMirror = false;
        public float ceilingAltitude = 12000f;
        public float ceilingBand = 0.4f;
        public bool underwaterGate = true;    // 水下不下雨(海平面以下)
        public float underwaterFade = 2f;     // 米;海平面 → 水面下此深度内线性淡出到 0
        public bool assumeWater = false;      // 预览专用:独立模式拿不到行星,假装有水才能验水下闸门
        public float rainStrength = 1f;      // 雨强度(与粒子数一起决定小雨/暴雨音效混合)
        public float rainVolume = 0.5f;      // 雨声音量
        public int rainRepeats = 3;          // 每条素材连播几遍再换下一条
        public float rainCrossfade = 2f;     // 换素材的交叉淡化时长(秒)
        public float distanceFade = 0.35f;   // 纵深:域内距离衰减(0 = 关)
        public float streakVariation = 0.5f; // 纵深:逐粒长度/宽度变化(0 = 全一样)
        public float streakRoll = 0f;        // 纵深:逐粒绕长轴滚转(默认 0;开了可能"面条")
        public bool adaptiveDomain = true;   // 域半径随相机速度自适应
        public float adaptiveMaxRadius = 120f; // 自适应半径上限(米)

        [Header("预览专用")]
        [Tooltip("手动喂给海拔闸门的相机海拔(编辑器里拿不到真实海拔)")]
        public bool overrideAltitude = false;
        public float altitudeOverrideValue = 0f;

        private RainParticles _rain;
        private float _yaw, _pitch;
        private bool _looking;
        private bool _panelOpen = true;
        private float _fps;
        private Vector2 _scroll;
        private bool _heavyDirty;      // 需要重建资源(容量/雨丝长宽)的改动,等松手再提交
        private bool _autoStart;
        private bool _paramsDirty;     // 参数变过 → 节流写进 PlayerPrefs(下次 Play 接着调)
        private float _lastSaveTime;
        private string _toast;         // 面板底部的一行提示(复制成功等)
        private float _toastUntil;
        private int _updateTicks;      // 自诊断:Update 跑了多少帧
        private int _seenTicks = -1;
        private bool _updateAlive = true;
        private int _frameCount;       // FPS:窗口内帧数(避免 1/delta 的极端值)
        private float _fpsWindowStart;
        private string _fatal;         // 异常信息 → 直接显示在面板上(不再静默 FPS 0)
        private bool _fatalLogged;

        /// <summary>勾过「自动启动」后,任意场景按 Play 都会自动生成预览台(仅编辑器生效,PlayerPrefs 记忆)。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            try
            {
                if (!Application.isEditor) return;
                if (PlayerPrefs.GetInt(AutoStartPref, 0) == 0) return;
                if (FindObjectOfType<RainPreview>() != null) return;
                var go = new GameObject("RainPreview(auto)");
                go.AddComponent<RainPreview>();
                UnityEngine.Debug.Log("[Volken] RainPreview 自动启动(PlayerPrefs " + AutoStartPref + "=1;面板里可关闭)");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Volken] RainPreview AutoSpawn: " + ex.Message);
            }
        }

        private void Awake()
        {
            try
            {
                // 编辑器里一律开"独立模式":雨完全不碰游戏 API(否则访问 Game.Instance 会触发游戏侧
                // 半初始化,刷出一堆 CelestialDatabase/SceneManager 之类的无关报错)。
                RainParticles.StandaloneMode = Application.isEditor;
                ResolveCamera();
                if (buildEnvironment) BuildEnvironment();
                EnsureRain();
                _autoStart = PlayerPrefs.GetInt(AutoStartPref, 0) == 1;
                LoadParams();      // 有记忆参数就先恢复(自动启动也能接着上次调)
                // 雨声(游戏里没有天气系统也能听;已存在则复用)
                RainAudio.Ensure(gameObject);
                PushHeavy();       // 首次:走 ApplyConfig(会建 buffer/网格)
            }
            catch (Exception ex)
            {
                _fatal = "Awake 异常: " + ex;
                UnityEngine.Debug.LogException(ex);
            }
        }

        private void OnDestroy()
        {
            try
            {
                SaveParams();
                RainParticles.DebugAltitudeOverride = float.NaN;
                RainParticles.Enabled = false;
                RainParticles.StandaloneMode = false;
            }
            catch { }
        }

        private void Update()
        {
            _updateTicks++;
            try
            {
                HandleLook();
                HandleMove();
                PushCheap();       // 便宜参数逐帧写(拖动即时反馈)
                if (_heavyDirty && !_leftHeld) PushHeavy();   // 松开左键(= 拖动结束)才重建资源
                if (_paramsDirty && Time.realtimeSinceStartup - _lastSaveTime > 1.5f) SaveParams();
                _lookDelta = Vector2.zero;   // 输入是"本帧累积",用完清零
                _scrollDelta = 0f;
            }
            catch (Exception ex)
            {
                // 异常不能每帧静默吞掉:存下来直接显示在面板上(否则只会看到 FPS 恒 0)
                _fatal = "Update 异常(每帧抛): " + ex.Message;
                if (!_fatalLogged) { _fatalLogged = true; UnityEngine.Debug.LogException(ex); }
            }

            // FPS:窗口内帧数 / 真实耗时(比 1/delta 稳,暂停时不会算出假值)
            _frameCount++;
            float now = Time.realtimeSinceStartup;
            if (now - _fpsWindowStart >= 0.5f)
            {
                _fps = _frameCount / Mathf.Max(1e-4f, now - _fpsWindowStart);
                _frameCount = 0;
                _fpsWindowStart = now;
            }
        }

        private void Toast(string msg)
        {
            _toast = msg;
            _toastUntil = Time.realtimeSinceStartup + 4f;
        }

        private void ResolveCamera()
        {
            if (targetCamera == null) targetCamera = GetComponent<Camera>();
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null)
            {
                var go = new GameObject("RainPreviewCamera");
                go.transform.SetParent(transform, false);
                targetCamera = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            targetCamera.clearFlags = CameraClearFlags.Skybox;
            targetCamera.farClipPlane = Mathf.Max(targetCamera.farClipPlane, 30000f);
            targetCamera.nearClipPlane = Mathf.Min(targetCamera.nearClipPlane, 0.1f);
            _yaw = targetCamera.transform.eulerAngles.y;
            _pitch = targetCamera.transform.eulerAngles.x;
            if (_pitch > 180f) _pitch -= 360f;
        }

        /// <summary>极简环境:地面 + 近/中/远参照方块,用于判断尺度与遮挡。不依赖任何 mod 资产。</summary>
        private void BuildEnvironment()
        {
            var root = new GameObject("RainPreviewEnv");
            root.transform.SetParent(transform, false);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(2000f, 1f, 2000f);   // Plane 默认 10m → 20km
            ApplySimpleMaterial(ground, new Color(0.22f, 0.26f, 0.2f));

            var sizes = new[] { 4f, 12f, 40f };
            var dists = new[] { 25f, 90f, 300f };
            for (int i = 0; i < sizes.Length; i++)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Ref_" + sizes[i];
                box.transform.SetParent(root.transform, false);
                box.transform.localScale = Vector3.one * sizes[i];
                box.transform.position = new Vector3(i * 60f - 60f, sizes[i] * 0.5f, dists[i]);
                ApplySimpleMaterial(box, new Color(0.45f, 0.45f, 0.5f));
            }

            if (FindObjectOfType<Light>() == null)
            {
                var lightGo = new GameObject("RainPreviewSun");
                lightGo.transform.SetParent(transform, false);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(38f, 145f, 0f);
            }
        }

        private static void ApplySimpleMaterial(GameObject go, Color color)
        {
            try
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return;
                var shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) return;
                renderer.sharedMaterial = new Material(shader) { color = color };
            }
            catch { }
        }

        private void EnsureRain()
        {
            if (targetCamera == null) return;
            _rain = targetCamera.GetComponent<RainParticles>();
            if (_rain == null) _rain = targetCamera.gameObject.AddComponent<RainParticles>();
            RainParticles.Enabled = rainEnabled;
        }

        /// <summary>便宜参数:逐帧直接写静态字段(Tick 每帧读取)→ 拖动滑块即时见效,零重建。</summary>
        private void PushCheap()
        {
            RainParticles.DomainRadius = Mathf.Clamp(domainRadius, 10f, 400f);
            RainParticles.FallSpeed = Mathf.Clamp(fallSpeed, 0.1f, 200f);
            RainParticles.StretchAmount = Mathf.Max(0f, stretchAmount);
            RainParticles.StretchLimit = Mathf.Clamp(stretchLimit, 1f, 20f);
            RainParticles.EdgeFade = Mathf.Clamp(edgeFade, 0f, 0.5f);
            RainParticles.InvFade = Mathf.Max(0f, softParticles);
            RainParticles.Falloff = Mathf.Clamp01(tailFalloff);
            RainParticles.Brightness = Mathf.Max(0f, brightness);
            RainParticles.Transparency = Mathf.Clamp01(transparency);
            RainParticles.StreamMode = streamMode;
            RainParticles.RespawnMirror = respawnMirror;
            RainParticles.CeilingAltitude = Mathf.Max(0f, ceilingAltitude);
            RainParticles.CeilingBand = Mathf.Clamp(ceilingBand, 0.02f, 1f);
            RainParticles.UnderwaterGate = underwaterGate;
            RainParticles.UnderwaterFade = Mathf.Clamp(underwaterFade, 0.1f, 50f);
            RainParticles.DebugAssumeWater = assumeWater;
            RainParticles.Strength = Mathf.Clamp(rainStrength, 0f, 4f);
            RainParticles.Volume = Mathf.Clamp01(rainVolume);
            RainAudio.RepeatsPerClip = Mathf.Max(1, rainRepeats);
            RainAudio.CrossfadeTime = Mathf.Clamp(rainCrossfade, 0.05f, 10f);
            RainParticles.DistanceFade = Mathf.Clamp01(distanceFade);
            RainParticles.StreakVariation = Mathf.Clamp01(streakVariation);
            RainParticles.StreakRoll = Mathf.Clamp01(streakRoll);
            RainParticles.AdaptiveDomain = adaptiveDomain;
            RainParticles.AdaptiveMaxRadius = Mathf.Clamp(adaptiveMaxRadius, 10f, 400f);
            RainParticles.DebugAltitudeOverride = overrideAltitude ? altitudeOverrideValue : float.NaN;
            RainParticles.Enabled = rainEnabled;
        }

        /// <summary>重参数:走 ApplyConfig(容量 → 重建 buffer;雨丝长宽 → 重建网格)。只在松手/按钮时调用。</summary>
        private void PushHeavy()
        {
            _heavyDirty = false;
            _paramsDirty = true;   // 提交过 = 参数变过 → 会节流存进 PlayerPrefs
            try
            {
                if (_rain == null) EnsureRain();
                var cfg = new VolkenWeatherConfig.RainSection
                {
                    enabled = rainEnabled,
                    amount = amount,
                    domainRadius = domainRadius,
                    fallSpeed = fallSpeed,
                    streakLength = streakLength,
                    streakWidth = streakWidth,
                    stretchAmount = stretchAmount,
                    stretchLimit = stretchLimit,
                    edgeFade = edgeFade,
                    softParticles = softParticles,
                    tailFalloff = tailFalloff,
                    brightness = brightness,
                    transparency = transparency,
                    streamMode = streamMode,
                    respawnMirror = respawnMirror,
                    ceilingAltitude = ceilingAltitude,
                    ceilingBand = ceilingBand,
                    underwaterGate = underwaterGate,
                    underwaterFade = underwaterFade,
                    strength = rainStrength,
                    volume = rainVolume,
                    adaptiveDomain = adaptiveDomain,
                    distanceFade = distanceFade,
                    streakVariation = streakVariation,
                };
                RainParticles.ApplyConfig(cfg);
                RainParticles.Enabled = rainEnabled;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Volken] RainPreview PushHeavy: " + ex.Message);
            }
        }

        //  **不要用旧版 UnityEngine.Input.***:本工程 activeInputHandler = 1,旧 API 每次调用都抛异常、
        //   Update 会在最前面中断(FPS 恒 0)。统一改用 IMGUI 事件(OnGUI 里的 Event.current)。

        private readonly HashSet<KeyCode> _keysHeld = new HashSet<KeyCode>();
        private Vector2 _lookDelta;      // 本帧右键拖拽的鼠标位移(像素)
        private float _scrollDelta;
        private bool _rightHeld;
        private bool _leftHeld;
        private Rect _panelRect = new Rect(10f, 40f, 450f, 0f);

        /// <summary>在 OnGUI 开头采集输入(IMGUI 事件)。右键拖拽/滚轮在面板外才消费,避免抢面板的鼠标。</summary>
        private void CollectInput(Event e)
        {
            if (e == null) return;
            bool overPanel = _panelOpen && _panelRect.Contains(e.mousePosition);
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 1) { _rightHeld = true; e.Use(); }
                    else if (e.button == 0) _leftHeld = true;
                    break;
                case EventType.MouseUp:
                    if (e.button == 1) { _rightHeld = false; e.Use(); }
                    else if (e.button == 0) _leftHeld = false;
                    break;
                case EventType.MouseDrag:
                    if (_rightHeld && e.button == 1) { _lookDelta += e.delta; e.Use(); }
                    break;
                case EventType.ScrollWheel:
                    if (!overPanel) { _scrollDelta += e.delta.y; e.Use(); }
                    break;
                case EventType.KeyDown:
                    _keysHeld.Add(e.keyCode);
                    break;
                case EventType.KeyUp:
                    _keysHeld.Remove(e.keyCode);
                    break;
            }
        }

        private bool KeyHeld(KeyCode k) => _keysHeld.Contains(k);

        private void HandleLook()
        {
            _looking = _rightHeld;
            if (!_looking || targetCamera == null) return;
            _yaw += _lookDelta.x * lookSensitivity * 0.15f;
            _pitch = Mathf.Clamp(_pitch - _lookDelta.y * lookSensitivity * 0.15f, -89f, 89f);
            targetCamera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void HandleMove()
        {
            if (targetCamera == null) return;

            if (Mathf.Abs(_scrollDelta) > 1e-4f)
                moveSpeed = Mathf.Clamp(moveSpeed * (1f + _scrollDelta * 1.5f), 1f, 2000f);

            var dir = Vector3.zero;
            if (KeyHeld(KeyCode.W)) dir += targetCamera.transform.forward;
            if (KeyHeld(KeyCode.S)) dir -= targetCamera.transform.forward;
            if (KeyHeld(KeyCode.D)) dir += targetCamera.transform.right;
            if (KeyHeld(KeyCode.A)) dir -= targetCamera.transform.right;
            if (KeyHeld(KeyCode.E)) dir += Vector3.up;
            if (KeyHeld(KeyCode.Q)) dir -= Vector3.up;
            if (dir.sqrMagnitude < 1e-6f) return;

            float speed = moveSpeed * ((KeyHeld(KeyCode.LeftShift) || KeyHeld(KeyCode.RightShift)) ? boostMultiplier : 1f);
            targetCamera.transform.position += dir.normalized * speed * Time.unscaledDeltaTime;
        }

        private void OnGUI()
        {
            // 自诊断:**OnGUI 即使在"编辑器暂停"时也会重绘,而 Update 不会跑** ——
            //   所以拿 Update 的帧计数比一比,就能区分"暂停"和"真的 0 FPS"。
            _updateAlive = _updateTicks != _seenTicks;
            _seenTicks = _updateTicks;

            // 面板矩形先算好(CollectInput 用它判断"鼠标在面板上",避免抢面板的滚轮/点击)
            _panelRect = new Rect(10f, 40f, 450f, _panelOpen ? Mathf.Max(0f, Screen.height - 60f) : 0f);
            CollectInput(Event.current);   // ← 输入采集(IMGUI 事件;不碰旧版 Input API)

            var header = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(10f, 10f, 220f, 24f), (_panelOpen ? "▼ " : "▶ ") + "雨预览台 Rain Preview", header))
                _panelOpen = !_panelOpen;

            string status = _updateAlive
                ? string.Format("FPS {0:F0}   dt {1:F0} ms   相机 {2:F0} m/s{3}",
                    _fps, Time.unscaledDeltaTime * 1000f, RainParticles.LastCamSpeed,
                    _fps > 0.01f && _fps < 10f ? "    很慢:把「粒子数量」降到 20000 试试(密度影响最大)" : "")
                : "⏸ Update 没在运行 = **编辑器处于暂停**(点 Editor 工具栏的 ▶ 继续;暂停时雨与相机都不会动,FPS 显示的是停住前的值)";
            if (!string.IsNullOrEmpty(_fatal)) status = "❌ " + _fatal;
            GUI.Label(new Rect(238f, 12f, 900f, 22f), status + "     右键拖拽转视角 / WASD QE 移动 / Shift 加速 / 滚轮调速");

            if (!_panelOpen) return;

            GUILayout.BeginArea(new Rect(10f, 40f, 450f, Screen.height - 60f), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Width(430f));

            bool enabled = GUILayout.Toggle(rainEnabled, " 启用雨");
            if (enabled != rainEnabled) { rainEnabled = enabled; PushHeavy(); }

            GUILayout.Label("── 密度 / 尺度(改动即时生效;带 ◈ 的松手才重建资源)──");
            amount = Slider("◈ 粒子数量(密度)", amount, 1000f, 200000f, false);
            GUILayout.Label(string.Format("     → 当前密度 {0:F4} 粒/m³(SP2 出厂 0.191;卡就往下拉)", RainParticles.DensityPerM3()));
            domainRadius = Slider("域半径 R(米)", domainRadius, 10f, 400f, false);
            fallSpeed = Slider("下落速度(m/s)", fallSpeed, 0.5f, 60f, false);
            streakLength = Slider("◈ 雨丝长度(米)", streakLength, 0.1f, 20f, false);
            streakWidth = Slider("◈ 雨丝宽度(米)", streakWidth, 0.005f, 0.6f, false);

            GUILayout.Label("── 视觉 ──");
            stretchAmount = Slider("拉伸系数(SP2 0.045)", stretchAmount, 0f, 0.3f, false);
            stretchLimit = Slider("拉伸上限(SP2 3.5)", stretchLimit, 1f, 12f, false);
            edgeFade = Slider("域边界淡出(0=关)", edgeFade, 0f, 0.5f, false);
            softParticles = Slider("软粒子(预览无云的深度图→通常无效)", softParticles, 0f, 3f, false);
            tailFalloff = Slider("尾淡(SP2 0.9)", tailFalloff, 0f, 1f, false);
            brightness = Slider("亮度增益", brightness, 0f, 3f, false);
            transparency = Slider("雨滴透明度(0=原效果,1=全透明)", transparency, 0f, 1f, false);

            GUILayout.Label("── 纵深线索(治\"快速缩放像一层平面\")──");
            distanceFade = Slider("域内距离衰减(0=关;近了亮远了暗)", distanceFade, 0f, 1f, false);
            streakVariation = Slider("逐粒长度/宽度变化(0=全一样长)", streakVariation, 0f, 1f, false);
            streakRoll = Slider("逐粒绕长轴滚转(默认 0;开了可能\"面条\")", streakRoll, 0f, 1f, false);

            GUILayout.Label("── 自适应域(相机越快 → 半径越大,治\"整片回收=没有视差\")──");
            bool adapt = GUILayout.Toggle(adaptiveDomain, " 域半径随相机速度自适应");
            if (adapt != adaptiveDomain) { adaptiveDomain = adapt; _paramsDirty = true; }
            adaptiveMaxRadius = Slider("自适应半径上限(米;越大密度越稀)", adaptiveMaxRadius, 10f, 400f, true);
            GUILayout.Label(string.Format("     → 本帧实际半径 R={0:F0}m(相机 {1:F0}m/s;补偿后容量 {2})",
                RainParticles.AdaptiveRadius, RainParticles.LastCamSpeed, RainParticles.BufferCapacity));

            GUILayout.Label("── 行为 ──");
            bool stream = GUILayout.Toggle(streamMode, " 雨丝朝向沿相对速度(关 = 恒径向下)");
            if (stream != streamMode) { streamMode = stream; _paramsDirty = true; }
            bool mirror = GUILayout.Toggle(respawnMirror, " 出域镜像重生(开 = 确定性传送带,会周期团块)");
            if (mirror != respawnMirror) { respawnMirror = mirror; _paramsDirty = true; }
            GUILayout.Label("     关掉镜像 = 随机混合:雨应是连续雨帘,没有周期性团块/脉动");

            GUILayout.Label("── 海拔闸门(编辑器里靠覆盖值模拟)──");
            bool oa = GUILayout.Toggle(overrideAltitude, " 使用覆盖海拔");
            if (oa != overrideAltitude) overrideAltitude = oa;
            if (overrideAltitude)
                altitudeOverrideValue = Slider("相机海拔(米;负 = 水下)", altitudeOverrideValue, -100f, 120000f, false);
            ceilingAltitude = Slider("海拔上限(米;0=关闭闸门)", ceilingAltitude, 0f, 60000f, false);
            ceilingBand = Slider("上限淡出带宽(比例)", ceilingBand, 0.05f, 1f, false);
            bool uw = GUILayout.Toggle(underwaterGate, " 水下不下雨(海平面以下)");
            if (uw != underwaterGate) { underwaterGate = uw; _paramsDirty = true; }
            underwaterFade = Slider("水下淡出深度(米;贴水面浮动时不闪断)", underwaterFade, 0.1f, 20f, false);
            bool aw = GUILayout.Toggle(assumeWater, " 假装行星有水(独立模式拿不到行星)");
            if (aw != assumeWater) assumeWater = aw;
            GUILayout.Label("     把海拔拉过上限 = 太空不画雨;拉到 0 以下 = 水下不画雨");

            GUILayout.Label("── 雨声(light / heavy 按雨量交叉淡化 + 淡入淡出)──");
            bool audioOn = GUILayout.Toggle(RainAudio.Enabled, " 雨声总开关(关掉只是不响,雨照下)");
            if (audioOn != RainAudio.Enabled) RainAudio.Enabled = audioOn;
            rainStrength = Slider("雨强度(与粒子数一起决定选哪组音效)", rainStrength, 0f, 4f, false);
            rainVolume = Slider("雨声音量", rainVolume, 0f, 1f, false);
            rainRepeats = Mathf.RoundToInt(Slider("每条素材连播遍数(再换下一条)", rainRepeats, 1f, 10f, false));
            rainCrossfade = Slider("换素材交叉淡化时长(秒)", rainCrossfade, 0.1f, 8f, false);
            GUILayout.Label(string.Format("     → 目标混合 {0:F2}(0 = 全小雨,1 = 全暴雨;与粒子数一起算)",
                RainAudio.ComputeIntensity()));

            GUILayout.Label("── 实时状态 ──");
            GUILayout.Label(RainParticles.StatsLine(), WrapLabel());
            GUILayout.Label("雨声:" + RainAudio.StatsLine(), WrapLabel());
            GUILayout.Label(string.Format("alt {0:F0}m  ceil {1}  altFade {2:F2}  stretch {3:F2}  axis ({4:F2},{5:F2},{6:F2})",
                RainParticles.LastAltitude,
                RainParticles.LastCeiling > 0f ? RainParticles.LastCeiling.ToString("F0") : "off",
                RainParticles.LastAltitudeFade, RainParticles.LastStretch,
                RainParticles.LastAxisDir.x, RainParticles.LastAxisDir.y, RainParticles.LastAxisDir.z), WrapLabel());

            GUILayout.Space(6f);
            bool auto = GUILayout.Toggle(_autoStart, " 下次 Play 自动启动(任意场景,仅编辑器)");
            if (auto != _autoStart)
            {
                _autoStart = auto;
                PlayerPrefs.SetInt(AutoStartPref, auto ? 1 : 0);
                PlayerPrefs.Save();
            }

            if (GUILayout.Button("▸ 复制参数为 weather.xml 的 <Rain> 段(调好一次粘进游戏)")) CopyRainXmlToClipboard();
            if (GUILayout.Button("重置为 SP2 出厂参数")) ResetToSp2Defaults();
            if (GUILayout.Button("把状态写入 Console(排查用)")) { RainParticles.DiagStatus(); RainAudio.DiagStatus(); }
            GUILayout.Label("参数会自动记住(下次 Play 接着调);预览测不到:软粒子(需云的深度图)、换帧重定位、真实海拔。", WrapLabel());
            if (!string.IsNullOrEmpty(_toast) && Time.realtimeSinceStartup < _toastUntil)
                GUILayout.Label("✓ " + _toast, WrapLabel());

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private float Slider(string label, float value, float min, float max, bool heavy)
        {
            GUILayout.Label(string.Format("{0}: {1:F4}", label, value));
            float v = GUILayout.HorizontalSlider(value, min, max);
            if (!Mathf.Approximately(v, value))
            {
                value = v;
                _paramsDirty = true;
                if (heavy) _heavyDirty = true;   // 容量/雨丝长宽 → 等松手再重建资源,避免拖动时卡顿
            }
            return value;
        }

        private static GUIStyle WrapLabel()
        {
            return new GUIStyle(GUI.skin.label) { wordWrap = true };
        }

        private void ResetToSp2Defaults()
        {
            amount = 100000f;
            domainRadius = 50f;
            fallSpeed = 15f;
            streakLength = 2.5f;
            streakWidth = 0.1f;
            stretchAmount = 0.045f;
            stretchLimit = 3.5f;
            edgeFade = 0.2f;
            softParticles = 1f;
            tailFalloff = 0.9f;
            brightness = 0f;
            transparency = 0f;
            streamMode = true;
            respawnMirror = false;
            ceilingAltitude = 12000f;
            ceilingBand = 0.4f;
            underwaterGate = true;
            underwaterFade = 2f;
            assumeWater = false;
            rainStrength = 1f;
            rainVolume = 0.5f;
            rainRepeats = 3;
            rainCrossfade = 2f;
            distanceFade = 0.35f;
            streakVariation = 0.5f;
            streakRoll = 0f;
            adaptiveDomain = true;
            adaptiveMaxRadius = 120f;
            PushHeavy();
            Toast("已重置为 SP2 出厂参数");
        }

        /// <summary>把当前参数存进 PlayerPrefs(下次 Play 接着调)。</summary>
        private void SaveParams()
        {
            _paramsDirty = false;
            _lastSaveTime = Time.realtimeSinceStartup;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var parts = new[]
                {
                    "v6",
                    rainEnabled ? "1" : "0",
                    amount.ToString("R", inv), domainRadius.ToString("R", inv), fallSpeed.ToString("R", inv),
                    streakLength.ToString("R", inv), streakWidth.ToString("R", inv),
                    stretchAmount.ToString("R", inv), stretchLimit.ToString("R", inv),
                    edgeFade.ToString("R", inv), softParticles.ToString("R", inv),
                    tailFalloff.ToString("R", inv), brightness.ToString("R", inv),
                    streamMode ? "1" : "0", respawnMirror ? "1" : "0",
                    ceilingAltitude.ToString("R", inv), ceilingBand.ToString("R", inv),
                    rainStrength.ToString("R", inv), rainVolume.ToString("R", inv),
                    rainRepeats.ToString(inv), rainCrossfade.ToString("R", inv),
                    distanceFade.ToString("R", inv), streakVariation.ToString("R", inv), streakRoll.ToString("R", inv),
                    adaptiveDomain ? "1" : "0", adaptiveMaxRadius.ToString("R", inv),
                    underwaterGate ? "1" : "0", underwaterFade.ToString("R", inv), assumeWater ? "1" : "0",
                    transparency.ToString("R", inv),
                };
                PlayerPrefs.SetString(ParamsPref, string.Join(";", parts));
                PlayerPrefs.Save();
            }
            catch { }
        }

        /// <summary>恢复上次记忆的参数;没有记忆则返回 false。</summary>
        private bool LoadParams()
        {
            try
            {
                string s = PlayerPrefs.GetString(ParamsPref, "");
                if (string.IsNullOrEmpty(s)) return false;
                var p = s.Split(';');
                // 新字段追加在末尾,旧版本的字段索引保持不变。
                bool hasTransparency = p.Length >= 30 && p[0] == "v6";
                bool hasWater = hasTransparency || (p.Length >= 29 && p[0] == "v5");
                bool hasDepth = hasWater || (p.Length >= 26 && p[0] == "v4");
                bool hasLoop = hasDepth || (p.Length >= 21 && p[0] == "v3");
                bool hasAudio = hasLoop || (p.Length >= 19 && p[0] == "v2");
                if (!hasAudio && (p.Length < 17 || p[0] != "v1")) return false;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                float F(int i) { float v; float.TryParse(p[i], System.Globalization.NumberStyles.Float, inv, out v); return v; }
                rainEnabled = p[1] == "1";
                amount = F(2); domainRadius = F(3); fallSpeed = F(4);
                streakLength = F(5); streakWidth = F(6);
                stretchAmount = F(7); stretchLimit = F(8);
                edgeFade = F(9); softParticles = F(10);
                tailFalloff = F(11); brightness = F(12);
                streamMode = p[13] == "1"; respawnMirror = p[14] == "1";
                ceilingAltitude = F(15); ceilingBand = F(16);
                if (hasAudio) { rainStrength = F(17); rainVolume = F(18); }
                if (hasLoop) { rainRepeats = Mathf.RoundToInt(F(19)); rainCrossfade = F(20); }
                if (hasDepth)
                {
                    distanceFade = F(21); streakVariation = F(22); streakRoll = F(23);
                    adaptiveDomain = p[24] == "1"; adaptiveMaxRadius = F(25);
                }
                if (hasWater) { underwaterGate = p[26] == "1"; underwaterFade = F(27); assumeWater = p[28] == "1"; }
                transparency = hasTransparency ? Mathf.Clamp01(F(29)) : 0f;
                UnityEngine.Debug.Log("[Volken] RainPreview: 已恢复上次记忆的参数(想从出厂值开始就点「重置为 SP2 出厂参数」)");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把当前参数复制成**可直接替换 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` 里 &lt;Rain&gt; 段**的 XML。
        /// </summary>
        private void CopyRainXmlToClipboard()
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string B(bool v) => v ? "true" : "false";
                string N(float v) => v.ToString("0.####", inv);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("  <Rain>");
                sb.AppendLine("    <enabled>" + B(rainEnabled) + "</enabled>");
                sb.AppendLine("    <amount>" + N(amount) + "</amount>");
                sb.AppendLine("    <domainRadius>" + N(domainRadius) + "</domainRadius>");
                sb.AppendLine("    <adaptiveDomain>" + B(adaptiveDomain) + "</adaptiveDomain>");
                sb.AppendLine("    <fallSpeed>" + N(fallSpeed) + "</fallSpeed>");
                sb.AppendLine("    <strength>" + N(rainStrength) + "</strength>");
                sb.AppendLine("    <windInfluence>1</windInfluence>");
                sb.AppendLine("    <streakLength>" + N(streakLength) + "</streakLength>");
                sb.AppendLine("    <streakWidth>" + N(streakWidth) + "</streakWidth>");
                sb.AppendLine("    <volume>" + N(rainVolume) + "</volume>");
                sb.AppendLine("    <streamMode>" + B(streamMode) + "</streamMode>");
                sb.AppendLine("    <stretchAmount>" + N(stretchAmount) + "</stretchAmount>");
                sb.AppendLine("    <stretchLimit>" + N(stretchLimit) + "</stretchLimit>");
                sb.AppendLine("    <edgeFade>" + N(edgeFade) + "</edgeFade>");
                sb.AppendLine("    <softParticles>" + N(softParticles) + "</softParticles>");
                sb.AppendLine("    <tailFalloff>" + N(tailFalloff) + "</tailFalloff>");
                sb.AppendLine("    <brightness>" + N(brightness) + "</brightness>");
                sb.AppendLine("    <transparency>" + N(Mathf.Clamp01(transparency)) + "</transparency>");
                sb.AppendLine("    <distanceFade>" + N(distanceFade) + "</distanceFade>");
                sb.AppendLine("    <streakVariation>" + N(streakVariation) + "</streakVariation>");
                sb.AppendLine("    <ceilingAltitude>" + N(ceilingAltitude) + "</ceilingAltitude>");
                sb.AppendLine("    <ceilingBand>" + N(ceilingBand) + "</ceilingBand>");
                sb.AppendLine("    <underwaterGate>" + B(underwaterGate) + "</underwaterGate>");
                sb.AppendLine("    <underwaterFade>" + N(underwaterFade) + "</underwaterFade>");
                sb.AppendLine("    <respawnMirror>" + B(respawnMirror) + "</respawnMirror>");
                sb.AppendLine("  </Rain>");
                GUIUtility.systemCopyBuffer = sb.ToString();
                Toast("已复制 <Rain> 段到剪贴板 → 覆盖 weather.xml 里同名段(windInfluence 未调,按原值改回)");
                UnityEngine.Debug.Log("[Volken] RainPreview 已复制参数 XML:\n" + sb);
            }
            catch (Exception ex)
            {
                Toast("复制失败: " + ex.Message);
            }
        }
    }
}
