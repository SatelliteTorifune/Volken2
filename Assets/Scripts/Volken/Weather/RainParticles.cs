using System;
using System.Text;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using Volken.Clouds;
using Volken.Core;

namespace Volken.Weather
{
    /// <summary>
    /// Phase 2/3 —— 最小 compute 管线驱动器 + 阶段 3 正式雨滴 shader。
    ///
    /// 【目的】(计划文档 §5 阶段 2)手写 <c>RainParticles.compute</c>(Randomize+Positioning+FillArgs),
    ///   配实例化 shader + <c>Graphics.RenderMeshIndirect</c> 把粒子画出来 ——
    ///   回答:compute → 间接绘制 → SV_InstanceID → buffer 索引 这条数据流在真机上通不通。
    ///   阶段 3 在这条数据流之上换正式雨滴 shader(SP2 第一手属性名 + 屏幕平面构轴 + 软粒子)。
    ///
    /// 【数据流】
    ///   首次分配 → Dispatch(Randomize) 初始化球域位置与随机数据
    ///   每帧(实例 OnPreCull):
    ///     CPU 复位 _ArgsBuffer(5×uint)→ 设 uniform(_time/_dt/域中心/相机基)
    ///     → Dispatch(Positioning) 积分下落+域环绕
    ///     → Dispatch(FillArgs) 原子统计活跃数 → instanceCount
    ///     → Graphics.RenderMeshIndirect(十字四边形 0.1×2.5,实例化 shader 按 SV_InstanceID 读位置)
    ///   诊断:1s 心跳 + 每 10s AsyncGPUReadback 回读 instanceCount(计划 §10.4 ④:
    ///     instanceCount==0 → 问题在 args/剔除环节,不是绘制环节)。
    ///
    /// 【阶段 3 正式版要点(sp2d4 第一手实锤后重做,§10.13)】
    ///   ① shader 属性用 SP2 第一手命名(_MainTex/_Emission/_MainColor/_InvFade);
    ///   ② **世界系构轴**(SP2 ParticleDomain.AlignStreaks 同款):C# 每帧算世界旋转矩阵
    ///      _RotationMatrix(列1 = 相对速度方向 × 拉伸)传给材质 —— 雨丝朝向锁在世界系,
    ///      转镜头/暂停/缩放都不改变它(旧屏幕平面投影是"贴相机 + 垂直看糊屏"的根因,已废弃);
    ///   ③ **位置改世界系下落**(SP2 同款:dt×(风+下落),无相机速度补偿)+ 出球重生,
    ///      域中心仍随相机(SP2 _domainPos = 相机位置)—— 这才是"独立物体"的下落感;
    ///   ④ 换帧原点重定位:ModApi IGameView.ReferenceFrameRecentered → TranslateFixed kernel
    ///      (SP2 ParticleHandler.OnFloatingOriginChanged 的等价物);
    ///   ⑤ 软粒子(采样 CloudRenderer.LinearSceneDepth);⑥ 无随机滚转角(SP2 无 roll)。
    ///
    /// 【原子加铁律(2026-09-29 真机编译错误实锤)】本游戏编译器**没有** RWByteAddressBuffer 的
    ///   4 参 InterlockedAdd;必须用带下标的 RWStructuredBuffer<uint> 2/3 参形式
    ///   (compute 里 InterlockedAdd(_ArgsBuffer[1], 1, orig))。旧"必须用 RWByteAddressBuffer"结论已纠正。
    ///   另:一个 kernel 编译失败会毒掉整个 compute,导入期 Editor.log 立刻报错,打包前必须确认。
    ///
    /// 【操作入口:天气面板「雨」分组】**没有控制台指令**(2026-09-29 用户要求删除 volkenRainP2* 系列)。
    ///   `VolkenWeather.Instance.Config.rain`(weather.xml)→ `ApplyConfig()` → 这里的静态字段(每帧读取)。
    ///   面板项:启用雨 / 立即切换雨 / 实时状态行 / 把状态写入日志(`DiagStatus`)/ 粒子数量 / 域半径 /
    ///   下落速度 / 雨丝长宽 / 拉伸 / 域边界淡出 / 软粒子 / 尾淡 / 亮度 / 朝向模式 / 出域镜像重生 /
    ///   海拔上限与带宽 / 触发阈值 / 开发:等距排自检(`TestRow`)。
    ///
    /// 【阶段 2.3 判读】TestRow 模式下屏幕上应出现一排等距色块;若全部重叠在一点
    ///   → SV_InstanceID 恒 0(改路径或查 buffer 绑定)。
    /// </summary>
    public class RainParticles : MonoBehaviour
    {
        // ================= 静态调试开关(铁律 14) =================

        /// <summary>总开关。默认关 —— 不改变现有画面。</summary>
        public static bool Enabled;

        /// <summary>阶段 2.3:1 = 相机前等距排(验证 SV_InstanceID 数据流)。</summary>
        public static bool TestRow;

        /// <summary>buffer 容量(固定;重建 buffer 才生效)。</summary>
        public static int Capacity = DefaultCapacity;

        public const int DefaultCapacity = 10000;   // 阶段 4 再按 EVE 密度重标定
        public const int ThreadGroupSize = 64;      // SP2 _threadGroupSize

        // SP2 的粒子参数(**UI/配置可调** → 静态字段;SP2 出厂值见 §10.15)
        public static float FallSpeed = 15f;         // _fallSpeed
        public static float StreakLength = 2.5f;     // _streakLength(网格长;改动需重建网格)
        public static float StreakThickness = 0.1f;  // _streakThickness(网格宽;改动需重建网格)
        public static float DomainRadius = 50f;      // _domainRadius(阶段 4 自适应重标定)
        public const float TestRowSpacing = 3.0f;    // 2.3 等距排间距(30m 处 fov20° 可见约 6~7 根)

        // 阶段 3 正式版参数(SP2 同款;**全部由天气面板调节**)
        public static float StretchAmount = 0.045f;  // SP2 _stretchAmount(相对速度 → 拉伸系数)
        public static float StretchLimit = 3.5f;     // SP2 _stretchLimit(拉伸上限;面板可调,治"太长像面条")
        public static float InvFade = 1.0f;          // SP2 _InvFade(软粒子因子;0 = 关闭软粒子)
        public static float Falloff = 0.9f;          // SP2 _falloff(尾淡/软边)
        public static float Brightness = 0f;         // 亮度增益(= shader _Emission;SP2 用 _mainLightIntensity 1.2)

        // ===== 海拔闸门(2026-09-29:JNO 与 SP2 的**相机缩放尺幅不同**所必需)=====
        // SP2 最大缩放只到"半个岛",远低于云层 → 它从不需要处理"相机在云层之上/太空"的情况
        //   (它的做法是 CloudHeightFade = Environment.CameraCloudFadeVal + 只在地面以上才更新)。
        // JNO 能把镜头缩到整颗星球 → 不加限制就会"在太空里下雨"。
        // ⚠️ 阈值是**雨自己的独立配置项**(2026-09-29 用户决定):早期阶段**不与云层联动**,
        //   不去读 CloudConfig.maxCloudHeight —— 少一层耦合、行为可预测。云层联动留到以后需要时再说。
        /// <summary>雨的海拔上限(米;**0 = 关闭闸门(不限制)**)。超过上限不再下雨。</summary>
        public static float CeilingAltitude = 12000f;

        /// <summary>上限处的淡出带宽(占上限的比例 0..1;默认 0.4 = 上限顶部 40% 渐隐到 0)。</summary>
        public static float CeilingBand = 0.4f;

        // ===== 出域处置模式(2026-09-29)=====
        // ★ 默认 false = **域内随机重生**(体积均匀、避开镜头近旁 0.15R)。
        //   随机化 = 持续混合 = 连续雨帘;这是"像真雨"的关键。
        // ⚠️ 曾经把"确定性镜像重生"设为默认(为了消除近旁爆闪),代价是**整片雨零混合**:
        //   同速下落 + 确定性处置 → 初始的每一簇粒子永远成团、周期性一起落下来,
        //   实测反馈"雨像下面条一样集中一股脑下降"(静止周期 ≈ 2R/15 ≈ 6.7s,飞行时 ≈ 0.6s = 高频脉动)。
        //   true 只保留用于对照实验。
        /// <summary>false = 域内随机重生(默认,持续混合、像真雨);true = 确定性镜像(零混合,会周期团块)。</summary>
        public static bool RespawnMirror = false;

        /// <summary>
        /// 编辑器预览用:强制指定相机海拔(米;NaN = 用真实值)。
        /// 编辑器里拿不到 craftNode/PlanetData(真实海拔返回 −1、闸门本来不生效),预览台用它试"云上/太空"场景。
        /// </summary>
        public static float DebugAltitudeOverride = float.NaN;

        /// <summary>
        /// **独立模式(编辑器预览台会置 true)**:完全不碰任何游戏 API(`Game.Instance` / craftNode / ModApi 事件)。
        /// 为什么需要:在编辑器里访问 `Game.Instance` 这类游戏单例,可能**触发游戏侧半初始化**,
        /// 刷出一堆无关报错(CelestialDatabase 找不到 / SceneManager prefab 缺失 / ArgumentNullException …)。
        /// 置 true 后各输入直接走降级值:
        ///   down = 世界 (0,-1,0);速度源 = 相机实测位移速度;海拔 = DebugAltitudeOverride(−1 = 闸门不生效);
        ///   不挂换帧重定位事件;不查"游戏相机"。
        /// </summary>
        public static bool StandaloneMode;

        /// <summary>诊断:本帧相机海拔(米;−1 = 取不到)。</summary>
        public static float LastAltitude = -1f;

        /// <summary>诊断:本帧海拔淡出系数(0 = 已完全抑制)。</summary>
        public static float LastAltitudeFade = 1f;

        /// <summary>诊断:本帧实际使用的海拔上限(米)。</summary>
        public static float LastCeiling = -1f;

        /// <summary>
        /// 阶段 3.1:雨丝朝向的速度源。1 = 沿相对速度(下落−飞行器速度;SP2 AlignStreaks 语义,默认);
        /// 0 = 恒径向"下"(雨丝永远竖直,不看速度)。
        /// ⚠️ 两者都是**世界系**朝向(C# 算世界旋转矩阵 _RotationMatrix),与相机无关 ——
        ///   转镜头/暂停/缩放都不会改变雨丝朝向("贴相机"根因见 §10.13)。
        /// </summary>
        public static bool StreamMode = true;

        /// <summary>
        /// 域边界淡出宽度(占半径比例;SP2 把 _DomainPos/_DomainRadius 传给 material,推定即此用途)。
        /// 0 = 关。默认 0.2:最外 20% 半径内渐隐,隐藏球域边界与粒子出域回收的突现。
        /// </summary>
        public static float EdgeFade = 0.2f;

        // ================= 诊断 =================

        /// <summary>实例 OnPreCull 累计触发次数。</summary>
        public static int PreCullCalls;

        /// <summary>发出间接绘制的帧计数(≈ draws/s)。</summary>
        public static int DrawCalls;

        /// <summary>最近一次 GPU 回读的 instanceCount(-1 = 尚未回读)。</summary>
        public static int LastInstanceCount = -1;

        /// <summary>换帧原点重定位(TranslateFixed)次数。</summary>
        public static int RecenterCount;

        /// <summary>ModApi 换帧重定位事件是否挂上(挂不上 = 飞远/跃迁后整片雨会跳)。</summary>
        public static bool RecenterHooked;

        /// <summary>ModApi 换帧事件触发次数(实测本环境不触发,兜底判跳变为主力)。</summary>
        public static int RecenterEventCount;

        /// <summary>兜底判定出的换帧次数(飞行器帧位置跳变)。</summary>
        public static int RecenterJumpCount;

        /// <summary>本帧飞行器帧位置跳变幅度(米;诊断"是否发生换帧")。</summary>
        public static float LastCraftJump;

        /// <summary>最近一次回读区间内的重生次数(判断回收是否在工作)。</summary>
        public static int LastRespawns = -1;

        /// <summary>最近一帧雨丝世界系朝向(归一化,诊断用)。</summary>
        public static Vector3 LastAxisDir;

        /// <summary>雨丝轴·相机前向(旧屏幕平面构轴恒 ≈0;世界系构轴随视角自由变化)。</summary>
        public static float LastAxisDotFwd;

        /// <summary>雨丝轴·径向"下"(静止时应 ≈1)。</summary>
        public static float LastAxisDotDown;

        /// <summary>
        /// 径向"下"·相机上向 —— **空间正确性的直接判据**:停机坪上镜头大致水平时真"下"应 ≈ -1;
        /// 若 ≈ 0 = down 不在渲染空间里(SR2 帧是 yaw-only,所以必须用 GravityFrameNormalized)。
        /// </summary>
        public static float LastDownDotCamUp;

        /// <summary>最近一次相机速度(诊断;阶段 4 用它驱动域半径)。</summary>
        public static float LastCamSpeed;

        /// <summary>最近一帧的雨丝拉伸倍率(诊断;阶段 3 准出"随速度拉伸")。</summary>
        public static float LastStretch = 1f;

        /// <summary>软粒子深度图当前是否就绪(CloudRenderer.LinearSceneDepth 可采样)。</summary>
        public static bool SoftDepthReady;

        /// <summary>资产就绪标记(compute/shader 是否加载到)。</summary>
        public static bool AssetsReady;

        public static string AssetsStatus = "未加载";

        // ================= 内部 =================

        private Camera _cam;
        private ComputeShader _compute;
        private Shader _shader;
        private Material _mat;
        private Mesh _mesh;
        private ComputeBuffer _positions;   // float4 × capacity
        private ComputeBuffer _randomData;  // float4 × capacity
        private ComputeBuffer _diag;        // uint × 4:x0 = 本帧重生次数(CPU 每帧复位)
        private Texture2D _streakTex;       // 程序化柔边雨丝贴图(SP2 为软 blob 贴图)
        private GraphicsBuffer _args;       // 5×uint 间接参数(RenderMeshIndirect 要 GraphicsBuffer)

        private static readonly uint[] ArgsReset = { 0, 0, 0, 0, 0 };
        private static readonly uint[] DiagReset = { 0, 0, 0, 0 };   // 诊断计数(回读后清零重启累计)
        private const float ReadbackInterval = 10f;                  // GPU 回读周期(秒)
        private const float ShaderBuild = 3f;                        // 期望的 shader 版本(与 shader 里 _ShaderVer 对齐)

        private int _kernelRandomize = -1;
        private int _kernelPositioning = -1;
        private int _kernelTranslateFixed = -1;
        private int _kernelFillArgs = -1;

        private float _lastDiagTime = -999f;
        private float _lastReadbackTime = -999f;
        private float _lastAliveLogTime = -999f;
        private bool _loggedFirstDraw;
        private Vector3 _lastCamPos;
        private bool _hasLastCamPos;
        private Vector3 _lastCamVel;              // 锁存:暂停(dt≈0)时保持最近测得的速度,流线轴不跳回竖直
        private float _lastDt;                    // 诊断:最近一帧 Time.deltaTime
        private Vector3 _lastDown;                // 诊断:最近一帧的径向"下"(看 GravityNormal 是否正常)
        private Vector3 _lastAxisVel;             // 诊断:流线轴/拉伸的速度源(飞行器本体速度,无飞行器时=camVel)
        private bool _recenterHooked;             // 换帧原点事件是否已挂
        private Vector3 _lastRecenterDelta;       // 诊断:最后一次换帧平移量
        private Vector3 _pendingRecenterDelta;    // 事件记录的待应用平移(在 Tick 里统一应用)
        private bool _hasPendingRecenter;
        private Vector3 _lastCraftFramePos;       // 兜底判定:飞行器帧位置
        private bool _hasLastCraftFramePos;
        private float _lastAltSkipLog = -999f;    // 海拔闸门抑制日志节流
        private float _lastAltWarnLog = -999f;    // 海拔取不到时的警告节流
        private Vector3 _lastReadbackPos0;        // 诊断:上次回读的 pos[0](算位移 → 判断下落是否在积分)
        private bool _hasLastReadbackPos0;

        /// <summary>十字四边形 0.1×2.5(SP2 同款:8 顶点 / 12 三角形,两个交叉面)。</summary>
        private void BuildMesh()
        {
            if (_mesh != null) return;
            _mesh = new Mesh { name = "RainParticlesMesh" };
            float hw = StreakThickness * 0.5f;   // 0.05
            float hl = StreakLength * 0.5f;      // 1.25
            _mesh.vertices = new[]
            {
                // 面 1:XY 平面(宽 X,长 Y)
                new Vector3(-hw, -hl, 0f), new Vector3(hw, -hl, 0f), new Vector3(hw, hl, 0f), new Vector3(-hw, hl, 0f),
                // 面 2:YZ 平面(宽 Z,长 Y)
                new Vector3(0f, -hl, -hw), new Vector3(0f, -hl, hw), new Vector3(0f, hl, hw), new Vector3(0f, hl, -hw),
            };
            _mesh.triangles = new[]
            {
                0, 1, 2, 0, 2, 3,
                4, 5, 6, 4, 6, 7,
            };
            // UV 全 0..1,不平铺(阶段 3.3 铁律:长度由矩阵列1 表达)。
            // ⚠️ 方向:uv.y 0=头(亮)→1=尾(淡);头 = +局部 y = +矩阵列1 = 相对运动前方
            //   (SP2 同款"亮头拖尾"),所以 +hl 顶点取 uv.y=0。
            _mesh.uv = new[]
            {
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            };
            _mesh.RecalculateBounds();
            _mesh.UploadMeshData(false);
        }

        /// <summary>
        /// 程序化生成雨丝贴图(SP2 用的是软 blob 贴图 `Resources/sprites/particle_blob_2.png` ——
        /// 这里自己生成同类"柔边 + 头亮尾淡"贴图,不搬资产):
        ///   横向 smoothstep 柔化侧边(硬边四边形是"面条感"的来源),纵向轻微头亮尾淡
        ///   (主渐变仍由 shader `_Falloff` 控制,避免叠加过淡)。
        /// uv.y=0 = 头(+轴/相对运动前方)。Texture2D 的 y=0 是底行,所以底行放亮的头。
        /// </summary>
        private void BuildStreakTexture()
        {
            if (_streakTex != null) return;
            const int W = 32, H = 64;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, true)
            {
                name = "RainParticlesStreakTex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color[W * H];
            // 贴图 = **软 blob 的长条化**(SP2 用 `Resources/sprites/particle_blob_2.png`):
            //   ① 横向:满宽软板条 —— 中段 45% 保持满不透明,外侧平滑衰减(保宽度,别变细线);
            //   ② 纵向:**快速收尾** —— 亮芯只占靠头的一段,过了 ~20% 就开始明显衰减、尾端收尖。
            // ⚠️ 两个"面条"成因都在这张图/宽度上:
            //   · 早期是"中心亮线"profile → 有效宽度只剩 ~40% → 细亮线;
            //   · 纵向亮度几乎满长(1.0→0.75)→ 拉伸 3.5 倍后是 8.75m 的**满长亮柱**;
            //   SP2 的软 blob 被拉伸后是"软芯长条",亮芯短、两端收尖 —— 这才读成雨丝而不是面条。
            const float EdgeStart = 0.45f;
            for (int y = 0; y < H; y++)
            {
                float v = y / (float)(H - 1);                 // 0 = 底行 = 头(+轴 = 相对运动前方)
                float len = 1f - Mathf.SmoothStep(0.2f, 1f, v);   // 纵向:亮芯在前段,尾端收 0
                for (int x = 0; x < W; x++)
                {
                    float u = Mathf.Abs(x / (float)(W - 1) * 2f - 1f);   // 0..1(中心→边)
                    float side = 1f - Mathf.Clamp01((u - EdgeStart) / (1f - EdgeStart));
                    side = side * side * (3f - 2f * side);               // smoothstep 软边
                    px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(side * len));
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            _streakTex = tex;
        }

        // ================= 资产与 buffer =================

        private void EnsureAssets()
        {
            if (AssetsReady) return;
            try
            {
                _compute = Mod.LoadVolkenAsset<ComputeShader>("Assets/Scripts/Volken/Weather/RainParticles.compute");
                _shader = Mod.LoadVolkenAsset<Shader>("Assets/Scripts/Volken/Weather/RainParticles.shader");
                if (_compute == null || _shader == null)
                {
                    AssetsStatus = "compute=" + (_compute != null) + " shader=" + (_shader != null) +
                                   " (检查 ModData.asset._otherAssets 是否含 RainParticles 两个资产并重建 bundle)";
                    Mod.Log("Volken:RainParticles assets missing — " + AssetsStatus);
                    return;
                }
                _kernelRandomize = _compute.FindKernel("Randomize");
                _kernelPositioning = _compute.FindKernel("Positioning");
                _kernelTranslateFixed = _compute.FindKernel("TranslateFixed");
                _kernelFillArgs = _compute.FindKernel("FillArgs");
                if (_kernelRandomize < 0 || _kernelPositioning < 0 || _kernelTranslateFixed < 0 || _kernelFillArgs < 0)
                {
                    AssetsStatus = "kernel FindKernel 失败(Randomize/Positioning/TranslateFixed/FillArgs)";
                    Mod.Log("Volken:RainParticles " + AssetsStatus);
                    return;
                }
                _mat = new Material(_shader);
                // SP2 第一手属性(阶段 3.1):_MainColor(不叫 _Color)、_Emission、_Falloff、_FadeAmount。
                // _MainTex 默认 white → 不绑贴图时退化为纯色+UV 尾淡渐变(阶段 7 再导入 SP2 billboard)。
                _mat.SetColor("_MainColor", new Color(0.72f, 0.82f, 1f, 1f));
                _mat.SetFloat("_Emission", 0f);
                _mat.SetFloat("_Falloff", Falloff);
                _mat.SetFloat("_FadeAmount", 1f);
                BuildStreakTexture();
                if (_streakTex != null) _mat.SetTexture("_MainTex", _streakTex);   // 柔边雨丝(SP2 用软 blob 贴图)
                _mat.renderQueue = 4000;
                AssetsReady = true;
                AssetsStatus = "ok(compute+shader+kernels)";
                Mod.Diag("Volken:RainParticles assets ready: {0}", AssetsStatus);
            }
            catch (Exception ex)
            {
                AssetsStatus = "加载异常: " + ex.Message;
                Mod.Log("Volken:RainParticles assets ERROR: " + ex);
            }
        }

        private void EnsureBuffers()
        {
            if (_positions != null) return;   // 已建;容量变更走 RebuildBuffers
            if (!AssetsReady || _compute == null || _mat == null) return;   // 资产未就绪时静默(修复 Awake 竞态 NRE)
            int cap = Mathf.Max(1, Capacity);
            _positions = new ComputeBuffer(cap, 16);                          // float4
            _randomData = new ComputeBuffer(cap, 16);                         // float4
            _diag = new ComputeBuffer(4, sizeof(uint));                       // 诊断计数
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, sizeof(uint));
            // ⚠️ 关键绑定:shader 顶点着色器按 SV_InstanceID 读 _Positions —— 必须绑到**材质**上,
            // RenderMeshIndirect 不会自动把 compute 的 buffer 带给材质(不绑 → 全部画在原点)。
            _mat.SetBuffer("_Positions", _positions);
            // 注:阶段 3.3 起材质不再需要 _RandomData(去掉随机滚转角,改 SP2 世界系构轴)
            // 首次分配:随机化(只跑一次 —— 绝不每帧重建/重跑)
            _compute.SetBuffer(_kernelRandomize, "_Positions", _positions);
            _compute.SetBuffer(_kernelRandomize, "_RandomData", _randomData);
            _compute.SetBuffer(_kernelPositioning, "_DiagBuffer", _diag);      // 重生计数
            _compute.SetBuffer(_kernelTranslateFixed, "_DiagBuffer", _diag);   // (TranslateFixed 不用,统一绑定防漏)
            _compute.SetInt("_capacity", cap);
            _compute.SetFloat("_domainRadius", DomainRadius);
            _compute.SetVector("_domainCenter", _cam != null ? _cam.transform.position : Vector3.zero);
            _compute.Dispatch(_kernelRandomize, Mathf.CeilToInt(cap / (float)ThreadGroupSize), 1, 1);
            Mod.Diag("Volken:RainParticles buffers created cap={0}", cap);
            LogSelfCheck(cap);   // 一次性自检日志(资产属性/内核/网格/分布密度实况)
        }

        /// <summary>重建网格(雨丝长/宽改动后调用;先销毁旧网格)。</summary>
        private void RebuildMesh()
        {
            if (_mesh != null) { Destroy(_mesh); _mesh = null; }
            BuildMesh();
        }

        private void RebuildBuffers()
        {
            ReleaseBuffers();
            EnsureBuffers();
        }

        private void ReleaseBuffers()
        {
            if (_positions != null) { _positions.Release(); _positions = null; }
            if (_randomData != null) { _randomData.Release(); _randomData = null; }
            if (_diag != null) { _diag.Release(); _diag = null; }
            if (_args != null) { _args.Release(); _args = null; }
        }

        /// <summary>
        /// 一次性自检日志:把"部署是否对得上"的全部关键事实打到日志里 ——
        /// 重点是材质属性是否存在(**旧 shader 缺 _RotationMatrix 时 SetMatrix 静默无效 → 雨丝朝向全错**,
        /// 这是最容易被"包里是旧资源"坑到的地方)、内核索引、网格规模、球域密度实况。
        /// </summary>
        private void LogSelfCheck(int cap)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("RainParticles SELFCHECK: cap=").Append(cap)
                  .Append(" domainR=").Append(DomainRadius.ToString("F0"))
                  .Append(" fallSpeed=").Append(FallSpeed)
                  .Append(" streakLen=").Append(StreakLength).Append(" thick=").Append(StreakThickness)
                  .Append(" stretchAmt=").Append(StretchAmount.ToString("F3")).Append(" limit=").Append(StretchLimit)
                  .Append(" invFade=").Append(InvFade.ToString("F2")).Append(" falloff=").Append(Falloff)
                  .Append(" stream=").Append(StreamMode ? 1 : 0)
                  .Append(" group=").Append(ThreadGroupSize);
                Mod.Diag(sb.ToString());

                // 球域密度:均匀体积分布的期望值(粒子/m³)—— 阶段 4 标定要拿它跟 EVE 0.139/m³ 比
                float vol = (4f / 3f) * Mathf.PI * DomainRadius * DomainRadius * DomainRadius;
                Mod.Diag("RainParticles SELFCHECK density: {0:F4} particles/m³ (均匀体积;SP2 参考 0.139/m³ @ 200k/半径70m)",
                    cap / Mathf.Max(1f, vol));

                Mod.Diag("RainParticles SELFCHECK kernels: Randomize={0} Positioning={1} TranslateFixed={2} FillArgs={3}",
                    _kernelRandomize, _kernelPositioning, _kernelTranslateFixed, _kernelFillArgs);

                // 资产版本判据:
                //   ① shader:读 Properties 里的 _ShaderVer 哨兵(**可靠**)—— 只在 HLSL 里声明的 uniform
                //      (_RotationMatrix/_Positions/_FadeAmount/_LinearSceneDepth)不在 Properties 表里,
                //      HasProperty 未必可见,不能用来判版本(会误报"旧 shader")。
                //   ② compute:看 TranslateFixed 内核索引是否 ≥0(上面 kernels 行)。
                // Properties 表里的 5 个(肉眼可见的)属性仍逐个确认一遍。
                string[] wanted = { "_MainTex", "_Emission", "_MainColor", "_InvFade", "_Falloff", "_ShaderVer", "_EdgeFade" };
                var miss = new StringBuilder();
                var have = new StringBuilder();
                foreach (var p in wanted)
                {
                    bool ok = _mat != null && _mat.HasProperty(p);
                    if (ok) have.Append(p).Append(' '); else miss.Append(p).Append(' ');
                }
                float shaderVer = _mat != null && _mat.HasProperty("_ShaderVer") ? _mat.GetFloat("_ShaderVer") : -1f;
                Mod.Diag("RainParticles SELFCHECK material: shaderVer={0}(期望 {1}) {2} | props ok=[{3}] missing=[{4}] | shaderSupported={5}",
                    shaderVer, ShaderBuild,
                    Mathf.Abs(shaderVer - ShaderBuild) < 0.5f ? "新版✓" : "✗包里是旧 shader!",
                    have.ToString().Trim(), miss.ToString().Trim(),
                    _shader != null && _shader.isSupported);

                Mod.Diag("RainParticles SELFCHECK mesh: verts={0} tris={1} bounds={2} shader='{3}' tex={4}",
                    _mesh != null ? _mesh.vertexCount : -1,
                    _mesh != null ? _mesh.triangles.Length / 3 : -1,
                    _mesh != null ? _mesh.bounds.size.ToString("F2") : "?",
                    _shader != null ? _shader.name : "?",
                    _streakTex != null ? (_streakTex.width + "x" + _streakTex.height + " 程序化柔边") : "white(硬边)");

                Mod.Diag("RainParticles SELFCHECK recenter: hooked={0} (ModApi IGameView.ReferenceFrameRecentered)", _recenterHooked);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles selfcheck ERROR: " + ex.Message);
            }
        }

        // ================= 挂载(幂等,与 RainAxisProbe 同模式) =================

        public static RainParticles Attach(Camera cam)
        {
            if (cam == null)
            {
                Mod.Diag("RainParticles: attach skipped — camera is null");
                return null;
            }
            try
            {
                var existing = cam.GetComponent<RainParticles>();
                if (existing != null) return existing;
                var probe = cam.gameObject.AddComponent<RainParticles>();
                Mod.Diag("RainParticles: attached to camera '{0}'", cam.name);
                return probe;
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles attach failed: " + ex.Message);
                return null;
            }
        }

        public static void AttachToCurrentView()
        {
            try
            {
                Camera cam = null;
                if (!StandaloneMode)
                {
                    try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
                    catch { }
                }
                if (cam == null)
                {
                    try { cam = Camera.main; } catch { }
                }
                Attach(cam);
                // 配置驱动的初始状态:weather.xml 里 rain.enabled=true 时自动开雨并套用参数
                // (⚠️ ApplyConfig 内部**不再**回头调 AttachToCurrentView,否则递归)
                try { ApplyConfig(VolkenWeather.Instance?.Config?.rain); }
                catch { }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles.AttachToCurrentView ERROR: " + ex.Message);
            }
        }

        // ================= 状态输出(面板按钮调用)=================

        /// <summary>
        /// 把完整状态写进 Player.log(天气面板「把雨状态写入日志」按钮;原 volkenRainP2 指令的 UI 化)。
        /// 同时顺带打印本帧关键判读项,方便用户直接把日志发出来排查。
        /// </summary>
        public static void DiagStatus()
        {
            try
            {
                Camera cam = null;
                if (!StandaloneMode)
                {
                    try { cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera; }
                    catch { }
                }
                if (cam == null) cam = Camera.main;
                if (cam != null) Attach(cam);
                Mod.Diag("RainParticles STATUS: enabled={0} testRow={1} cap={2} R={3:F0} score={4:F4}/m³ calls={5} draws={6} lastInstanceCount={7}",
                    Enabled, TestRow, Capacity, DomainRadius, DensityPerM3(), PreCullCalls, DrawCalls, LastInstanceCount);
                Mod.Diag("RainParticles STATUS params: fall={0:F1} len={1:F2} w={2:F3} stretch={3:F3} edge={4:F2} soft={5:F2} falloff={6:F2} bright={7:F2} stream={8} respawnMirror={9}",
                    FallSpeed, StreakLength, StreakThickness, StretchAmount, EdgeFade, InvFade, Falloff, Brightness, StreamMode, RespawnMirror);
                Mod.Diag("RainParticles STATUS state: alt={0:F0}m ceil={1} altFade={2:F3} stretchNow={3:F2} axis=({4:F2},{5:F2},{6:F2}) ·fwd={7:F2} down·camUp={8:F2} softDepth={9} assets='{10}'",
                    LastAltitude, LastCeiling > 0f ? LastCeiling.ToString("F0") : "off", LastAltitudeFade, LastStretch,
                    LastAxisDir.x, LastAxisDir.y, LastAxisDir.z, LastAxisDotFwd, LastDownDotCamUp, SoftDepthReady, AssetsStatus);
                Mod.Diag("RainParticles STATUS counters: respawn={0} recenter={1}(ev{2}/jp{3}) craftJump={4:F0}m camSpd={5:F1}m/s",
                    LastRespawns, RecenterCount, RecenterEventCount, RecenterJumpCount, LastCraftJump, LastCamSpeed);
                Mod.Diag("RainParticles STATUS ui: {0}", StatsLine());
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles.DiagStatus ERROR: " + ex.Message);
            }
        }

        public static void SetEnabled(int on)
        {
            if (on != 0) AttachToCurrentView();   // 先确保挂载(教训:只开开关不挂载 = 静默无效果)
            Enabled = on != 0;
            var sb = new StringBuilder();
            sb.Append("RainParticles: enabled = ").Append(Enabled)
              .Append(" | cap=").Append(Capacity)
              .Append(" domainR=").Append(DomainRadius.ToString("F0"))
              .Append(" stream=").Append(StreamMode ? 1 : 0)
              .Append(" stretchAmt=").Append(StretchAmount.ToString("F3"))
              .Append(" invFade=").Append(InvFade.ToString("F2"))
              .Append(" soft=").Append(SoftDepthReady ? "ready" : "off")
              .Append(" recenterHooked=").Append(RecenterHooked ? "yes" : "NO(事件没挂上)")
              .Append(" | 日志看:SELFCHECK 有无 missing、axis 行 ·fwd(旧屏幕平面构轴恒≈0)、dist 行 mean/R≈0.75、diag respawns/s");
            Mod.Diag(sb.ToString());
        }

        /// <summary>
        /// 阶段 2.3 自检:等距排模式(面板「开发:等距排自检」写入)。
        /// 注:参数由天气面板写入(2026-09-29 起不再有控制台指令)。
        /// </summary>

        /// <summary>
        /// 相机海拔(ASL,米)。公式与 <c>CloudRenderer.ComputeCameraAltitude</c> 一致:
        /// |camPos − 行星中心| − 行星半径(帧空间)。取不到时返回 −1(闸门按"不限制"处理)。
        /// </summary>
        private static float ComputeCameraAltitude(Camera cam)
        {
            if (StandaloneMode) return -1f;   // 独立模式:不碰 craftNode/PlanetData
            try
            {
                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                if (cam == null || craftNode == null || craftNode.ReferenceFrame == null || craftNode.Parent == null)
                    return -1f;
                Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                float surfaceRadius = (float)craftNode.Parent.PlanetData.Radius;
                return (cam.transform.position - planetCenter).magnitude - surfaceRadius;
            }
            catch
            {
                return -1f;
            }
        }

        /// <summary>
        /// 本行星适用的雨海拔上限(米;0 = 闸门关闭)。**独立配置项,不与云层联动**(用户 2026-09-29 决定)。
        /// 早期阶段保持简单可预测:要调就调这个数(配置面板同名字段),云层联动以后再考虑。
        /// </summary>
        public static float ResolveCeiling()
        {
            return CeilingAltitude > 1f ? CeilingAltitude : 0f;   // ≤1m 视为"关闭闸门"
        }

        /// <summary>当前视图相机上的雨实例(UI/配置改参数时要在它身上重建资源)。</summary>
        private static RainParticles FindCurrentInstance()
        {
            if (StandaloneMode) return null;   // 独立模式:不查游戏相机(避免触发游戏侧半初始化)
            try
            {
                var cam = Game.Instance?.FlightScene?.ViewManager?.GameView?.GameCamera?.NearCamera;
                if (cam != null) return cam.GetComponent<RainParticles>();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 把天气配置里的雨参数推到运行时(UI / weather.xml → 系统)。
        /// 需要重建资源的项自动处理:**容量 → buffer**;**雨丝长宽 → 网格**。
        /// (持续型参数在 Tick 里每帧读取静态字段,所以推一次即生效。)
        /// </summary>
        public static void ApplyConfig(VolkenWeatherConfig.RainSection cfg)
        {
            if (cfg == null) return;
            int newCap = Mathf.Clamp(Mathf.RoundToInt(cfg.amount), 1, 400000);
            float newLen = Mathf.Clamp(cfg.streakLength, 0.05f, 50f);
            float newWidth = Mathf.Clamp(cfg.streakWidth, 0.001f, 2f);
            bool capChanged = newCap != Capacity;
            bool meshChanged = !Mathf.Approximately(newLen, StreakLength) || !Mathf.Approximately(newWidth, StreakThickness);

            Capacity = newCap;
            DomainRadius = Mathf.Clamp(cfg.domainRadius, 10f, 400f);
            FallSpeed = Mathf.Clamp(cfg.fallSpeed, 0.1f, 200f);
            StretchAmount = Mathf.Max(0f, cfg.streakLength);
            StretchLimit = Mathf.Clamp(cfg.stretchLimit, 1f, 20f);
            InvFade = Mathf.Max(0f, cfg.softParticles);
            EdgeFade = Mathf.Clamp(cfg.edgeFade, 0f, 0.5f);
            StreamMode = cfg.streamMode;
            Falloff = Mathf.Clamp01(cfg.tailFalloff);
            Brightness = Mathf.Max(0f, cfg.brightness);
            CeilingAltitude = Mathf.Max(0f, cfg.ceilingAltitude);   // 0 = 关闭闸门(不限制)
            CeilingBand = Mathf.Clamp(cfg.ceilingBand, 0.02f, 1f);
            RespawnMirror = cfg.respawnMirror;
            StreakLength = newLen;
            StreakThickness = newWidth;
            Enabled = cfg.enabled;

            var inst = FindCurrentInstance();
            if (inst != null)
            {
                if (meshChanged) inst.RebuildMesh();
                if (capChanged) inst.RebuildBuffers();
                inst.EnsureBuffers();   // 资产晚到时补建(幂等;不调 AttachToCurrentView,避免与本方法互相递归)
            }
            Mod.Diag("RainParticles ApplyConfig: enabled={0} cap={1}{2} R={3:F0} fall={4:F1} len={5:F2}{6} w={7:F3} stretch={8:F3} edge={9:F2} soft={10:F2} stream={11} ceil={12} density={13:F4}/m³",
                Enabled, Capacity, capChanged ? "(重建)" : "",
                DomainRadius, FallSpeed, StreakLength, meshChanged ? "(重建)" : "", StreakThickness,
                StretchAmount, EdgeFade, InvFade, StreamMode,
                CeilingAltitude > 1f ? CeilingAltitude.ToString("F0") + "m(独立配置)" : "off(不限制)",
                DensityPerM3());
        }

        /// <summary>当前密度(粒子/m³;SP2 出厂 100000@R50 = 0.191,EVE 参考 0.139)。</summary>
        public static float DensityPerM3()
        {
            float vol = (4f / 3f) * Mathf.PI * DomainRadius * DomainRadius * DomainRadius;
            return Mathf.Max(1, Capacity) / Mathf.Max(1f, vol);
        }

        /// <summary>UI 状态行(与雷电组同风格:紧凑、纯数值 + 短标签)。</summary>
        public static string StatsLine()
        {
            if (!Enabled) return "off — 在天气面板勾选“启用雨”";
            if (LastAltitudeFade <= 0.001f)
                return string.Format("suppressed by altitude — alt {0:F0}m ≥ ceiling {1:F0}m(太空/云层之上)  R {2:F0}m  ρ {3:F3}/m³",
                    LastAltitude, Mathf.Max(1f, LastCeiling), DomainRadius, DensityPerM3());
            return string.Format(
                "inst {0}/{1}  R {2:F0}m  ρ {3:F3}/m³  respawn {4:F0}/s  stretch {5:F2}  axis {6}  edge {7:F2}  alt {8:F0}m ceil {9}  fade {10:F2}  rec {11}(ev{12}/jp{13})  cam {14:F0}m/s",
                LastInstanceCount, Capacity, DomainRadius, DensityPerM3(),
                LastRespawns / Mathf.Max(1f, ReadbackInterval), LastStretch,
                StreamMode ? "relVel" : "down", EdgeFade, LastAltitude,
                LastCeiling > 0f ? LastCeiling.ToString("F0") : "off",
                LastAltitudeFade,
                RecenterCount, RecenterEventCount, RecenterJumpCount, LastCamSpeed);
        }

        /// <summary>
        /// 阶段 3:雨丝随速度拉伸系数(SP2 _stretchAmount;0 = 不拉伸)。
        /// 注:参数由天气面板 → `ApplyConfig` 写入(2026-09-29 起不再有控制台指令)。
        /// </summary>

        /// <summary>
        /// SP2 ParticleDomain.AlignStreaks 逐行移植:把世界系朝向 dir 构造成旋转矩阵
        /// (列0=侧向1、列1=雨丝轴×拉伸、列2=侧向2),shader 用 local = M * v.vertex 定位十字四边形。
        /// 拉伸 = clamp(|dir| * _stretchAmount, 1, _stretchLimit),同 SP2。
        /// </summary>
        private static Matrix4x4 BuildStreakMatrix(Vector3 dir, out float stretch)
        {
            Vector3 n = dir.normalized;
            // SP2:参考轴取世界 up;n 与 up 近于平行(|n.y|>=0.999)时改用 forward 避免叉乘退化
            Vector3 reference = (Mathf.Abs(n.y) < 0.999f) ? Vector3.up : Vector3.forward;
            Vector3 side1 = Vector3.Cross(reference, n).normalized;
            Vector3 side2 = Vector3.Cross(n, side1);
            stretch = Mathf.Clamp(dir.magnitude * StretchAmount, 1f, StretchLimit);

            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(side1.x, side1.y, side1.z, 0f));
            m.SetColumn(1, new Vector4(n.x * stretch, n.y * stretch, n.z * stretch, 0f));
            m.SetColumn(2, new Vector4(side2.x, side2.y, side2.z, 0f));
            m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return m;
        }

        /// <summary>
        /// 换帧原点重定位(ModApi <c>IGameView.ReferenceFrameRecentered</c>):世界系下落的位置存在
        /// 帧空间坐标里,参考系重定位时整个世界平移了 positionDelta,粒子必须同步平移,否则整片雨
        /// 相对世界"跳"走(实测:雨被留在原地 → 全部粒子瞬间出域 → 4 次/秒批量重生)。
        /// ⚠️ 这里**只记录不应用**:ModApi 文档明确警告"订阅了 ReferenceFrameRecentered 就不要在
        ///   逐节点回调里再加 delta,否则会加两次"。统一由 <see cref="DetectRecenterJump"/> 在 Tick 里
        ///   应用一次(事件 delta 优先,事件没来时用飞行器帧位置跳变兜底)。
        /// ⚠️ 实测这个事件在本 mod 环境下**不触发**(rec=0 但相机帧坐标跳了 0.5~1.6km),所以兜底是主力。
        /// </summary>
        private void OnReferenceFrameRecentered(IReferenceFrame referenceFrame, Vector3d positionDelta, Vector3d velocityDelta)
        {
            try
            {
                var delta = new Vector3((float)positionDelta.x, (float)positionDelta.y, (float)positionDelta.z);
                if (!IsFinite(delta) || delta.sqrMagnitude < 1e-8f) return;
                _pendingRecenterDelta = delta;
                _hasPendingRecenter = true;
                RecenterEventCount++;
                Mod.Diag("RainParticles RECENTER(event): delta=({0:F1},{1:F1},{2:F1}) |d|={3:F1}m (延迟到 Tick 应用,避免双重平移)",
                    delta.x, delta.y, delta.z, delta.magnitude);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles recenter(event) ERROR: " + ex.Message);
            }
        }

        /// <summary>
        /// 换帧重定位的**兜底判定**:飞行器是"世界物体",换帧时它的帧位置会整体跳变(而它自己并没有动)。
        /// 判据:本帧飞行器帧位置跳变幅度 &gt; max(100m, 自身运动×3 + 30m) → 判定为换帧。
        /// 应用 delta = 飞行器帧位置跳变(即世界的平移量,与 ModApi positionDelta 同义)。
        /// 返回 true 表示本帧已应用(调用方跳过后续逻辑)。
        /// </summary>
        private bool DetectRecenterJump(Vector3 craftFramePos, float dt, out Vector3 delta)
        {
            delta = Vector3.zero;
            if (!_hasLastCraftFramePos)
            {
                _lastCraftFramePos = craftFramePos;
                _hasLastCraftFramePos = true;
                return false;
            }
            Vector3 jump = craftFramePos - _lastCraftFramePos;
            _lastCraftFramePos = craftFramePos;
            float jumpMag = jump.magnitude;
            LastCraftJump = jumpMag;
            if (jumpMag < 0.01f) return false;

            float selfMotion = _lastAxisVel.magnitude * Mathf.Max(0f, dt);   // 本帧飞行器自身运动上限
            float threshold = Mathf.Max(100f, selfMotion * 3f + 30f);
            if (jumpMag <= threshold) return false;

            delta = jump;
            RecenterJumpCount++;
            Mod.Diag("RainParticles RECENTER(jump): |d|={0:F1}m threshold={1:F0}m dt={2:F4} craftSpd={3:F1} → TranslateFixed",
                jumpMag, threshold, dt, _lastAxisVel.magnitude);
            return true;
        }

        /// <summary>应用一次换帧平移(TranslateFixed kernel)。</summary>
        private void ApplyRecenter(Vector3 delta)
        {
            try
            {
                if (_positions == null || _compute == null || _kernelTranslateFixed < 0) return;
                if (!IsFinite(delta) || delta.sqrMagnitude < 1e-8f) return;
                int cap = Mathf.Max(1, Capacity);
                _compute.SetBuffer(_kernelTranslateFixed, "_Positions", _positions);
                _compute.SetVector("_translation", delta);
                _compute.SetInt("_capacity", cap);
                _compute.Dispatch(_kernelTranslateFixed, Mathf.CeilToInt(cap / (float)ThreadGroupSize), 1, 1);
                RecenterCount++;
                _lastRecenterDelta = delta;
                _hasLastCamPos = false;   // 那一帧相机位置会突跳,别污染 camVel 诊断
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles recenter apply ERROR: " + ex.Message);
            }
        }

        private void HookRecenter()
        {
            if (StandaloneMode) return;   // 独立模式(编辑器预览):不碰游戏事件
            if (_recenterHooked) return;
            try
            {
                Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered += OnReferenceFrameRecentered;
                _recenterHooked = true;
                RecenterHooked = true;
                Mod.Diag("RainParticles: recenter hook ok (ModApi IGameView.ReferenceFrameRecentered)");
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles recenter hook failed: " + ex.Message);
            }
        }

        private void UnhookRecenter()
        {
            if (!_recenterHooked) return;
            try { Game.Instance.FlightScene.ViewManager.GameView.ReferenceFrameRecentered -= OnReferenceFrameRecentered; }
            catch { }
            _recenterHooked = false;
            RecenterHooked = false;
        }

        /// <summary>Vector3 是否有限(NaN/Inf → false)。</summary>
        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                  || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        /// <summary>
        /// 阶段 3.2:域边界淡出宽度(0 = 关;默认 0.2 = 最外 20% 半径渐隐)。
        /// SP2 把 _DomainPos/_DomainRadius 传给 material,推定用于隐藏球域边界与回收突现
        /// —— 与"镜像重生"配套:新粒子在 50m 外的边界渐显,而非在近旁爆闪。
        /// 注:参数由天气面板 → `ApplyConfig` 写入(2026-09-29 起不再有控制台指令)。
        /// </summary>

        // ================= 生命周期 =================

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            BuildMesh();
            EnsureAssets();
            HookRecenter();                     // 换帧原点重定位(世界系下落必须同步平移)
            if (AssetsReady) EnsureBuffers();   // 资产未就绪时留到 OnPreCull 的 NOT READY 心跳里等(修复场景切换瞬间 NRE)
        }

        private void OnDestroy()
        {
            UnhookRecenter();
            ReleaseBuffers();
            if (_mesh != null) Destroy(_mesh);
            if (_mat != null) Destroy(_mat);
            if (_streakTex != null) Destroy(_streakTex);
        }

        // ================= 每帧 =================

        private void OnPreCull()
        {
            PreCullCalls++;
            if (!Enabled)
            {
                if (Time.realtimeSinceStartup - _lastAliveLogTime > 5f)
                {
                    _lastAliveLogTime = Time.realtimeSinceStartup;
                    Mod.Diag("RainParticles alive: calls={0} enabled=OFF assets='{1}' (在天气面板「雨」里勾选启用)", PreCullCalls, AssetsStatus);
                }
                return;
            }
            if (_cam == null || _compute == null || _positions == null || _args == null)
            {
                if (Time.realtimeSinceStartup - _lastAliveLogTime > 5f)
                {
                    _lastAliveLogTime = Time.realtimeSinceStartup;
                    Mod.Diag("RainParticles: enabled but NOT READY — cam={0} compute={1} positions={2} args={3}",
                        _cam != null, _compute != null, _positions != null, _args != null);
                }
                return;
            }
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Mod.LogThrottled("RainParticles", ex);
            }
        }

        private void Tick()
        {
            Vector3 camPos = _cam.transform.position;
            Vector3 camRight = _cam.transform.right;
            Vector3 camUp = _cam.transform.up;
            Vector3 camFwd = _cam.transform.forward;
            float dt = Mathf.Max(0f, Time.deltaTime);
            _lastDt = dt;
            float now = Time.realtimeSinceStartup;

            // 相机速度(**仅诊断**:位置积分已改世界系下落,雨丝朝向用飞行器本体速度,都不吃 camVel)
            // ⚠️ 暂停(dt≈0)时无法测量 → 锁存最近一次测得值,心跳显示不跳 0(真机判读用)。
            Vector3 camVel = _lastCamVel;
            if (_hasLastCamPos && dt > 1e-4f)
            {
                camVel = (camPos - _lastCamPos) / dt;
                _lastCamVel = camVel;
                LastCamSpeed = camVel.magnitude;
            }
            _lastCamPos = camPos;
            _hasLastCamPos = true;

            // 径向"下" = 指向行星中心(帧空间)= craft.GravityNormal。
            // ⚠️ 坐标系铁律(jnoCode ReferenceFrame/GravityNormal 实锤):世界(帧)空间是
            //   浮点原点+绕行星Y轴旋转,重力径向 → "下"随球面位置变,不是世界 (0,-1,0);
            //   球面赤道处径向"下"≈世界水平,写死世界下会下错方向。
            Vector3 down = Vector3.down;
            if (!StandaloneMode)
            {
                try
                {
                    var node = Game.Instance?.FlightScene?.CraftNode;
                    if (node != null && node.CraftScript != null) down = node.CraftScript.GravityNormal;
                }
                catch { }
            }
            if (down.sqrMagnitude < 1e-6f) down = Vector3.down;   // 深空/无重力兜底(避免 NaN)
            down.Normalize();
            _lastDown = down;

            // 流线轴/拉伸的速度源:用飞行器本体速度(craft.FrameVelocity,帧空间,平移分量),
            // **不用相机位移差 camVel** —— camVel 在视角旋转(绕飞/转头)时混入切向分量,
            //   会让雨丝朝向随摄像机角速度摆动(真机反馈 2026-09-29)。无飞行器(深空/观察者)回退 camVel。
            Vector3 axisVel = camVel;
            if (!StandaloneMode)
            {
                try
                {
                    var cs = Game.Instance?.FlightScene?.CraftNode?.CraftScript;
                    if (cs != null) axisVel = cs.FrameVelocity;
                }
                catch { }
            }
            if (!IsFinite(axisVel)) axisVel = camVel;
            _lastAxisVel = axisVel;

            // 换帧原点重定位:每次 Tick 开头**统一应用一次**(事件 delta 优先 → 兜底用飞行器帧位置跳变)。
            //   世界系下落的位置存在帧空间坐标里,不跟着平移 → 雨被留在原地 → 全部粒子出域批量重生
            //   (真机实测:rec=0 但相机帧坐标跳 0.5~1.6km,重生率飙到 4 万/s)。
            Vector3 craftFramePos = Vector3.zero;
            bool hasCraftFramePos = false;
            if (!StandaloneMode)
            {
                try
                {
                    var cs2 = Game.Instance?.FlightScene?.CraftNode?.CraftScript;
                    if (cs2 != null) { craftFramePos = cs2.FramePosition; hasCraftFramePos = true; }
                }
                catch { }
            }

            // ===== 海拔闸门(JNO 能把镜头缩到整颗星球,SP2 只到半个岛)=====
            //   上限 = **雨自己的配置项** CeilingAltitude(米;0 = 关闭闸门,不与云层联动);
            //   在 [上限×(1−带宽), 上限] 内线性淡出。
            //   超过上限/进入太空 → 直接不 dispatch、不绘制(不在太空下雨,且省性能)。
            float camAlt = float.IsNaN(DebugAltitudeOverride)
                ? (StandaloneMode ? -1f : ComputeCameraAltitude(_cam))
                : DebugAltitudeOverride;
            float ceiling = ResolveCeiling();
            float band = Mathf.Clamp(CeilingBand, 0.02f, 1f);
            bool gateOn = ceiling > 1f;
            float altFade = (!gateOn || camAlt < 0f)
                ? 1f
                : Mathf.Clamp01((ceiling - camAlt) / Mathf.Max(1f, ceiling * band));
            LastAltitude = camAlt;
            LastCeiling = gateOn ? ceiling : -1f;
            LastAltitudeFade = altFade;
            if (camAlt < 0f && now - _lastAltWarnLog > 30f)
            {
                _lastAltWarnLog = now;
                Mod.Log("Volken:RainParticles 海拔取不到(craftNode/ReferenceFrame/PlanetData 缺失)→ 高度闸门暂不生效,雨在太空也会画");
            }
            if (altFade <= 0.001f)
            {
                // ⚠️ 抑制期间必须同步换帧状态:否则恢复下雨时,抑制期间累积的帧位置跳变会被
                //    DetectRecenterJump 误判成一次换帧,把整片雨甩飞(雨会在域外 → 整批重生)。
                if (hasCraftFramePos) { _lastCraftFramePos = craftFramePos; _hasLastCraftFramePos = true; }
                _hasPendingRecenter = false;
                _pendingRecenterDelta = Vector3.zero;
                if (now - _lastAltSkipLog > 5f)
                {
                    _lastAltSkipLog = now;
                    Mod.Diag("RainParticles: 高度闸门 → 不绘制(alt={0:F0}m 上限={1:F0}m 带宽={2:F2})太空/云层之上不下雨",
                        camAlt, ceiling, band);
                }
                return;
            }
            if (_hasPendingRecenter)
            {
                _hasPendingRecenter = false;
                ApplyRecenter(_pendingRecenterDelta);
                _pendingRecenterDelta = Vector3.zero;
            }
            else if (hasCraftFramePos && DetectRecenterJump(craftFramePos, dt, out Vector3 jumpDelta))
            {
                ApplyRecenter(jumpDelta);
            }

            int cap = Mathf.Max(1, Capacity);
            int amount = TestRow ? 20 : cap;   // 2.3 等距排只画一小排(20 粒,30m 外 3m 间距)

            // 1) 复位间接参数(indexCountPerInstance = 12,instanceCount = 0)
            ArgsReset[0] = 12;
            _args.SetData(ArgsReset);
            // 注:诊断计数 _diag 不在这里清零 —— 让它累计到下一次回读(得到精确的"这 10s 重生多少次"),
            //     回读回调里再清零重启累计。

            // 2) Positioning:积分 + 域环绕(或 2.3 等距排覆盖)
            // 注:SetFloat/SetInt/SetVector 无 per-kernel 重载(属性是全局共享的),只传名字。
            _compute.SetBuffer(_kernelPositioning, "_Positions", _positions);
            _compute.SetBuffer(_kernelPositioning, "_RandomData", _randomData);   // 防御:防未绑定读让 kernel 空转
            _compute.SetFloat("_time", Time.time);
            _compute.SetFloat("_dt", dt);
            _compute.SetFloat("_fallSpeed", FallSpeed);
            _compute.SetVector("_downDir", down);
            _compute.SetFloat("_domainRadius", DomainRadius);
            _compute.SetVector("_domainCenter", camPos);
            _compute.SetInt("_capacity", cap);
            _compute.SetInt("_amount", amount);
            _compute.SetInt("_testRow", TestRow ? 1 : 0);
            _compute.SetFloat("_testRowSpacing", TestRowSpacing);
            _compute.SetInt("_respawnMode", RespawnMirror ? 1 : 0);
            _compute.SetVector("_camRight", camRight);
            _compute.SetVector("_camUp", camUp);
            _compute.SetVector("_camFwd", camFwd);
            _compute.Dispatch(_kernelPositioning, Mathf.CeilToInt(cap / (float)ThreadGroupSize), 1, 1);

            // 3) FillArgs:统计活跃数(原子加,复位已在 1)完成)
            _compute.SetBuffer(_kernelFillArgs, "_ArgsBuffer", _args);
            _compute.SetInt("_amount", amount);
            _compute.Dispatch(_kernelFillArgs, Mathf.CeilToInt(amount / (float)ThreadGroupSize), 1, 1);

            // 4) 间接绘制:十字四边形 × instanceCount,实例化 shader 按 SV_InstanceID 读位置
            if (_mat != null)
            {
                // 阶段 3(§10.13 重做):**世界系构轴** —— SP2 ParticleDomain.AlignStreaks 同款。
                //   朝向 = 相对速度方向(下落 − 飞行器速度;StreamMode=0 时 = 径向"下"),
                //   拉伸烘焙进矩阵列1,雨丝朝向完全锁在世界系 → 转镜头/暂停/缩放都不改变朝向
                //   (旧屏幕平面投影实现已废弃,那是"贴相机 + 垂直看糊屏"的根因)。
                Vector3 streakDir = StreamMode
                    ? down * FallSpeed - new Vector3(axisVel.x, Mathf.Abs(axisVel.y), axisVel.z)  // SP2 对 y 取绝对值(俯冲时雨丝不翻转)
                    : down;
                if (streakDir.sqrMagnitude < 1e-6f) streakDir = down;
                _mat.SetMatrix("_RotationMatrix", BuildStreakMatrix(streakDir, out LastStretch));
                // 域边界淡出(SP2 material 同名 uniform:_DomainPos/_DomainRadius):
                //   粒子在球域外围渐隐 → 球域边界与"出域回收"的突现都不显形(面板「域边界淡出」调 0 可关)。
                _mat.SetVector("_DomainPos", camPos);
                _mat.SetFloat("_DomainRadius", DomainRadius);
                _mat.SetFloat("_EdgeFade", EdgeFade);
                // 诊断:雨丝世界系朝向(归一化)+ 它和相机前向的夹角余弦 ——
                //   axFwd≈0 恒成立 = 雨丝被压在屏幕平面里(旧屏幕平面构轴的指纹);
                //   世界系构轴下 axFwd 随视角变化(这正是"朝向锁世界、不贴相机"的证据)。
                LastAxisDir = streakDir.normalized;
                LastAxisDotFwd = Vector3.Dot(LastAxisDir, camFwd);
                LastAxisDotDown = Vector3.Dot(LastAxisDir, down);
                LastDownDotCamUp = Vector3.Dot(down, camUp);   // ≈ -1 = down 确实指向画面下方(验证空间正确)

                // 阶段 3:软粒子 —— 采样 CloudRenderer 的线性场景深度(RFloat,LinearEyeDepth 米)。
                //   深度图未就绪(云渲染器未挂/未创建)时置 _InvFade=0 → shader 跳过采样,退化为普通透明。
                RenderTexture depthTex = null;
                try
                {
                    var cr = _cam.GetComponent<CloudRenderer>();
                    if (cr != null) depthTex = cr.LinearSceneDepth;
                }
                catch { }
                SoftDepthReady = depthTex != null && depthTex.IsCreated();
                _mat.SetFloat("_InvFade", SoftDepthReady ? InvFade : 0f);
                if (SoftDepthReady) _mat.SetTexture("_LinearSceneDepth", depthTex);

                // 海拔淡出(阶段 5 的天气淡入淡出后续乘进来)
                _mat.SetFloat("_FadeAmount", altFade);
                _mat.SetFloat("_Emission", Brightness);
            }
            var rp = new RenderParams(_mat)
            {
                camera = _cam,
                layer = 0,
                worldBounds = new Bounds(camPos, Vector3.one * (DomainRadius * 2f + 100f)),   // 视锥剔除用,必须罩住粒子
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMeshIndirect(rp, _mesh, _args, 1, 0);
            DrawCalls++;

            // 5) 诊断:首次绘制 + 心跳 + GPU 回读
            if (!_loggedFirstDraw)
            {
                _loggedFirstDraw = true;
                Mod.Diag("RainParticles first draw: cap={0} amount={1} testRow={2} cam='{3}' mat={4} verts={5}",
                    cap, amount, TestRow, _cam.name, _mat != null, _mesh.vertexCount);
            }
            LogHeartbeat(amount, now);
            if (now - _lastReadbackTime > ReadbackInterval)
            {
                _lastReadbackTime = now;
                RequestReadbacks();
            }
        }

        /// <summary>GPU 回读:args.instanceCount + 诊断计数 + 前 64 个位置(分布实况)。</summary>
        private void RequestReadbacks()
        {
            try
            {
                AsyncGPUReadback.Request(_args, OnArgsReadback);
                AsyncGPUReadback.Request(_positions, 64 * 16, 0, OnPositionsReadback);   // 前 64 粒(size/offset 字节)
                if (_diag != null) AsyncGPUReadback.Request(_diag, OnDiagReadback);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles readback request ERROR: " + ex.Message);
            }
        }

        private void OnDiagReadback(AsyncGPUReadbackRequest req)
        {
            try
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                if (data.Length < 1) return;
                LastRespawns = (int)data[0];
                Mod.Diag("RainParticles diag: respawns={0} in last {1:F0}s → {2:F0}/s  (恒 0 且游戏在跑 = 粒子从不离域,回收没在工作)",
                    LastRespawns, ReadbackInterval, LastRespawns / Mathf.Max(1f, ReadbackInterval));
                if (_diag != null) _diag.SetData(DiagReset);   // 重启累计(数据已拷到 CPU,安全)
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles diag readback ERROR: " + ex.Message);
            }
        }

        private void OnArgsReadback(AsyncGPUReadbackRequest req)
        {
            try
            {
                if (req.hasError)
                {
                    Mod.Diag("RainParticles readback ERROR (hasError)");
                    return;
                }
                var data = req.GetData<uint>();
                if (data.Length >= 5)
                {
                    LastInstanceCount = (int)data[1];   // instanceCount
                    Mod.Diag("RainParticles readback: indexCount={0} instanceCount={1}", data[0], data[1]);
                }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles readback ERROR: " + ex.Message);
            }
        }

        private void OnPositionsReadback(AsyncGPUReadbackRequest req)
        {
            try
            {
                if (req.hasError) return;
                var data = req.GetData<Vector4>();
                if (data.Length < 3) return;
                // 位移量:与上次回读的 pos[0] 相比 —— 正常下落 10s 应移动 ≈150m(或被回收后相位变化);
                //   恒 0 且 w0 在变 = 粒子没动(查 dt/down);恒 0 且 w0 恒不变 = kernel 没在跑(或游戏暂停)。
                float pos0Delta = _hasLastReadbackPos0 ? ((Vector3)data[0] - _lastReadbackPos0).magnitude : -1f;
                _lastReadbackPos0 = data[0];
                _hasLastReadbackPos0 = true;

                // 球域分布实况:对前 N 粒统计到域中心(=相机)的距离 ——
                //   均匀体积分布时 mean/R ≈ 0.75、max≈R、min 可以很小;
                //   若 mean/R 明显偏小(≈0.5)且大量粒子挤在近处 → 半径还是 R*u(中心堆积),分布没生效。
                Vector3 center = _cam != null ? _cam.transform.position : Vector3.zero;
                int n = Mathf.Min(data.Length, 64);
                float dmin = float.MaxValue, dmax = 0f, dsum = 0f;
                int near5 = 0, mid25 = 0, beyond = 0;
                for (int i = 0; i < n; i++)
                {
                    float dd = ((Vector3)data[i] - center).magnitude;
                    dmin = Mathf.Min(dmin, dd); dmax = Mathf.Max(dmax, dd); dsum += dd;
                    if (dd < DomainRadius * 0.5f) near5++;
                    else if (dd < DomainRadius) mid25++;
                    else beyond++;   // > R = 还没被回收(或回读滞后一帧)
                }
                float dmean = dsum / Mathf.Max(1, n);

                Mod.Diag("RainParticles pos[0..2]: ({0:F1},{1:F1},{2:F1}) ({3:F1},{4:F1},{5:F1}) ({6:F1},{7:F1},{8:F1}) testRow={9} pos0Δ={10:F1} w0={11:F1}",
                    data[0].x, data[0].y, data[0].z,
                    data[1].x, data[1].y, data[1].z,
                    data[2].x, data[2].y, data[2].z, TestRow, pos0Delta, data[0].w);
                Mod.Diag("RainParticles dist[{0}]: min={1:F1} mean={2:F1} max={3:F1} (R={4:F0}, 均匀体积期望 mean/R≈0.75) near<R/2={5} mid={6} beyondR={7} respawn={8}",
                    n, dmin, dmean, dmax, DomainRadius, near5, mid25, beyond, LastRespawns);
                // w0 与当前 Time.time 对比:w0 远落后 → kernel 没在跑;w0 恒不变但 Time.time 也在冻结 → 游戏暂停
                Mod.Diag("RainParticles time: kernelW0={0:F1} nowTime={1:F1} delta={2:F1} (delta≈0 = kernel 在跑;越大越滞后)",
                    data[0].w, Time.time, Time.time - data[0].w);
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles positions readback ERROR: " + ex.Message);
            }
        }

        private void LogHeartbeat(int amount, float now)
        {
            if (now - _lastDiagTime < 1f) return;
            _lastDiagTime = now;
            var sb = new StringBuilder();
            sb.Append("RainParticles: calls=").Append(PreCullCalls)
              .Append(" draws=").Append(DrawCalls)
              .Append(" cap=").Append(Capacity)
              .Append(" amount=").Append(amount)
              .Append(" testRow=").Append(TestRow)
              .Append(" domainR=").Append(DomainRadius.ToString("F0"))
              .Append(" camSpd=").Append(LastCamSpeed.ToString("F1"))
              .Append(" craftSpd=").Append(_lastAxisVel.magnitude.ToString("F1"))
              .Append(" dt=").Append(_lastDt.ToString("F4"))
              .Append(" down=(").Append(_lastDown.x.ToString("F2")).Append(',').Append(_lastDown.y.ToString("F2")).Append(',').Append(_lastDown.z.ToString("F2")).Append(')')
              .Append(" stretch=").Append(LastStretch.ToString("F2"))
              .Append(" stream=").Append(StreamMode ? 1 : 0)
              .Append(" alt=").Append(LastAltitude.ToString("F0")).Append("m/ceil=").Append(LastCeiling.ToString("F0"))
              .Append(" altFade=").Append(LastAltitudeFade.ToString("F3"))
              .Append(" rec=").Append(RecenterCount)
              .Append("(ev").Append(RecenterEventCount).Append("/jp").Append(RecenterJumpCount).Append(')')
              .Append("/").Append(_lastRecenterDelta.magnitude.ToString("F0"))
              .Append(" craftJump=").Append(LastCraftJump.ToString("F0"))
              .Append(" soft=").Append(SoftDepthReady ? InvFade.ToString("F1") : "off")
              .Append(" readback=").Append(LastInstanceCount)
              .Append(" assets='").Append(AssetsStatus).Append('\'')
              .Append(" cam='").Append(_cam != null ? _cam.name : "?").Append('\'');
            Mod.Diag(sb.ToString());

            // 第二行:朝向自检(世界系构轴 vs 旧屏幕平面构轴的判定指纹)
            //   axFwd = 雨丝轴·相机前向。旧屏幕平面构轴把轴压在屏幕平面内 → axFwd 恒 ≈0;
            //   世界系构轴下 axFwd 随视角自由变化;垂直向下看时 |axFwd|→1(雨丝与视线平行 = 点状,不糊屏)。
            Mod.Diag("RainParticles axis: dir=({0:F2},{1:F2},{2:F2}) stretch={3:F2} ·fwd={4:F2} ·down={5:F2} down·camUp={6:F2} edge={7:F2} shaderVer={8}",
                LastAxisDir.x, LastAxisDir.y, LastAxisDir.z, LastStretch, LastAxisDotFwd, LastAxisDotDown,
                LastDownDotCamUp, EdgeFade,
                _mat != null ? _mat.GetFloat("_ShaderVer").ToString("F0") : "?");
        }
    }
}
