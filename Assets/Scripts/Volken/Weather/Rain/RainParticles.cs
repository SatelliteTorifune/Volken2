using System;
using System.Text;
using Assets.Scripts;
using ModApi.Craft;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using Volken.Clouds;

namespace Volken.Weather
{
    /// <summary>
    /// 雨粒子:手写 compute(Randomize/Positioning/FillArgs/TranslateFixed)+ 实例化雨丝 shader,经 Graphics.RenderMeshIndirect 绘制。
    ///  雨丝朝向必须**世界系**:C# 每帧算世界旋转矩阵 _RotationMatrix 传给材质 —— 不要用屏幕平面投影(贴相机,垂直看会糊成横块)。
    ///  本游戏编译器**没有** RWByteAddressBuffer 的 4 参 InterlockedAdd,必须用带下标的 RWStructuredBuffer&lt;uint&gt; 2/3 参形式;一个 kernel 编译失败会毒掉整个 compute。
    /// 设计沿革与九轮踩坑见 docs/sp2-rain-particledomain-port-2026-09-28.md §10。
    /// </summary>
    public class RainParticles : MonoBehaviour
    {

        public static bool Enabled;
        public static bool TestRow;                     // 1 = 画相机前等距排;全挤在一点 = SV_InstanceID 恒 0(查 buffer 绑定)
        public static int Capacity = DefaultCapacity;   // buffer 容量(重建 buffer 才生效)

        public const int DefaultCapacity = 10000;   // 出厂值(后续按 EVE 密度重标定)
        public const int ThreadGroupSize = 64;      // SP2 _threadGroupSize

        // 粒子参数(**UI/配置可调** → 静态字段;每帧读,推一次即生效)
        public static float FallSpeed = 15f;         // _fallSpeed(m/s)
        public static float StreakLength = 2.5f;     // _streakLength(网格长;改动需重建网格)
        public static float StreakThickness = 0.1f;  // _streakThickness(网格宽;改动需重建网格)
        public static float DomainRadius = 50f;      // _domainRadius(米)
        public const float TestRowSpacing = 3.0f;    // 等距排间距(30m 处 fov20° 可见约 6~7 根)

        // 雨丝外观参数(SP2 同款;**全部由天气面板调节**)
        public static float StretchAmount = 0.045f;  // SP2 _stretchAmount(相对速度 → 拉伸系数)
        public static float StretchLimit = 3.5f;     // SP2 _stretchLimit(拉伸上限)
        public static float InvFade = 1.0f;          // SP2 _InvFade(软粒子因子;0 = 关闭软粒子)
        public static float Falloff = 0.9f;          // SP2 _falloff(尾淡/软边)
        public static float Brightness = 0f;         // 亮度增益(= shader _Emission)

        // 海拔闸门(JNO 镜头能缩到整颗星球,不加限制会在太空里下雨)
        //  上限是**雨自己的独立配置项**:不与云层联动,不去读 CloudConfig.maxCloudHeight(少一层耦合、行为可预测)。
        public static float CeilingAltitude = 12000f;   // 米;0 = 关闭闸门(不限制)
        public static float CeilingBand = 0.4f;         // 上限处的淡出带宽(占上限比例;0.4 = 顶部 40% 渐隐到 0)

        // 音频侧参数(消费者 = RainAudio;粒子渲染不读):放这里是为了让天气面板与编辑器预览台共用同一份参数。
        public static float Strength = 1f;              // 强度倍率(与 Capacity 一起决定小雨/暴雨音效混合)
        public static float Volume = 0.5f;              // 雨声音量(0 = 静音)

        // ★ 默认 false = **域内随机重生**(体积均匀、避开镜头近旁 0.15R);随机化 = 持续混合 = 连续雨帘。
        //  true = 确定性镜像重生会让整片雨**零混合**:每簇粒子同速下落 → 周期性团块("像下面条一样集中一股脑下降"),只用于对照实验。
        public static bool RespawnMirror = false;

        /// <summary>编辑器预览用:强制指定相机海拔(米;NaN = 用真实值)。</summary>
        public static float DebugAltitudeOverride = float.NaN;

        /// <summary>
        /// **独立模式(编辑器预览台置 true)**:完全不碰任何游戏 API(Game.Instance / craftNode / ModApi 事件)——
        /// 在编辑器里访问游戏单例可能**触发游戏侧半初始化**,刷出一堆无关报错。
        /// 置 true 后各输入直接走降级值:down = 世界 (0,-1,0);速度源 = 相机实测位移;海拔 = DebugAltitudeOverride(−1 = 闸门不生效);不挂换帧事件、不查"游戏相机"。
        /// </summary>
        public static bool StandaloneMode;

        public static float LastAltitude = -1f;        // 诊断:本帧相机海拔(米;−1 = 取不到 → 闸门不生效)
        public static float LastAltitudeFade = 1f;     // 诊断:本帧海拔淡出系数(0 = 已完全抑制)
        public static float LastCeiling = -1f;         // 诊断:本帧实际使用的海拔上限(米)

        /// <summary>雨丝朝向的速度源:true = 沿相对速度(下落 − 飞行器速度);false = 恒径向"下"(雨丝永远竖直)。两者都是**世界系**朝向,与相机无关。</summary>
        public static bool StreamMode = true;

        public static float EdgeFade = 0.2f;   // 域边界淡出宽度(占半径比例;0 = 关);隐藏球域边界与出域回收的突现

        public static int PreCullCalls;            // 实例 OnPreCull 累计触发次数
        public static int DrawCalls;               // 发出间接绘制的帧计数(≈ draws/s)
        public static int LastInstanceCount = -1;  // 最近一次 GPU 回读的 instanceCount(−1 = 尚未回读)
        public static int RecenterCount;           // 换帧原点重定位(TranslateFixed)次数
        public static bool RecenterHooked;         // ModApi 换帧重定位事件是否挂上(挂不上 = 飞远/跃迁后整片雨会跳)
        public static int RecenterEventCount;      // ModApi 换帧事件触发次数(本环境实测不触发,兜底判跳变为主力)
        public static int RecenterJumpCount;       // 兜底判定出的换帧次数(飞行器帧位置跳变)
        public static float LastCraftJump;         // 本帧飞行器帧位置跳变幅度(米)
        public static int LastRespawns = -1;       // 最近一次回读区间内的重生次数(判回收是否在工作;恒 0 = 粒子从不离域)
        public static Vector3 LastAxisDir;         // 最近一帧雨丝世界系朝向(归一化)
        public static float LastAxisDotFwd;        // 雨丝轴·相机前向(恒 ≈0 = 雨丝被压在屏幕平面里 = 构轴错误)
        public static float LastAxisDotDown;       // 雨丝轴·径向"下"(静止时 ≈1)
        public static float LastDownDotCamUp;      // 径向"下"·相机上向:停机坪水平镜头下真"下" ≈ −1;≈0 = down 不在渲染空间(SR2 帧是 yaw-only,必须用 GravityFrameNormalized)
        public static float LastCamSpeed;          // 最近一次相机速度
        public static float LastStretch = 1f;      // 最近一帧雨丝拉伸倍率
        public static bool SoftDepthReady;         // 本相机能不能取到 CloudRenderer.LinearSceneDepth(取不到 → _InvFade=0 退化)

        public static string AssetsStatus = "未加载";   // 诊断:最近一次资产加载结果(门控用 **实例** 字段,见 _assetsReady)

        private Camera _cam;
        private bool _assetsReady;          //  不要改成 static:雨挂在场景相机上,换场景 = 组件销毁 + 新实例;static 会让新实例跳过 EnsureAssets → compute/shader 永远为空
        private ComputeShader _compute;
        private Shader _shader;
        private Material _mat;
        private Mesh _mesh;
        private ComputeBuffer _positions;   // float4 × capacity
        private ComputeBuffer _randomData;  // float4 × capacity
        private ComputeBuffer _diag;        // uint × 4:x0 = 本帧重生次数(CPU 每帧复位)
        private Texture2D _streakTex;       // 程序化柔边雨丝贴图
        private GraphicsBuffer _args;       // 5×uint 间接参数(RenderMeshIndirect 要 GraphicsBuffer)

        private static readonly uint[] ArgsReset = { 12, 0, 0, 0, 0 };   // [0] = indexCount(十字四边形 12);建 buffer 时写入一次
        private static readonly uint[] DiagReset = { 0, 0, 0, 0 };   // 诊断计数(回读后清零重启累计)
        private const float ReadbackInterval = 10f;                  // GPU 回读周期(秒)
        private const float ShaderBuild = 3f;                        // 期望的 shader 版本(与 shader 里 _ShaderVer 对齐)
        private const float AssetRetryInterval = 5f;                 // 资产加载重试间隔(秒)
        private const int AssetMaxRetries = 6;                       // 资产加载最多重试次数(Awake 那一刻资源加载器可能还没就绪)

        private int _kernelRandomize = -1;
        private int _kernelPositioning = -1;
        private int _kernelTranslateFixed = -1;
        private int _kernelFillArgs = -1;

        private float _lastDiagTime = -999f;
        private float _lastReadbackTime = -999f;
        private float _lastAliveLogTime = -999f;
        private bool _loggedFirstDraw;
        private CloudRenderer _cloudRenderer;      // 缓存本相机的云渲染器(每帧 GetComponent 是原生查找)
        private float _cloudProbeTime = -999f;     // 缓存未命中时的重探时间点
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
        private int _assetRetries;                // 资产加载已重试次数(重试耗尽后只留 NOT READY 心跳)
        private float _nextAssetRetry;            // 下一次资产加载重试时间点
        private Vector3 _lastReadbackPos0;        // 诊断:上次回读的 pos[0](算位移 → 判断下落是否在积分)
        private bool _hasLastReadbackPos0;

        private void BuildMesh()   // 十字四边形:0.1×2.5,8 顶点 / 12 三角形(两个交叉面)
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
            //  uv 全 0..1 不平铺(长度由矩阵列1 表达);uv.y 0=头(亮)→1=尾(淡),头 = +局部 y = +矩阵列1 = 相对运动前方,所以 +hl 顶点取 uv.y=0。
            _mesh.uv = new[]
            {
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            };
            _mesh.RecalculateBounds();
            _mesh.UploadMeshData(false);
        }

        // 程序化雨丝贴图。 uv.y=0 = 头(+轴/相对运动前方);Texture2D 的 y=0 是底行,所以底行放亮的头。
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
            // 软 blob 的长条化:横向满宽软板条(中段 45% 满不透明),纵向快速收尾(亮芯靠头、尾端收尖)。
            //  中心亮线 profile(有效宽度只剩 ~40%)或纵向近乎满长亮度,拉伸后都会读成"面条"而不是雨丝。
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

        private void EnsureAssets()
        {
            if (_assetsReady) return;
            try
            {
                _compute = Mod.LoadVolkenAsset<ComputeShader>("Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.compute");
                _shader = Mod.LoadVolkenAsset<Shader>("Assets/Scripts/Volken/Weather/Rain/Shader/RainParticles.shader");
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
                // SP2 属性名:_MainColor(不叫 _Color)、_Emission、_Falloff、_FadeAmount;_MainTex 默认 white → 不绑贴图时是纯色 + UV 尾淡
                _mat.SetColor("_MainColor", new Color(0.72f, 0.82f, 1f, 1f));
                _mat.SetFloat("_Emission", 0f);
                _mat.SetFloat("_Falloff", Falloff);
                _mat.SetFloat("_FadeAmount", 1f);
                BuildStreakTexture();
                if (_streakTex != null) _mat.SetTexture("_MainTex", _streakTex);   // 柔边雨丝贴图
                _mat.renderQueue = 4000;
                _assetsReady = true;
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
            if (!_assetsReady || _compute == null || _mat == null) return;   // 资产未就绪时静默(修复 Awake 竞态 NRE)
            int cap = Mathf.Max(1, Capacity);
            _positions = new ComputeBuffer(cap, 16);                          // float4
            _randomData = new ComputeBuffer(cap, 16);                         // float4
            _diag = new ComputeBuffer(4, sizeof(uint));                       // 诊断计数
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, sizeof(uint));
            _args.SetData(ArgsReset);   // 置 indexCount = 12(Tick 里每帧只复位 instanceCount)
            //  关键绑定:shader 顶点着色器按 SV_InstanceID 读 _Positions —— 必须绑到**材质**上;
            // RenderMeshIndirect 不会自动把 compute 的 buffer 带给材质(不绑 → 全部画在原点)。
            _mat.SetBuffer("_Positions", _positions);
            // 首次分配:随机化(只跑一次,绝不每帧重跑)
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

        /// <summary>部署自检日志:重点是材质属性 —— **旧 shader 缺 _RotationMatrix 时 SetMatrix 静默无效 → 雨丝朝向全错**(最常被"包里是旧资源"坑到)。</summary>
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

                // 球域密度:均匀体积分布的期望值(粒子/m³)—— 标定要拿它跟 EVE 0.139/m³ 比
                float vol = (4f / 3f) * Mathf.PI * DomainRadius * DomainRadius * DomainRadius;
                Mod.Diag("RainParticles SELFCHECK density: {0:F4} particles/m³ (均匀体积;SP2 参考 0.139/m³ @ 200k/半径70m)",
                    cap / Mathf.Max(1f, vol));

                Mod.Diag("RainParticles SELFCHECK kernels: Randomize={0} Positioning={1} TranslateFixed={2} FillArgs={3}",
                    _kernelRandomize, _kernelPositioning, _kernelTranslateFixed, _kernelFillArgs);

                // 版本判据:① shader 读 Properties 里的 _ShaderVer 哨兵(**可靠**)—— _RotationMatrix/_Positions/_FadeAmount/_LinearSceneDepth 只在 HLSL 里声明、不在 Properties 表里,HasProperty 未必可见,拿它判版本会误报"旧 shader";② compute 看 TranslateFixed 内核索引是否 ≥0。
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

        // 挂载(幂等)

        private static RainParticles _current;   // 最近挂上的实例;相机随 Flight 场景卸载 → 该引用在 Unity 语义下比较 == null
        private static float _nextAttachTry;     // 未挂上时的重试时间点(相机可能还没建好)

        /// <summary>取当前视图相机:游戏近相机;独立模式(编辑器预览)或取不到时回退 <see cref="Camera.main"/>。</summary>
        private static Camera ResolveViewCamera()
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
            return cam;
        }

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
                if (existing != null) { _current = existing; return existing; }
                var probe = cam.gameObject.AddComponent<RainParticles>();
                _current = probe;
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
            AttachToCurrentView(ResolveViewCamera());
        }

        /// <summary>挂到指定相机并按配置套参(<paramref name="cam"/> 为 null 时只留一行诊断,不抛)。</summary>
        private static void AttachToCurrentView(Camera cam)
        {
            try
            {
                Attach(cam);
                // 配置驱动初始状态:rain.enabled=true 时自动开雨并套参( ApplyConfig 不回调 AttachToCurrentView,否则递归)
                try { ApplyConfig(VolkenWeather.Instance?.Config?.rain); }
                catch { }
            }
            catch (Exception ex)
            {
                Mod.Log("Volken:RainParticles.AttachToCurrentView ERROR: " + ex.Message);
            }
        }

        /// <summary>
        /// 按配置把雨挂到当前视图相机(幂等),由常驻天气 tick 每帧调用 —— 相机会随 Flight 场景卸载,而雨的资产/缓冲全是实例字段,
        /// 所以**换场景后必须重挂**;命中缓存时开销 = 一次静态引用判空。
        /// </summary>
        public static void SyncToCurrentView()
        {
            var cfg = VolkenWeather.Instance?.Config?.rain;
            if (cfg == null) return;
            if (!cfg.enabled)
            {
                Enabled = false;   // 配置关着:清掉上一场景遗留的静态开关(雨声门控与面板状态都读它)
                return;
            }
            if (_current != null) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextAttachTry) return;   // 相机还没建好 → 别每帧穿透 GameCamera 链
            _nextAttachTry = now + 0.5f;
            var cam = ResolveViewCamera();
            if (cam == null) return;            // 静默留到下个窗口,不刷 attach skipped 诊断
            AttachToCurrentView(cam);
        }

        // 状态输出(面板按钮调用)

        /// <summary>把完整状态写进 Player.log(天气面板「把雨状态写入日志」按钮),便于用户直接发日志排查。</summary>
        public static void DiagStatus()
        {
            try
            {
                var cam = ResolveViewCamera();
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
            if (on != 0) AttachToCurrentView();   // 先确保挂载:只开开关不挂载 = 静默无效果
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

        /// <summary>一帧一次的 craft 属性链采样:径向"下" / 本体速度 / 参考帧位置 / 相机海拔都取自同一条链。</summary>
        private struct CraftSample
        {
            public bool HasScript;        // CraftScript 取到 → Down / FrameVelocity 有效
            public Vector3 Down;          // 径向"下";零 = 取不到
            public Vector3 FrameVelocity;
            public bool HasFramePos;
            public Vector3 FramePos;
            public float Altitude;        // ASL 米;−1 = 取不到(闸门按"不限制"处理)
        }

        /// <summary>取一次 craft 链(**独立模式不要调用**:在编辑器里访问游戏单例可能触发游戏侧半初始化)。</summary>
        private static CraftSample SampleCraft(Vector3 camPos)
        {
            var sample = new CraftSample { Altitude = -1f };
            try
            {
                var craftNode = Game.Instance?.FlightScene?.CraftNode;
                if (craftNode == null) return sample;

                var cs = craftNode.CraftScript;
                if (cs != null)
                {
                    sample.HasScript = true;
                    sample.Down = cs.GravityNormal;
                    sample.FrameVelocity = cs.FrameVelocity;
                    sample.FramePos = cs.FramePosition;
                    sample.HasFramePos = true;
                }
                if (craftNode.ReferenceFrame != null && craftNode.Parent?.PlanetData != null)
                {
                    Vector3 planetCenter = craftNode.ReferenceFrame.PlanetToFramePosition(Vector3d.zero);
                    sample.Altitude = (camPos - planetCenter).magnitude - (float)craftNode.Parent.PlanetData.Radius;
                }
            }
            catch { }
            return sample;
        }

        /// <summary>本行星适用的雨海拔上限(米;0 = 闸门关闭)。</summary>
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
                var cam = ResolveViewCamera();
                if (cam != null) return cam.GetComponent<RainParticles>();
            }
            catch { }
            return null;
        }

        /// <summary>把天气配置里的雨参数推到运行时:需要重建资源的项自动处理(**容量 → buffer**、**雨丝长宽 → 网格**);持续型参数由 Tick 每帧读静态字段,推一次即生效。</summary>
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
            StretchAmount = Mathf.Clamp(cfg.stretchAmount, 0f, 1f);   //  不要改回 cfg.streakLength —— 会把雨丝长度当拉伸系数(拉伸恒撞上限、该滑块失效)
            StretchLimit = Mathf.Clamp(cfg.stretchLimit, 1f, 20f);
            InvFade = Mathf.Max(0f, cfg.softParticles);
            EdgeFade = Mathf.Clamp(cfg.edgeFade, 0f, 0.5f);
            StreamMode = cfg.streamMode;
            Falloff = Mathf.Clamp01(cfg.tailFalloff);
            Brightness = Mathf.Max(0f, cfg.brightness);
            Strength = Mathf.Clamp(cfg.strength, 0f, 4f);             // 音频(见 RainAudio)
            Volume = Mathf.Clamp01(cfg.volume);
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
                inst.EnsureAssets();    // 资产晚到时补加载(幂等;不调 AttachToCurrentView,避免与本方法互相递归)
                inst.EnsureBuffers();
            }
            Mod.Diag("RainParticles ApplyConfig: enabled={0} cap={1}{2} R={3:F0} fall={4:F1} len={5:F2}{6} w={7:F3} stretch={8:F3} edge={9:F2} soft={10:F2} stream={11} ceil={12} density={13:F4}/m³ strength={14:F2} rainVol={15:F2}",
                Enabled, Capacity, capChanged ? "(重建)" : "",
                DomainRadius, FallSpeed, StreakLength, meshChanged ? "(重建)" : "", StreakThickness,
                StretchAmount, EdgeFade, InvFade, StreamMode,
                CeilingAltitude > 1f ? CeilingAltitude.ToString("F0") + "m(独立配置)" : "off(不限制)",
                DensityPerM3(), Strength, Volume);
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

        /// <summary>世界系朝向 dir → 旋转矩阵(列1 = 雨丝轴 × 拉伸);shader 用 local = M * v.vertex 定位十字四边形,拉伸 = clamp(|dir| * StretchAmount, 1, StretchLimit)。</summary>
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
        /// 换帧原点重定位事件:位置存在帧空间坐标里,参考系重定位时整个世界平移了 positionDelta,粒子不同步平移 → 整片雨相对世界"跳"走、批量重生。
        ///  这里**只记录不应用**(调用方在 Tick 里统一应用一次,避免双重平移); 实测本 mod 环境下该事件**不触发**,兜底才是主力。
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

        /// <summary>换帧重定位兜底:飞行器是"世界物体",换帧时它的帧位置整体跳变(自己没动)。判据:跳变 &gt; max(100m, 自身运动×3 + 30m) → 判换帧,delta = 该跳变量;true = 本帧已应用。</summary>
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

        private static bool IsFinite(Vector3 v)   // NaN/Inf → false
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                  || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            BuildMesh();
            EnsureAssets();
            HookRecenter();                     // 换帧原点重定位(世界系下落必须同步平移)
            if (_assetsReady) EnsureBuffers();  // 资产未就绪时留到 OnPreCull 的 NOT READY 里限次重试(修复场景切换瞬间 NRE)
        }

        private void OnDestroy()
        {
            if (_current == this) _current = null;   // 相机随场景卸载 → 下一个飞行场景必须能重新挂上
            UnhookRecenter();
            ReleaseBuffers();
            if (_mesh != null) Destroy(_mesh);
            if (_mat != null) Destroy(_mat);
            if (_streakTex != null) Destroy(_streakTex);
        }

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
                // 资产/缓冲可能晚到或上一次尝试失败 → 自己限次重试:否则本实例会永久 NOT READY(面板开关怎么拨都没雨)
                float now = Time.realtimeSinceStartup;
                if (_assetRetries < AssetMaxRetries && now >= _nextAssetRetry)
                {
                    _assetRetries++;
                    _nextAssetRetry = now + AssetRetryInterval;
                    if (!_assetsReady) EnsureAssets();   // 资产就绪但建 buffer 失败(设备丢失/容量异常)时也要能恢复
                    EnsureBuffers();
                }
                if (now - _lastAliveLogTime > 5f)
                {
                    _lastAliveLogTime = now;
                    Mod.Diag("RainParticles: enabled but NOT READY — cam={0} compute={1} positions={2} args={3} retries={4}",
                        _cam != null, _compute != null, _positions != null, _args != null, _assetRetries);
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
            Vector3 camUp = _cam.transform.up;
            Vector3 camFwd = _cam.transform.forward;
            float dt = Mathf.Max(0f, Time.deltaTime);
            _lastDt = dt;
            float now = Time.realtimeSinceStartup;

            // 相机速度(**仅诊断**:位置积分与雨丝朝向都不吃 camVel);暂停(dt≈0)时无法测量 → 锁存最近值,心跳不跳 0
            Vector3 camVel = _lastCamVel;
            if (_hasLastCamPos && dt > 1e-4f)
            {
                camVel = (camPos - _lastCamPos) / dt;
                _lastCamVel = camVel;
                LastCamSpeed = camVel.magnitude;
            }
            _lastCamPos = camPos;
            _hasLastCamPos = true;

            // craft 链一帧只取一次:径向"下"、本体速度、参考帧位置、相机海拔都从这份采样里出(旧写法每帧穿透 4 次属性链)
            CraftSample craft = StandaloneMode ? default(CraftSample) : SampleCraft(camPos);

            // 径向"下" = 指向行星中心(帧空间)= craft.GravityNormal。
            //  世界(帧)空间是浮点原点 + 绕行星 Y 轴旋转 → 球面赤道处径向"下"≈世界水平,写死世界 (0,-1,0) 会下错方向。
            Vector3 down = (craft.HasScript && craft.Down.sqrMagnitude > 1e-6f) ? craft.Down : Vector3.down;
            down.Normalize();
            _lastDown = down;

            // 流线轴/拉伸的速度源用飞行器本体速度(craft.FrameVelocity)**不用 camVel** —— camVel 在视角旋转(绕飞/转头)时混入切向分量,雨丝朝向会随相机角速度摆动;无飞行器回退 camVel。
            Vector3 axisVel = (craft.HasScript && IsFinite(craft.FrameVelocity)) ? craft.FrameVelocity : camVel;
            _lastAxisVel = axisVel;

            // 换帧原点重定位:每次 Tick 开头**统一应用一次**(事件 delta 优先 → 兜底用飞行器帧位置跳变)——
            // 位置存在帧空间里,不跟着平移就全被留在原地 → 整批出域重生。
            Vector3 craftFramePos = craft.FramePos;
            bool hasCraftFramePos = craft.HasFramePos;

            // 海拔闸门(不做限制就会"在太空里下雨")
            //   上限 = **雨自己的配置项** CeilingAltitude(米;0 = 关闭闸门,**不读 CloudConfig**);在 [上限×(1−带宽), 上限] 内线性淡出。
            float camAlt = !float.IsNaN(DebugAltitudeOverride)
                ? DebugAltitudeOverride
                : (StandaloneMode ? -1f : craft.Altitude);
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
                //  抑制期间必须同步换帧状态:否则恢复下雨时,抑制期间累积的帧位置跳变会被 DetectRecenterJump 误判成一次换帧,整片雨被甩飞。
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
            bool paused = GamePause.IsPaused;
            if (paused)
            {
                // 暂停:不做换帧判定,但要把换帧基准同步掉 —— 否则恢复时会被误判成一次换帧(同高度闸门那条的理由)
                if (hasCraftFramePos) { _lastCraftFramePos = craftFramePos; _hasLastCraftFramePos = true; }
                _hasPendingRecenter = false;
                _pendingRecenterDelta = Vector3.zero;
            }
            else if (_hasPendingRecenter)
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
            int amount = TestRow ? 20 : cap;   // 等距排只画一小排(20 粒,30m 外 3m 间距)

            // 1) 复位实例数(只写 args[1];args[0] = indexCount 建 buffer 时已置好,不必每帧上传整条)
            //  暂停时三个 Dispatch/SetData 全部跳过:位置与实例数都保持上一帧的样子,于是**冻住的那片雨照画**(不是消失)。
            if (!paused) _args.SetData(ArgsReset, 1, 1, 1);
            // 注:诊断计数 _diag 不在这里清零 —— 累计到下一次回读(得到精确的"这 10s 重生多少次"),回读回调里再清零。

            // 2) Positioning:积分 + 域环绕(TestRow 时覆盖为等距排)
            // 注:SetFloat/SetInt/SetVector 没有 per-kernel 重载(属性全局共享),只传名字。
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
            _compute.SetInt("_respawnMode", RespawnMirror ? 1 : 0);
            // 相机基与间距只在 Positioning 的 _testRow 分支里被读 → 正式运行时不必每帧上传
            if (TestRow)
            {
                _compute.SetFloat("_testRowSpacing", TestRowSpacing);
                _compute.SetVector("_camRight", _cam.transform.right);
                _compute.SetVector("_camUp", camUp);
                _compute.SetVector("_camFwd", camFwd);
            }
            if (!paused) _compute.Dispatch(_kernelPositioning, Mathf.CeilToInt(cap / (float)ThreadGroupSize), 1, 1);

            // 3) FillArgs:原子统计活跃数(复位已在 1)做完)。 原子加只能用**带下标的** RWStructuredBuffer 2/3 参形式,见类注释。
            _compute.SetBuffer(_kernelFillArgs, "_ArgsBuffer", _args);
            _compute.SetInt("_amount", amount);
            if (!paused) _compute.Dispatch(_kernelFillArgs, Mathf.CeilToInt(amount / (float)ThreadGroupSize), 1, 1);

            // 4) 间接绘制:十字四边形 × instanceCount,实例化 shader 按 SV_InstanceID 读位置
            if (_mat != null)
            {
                // **世界系构轴**:不要用屏幕平面投影(会"贴相机",垂直看糊成横块) ——
                //   朝向 = 相对速度方向(下落 − 飞行器速度;StreamMode=0 时 = 径向"下"),拉伸烘焙进矩阵列1 → 转镜头/暂停/缩放都不改变朝向。
                Vector3 streakDir = StreamMode
                    ? down * FallSpeed - new Vector3(axisVel.x, Mathf.Abs(axisVel.y), axisVel.z)  // SP2 对 y 取绝对值(俯冲时雨丝不翻转)
                    : down;
                if (streakDir.sqrMagnitude < 1e-6f) streakDir = down;
                _mat.SetMatrix("_RotationMatrix", BuildStreakMatrix(streakDir, out LastStretch));
                // 域边界淡出(SP2 同名 uniform:_DomainPos/_DomainRadius)→ 球域边界与"出域回收"的突现都不显形(面板调 0 可关)
                _mat.SetVector("_DomainPos", camPos);
                _mat.SetFloat("_DomainRadius", DomainRadius);
                _mat.SetFloat("_EdgeFade", EdgeFade);
                // 诊断:·fwd ≈0 恒成立 = 雨丝被压在屏幕平面里(构轴错误);世界系构轴下它随视角变化
                LastAxisDir = streakDir.normalized;
                LastAxisDotFwd = Vector3.Dot(LastAxisDir, camFwd);
                LastAxisDotDown = Vector3.Dot(LastAxisDir, down);
                LastDownDotCamUp = Vector3.Dot(down, camUp);   // ≈ -1 = down 确实指向画面下方(验证空间正确)

                // 软粒子:用本相机云渲染器的线性场景深度(RFloat,LinearEyeDepth 米);没有云渲染器(或无云)→ 退化为普通透明雨丝。
                // 组件缓存 + 每秒重探:云渲染器是后装配的,缓存空/深度图未建时还得能自己恢复,但不该每帧 GetComponent。
                RenderTexture depthTex = _cloudRenderer != null ? _cloudRenderer.LinearSceneDepth : null;
                if (depthTex == null && now >= _cloudProbeTime)
                {
                    _cloudProbeTime = now + 1f;
                    try { _cloudRenderer = _cam.GetComponent<CloudRenderer>(); }
                    catch { _cloudRenderer = null; }
                    depthTex = _cloudRenderer != null ? _cloudRenderer.LinearSceneDepth : null;
                }
                SoftDepthReady = depthTex != null && depthTex.IsCreated();
                _mat.SetFloat("_InvFade", SoftDepthReady ? InvFade : 0f);
                if (SoftDepthReady) _mat.SetTexture("_LinearSceneDepth", depthTex);

                // 海拔淡出
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
                // 位移量:与上次回读的 pos[0] 比,正常下落 10s 应动 ≈150m;恒 0 且 w0 在变 = 粒子没动(查 dt/down),恒 0 且 w0 恒不变 = kernel 没在跑(或游戏暂停)。
                float pos0Delta = _hasLastReadbackPos0 ? ((Vector3)data[0] - _lastReadbackPos0).magnitude : -1f;
                _lastReadbackPos0 = data[0];
                _hasLastReadbackPos0 = true;

                // 球域分布实况:均匀体积分布时 mean/R ≈ 0.75、max≈R;mean/R 明显偏小(≈0.5)且粒子挤在近处 → 半径还是 R*u(中心堆积),分布没生效。
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

            // 第二行:朝向自检。·fwd 恒 ≈0 = 轴被压在屏幕平面里(错误);世界系构轴下随视角变化,垂直俯视时 |·fwd|→1(雨丝与视线平行 = 点状,不糊屏)。
            Mod.Diag("RainParticles axis: dir=({0:F2},{1:F2},{2:F2}) stretch={3:F2} ·fwd={4:F2} ·down={5:F2} down·camUp={6:F2} edge={7:F2} shaderVer={8}",
                LastAxisDir.x, LastAxisDir.y, LastAxisDir.z, LastStretch, LastAxisDotFwd, LastAxisDotDown,
                LastDownDotCamUp, EdgeFade,
                _mat != null ? _mat.GetFloat("_ShaderVer").ToString("F0") : "?");
        }
    }
}
