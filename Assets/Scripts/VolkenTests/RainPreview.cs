using System;
using System.Collections.Generic;
using UnityEngine;
using Volken.Core;
using Volken.Weather;

namespace Volken.Tests
{
    /// <summary>
    /// 雨系统**编辑器内预览台** —— 不打包、不进游戏,直接在 Unity 里按 Play 迭代视觉(5 分钟 → 5 秒)。
    ///
    /// 【为什么能这么干】<see cref="RainParticles"/> 对游戏 API 的依赖都写成了"失败即降级":
    ///   · 下落方向 ← <c>craft.GravityNormal</c>,拿不到 → 世界 (0,-1,0);
    ///   · 朝向/拉伸的速度源 ← <c>craft.FrameVelocity</c>,拿不到 → **相机实测位移速度**(正是预览想要的);
    ///   · 海拔闸门 ← craftNode/PlanetData,拿不到 → 返回 −1(闸门不生效);本组件用 DebugAltitudeOverride 手动喂;
    ///   · 软粒子 ← CloudRenderer 深度图,拿不到 → <c>_InvFade=0</c> 自动关;
    ///   · 换帧重定位 ← ModApi 事件,挂不上 → 只打日志(预览不需要);
    ///   · 资产加载 ← <c>Mod.LoadVolkenAsset</c> 在编辑器里回退到 <c>AssetDatabase</c>。
    /// 所以这里跑的是**同一份 compute/shader/驱动代码**,只是把输入换成"手动/相机"。
    ///
    /// 【用法】任意场景新建空物体 → Add Component → <c>Volken Rain Preview</c> → Play。
    ///   勾上面板里的「下次 Play 自动启动」后,以后**任意场景按 Play 都会自动出现预览台**(写入 PlayerPrefs,
    ///   并且只在编辑器里自动启动,不影响正式游戏)。
    ///
    /// 【操作】右键拖拽 = 转视角;WASD = 前后左右;Q/E = 升降;Shift = 加速;滚轮 = 调基础速度。
    ///   相机的位移速度就是雨丝朝向/拉伸的输入 → 直接看"飞行时"的样子。
    ///
    /// 【预览测不到的项】软粒子(需要云的深度图)、换帧重定位、真实海拔(用手动覆盖值代替)、
    ///   游戏内天气面板/配置持久化、与其他 mod 系统的交互。
    /// </summary>
    [AddComponentMenu("Volken/Rain Preview (编辑器预览台)")]
    public class RainPreview : MonoBehaviour
    {
        /// <summary>PlayerPrefs 键:下次 Play 是否自动生成预览台(仅编辑器生效)。</summary>
        private const string AutoStartPref = "Volken.RainPreview.AutoStart";

        /// <summary>PlayerPrefs 键:记住上次调好的参数(自动启动时也能接着调)。</summary>
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
        public bool streamMode = true;
        public bool respawnMirror = false;
        public float ceilingAltitude = 12000f;
        public float ceilingBand = 0.4f;

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

        // ======================= 自动启动 =======================

        /// <summary>
        /// 勾过「下次 Play 自动启动」后,任意场景按 Play 都会自动生成预览台。
        /// 只在编辑器里生效(Application.isEditor),不会影响正式游戏。
        /// </summary>
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

        // ======================= 生命周期 =======================

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
                // 关键:不要再让异常每帧静默中断(那正是"FPS 一直 0"的成因)——存起来直接显示在面板上
                _fatal = "Update 异常(每帧抛): " + ex.Message;
                if (!_fatalLogged) { _fatalLogged = true; UnityEngine.Debug.LogException(ex); }
            }

            // FPS:窗口内帧数 / 真实耗时(比 1/delta 稳;暂停时不会算出 10000 这种假值)
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

        // ======================= 相机 / 场景 =======================

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

        /// <summary>极简环境:地面 + 近/中/远三个参照方块,用来判断尺度、距离感与遮挡。不依赖任何 mod 资产。</summary>
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

        // ======================= 雨的挂载与参数同步 =======================

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
            RainParticles.StreamMode = streamMode;
            RainParticles.RespawnMirror = respawnMirror;
            RainParticles.CeilingAltitude = Mathf.Max(0f, ceilingAltitude);
            RainParticles.CeilingBand = Mathf.Clamp(ceilingBand, 0.02f, 1f);
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
                    streamMode = streamMode,
                    respawnMirror = respawnMirror,
                    ceilingAltitude = ceilingAltitude,
                    ceilingBand = ceilingBand,
                };
                RainParticles.ApplyConfig(cfg);
                RainParticles.Enabled = rainEnabled;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Volken] RainPreview PushHeavy: " + ex.Message);
            }
        }

        // ======================= 自由飞行 =======================
        // ⚠️ **不要用旧版 UnityEngine.Input.***:本工程 Player Settings 是
        //   `activeInputHandler: 1`(只用 Input System Package (New)),旧 API 每次调用都会抛
        //   InvalidOperationException("...switched active Input handling to Input System package...")
        //   → Update 每帧在最前面就中断 → 面板显示"❌ Update 异常(每帧抛)"、FPS 永远 0(实测踩过)。
        //   这里统一改用 **IMGUI 事件**(OnGUI 里的 Event.current):纯运行时 API,
        //   不受输入后端设置影响,也不需要引入 Input System 包。

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

        // ======================= IMGUI 面板 =======================

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
                    _fps > 0.01f && _fps < 10f ? "   ⚠ 很慢:把「粒子数量」降到 20000 试试(密度影响最大)" : "")
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
                altitudeOverrideValue = Slider("相机海拔(米)", altitudeOverrideValue, 0f, 120000f, false);
            ceilingAltitude = Slider("海拔上限(米;0=关闭闸门)", ceilingAltitude, 0f, 60000f, false);
            ceilingBand = Slider("上限淡出带宽(比例)", ceilingBand, 0.05f, 1f, false);
            GUILayout.Label("     把海拔拉过上限:雨应渐隐并在超过上限后完全不画");

            GUILayout.Label("── 实时状态 ──");
            GUILayout.Label(RainParticles.StatsLine(), WrapLabel());
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
            if (GUILayout.Button("把状态写入 Console(排查用)")) RainParticles.DiagStatus();
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
            streamMode = true;
            respawnMirror = false;
            ceilingAltitude = 12000f;
            ceilingBand = 0.4f;
            PushHeavy();
            Toast("已重置为 SP2 出厂参数");
        }

        // ======================= 参数记忆 / 导出 =======================

        /// <summary>把当前参数存进 PlayerPrefs(下次 Play、包括自动启动,都接着这次调)。</summary>
        private void SaveParams()
        {
            _paramsDirty = false;
            _lastSaveTime = Time.realtimeSinceStartup;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var parts = new[]
                {
                    "v1",
                    rainEnabled ? "1" : "0",
                    amount.ToString("R", inv), domainRadius.ToString("R", inv), fallSpeed.ToString("R", inv),
                    streakLength.ToString("R", inv), streakWidth.ToString("R", inv),
                    stretchAmount.ToString("R", inv), stretchLimit.ToString("R", inv),
                    edgeFade.ToString("R", inv), softParticles.ToString("R", inv),
                    tailFalloff.ToString("R", inv), brightness.ToString("R", inv),
                    streamMode ? "1" : "0", respawnMirror ? "1" : "0",
                    ceilingAltitude.ToString("R", inv), ceilingBand.ToString("R", inv),
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
                if (p.Length < 17 || p[0] != "v1") return false;
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
                UnityEngine.Debug.Log("[Volken] RainPreview: 已恢复上次记忆的参数(想从出厂值开始就点「重置为 SP2 出厂参数」)");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把当前参数复制成**可直接替换 `UserData/VolkenWeatherConfig/{行星}/{预设}.xml` 里 &lt;Rain&gt; 段**的 XML。
        /// 这样编辑器里调好的值一次粘贴就能进游戏(不必在游戏面板里照抄一遍)。
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
                sb.AppendLine("    <triggerValue>2.25</triggerValue>");     // 阶段 6 天气状态机用,预览不涉及
                sb.AppendLine("    <amount>" + N(amount) + "</amount>");
                sb.AppendLine("    <domainRadius>" + N(domainRadius) + "</domainRadius>");
                sb.AppendLine("    <adaptiveDomain>true</adaptiveDomain>");
                sb.AppendLine("    <fallSpeed>" + N(fallSpeed) + "</fallSpeed>");
                sb.AppendLine("    <strength>1</strength>");
                sb.AppendLine("    <windInfluence>1</windInfluence>");
                sb.AppendLine("    <streakLength>" + N(streakLength) + "</streakLength>");
                sb.AppendLine("    <streakWidth>" + N(streakWidth) + "</streakWidth>");
                sb.AppendLine("    <volume>0.5</volume>");
                sb.AppendLine("    <streamMode>" + B(streamMode) + "</streamMode>");
                sb.AppendLine("    <stretchAmount>" + N(stretchAmount) + "</stretchAmount>");
                sb.AppendLine("    <stretchLimit>" + N(stretchLimit) + "</stretchLimit>");
                sb.AppendLine("    <edgeFade>" + N(edgeFade) + "</edgeFade>");
                sb.AppendLine("    <softParticles>" + N(softParticles) + "</softParticles>");
                sb.AppendLine("    <tailFalloff>" + N(tailFalloff) + "</tailFalloff>");
                sb.AppendLine("    <brightness>" + N(brightness) + "</brightness>");
                sb.AppendLine("    <ceilingAltitude>" + N(ceilingAltitude) + "</ceilingAltitude>");
                sb.AppendLine("    <ceilingBand>" + N(ceilingBand) + "</ceilingBand>");
                sb.AppendLine("    <respawnMirror>" + B(respawnMirror) + "</respawnMirror>");
                sb.AppendLine("  </Rain>");
                GUIUtility.systemCopyBuffer = sb.ToString();
                Toast("已复制 <Rain> 段到剪贴板 → 覆盖 weather.xml 里同名段(triggerValue/strength/windInfluence/volume 未调,按原值改回)");
                UnityEngine.Debug.Log("[Volken] RainPreview 已复制参数 XML:\n" + sb);
            }
            catch (Exception ex)
            {
                Toast("复制失败: " + ex.Message);
            }
        }
    }
}
