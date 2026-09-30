using System;
using System.Text;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.Sim;
using UnityEngine;
using Volken.Weather;

namespace Volken.Tests
{
    /// <summary>
    /// Phase 1 —— 最小 billboard 朝向验证探针(「屏幕平面构轴」算法)。
    ///
    /// 【目的】(对应文档《Volken-SP2雨系统ParticleDomain移植难点与分步计划-2026-09-28.md》阶段 1)
    /// 用 1~10 个粒子、CPU 算顶点、不碰 compute —— 以最小代价回答:
    ///   "屏幕平面内构造长度轴"这套基向量在真机上到底立不立得住。
    /// 上一次失败(雨丝朝向 8 轮不收敛)的根源就是"诊断量错对象"(用世界空间夹角当屏幕倾角),
    /// 所以本探针**只输出屏幕空间**的量(倾角 / 长宽比),并同时画出两条算法的四边形做直接对比:
    ///   - 版本 A(青色)= 本项目方案:长度轴在屏幕平面内构造,宽度轴 = cross(L, viewDir)
    ///     (复盘 §3.1㈢ 的算法骨架,原文自注"尚未在真机验证" —— 本探针就是它的验证);
    ///   - 版本 B(品红)= SP2 <c>AlignStreaks</c> 原样复刻(世界空间长度轴 + 任意宽度轴),
    ///     作为"对照实验":模式 2(雨朝相机飞)下它应当复现"横块/细线"退化,而 A 不应退化。
    ///
    /// 【挂载】游戏近相机(由 <c>Volken.Core.VolkenMod.OnSceneLoaded</c> 挂,幂等)。
    /// 绘制走**实例 OnPreCull** + <c>Graphics.DrawMesh</c>(命令提交类 API —— 计划 §10.2b ⑪
    /// 真机验证过这条路径可靠;静态 <c>Camera.onPreCull</c> 在本工程从不触发)。
    ///
    /// 【dev 命令】(见 <c>Mod.RegisterCommands</c>)
    ///   volkenRainAxis            → 打印状态(未挂载则尝试挂载)
    ///   volkenRainAxisOn 0|1      → 开关(默认 0:不改变现有画面)
    ///   volkenRainAxisMode 0..4   → 气流方向模式(0=竖直下落 1=前下 45° 2=朝相机[退化] 3=自定义 4=重力−相机速度)
    ///   volkenRainAxisVec x y z   → 自定义气流方向(配合 Mode 3)
    ///   volkenRainAxisCount 1..10 → 测试粒子数
    ///
    /// 【验收判据】(阶段 1 准出,逐条对应)
    ///   - 相机任意朝向(含正仰视/正俯视/雨丝朝相机飞):A **不出现**"横块"**也不**出现"细线拉飞";
    ///   - 屏幕倾角随视角单调连续,无跳变、无径向爆散;
    ///   - 退化视角(气流 ∥ 视线):A 稳定表现为**竖直下落**(d2≈0 → 显式兜底 (0,-1)),
    ///     而不是随机方向;同时 B 在同一视角应量出退化(横块/细线)作为对照。
    /// </summary>
    public class RainAxisProbe : MonoBehaviour
    {
        // ================= 静态调试开关(铁律 14:全局调试开关必须 static) =================

        /// <summary>总开关。默认关 —— 不改变现有画面。</summary>
        public static bool Enabled;

        /// <summary>
        /// 气流方向模式:
        /// 0 = 竖直下落(行星径向,量级 = <see cref="FallSpeed"/>);
        /// 1 = 前下 45°(径向下落 + 0.6×相机前向);
        /// 2 = **朝相机**(退化视角:气流 ∥ 视线,旧实现的"横块"复现场景);
        /// 3 = 自定义 <see cref="CustomAirDir"/>;
        /// 4 = 重力 − 相机速度(移动中的真实朝向贡献)。
        /// </summary>
        public static int Mode;

        /// <summary>模式 3 的自定义气流方向(米/秒量级)。</summary>
        public static Vector3 CustomAirDir = new Vector3(5f, -15f, 3f);

        /// <summary>测试粒子数(1~10)。</summary>
        public static int ParticleCount = 6;

        // SP2 的拉伸参数(与 ParticleDomain 一致,便于对照)
        public const float FallSpeed = 15f;          // _fallSpeed
        public const float StreakLength = 2.5f;      // _streakLength(网格长)
        public const float StreakThickness = 0.1f;   // _streakThickness(网格宽)
        public const float StretchAmount = 0.045f;   // _stretchAmount
        public const float StretchLimit = 3.5f;      // _stretchLimit

        // ================= 心跳(诊断) =================

        /// <summary>实例 OnPreCull 累计触发次数 —— 心跳:验证实例回调真的在跑。</summary>
        public static int PreCullCalls;

        /// <summary>最近一帧的气流方向(诊断)。</summary>
        public static Vector3 LastAirDir;

        /// <summary>最近一帧的相机基(诊断)。</summary>
        public static Vector3 LastCamRight, LastCamUp, LastCamFwd;

        /// <summary>每粒子屏幕诊断(版本 A)。tiltDeg:0=竖直,90=横;aspect=屏幕长/宽。</summary>
        public static readonly float[] TiltDegA = new float[10];
        public static readonly float[] AspectA = new float[10];

        /// <summary>每粒子屏幕诊断(版本 B,SP2 原样)。</summary>
        public static readonly float[] TiltDegB = new float[10];
        public static readonly float[] AspectB = new float[10];

        /// <summary>每粒子退化分类:0=正常 1=横块(长≈0) 2=细线(宽≈0)。</summary>
        public static readonly int[] DegenerateA = new int[10];
        public static readonly int[] DegenerateB = new int[10];

        // ================= 内部 =================

        private Camera _cam;
        private Mesh _mesh;
        private Material _matA;   // 青色:屏幕平面构轴
        private Material _matB;   // 品红:SP2 AlignStreaks 原样
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private int[] _trisA;
        private int[] _trisB;
        private float _lastDiagTime = -999f;
        private float _lastAliveLogTime = -999f;
        private float _lastNotReadyLogTime = -999f;
        private bool _loggedFirstEnable;

        private static readonly Color ColorA = new Color(0f, 1f, 1f, 1f);   // 青色 = 屏幕平面构轴(本项目方案)
        private static readonly Color ColorB = new Color(1f, 0f, 1f, 1f);   // 品红 = SP2 AlignStreaks 原样

        // 粒子在相机前的排布网格(相机空间,米):前 8m,横向 ±3m,纵向 ±3m
        private static readonly Vector3[] GridOffsets =
        {
            new Vector3(-3f, -3f, 8f), new Vector3(0f, -3f, 8f), new Vector3(3f, -3f, 8f),
            new Vector3(-3f, 0f, 8f),  new Vector3(0f, 0f, 8f),  new Vector3(3f, 0f, 8f),
            new Vector3(-3f, 3f, 8f),  new Vector3(0f, 3f, 8f),  new Vector3(3f, 3f, 8f),
            new Vector3(0f, 0f, 11f),
        };

        // ================= 挂载(幂等) =================

        /// <summary>把探针挂到目标相机物体上(已有则返回现有实例)。</summary>
        public static RainAxisProbe Attach(Camera cam)
        {
            if (cam == null)
            {
                // ⚠️ 诊断:挂载收到 null 相机 —— 之前这里是静默返回,导致"命令开了但什么都不画、零日志"。
                Mod.Diag("RainAxis: attach skipped — camera is null");
                return null;
            }
            try
            {
                var existing = cam.GetComponent<RainAxisProbe>();
                if (existing != null)
                {
                    Mod.Diag("RainAxis: attach reused existing on '{0}'", cam.name);
                    return existing;
                }
                var probe = cam.gameObject.AddComponent<RainAxisProbe>();
                Mod.Diag("RainAxis: attached to camera '{0}' active={1} cullingMask={2:X}", cam.name, cam.enabled, cam.cullingMask);
                return probe;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainAxisProbe attach failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>解析当前游戏近相机并挂载探针(幂等)。场景加载 / SOI 切换时调用。</summary>
        public static void AttachToCurrentView()
        {
            string via = "?";
            try
            {
                Camera cam = null;
                try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
                catch { }
                if (cam != null) via = "NearCamera";
                if (cam == null)
                {
                    try { cam = Camera.main; } catch { }
                    if (cam != null) via = "Camera.main";
                }
                if (cam == null) via = "NULL(两路都解析不到)";
                Mod.Diag("RainAxis: AttachToCurrentView via {0}", via);
                Attach(cam);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainAxisProbe.AttachToCurrentView ERROR: " + ex.Message);
            }
        }

        /// <summary>dev 命令:打印状态;未挂载则尝试挂载。</summary>
        public static void DiagStatus()
        {
            try
            {
                Camera cam = null;
                try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
                catch { }
                if (cam == null) cam = Camera.main;

                var probe = cam != null ? Attach(cam) : null;
                Mod.Diag("RainAxis: enabled={0} mode={1}({2}) count={3} preCullCalls={4} attached={5}",
                    Enabled, Mode, ModeName(Mode), ParticleCount, PreCullCalls, probe != null);
                Mod.Diag("RainAxis: airDir={0} camR={1} camU={2} camF={3}",
                    LastAirDir, LastCamRight, LastCamUp, LastCamFwd);
                for (int i = 0; i < ParticleCount; i++)
                {
                    Mod.Diag("RainAxis: p{0} A(tilt={1:F1}° asp={2:F1} {3})  B(tilt={4:F1}° asp={5:F1} {6})",
                        i, TiltDegA[i], AspectA[i], DegName(DegenerateA[i]),
                        TiltDegB[i], AspectB[i], DegName(DegenerateB[i]));
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainAxisProbe.DiagStatus ERROR: " + ex.Message);
            }
        }

        public static void SetEnabled(int on)
        {
            if (on != 0) AttachToCurrentView();   // 先确保挂载:只开开关而不挂载 = 静默无效果(上次的教训)
            Enabled = on != 0;
            Mod.Diag("RainAxis: enabled = {0}", Enabled);
        }

        public static void SetMode(int mode)
        {
            Mode = Mathf.Clamp(mode, 0, 4);
            Mod.Diag("RainAxis: mode = {0} ({1})", Mode, ModeName(Mode));
        }

        public static void SetAirDir(float x, float y, float z)
        {
            CustomAirDir = new Vector3(x, y, z);
            Mode = 3;
            Mod.Diag("RainAxis: custom airDir = {0}", CustomAirDir);
        }

        public static void SetCount(int count)
        {
            ParticleCount = Mathf.Clamp(count, 1, 10);
            Mod.Diag("RainAxis: count = {0}", ParticleCount);
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case 0: return "竖直下落";
                case 1: return "前下45°";
                case 2: return "朝相机(退化)";
                case 3: return "自定义";
                case 4: return "重力−相机速度";
                default: return "?";
            }
        }

        private static string DegName(int deg)
        {
            switch (deg)
            {
                case 1: return "横块!";
                case 2: return "细线!";
                default: return "ok";
            }
        }

        // ================= 生命周期 =================

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            EnsureMesh();
            EnsureMaterial();
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_matA != null) Destroy(_matA);
            if (_matB != null) Destroy(_matB);
        }

        private void EnsureMesh()
        {
            if (_mesh != null) return;
            _mesh = new Mesh { name = "RainAxisProbeMesh" };
            _mesh.MarkDynamic();
        }

        private void EnsureMaterial()
        {
            if (_matA != null) return;
            // 调试期直接用内置 Unlit/Color(两个材质 = 两种颜色区分 A/B),不需要新 shader 文件。
            // 注意:Unlit/Color 忽略顶点色,只画 _Color —— 正好用于区分两条算法。
            _matA = TryCreateMaterial(ColorA);   // 青色 = 屏幕平面构轴(本项目方案)
            _matB = TryCreateMaterial(ColorB);   // 品红 = SP2 AlignStreaks 原样
        }

        private static Material TryCreateMaterial(Color color)
        {
            Shader shader = null;
            try { shader = Shader.Find("Unlit/Color"); } catch { }
            if (shader == null)
            {
                // ⚠️ 诊断:shader 找不到 = 探针什么都不画(静默)。BIRP 下 Unlit/Color 应存在。
                Mod.Log("Volken:RainAxisProbe ERROR: Shader.Find(\"Unlit/Color\") returned null — quads will NOT draw");
                return null;
            }
            var mat = new Material(shader) { color = color };
            // 调试探针必须"永远置顶"可见:ZTest Always + 不写深度 + Overlay 队列。
            // 否则座舱内/近处几何体会把 8m 前的色块深度剔除掉 —— "画了但看不见"正是这类坑。
            mat.renderQueue = 5000;   // Overlay:最后画,盖在场景之上
            try { mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); } catch { }
            try { mat.SetInt("_ZWrite", 0); } catch { }
            Mod.Diag("RainAxis: material created shader='{0}' queue=5000 ztest=Always zwrite=0", shader.name);
            return mat;
        }

        // ================= 每帧 =================

        private void OnPreCull()
        {
            PreCullCalls++;
            if (!Enabled)
            {
                // ⚠️ 诊断:探针活着但未启用 —— 证明实例回调在跑(区分"没挂上"与"关了")
                if (Time.realtimeSinceStartup - _lastAliveLogTime > 5f)
                {
                    _lastAliveLogTime = Time.realtimeSinceStartup;
                    Mod.Diag("RainAxis alive: calls={0} enabled=OFF cam='{1}' (run volkenRainAxisOn 1)",
                        PreCullCalls, _cam != null ? _cam.name : "?");
                }
                return;
            }
            if (_cam == null || _mesh == null)
            {
                // ⚠️ 诊断:开了但实例字段没就绪(Awake 没跑?AddComponent 时机问题?)
                if (Time.realtimeSinceStartup - _lastNotReadyLogTime > 5f)
                {
                    _lastNotReadyLogTime = Time.realtimeSinceStartup;
                    Mod.Diag("RainAxis: enabled but NOT READY — _cam={0} _mesh={1} (Awake didn't run?)", _cam != null, _mesh != null);
                }
                return;
            }
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Mod.LogThrottled("RainAxis", ex);
            }
        }

        private void Tick()
        {
            Vector3 camPos = _cam.transform.position;
            Vector3 camRight = _cam.transform.right;
            Vector3 camUp = _cam.transform.up;
            Vector3 camFwd = _cam.transform.forward;
            LastCamRight = camRight; LastCamUp = camUp; LastCamFwd = camFwd;

            Vector3 airDir = ResolveAirDir(camPos, camFwd);
            LastAirDir = airDir;

            int count = Mathf.Clamp(ParticleCount, 1, 10);
            EnsureCapacity(count);

            // 版本 A 每粒子基向量(屏幕平面构轴,教训 §3.1㈢)
            float ax = Vector3.Dot(airDir, camRight);
            float ay = Vector3.Dot(airDir, camUp);
            if (ax * ax + ay * ay < 1e-6f)
            {
                ax = 0f; ay = -1f;   // 退化 → 竖直下落,绝不交给浮点噪声
            }
            Vector3 l2 = new Vector3(ax, ay, 0f).normalized;
            Vector3 lw = camRight * l2.x + camUp * l2.y;      // 屏幕平面内的长度轴(世界)
            Vector3 wA = Vector3.Cross(lw, camFwd);           // 宽度轴:⊥ L 且 ⊥ 视线
            float wl = wA.magnitude;
            if (wl < 1e-6f) wA = camRight; else wA /= wl;

            // 版本 B 每粒子基向量(SP2 AlignStreaks 原样)
            Vector3 fwdB = airDir.normalized;
            Vector3 sideB = Vector3.Cross(Mathf.Abs(fwdB.y) < 0.999f ? Vector3.up : Vector3.forward, fwdB);
            float sl = sideB.magnitude;
            if (sl < 1e-6f) sideB = Vector3.Cross(Vector3.forward, fwdB);
            else sideB /= sl;
            Vector3 upB = Vector3.Cross(fwdB, sideB);

            float stretch = Mathf.Clamp(airDir.magnitude * StretchAmount, 1f, StretchLimit);
            float halfLen = StreakLength * stretch * 0.5f;
            float halfWid = StreakThickness * 0.5f;

            for (int i = 0; i < count; i++)
            {
                Vector3 offset = GridOffsets[i % GridOffsets.Length];
                Vector3 centerA = camPos + camRight * offset.x + camUp * offset.y + camFwd * offset.z;
                // 版本 B 向右错开 1.5m,避免两套四边形叠在一起
                Vector3 centerB = centerA + camRight * 1.5f;

                // 顶点:0..3 = A 四边形,4..7 = B 四边形
                // A corners: center ± lw*halfLen ± wA*halfWid
                _verts[i * 8 + 0] = centerA + lw * halfLen + wA * halfWid;
                _verts[i * 8 + 1] = centerA + lw * halfLen - wA * halfWid;
                _verts[i * 8 + 2] = centerA - lw * halfLen - wA * halfWid;
                _verts[i * 8 + 3] = centerA - lw * halfLen + wA * halfWid;
                // B corners: center ± fwdB*(len) ± sideB*(width) —— col1=长度轴(col0 宽度轴),与 SP2 一致
                _verts[i * 8 + 4] = centerB + fwdB * (halfLen * 2f) + sideB * halfWid;
                _verts[i * 8 + 5] = centerB + fwdB * (halfLen * 2f) - sideB * halfWid;
                _verts[i * 8 + 6] = centerB - fwdB * (halfLen * 2f) - sideB * halfWid;
                _verts[i * 8 + 7] = centerB - fwdB * (halfLen * 2f) + sideB * halfWid;

                // 屏幕空间诊断(只量屏幕,不量世界 —— 铁律 16)
                Measure(centerA, lw * (halfLen * 2f), wA * (halfWid * 2f), camFwd, camUp,
                        out TiltDegA[i], out AspectA[i], out DegenerateA[i]);
                Measure(centerB, fwdB * (halfLen * 2f), sideB * (halfWid * 2f), camFwd, camUp,
                        out TiltDegB[i], out AspectB[i], out DegenerateB[i]);
            }

            _mesh.vertices = _verts;
            _mesh.uv = _uvs;
            _mesh.subMeshCount = 2;
            _mesh.SetTriangles(_trisA, 0);
            _mesh.SetTriangles(_trisB, 1);

            if (_matA != null) Graphics.DrawMesh(_mesh, Matrix4x4.identity, _matA, 0, _cam, 0);
            if (_matB != null) Graphics.DrawMesh(_mesh, Matrix4x4.identity, _matB, 0, _cam, 1);

            // ⚠️ 诊断:首次启用时打一行完整状态,确认绘制参数与材质
            if (!_loggedFirstEnable)
            {
                _loggedFirstEnable = true;
                Mod.Diag("RainAxis first draw: count={0} mode={1} cam='{2}' ortho={3} fov={4:F0} pos={5} rot={6} matA={7} matB={8} verts={9} airDir={10}",
                    count, Mode, _cam.name, _cam.orthographic, _cam.fieldOfView, _cam.transform.position, _cam.transform.eulerAngles,
                    _matA != null, _matB != null, _mesh.vertexCount, airDir);
            }

            LogHeartbeat(airDir, count);
        }

        /// <summary>
        /// 屏幕空间测量:把"长轴 dirL、宽轴 dirW(世界,未归一化,已含各自全长)"投到屏幕平面,
        /// 量投影后的长度/宽度之比,以及长度轴与屏幕竖直方向的夹角。
        /// 退化分类:len &lt; 0.1×wid → 横块(旧实现的死法);wid &lt; 0.1×len → 细线(SP2 的另一种死法)。
        /// </summary>
        private static void Measure(Vector3 center, Vector3 dirL, Vector3 dirW, Vector3 viewDir, Vector3 camUp,
                                    out float tiltDeg, out float aspect, out int degenerate)
        {
            Vector3 lProj = Vector3.ProjectOnPlane(dirL, viewDir);
            Vector3 wProj = Vector3.ProjectOnPlane(dirW, viewDir);
            float lenSq = lProj.sqrMagnitude;
            float widSq = wProj.sqrMagnitude;

            // 长度轴与屏幕竖直方向的夹角:0=竖直,90=横。
            // Vector3.Angle 返回 0~180 —— 竖直线条朝下时会读出 180,须映射回 0~90(方向正反不算横)。
            float angleWithUp = lenSq < 1e-8f ? 90f : Vector3.Angle(lProj, camUp);
            tiltDeg = Mathf.Min(angleWithUp, 180f - angleWithUp);
            float wid = Mathf.Sqrt(widSq);
            float len = Mathf.Sqrt(lenSq);
            aspect = wid < 1e-3f ? 999f : len / wid;

            // 退化分类阈值(2026-09-29 修正):正常雨丝设计长宽比 = 2.5m/0.1m = **25**,
            // 旧阈值 wid<0.1*len(aspect>10) 会把一切正常雨丝误标成"细线!"(实测误报)。
            // 病理值量级:旧失败 = 长宽比 175(水平细条);横块 = 长投影→0(aspect→0)。
            // 取 aspect<0.2 = 横块、aspect>50 = 细线,25 稳稳落在 ok。
            if (len < 0.2f * wid) degenerate = 1;        // 横块:长被压没,宽接管屏幕
            else if (wid < 0.02f * len) degenerate = 2;  // 细线:宽被压没
            else degenerate = 0;
        }

        /// <summary>按模式解析气流方向(米/秒)。</summary>
        private static Vector3 ResolveAirDir(Vector3 camPos, Vector3 camFwd)
        {
            Vector3 down = GetRadialDown(camPos);
            switch (Mode)
            {
                case 0: return down * FallSpeed;
                case 1:
                {
                    Vector3 d = down * FallSpeed + camFwd * 8f;
                    return d;
                }
                case 2:
                    // 雨朝相机飞:气流 ∥ 视线 → 屏幕平面分量 ≈ 0 → 退化解
                    return -camFwd * FallSpeed;
                case 3:
                    return CustomAirDir;
                case 4:
                {
                    var w = VolkenWeather.Instance;
                    Vector3 camVel = w != null ? w.CameraVelocity : Vector3.zero;
                    return down * FallSpeed - camVel;
                }
                default:
                    return down * FallSpeed;
            }
        }

        /// <summary>相机处的行星径向"下"(单位向量)。取不到时回退世界 Y 反方向。</summary>
        private static Vector3 GetRadialDown(Vector3 camPos)
        {
            try
            {
                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                if (craftNode?.ReferenceFrame != null)
                {
                    Vector3 center = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                    Vector3 up = camPos - center;
                    if (up.sqrMagnitude > 1e-6f) return -up.normalized;
                }
            }
            catch { }
            return Vector3.down;
        }

        private void EnsureCapacity(int count)
        {
            int need = count * 8;
            bool resize = _verts == null || _verts.Length < need;
            if (resize)
            {
                _verts = new Vector3[need];
                _uvs = new Vector2[need];
                _trisA = new int[count * 6];
                _trisB = new int[count * 6];
            }
            // 三角形索引只依赖数量(与顶点数据无关),每次重填很便宜(≤10 粒子 = 120 个 int)。
            // ⚠️ 必须在这里填而不是只在 resize 时:首次挂载时 EnsureCapacity 由 EnsureMesh 的数组
            // 提前分配、又不触发 resize,若不重填则三角形恒为 0 → 什么都画不出来。
            for (int i = 0; i < count; i++)
            {
                int v = i * 8;
                int t = i * 6;
                _trisA[t + 0] = v + 0; _trisA[t + 1] = v + 1; _trisA[t + 2] = v + 2;
                _trisA[t + 3] = v + 0; _trisA[t + 4] = v + 2; _trisA[t + 5] = v + 3;
                _trisB[t + 0] = v + 4; _trisB[t + 1] = v + 5; _trisB[t + 2] = v + 6;
                _trisB[t + 3] = v + 4; _trisB[t + 4] = v + 6; _trisB[t + 5] = v + 7;
            }
        }

        /// <summary>
        /// 心跳日志(1s 节流):内部状态 + 屏幕空间量(铁律 17/19)。
        /// 一行能看出:实例回调是否在跑(preCullCalls 是否增长)、气流方向、每粒子倾角/长宽比/退化分类。
        /// </summary>
        private void LogHeartbeat(Vector3 airDir, int count)
        {
            if (Time.realtimeSinceStartup - _lastDiagTime < 1f) return;
            _lastDiagTime = Time.realtimeSinceStartup;

            var sb = new StringBuilder();
            sb.Append("RainAxis: calls=").Append(PreCullCalls)
              .Append(" mode=").Append(Mode).Append('(').Append(ModeName(Mode)).Append(')')
              .Append(" air=").Append(airDir.ToString("F1"))
              .Append(" cam='").Append(_cam != null ? _cam.name : "?").Append('\'')
              .Append(" matA=").Append(_matA != null).Append(" matB=").Append(_matB != null)
              .Append(" |camR=").Append(LastCamRight.ToString("F2"))
              .Append(" camU=").Append(LastCamUp.ToString("F2"));
            for (int i = 0; i < count; i++)
            {
                sb.Append(" p").Append(i)
                  .Append("A[").Append(TiltDegA[i].ToString("F1")).Append('°')
                  .Append(' ').Append(AspectA[i].ToString("F1"))
                  .Append(' ').Append(DegName(DegenerateA[i])).Append(']')
                  .Append("B[").Append(TiltDegB[i].ToString("F1")).Append('°')
                  .Append(' ').Append(AspectB[i].ToString("F1"))
                  .Append(' ').Append(DegName(DegenerateB[i])).Append(']');
            }
            Mod.Diag(sb.ToString());
        }
    }
}
