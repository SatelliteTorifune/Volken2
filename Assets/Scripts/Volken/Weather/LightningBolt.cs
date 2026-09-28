using System;
using Assets.Scripts;
using ModApi;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Weather
{
    /// <summary>
    /// 一道闪电的视觉表现 —— 移植自 SP2 的 <c>Enviro.Lightning</c>(约 170 行,纯 C#、与渲染管线无关)。
    ///
    /// 【原版行为(逐条对应)】
    ///   1. 主干 <see cref="LineRenderer"/> 逐段"生长":共 <c>arcs</c> 段,每段从前一点朝目标方向
    ///      加随机扰动,再乘 <c>Random.Range(arcLength*arcVariation, arcLength) * arcDist</c>;
    ///      每段等待 <c>Random.Range(0.001, 0.005)</c> 秒 → 肉眼看到闪电劈下来的过程;
    ///   2. 每段(除最后两段)生成 <c>splits+1</c> 条分叉子线,子线各 8 点、朝随机方向飞
    ///      <c>Random.insideUnitSphere*500 + dir*500</c>,存活 <c>Random.Range(0.2, 0.5)</c> 秒后销毁;
    ///   3. 材质 <c>_Intensity</c> 每段随机 0~2(闪烁感);
    ///   4. 追到目标后:点光源定位到落点并朝向相机,主干材质 <c>_Intensity = flashIntensity</c>(50),
    ///      闪光球材质 = 20,点光源开 → 等 25~35ms → 关 → 等 25~35ms …… 共 **4 次闪烁**;
    ///   5. <c>fadeOut = true</c> 后每帧 <c>fadeTimer = Lerp(fadeTimer, 0, 10*dt)</c>,
    ///      用 <c>fadeTimer</c>(初值 50)当 <c>_Intensity</c> 淡出,降到 ≤1 时销毁。
    ///
    /// 【对原版做的改动(及原因)】
    ///   - 原版用 <c>EnviroManager.instance.Camera</c> 让闪光朝向相机;Volken 不引入 Enviro3,
    ///     改为静态 <see cref="Camera"/> 字段,由 <see cref="LightningModule"/> 在创建时注入;
    ///   - 原版用 <c>Object.DestroyImmediate</c> 销毁分叉/自身 —— 在飞行中的每一帧都会触发,
    ///     且 <c>DestroyImmediate</c> 在运行时是**同步销毁、会打断渲染批次**;改为
    ///     <c>Destroy</c>(帧末销毁),行为等价但不会有运行时副作用;
    ///   - 原版 <c>CreateSplit</c> 里有句无副作用的废语句(算了个 normalized 向量就丢掉),
    ///     这里不再保留;
    ///   - 主干与分叉共用一个材质实例副本(原版靠 <c>lineRend.material</c> 共享)。
    ///
    /// 【实例复用】一道闪电 = 一个挂在本组件上的 GameObject 树。播完自毁。
    /// 不池化:雷击间隔是秒级(默认 6~45s),实例化开销完全可以忽略,池化反而引入状态残留风险。
    ///
    /// ============================================================================
    /// 【2026-09-28 重写:修"闪电不消失、永久残留在天上"】
    ///
    /// 症状:某些情况下生成的闪电不消失,一直存在。根因有**两层**,叠在一起才是致命的:
    ///
    ///   ① **`StartCoroutine` 会静默失败**。实测 Player.log:
    ///      <c>Coroutine couldn't be started because the the game object 'VolkenLightningBolt'
    ///      is inactive!</c>
    ///      旧实现把 bolt 挂在 <c>NearCamera</c> 下,并在**自己身上**启动主协程与
    ///      <c>CreateSplit</c> 分叉协程。Unity 在 <c>activeInHierarchy == false</c> 时
    ///      **不启动协程且只打一行警告** —— 此时 <c>_playing</c> 已被置 true,但协程从未运行,
    ///      于是 <c>_fadeOut</c> 永远是 false。
    ///
    ///   ② **自毁链是单点故障**。<c>Update()</c> 的第一行是 <c>if (!_fadeOut) return;</c>,
    ///      而 <c>_fadeOut = true</c> 只在**主协程跑完最后一句**时才赋值。
    ///      协程因为①(或任何其他原因:场景卸载、相机销毁、父物体被置非激活)没跑到最后,
    ///      就**没有任何代码路径能够销毁这个对象** —— 相机恢复激活后,残留物永远停在全亮状态。
    ///
    /// 修法(结构性,不是打补丁):
    ///   - **取消对"活着的协程"的全部依赖**。动画改由 <see cref="Update"/> 驱动的时间状态机
    ///     (<see cref="Phase.Grow"/> → <see cref="Phase.Flash"/> → <see cref="Phase.Fade"/>):
    ///     Update 天然只在 <c>activeInHierarchy</c> 时运行 —— bolt 被藏起来时动画自己暂停、
    ///     恢复时接着播,**不会再出现"协程死了但对象还活着"的状态**。顺带干掉了那行警告。
    ///   - **硬性寿命兜底** <see cref="HardLifetime"/>:无论动画处于什么状态、无论怎么被打断,
    ///     自创建起满 6 秒一定销毁。残影 ≤6s 与环境里挂一道雷**永远**存在是两个性质的问题。
    ///   - **僵死检测**:连续不可见超过 <see cref="MaxHiddenSeconds"/> → 立刻销毁,不必等到硬性寿命。
    ///     正常切换视角/相机时动画几乎马上会恢复,不会误杀。
    ///   - **bolt 不再被挂到相机的子物体下**(见 <see cref="Create"/>):LineRenderer 本来就是
    ///     world space,挂上去只是为了让"随场景一起销毁"这一条成立;而那条恰好是①的成因。
    ///     现在 bolt 是**根物体**,生命周期由本组件自己负责(状态机 + 兜底寿命 +
    ///     <see cref="ActiveBolts"/> 注册表),与相机是否活跃、是否被换掉完全无关。
    ///   - **注册表 + 整批销毁**:<see cref="Volken.Weather.LightningModule"/> 在停用时调
    ///     <see cref="DestroyAll"/>,离开飞行场景不会有任何一道雷被留到下一个场景。
    ///
    /// ============================================================================
    /// 【2026-09-28 第二处修复:闪电偶发**紫红色**】
    ///
    /// 紫红/淡紫 = Unity 在"材质丢失或 shader 不可用"时回退的 **Error shader**。
    /// 日志里**没有**任何 shader 编译错误(`Shader error in 'Hidden/Volken/LightningBolt'`),
    /// 所以不是编译失败,而是**运行时材质被销毁**。成因链:
    ///
    ///   1. 分叉线**共享**主干的材质实例(<c>sr.material = _boltMat</c>);
    ///   2. 主干收尾时 <c>Destroy(_boltMat)</c>;
    ///   3. 分叉的存活计时原来用 <c>Time.realtimeSinceStartup</c> —— 它在失焦/暂停时**仍前进**,
    ///      但 <c>Update</c> 在分叉被隐藏时**不跑**,于是分叉完全可以比主干活得久;
    ///   4. 渲染时材质已销毁 → 那条线变紫红。
    ///
    /// 修法(三条一起,缺一不可):
    ///   - 分叉改持**自己的**材质实例(独占,不会被他人的销毁牵连);
    ///   - 主干在 <see cref="Finish"/> 里连分叉一起销毁(<see cref="DestroySplits"/>),
    ///     保证"渲染器 ↔ 材质"同生共死,同时回收材质(否则每次落雷都漏几份);
    ///   - 计时改 <c>Time.unscaledTime</c>:暂停时跟着停,不会"暂停期间偷偷把分叉耗死"。
    ///
    /// 另外两处 shader 侧的加固(见 <c>LightningBolt.shader</c> / <c>LightningFlash.shader</c>):
    ///   - **主干与落点闪光拆成两个单 Pass shader**。原先共用一个双 Pass shader 靠 Pass 名区分,
    ///     但"一个 Material 只用第一个匹配 Pass",那两个 Pass 又没有 <c>LightMode</c> 标签,
    ///     所以闪光球其实一直在跑主干那套逻辑(<c>_CoreWidth</c>/halo 形同虚设);
    ///   - **<c>Fallback Off</c> → <c>Fallback "Hidden/Internal-Colored"</c>**。
    ///     <c>Fallback Off</c> 让"shader 不可用"直接表现为 Error shader(紫红),既难看又掩盖真因;
    ///     换成 Unity 内置着色器后,最坏情况是"画得不对但能看出是什么",便于定位。
    /// </summary>
    public class LightningBolt : MonoBehaviour
    {
        /// <summary>相机(闪光朝向 + 距离衰减用)。每道闪电各自持有 —— 用实例字段而非静态,
        /// 否则多相机(PIP)场景里后创建的那台会覆盖前者,闪光朝向会串。</summary>
        public Camera targetCamera;

        // ==== 参数(由 LightningModule 从 VolkenWeatherConfig.LightningSection 灌入) ====
        public float flashIntensity = 50f;
        public float flashPlaneIntensity = 20f;
        public int arcs = 20;
        public float arcLength = 1f;
        public float arcVariation = 1f;
        public float inaccuracy = 0.5f;
        public int splits = 4;
        public float width = 10f;
        public float baseIntensity = 1f;
        public float lightIntensity = 8f;
        public float lightRange = 8000f;

        /// <summary>
        /// 落雷后回调(用于延迟播放雷声)。**不带参数** —— 距离必须由调用方按
        /// "落点 ↔ 观测者"自己算。
        ///
        /// 【2026-09-28 修正】此前这里回传 <c>Distance(transform.position, Target)</c>,
        /// 并在注释里写成"落点到相机的距离" —— 那是**错的**:<c>transform.position</c> 是
        /// bolt 的**起点(云里)**,所以它其实是 **bolt 自身长度**(云底到地面那几公里),
        /// 与玩家在哪毫无关系。雷声的"远/近"因此从来没有真的按距离区分过。
        /// 现在距离在 <see cref="LightningModule"/> 里现算(那里才拿得到观测者位置)。
        /// </summary>
        public Action OnBoltLanded;

        /// <summary>整道闪电播完(自毁前)回调。**保证最多被调用一次**。</summary>
        public Action OnBoltFinished;

        public Vector3 Target { get; private set; }

        // ================= 生命周期兜底常量 =================
        //
        // 【两个时间量必须分清楚,这是本次 bug 最容易改错的地方】
        //   _lastActiveTime = **最近一次还在 Update 的时刻**,随时间**前移**;
        //   _bornTime       = 出生时刻,固定不变。
        // 不可见时长用前者算(会前移),总年龄用后者算(不会前移)。混用会让"僵死判定"
        // 退化成"活满 N 秒就杀",正常雷都会被误杀。

        /// <summary>硬性寿命(真实时间,秒,自<b>出生</b>起算):超过它无条件销毁。正常一道雷 &lt;2s 播完。</summary>
        private const float HardLifetime = 3.5f;

        /// <summary>落点闪烁次数(原版 4 次:亮 25~35ms → 灭 25~35ms,最后一次闪完直接淡出)。</summary>
        private const int FlashCount = 4;

        /// <summary>
        /// 允许的**连续不可见时长**(秒,真实时间,自<b>上次还在 Update</b>起算):超过它 → 认定不会再恢复 → 销毁。
        ///
        /// 取 1.5s 的依据:一道雷理论最坏总时长 ≈ 生长 1.5s + 闪烁 0.28s + 淡出 2s,但那只是上界
        /// (实测整段通常 &lt;1.2s);而 `minDelay` 默认 6s 起,每道雷本来就有独立的存在感。
        /// 正常切换视角/过场里相机被短暂置非激活几乎不会超过 1.5s;真超过了,那道雷本来也已经看不见,
        /// 清掉正好。**误杀的代价 ≈ 0,漏杀的代价 = 天上挂一道永远不灭的雷。**
        /// </summary>
        private const float MaxHiddenSeconds = 1.5f;

        /// <summary>
        /// 当前存活的全部闪电。用于"整批销毁"(离开场景 / 模块停用),
        /// 避免任何一道雷被留到下一个场景 —— 这是"永久残留"最常见的入口。
        /// </summary>
        private static readonly System.Collections.Generic.List<LightningBolt> ActiveBolts =
            new System.Collections.Generic.List<LightningBolt>();

        /// <summary>销毁当前所有闪电(模块停用 / 离开飞行场景时调用)。</summary>
        public static void DestroyAll()
        {
            // 倒序 + 判空:Destroy 是帧末生效,列表里可能有已销毁(== null)的条目
            for (int i = ActiveBolts.Count - 1; i >= 0; i--)
            {
                var b = ActiveBolts[i];
                if (b == null) { ActiveBolts.RemoveAt(i); continue; }
                b.Release();
                // 销毁不要求 GameObject 处于激活状态,所以不需要(也不应该)先 SetActive(true)
                UnityEngine.Object.Destroy(b.gameObject);
            }
            ActiveBolts.Clear();
        }

        /// <summary>当前存活闪电数(诊断用)。</summary>
        public static int ActiveCount => ActiveBolts.Count;

        // ================= 私有状态 =================

        /// <summary>动画阶段。由 <see cref="Update"/> 单一驱动 —— 不再有协程。</summary>
        private enum Phase
        {
            /// <summary>主干逐段生长(对应原版逐段 await)。</summary>
            Grow,
            /// <summary>落点 4 次闪烁(对应原版 4 次开关)。</summary>
            Flash,
            /// <summary>整体淡出(对应原版 fadeOut + fadeTimer)。</summary>
            Fade,
            /// <summary>已播完 / 已请求销毁。</summary>
            Done,
        }

        private LineRenderer _line;
        private Light _light;
        private Transform _flashPlane;
        private Material _boltMat;
        private Material _flashMat;
        private Mesh _flashMesh;

        /// <summary>
        /// 本道闪电生出的全部分叉。收尾时要与它们**同步**销毁 —— 见 <see cref="Finish"/>。
        /// </summary>
        private readonly System.Collections.Generic.List<GameObject> _splits =
            new System.Collections.Generic.List<GameObject>();

        private Phase _phase = Phase.Done;
        private bool _begun;
        private bool _finishedInvoked;
        private bool _released;
        private float _bornTime;
        private float _phaseElapsed;

        // Grow
        private int _arcIndex;
        private float _arcTimer;
        private float _arcInterval;
        private Vector3 _lastPoint;
        private float _arcDist;

        // Flash
        private float[] _flashTimestamps;
        private int _flashState = -1;   // -1 = 未设置;0 = 灭;1 = 亮

        // Fade
        private float _fadeTimer;

        // ================= 构建 =================

        /// <summary>
        /// 创建一道闪电。返回的组件已就位但尚未开播(调用 <see cref="CastBolt(Vector3, Vector3)"/>)。
        /// </summary>
        /// <param name="boltShader">主干/分叉 shader(<c>Hidden/Volken/LightningBolt</c>)。</param>
        /// <param name="flashShader">落点闪光 shader(<c>Hidden/Volken/LightningFlash</c>);为 null 时退化为共用 boltShader。</param>
        /// <param name="cam">用于闪光朝向的相机(可为 null,退化为 Camera.main)。</param>
        /// <remarks>
        /// 【2026-09-28】不再接受 <c>parent</c> 参数:bolt 现在是**根物体**。
        /// 旧实现把它挂到 <c>NearCamera</c> 下,换来的唯一好处是"随相机一起销毁",
        /// 而代价是相机一旦 <c>activeInHierarchy == false</c>,在自己身上
        /// <c>StartCoroutine</c> 就会**静默失败**,自毁链从此断掉(见类注释)。
        /// 生命周期现在完全由本组件负责,不依赖任何外部物体。
        /// </remarks>
        public static LightningBolt Create(Shader boltShader, Shader flashShader, Camera cam)
        {
            var go = new GameObject("VolkenLightningBolt");
            // 刻意不 SetParent:根物体不会被任何场景物体的激活状态牵连。
            // LineRenderer 用 world space,节点位置本来就是世界坐标,父物体没有任何必要。
            var bolt = go.AddComponent<LightningBolt>();
            bolt.targetCamera = cam;
            bolt.Build(boltShader, flashShader);
            bolt._bornTime = Time.realtimeSinceStartup;
            ActiveBolts.Add(bolt);
            return bolt;
        }

        private void Build(Shader boltShader, Shader flashShader)
        {
            if (boltShader == null)
            {
                Mod.Log("Volken:LightningBolt: bolt shader is null — bolt will be invisible");
                return;
            }

            // 【2026-09-28】主干与落点闪光**各用一个单 Pass shader**。
            // 原先两者共用一个双 Pass shader,而"一个 Material 只用第一个匹配 Pass"这条规则
            // 让闪光球一直在跑主干那套逻辑(_CoreWidth/halo 全部失效),并且"按名字启用 Pass"
            // 在没有 LightMode 标签时并不可靠 —— 拆开成两个单 Pass shader 后不存在歧义。
            // flashShader 缺失时退化:宁可闪光球用主干的画法,也不要整块变紫红(材质丢失)。
            Shader flashSrc = flashShader != null ? flashShader : boltShader;
            if (flashShader == null)
            {
                Mod.Log("Volken:LightningBolt: flash shader missing — falling back to the bolt shader for the strike flash");
            }

            _boltMat = new Material(boltShader);
            _boltMat.SetFloat("_Intensity", baseIntensity);
            _boltMat.SetColor("_Color", new Color(0.85f, 0.9f, 1f, 1f));

            _flashMat = new Material(flashSrc);
            _flashMat.SetFloat("_Intensity", 0f);
            _flashMat.SetColor("_Color", new Color(0.8f, 0.85f, 1f, 1f));
            _flashMat.SetFloat("_CoreWidth", 0.35f);

            // 主干
            _line = gameObject.AddComponent<LineRenderer>();
            _line.material = _boltMat;
            _line.positionCount = 0;
            _line.widthMultiplier = width;
            _line.useWorldSpace = true;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;
            _line.numCapVertices = 2;
            _line.numCornerVertices = 2;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.lightProbeUsage = LightProbeUsage.Off;
            _line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            // 两端渐隐:核心最亮、末端淡出(LineRenderer 顶点色会被 shader 乘进颜色)
            _line.colorGradient = BuildBoltGradient();

            // 落点闪光球
            var flashGo = new GameObject("FlashPlane");
            flashGo.transform.SetParent(transform, false);
            _flashPlane = flashGo.transform;
            var mf = flashGo.AddComponent<MeshFilter>();
            _flashMesh = BuildSphereMesh(8, 12);
            mf.sharedMesh = _flashMesh;
            var mr = flashGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _flashMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            flashGo.SetActive(false);

            // 落点点光源
            var lightGo = new GameObject("BoltLight");
            lightGo.transform.SetParent(transform, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.intensity = lightIntensity;
            _light.range = lightRange;
            _light.color = new Color(0.85f, 0.9f, 1f, 1f);
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        private static Gradient BuildBoltGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.75f, 0.82f, 1f), 0f),
                    new GradientColorKey(Color.white, 0.15f),
                    new GradientColorKey(Color.white, 0.85f),
                    new GradientColorKey(new Color(0.75f, 0.82f, 1f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(1f, 0.9f),
                    new GradientAlphaKey(0.1f, 1f),
                });
            return g;
        }

        /// <summary>
        /// 一个单位半径的经纬球(只取 uv + position)。SP2 的 planeMat 挂在一个球上,
        /// 这里用程序生成避免依赖任何内置资源。
        /// </summary>
        private static Mesh BuildSphereMesh(int segments, int rings)
        {
            var mesh = new Mesh { name = "VolkenLightningFlashSphere" };
            int vCount = (rings + 1) * (segments + 1);
            var verts = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            var tris = new int[rings * segments * 6];

            for (int y = 0; y <= rings; y++)
            {
                float v = y / (float)rings;
                float phi = v * Mathf.PI;
                for (int x = 0; x <= segments; x++)
                {
                    float u = x / (float)segments;
                    float theta = u * Mathf.PI * 2f;
                    int i = y * (segments + 1) + x;
                    verts[i] = new Vector3(
                        Mathf.Sin(phi) * Mathf.Cos(theta),
                        Mathf.Cos(phi),
                        Mathf.Sin(phi) * Mathf.Sin(theta));
                    uvs[i] = new Vector2(u, v);
                }
            }

            int t = 0;
            for (int y = 0; y < rings; y++)
            {
                for (int x = 0; x < segments; x++)
                {
                    int a = y * (segments + 1) + x;
                    int b = a + segments + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = a + 1;
                    tris[t++] = a + 1; tris[t++] = b; tris[t++] = b + 1;
                }
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        // ================= 播放 =================

        public void CastBolt(Vector3 origin, Vector3 target)
        {
            transform.position = origin;
            Target = target;
            CastBolt();
        }

        /// <summary>
        /// 开播。**不再启动协程** —— 只把状态机置到第一相,由 <see cref="Update"/> 推进。
        /// 因此即使当前 <c>activeInHierarchy == false</c> 也不会丢动画(见类注释①)。
        /// </summary>
        public void CastBolt()
        {
            if (_line == null || _phase != Phase.Done) return;

            _phase = Phase.Grow;
            _begun = false;
            _finishedInvoked = false;
            _phaseElapsed = 0f;
            _arcIndex = 1;
            _arcTimer = 0f;
            _flashTimestamps = null;
            _flashState = -1;
        }

        /// <summary>第一帧进入 Grow 时做一次性的初始化(原来在协程开头做)。</summary>
        private void BeginAnimation()
        {
            _begun = true;

            _light.enabled = false;
            _line.widthMultiplier = width;
            _boltMat.SetFloat("_Intensity", baseIntensity);
            _line.positionCount = 2;
            _line.SetPosition(0, transform.position);
            _line.SetPosition(1, transform.position);

            _lastPoint = transform.position;
            float totalDist = Vector3.Distance(transform.position, Target);
            _arcDist = totalDist / Mathf.Max(1, arcs);
            _arcInterval = UnityEngine.Random.Range(0.001f, 0.005f);
        }

        /// <summary>
        /// 主干生长(对应原版 <c>for (i = 1; i &lt; arcs; i++) { ...; yield return Random(0.001,0.005); }</c>)。
        /// 每段推进一次,逻辑与原版逐条一致;差别只在"等待"由 <see cref="Update"/> 计时而非 yield。
        /// </summary>
        private void StepGrow(float dt)
        {
            _arcTimer += dt;
            if (_arcTimer < _arcInterval) return;
            _arcTimer = 0f;
            _arcInterval = UnityEngine.Random.Range(0.001f, 0.005f);

            _boltMat.SetFloat("_Intensity", UnityEngine.Random.Range(0f, 2f));
            _line.positionCount = _arcIndex + 1;

            Vector3 dir = Target - _lastPoint;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
            dir.Normalize();
            Vector3 next = Randomize(dir, inaccuracy);
            next *= UnityEngine.Random.Range(arcLength * arcVariation, arcLength) * _arcDist;
            next += _lastPoint;
            _line.SetPosition(_arcIndex, next);

            // 原版:i < arcs - 2 的分叉点各生 splits+1 条
            if (_arcIndex < arcs - 2 && splits >= 0)
            {
                for (int j = 0; j <= splits; j++)
                {
                    SpawnSplit(next);
                }
            }

            _lastPoint = next;
            _arcIndex++;

            if (_arcIndex >= arcs)
            {
                // 主干收尾(原版:SetPosition(arcs - 1, Target) 之后进入闪光)
                _line.SetPosition(arcs - 1, Target);
                BeginFlash();
            }
        }

        /// <summary>落点就位 + 排好 4 次闪烁的时刻表(对应原版在闪光循环之前的那一段)。</summary>
        private void BeginFlash()
        {
            // 落点点光源朝向相机(原版:myLight.transform.LookAt(EnviroManager.instance.Camera...))
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam != null && _light != null)
            {
                _light.transform.position = Target;
                _light.transform.LookAt(cam.transform.position, Vector3.up);
            }

            // 落点闪光球定位 + 缩放(直径 = 1/10 落距,给一个可见但不荒谬的闪光范围)
            float totalDist = Vector3.Distance(transform.position, Target);
            float flashDiameter = Mathf.Clamp(totalDist * 0.1f, 20f, 4000f);
            if (_flashPlane != null)
            {
                _flashPlane.position = Target;
                _flashPlane.localScale = Vector3.one * flashDiameter;
                _flashPlane.gameObject.SetActive(true);
            }

            // 时刻表:每次"亮 25~35ms → 灭 25~35ms";最后一次闪完没有熄灭间隔,直接进淡出
            _flashTimestamps = new float[FlashCount * 2];
            float t = 0f;
            for (int i = 0; i < FlashCount; i++)
            {
                _flashTimestamps[i * 2] = t;                                        // 亮
                t += UnityEngine.Random.Range(0.025f, 0.035f);
                if (i < FlashCount - 1)
                {
                    _flashTimestamps[i * 2 + 1] = t;                                // 灭
                    t += UnityEngine.Random.Range(0.025f, 0.035f);
                }
            }

            _phase = Phase.Flash;
            _phaseElapsed = 0f;
            _flashState = -1;
        }

        /// <summary>
        /// 4 次闪烁(对应原版 4 轮"亮 → yield → 灭 → yield")。
        ///
        /// 【为什么用时刻表而不是一堆布尔状态】原版是"亮 → await → 灭 → await"顺序执行,
        /// 改写为每帧推进后必须能容忍 <c>dt</c> 跨过多个时间点(帧率抖动/时间加速),
        /// 所以每隔一段就按 <c>_phaseElapsed</c> **重算**当前应该是亮还是灭,而不是"翻一次面"。
        /// </summary>
        private void StepFlash(float dt)
        {
            _phaseElapsed += dt;

            int want = 0;   // 0 = 灭,1 = 亮
            for (int i = _flashTimestamps.Length - 1; i >= 0; i--)
            {
                if (_phaseElapsed >= _flashTimestamps[i]) { want = (i % 2 == 0) ? 1 : 0; break; }
            }

            if (want != _flashState)
            {
                _flashState = want;
                bool on = want == 1;
                _boltMat.SetFloat("_Intensity", on ? flashIntensity : baseIntensity);
                _flashMat.SetFloat("_Intensity", on ? flashPlaneIntensity : 0f);
                _light.enabled = on;
            }

            float flashTotal = _flashTimestamps[_flashTimestamps.Length - 1] +
                               UnityEngine.Random.Range(0.025f, 0.035f);
            if (_phaseElapsed >= flashTotal)
            {
                // 确保以"灭"收尾(原版最后一次闪完直接进淡出)
                if (_flashState != 0)
                {
                    _flashState = 0;
                    _flashMat.SetFloat("_Intensity", 0f);
                    _light.enabled = false;
                }

                // 通知外部(雷声)。距离由调用方按"落点 ↔ 观测者"自算,见 OnBoltLanded 的说明。
                try { OnBoltLanded?.Invoke(); }
                catch (Exception ex) { Mod.Log("Volken:LightningBolt OnBoltLanded threw: " + ex.Message); }

                _fadeTimer = 50f;   // 原版:用 50 当初始强度淡出
                _phase = Phase.Fade;
                _phaseElapsed = 0f;
            }
        }

        /// <summary>淡出并销毁(对应原版 <c>fadeTimer = Lerp(fadeTimer, 0, 10*dt)</c>)。</summary>
        private void StepFade(float dt)
        {
            _fadeTimer = Mathf.Lerp(_fadeTimer, 0f, 10f * Mathf.Max(0f, dt));   // 原版:10*dt
            if (_boltMat != null) _boltMat.SetFloat("_Intensity", _fadeTimer);

            if (_fadeTimer <= 1f)
            {
                Finish();
            }
        }

        /// <summary>收尾:清理视觉残留 + 回调 + 销毁自身。**保证只执行一次**。</summary>
        private void Finish()
        {
            if (_phase == Phase.Done) return;
            _phase = Phase.Done;

            if (_line != null) _line.positionCount = 0;
            if (_flashPlane != null) _flashPlane.gameObject.SetActive(false);
            if (_light != null) _light.enabled = false;

            // 【2026-09-28】分叉与主干**同步销毁**。
            // 这是"闪电偶发紫红色"的正解:分叉各自持有自己的材质实例,而那些实例是
            // <c>new Material(_boltMat)</c> 出来的——"父材质" 的引用计数不会保护它们,
            // 但同样地,只要分叉还活着、材质就还活着。既然这里连 GameObject 一起销毁,
            // 就不会再出现"渲染器指向已销毁材质"(=Error shader 淡紫)。
            DestroySplits();

            if (!_finishedInvoked)
            {
                _finishedInvoked = true;
                try { OnBoltFinished?.Invoke(); }
                catch (Exception ex) { Mod.Log("Volken:LightningBolt OnBoltFinished threw: " + ex.Message); }
            }

            Release();
            Destroy(gameObject);
        }

        /// <summary>销毁本道闪电的全部分叉(连同它们各自的材质实例)。幂等。</summary>
        private void DestroySplits()
        {
            for (int i = 0; i < _splits.Count; i++)
            {
                var go = _splits[i];
                if (go == null) continue;

                // 材质是每个分叉独占的实例(new Material(_boltMat)),所以可以就地销毁 —— 
                // 不销毁的话每次落雷都会留下几份永不回收的材质。
                var sr = go.GetComponent<LineRenderer>();
                if (sr != null && sr.sharedMaterial != null) Destroy(sr.sharedMaterial);

                Destroy(go);
            }
            _splits.Clear();
        }

        /// <summary>从注册表移除(销毁/被整批销毁时调用,幂等)。</summary>
        private void Release()
        {
            if (_released) return;
            _released = true;
            ActiveBolts.Remove(this);
        }

        /// <summary>
        /// 一条分叉(对应 SP2 <c>Enviro.Lightning.CreateSplit</c>):8 点、朝"目标方向 + 球内随机 ×500"飞,
        /// 存活 0.2~0.5s。
        ///
        /// 【2026-09-28 两处修正】
        ///   ① 从协程改成"带自毁定时器的根物体":原来在 bolt 自己身上 <c>StartCoroutine</c>,
        ///      bolt 一旦 inactive 就静默失败,分叉线会永远挂在那里。
        ///   ② **不再共享 bolt 的材质实例**。原来 <c>sr.material = _boltMat</c>,
        ///      而 bolt 收尾时会 <c>Destroy(_boltMat)</c> —— 只要分叉比 bolt 活得久
        ///      (它的计时器原来是 <c>realtimeSinceStartup</c>,暂停/失焦时**不前进**,可以被无限推迟),
        ///      渲染时材质已销毁 → Unity 回退 **Error shader(淡紫/品红)**,正是"闪电偶发紫红色"。
        ///      现在每条分叉各自持有材质实例,并登记到 <see cref="_splits"/>,
        ///      由 bolt 在 <see cref="Finish"/> 里与主干**同步**销毁,不可能出现"引用已销毁材质"。
        /// </summary>
        private void SpawnSplit(Vector3 from)
        {
            var splitGo = new GameObject("Split");
            splitGo.transform.position = from;
            var sr = splitGo.AddComponent<LineRenderer>();
            sr.material = new Material(_boltMat);   // 自己的实例,见上面②
            sr.widthMultiplier = width * 0.5f;
            sr.useWorldSpace = true;
            sr.alignment = LineAlignment.View;
            sr.positionCount = 2;
            sr.SetPosition(0, from);
            sr.SetPosition(1, from);
            sr.colorGradient = BuildBoltGradient();
            sr.shadowCastingMode = ShadowCastingMode.Off;
            sr.receiveShadows = false;
            sr.lightProbeUsage = LightProbeUsage.Off;
            sr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            // 原版:toTarget 归一化后朝它飞,但 targetPos 实际是"球内随机*500 + pos + toTarget*500"
            Vector3 toTarget = Target - from;
            if (toTarget.sqrMagnitude < 1e-8f) toTarget = Vector3.down;
            toTarget.Normalize();
            Vector3 targetPos = UnityEngine.Random.insideUnitSphere * 500f + from + toTarget * 500f;

            // 折线点一次性算完(原版是协程逐点加 0.004~0.006s 延迟;分叉只是点缀,
            // 一次性画完视觉差异不可辨,却把"协程"这个故障源彻底去掉)
            const int splitPoints = 7;   // i = 1..7
            sr.positionCount = splitPoints + 1;
            Vector3 lastPoint = from;
            float dist = Vector3.Distance(from, targetPos);
            float arcDist = dist / splitPoints;
            for (int i = 1; i <= splitPoints; i++)
            {
                Vector3 dir = targetPos - lastPoint;
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
                dir.Normalize();
                Vector3 next = Randomize(dir, inaccuracy);
                next *= UnityEngine.Random.Range(1f, 1.5f) * arcDist;
                next += lastPoint;
                sr.SetPosition(i, next);
                lastPoint = next;
            }
            sr.SetPosition(splitPoints, targetPos);

            // 自毁:存活 0.2~0.5s(原版),用独立组件而不是协程
            var selfDestruct = splitGo.AddComponent<SelfDestruct>();
            selfDestruct.lifetime = UnityEngine.Random.Range(0.2f, 0.5f);

            _splits.Add(splitGo);
        }

        private Vector3 Randomize(Vector3 dir, float deviation)
        {
            dir += new Vector3(
                UnityEngine.Random.Range(-1f, 1f),
                UnityEngine.Random.Range(-1f, 1f),
                UnityEngine.Random.Range(-1f, 1f)) * deviation;
            dir.Normalize();
            return dir;
        }

        // ================= 每帧驱动 =================

        /// <summary>
        /// 动画与**自毁**的唯一推进点。
        ///
        /// 【为什么把自毁放进这里,而不是只留在"播完"那条路径上】
        /// 这是本次 bug 的核心教训:旧实现里"销毁"只挂在"主协程跑完"这一条路径上,
        /// 协程一旦没跑完(最常见:bolt 的 GameObject 在层级里 inactive,Unity 静默拒绝启动协程),
        /// 就**再也没有任何东西能销毁它**。现在销毁有**三条独立**的触发路径:
        ///   ① 正常播完(<see cref="Finish"/>);
        ///   ② <see cref="MaxHiddenSeconds"/>:连续不可见过久 → 认定不会恢复(见下);
        ///   ③ <see cref="HardLifetime"/>:无条件兜底上限;
        /// 再加上模块停用时的 <see cref="DestroyAll"/> 整批清理。
        /// </summary>
        private void Update()
        {
            // 连续不可见时长 = 现在 − **最近一次还在 Update 的时刻**(该时刻随时间前移)。
            // 不要用 _bornTime:那是"总年龄",用它会把"活满 1.5s 就杀"当成僵死判定。
            float hiddenFor = Time.realtimeSinceStartup - _lastActiveTime;

            if (_phase != Phase.Done)
            {
                // ② 僵死:连续不可见超过 MaxHiddenSeconds → 认定不会再恢复,直接销毁。
                //    正常切换视角/相机几乎立刻恢复,不会误杀。
                if (hiddenFor > MaxHiddenSeconds)
                {
                    Mod.Log($"Volken:LightningBolt hidden for {hiddenFor:F1}s (phase={_phase}) — cleaning up");
                    Finish();
                    return;
                }

                switch (_phase)
                {
                    case Phase.Grow:
                        if (!_begun) BeginAnimation();
                        StepGrow(Time.deltaTime);
                        break;
                    case Phase.Flash:
                        StepFlash(Time.deltaTime);
                        break;
                    case Phase.Fade:
                        StepFade(Time.deltaTime);
                        break;
                }
            }

            // ③ 硬性兜底寿命:无条件上限,保证任何状态下都不会有雷永久残留
            if (_phase == Phase.Done) return;
            if (Time.realtimeSinceStartup - _bornTime > HardLifetime)
            {
                Mod.Log($"Volken:LightningBolt hard lifetime {HardLifetime:F1}s exceeded (phase={_phase}) — cleaning up");
                Finish();
            }
        }

        /// <summary>最近一次"本组件在 update"的时间戳,用于检测自己是否被藏起来(见 Update 的②)。</summary>
        private float _lastActiveTime;

        private void LateUpdate()
        {
            // 放在 LateUpdate:Update 里若已 Finish,这里就不会再刷新时间戳
            if (_phase != Phase.Done) _lastActiveTime = Time.realtimeSinceStartup;
        }

        private void OnEnable()
        {
            _lastActiveTime = Time.realtimeSinceStartup;
        }

        private void OnDestroy()
        {
            Release();
            DestroySplits();   // 防漏:DestroyAll / 异常路径下也要把分叉带走(幂等)
            if (_boltMat != null) Destroy(_boltMat);
            if (_flashMat != null) Destroy(_flashMat);
            if (_flashMesh != null) Destroy(_flashMesh);
        }
    }

    /// <summary>
    /// 极简自毁组件:存活 <see cref="lifetime"/> 秒后销毁自己的 GameObject。
    ///
    /// 用途 = 闪电分叉线。**刻意不用协程**:协程在 <c>activeInHierarchy == false</c> 时
    /// 会静默启动失败,这正是"闪电残留"的根因之一;而 <c>Update</c> 天然只在可见时跑,
    /// 一旦恢复可见就会继续计时并销毁,不存在"永久残留"的状态。
    ///
    /// 【计时用 Time.unscaledTime,不用 realtimeSinceStartup】
    /// <c>realtimeSinceStartup</c> 在失焦/暂停时**仍会前进**,而 <c>unscaledTime</c> 随游戏暂停一起停 —— 
    /// 分叉是"闪电的余晖",暂停时不该继续自毁;恢复后接着数完,行为更自然。
    /// (注意与材质生命周期的关系:分叉各持自己的材质实例,且 bolt 收尾时会连分叉一起销毁,
    ///  见 <c>LightningBolt.DestroySplits</c> —— 所以这里就算晚一点销毁也不会出现"引用已销毁材质"。)
    /// </summary>
    public class SelfDestruct : MonoBehaviour
    {
        /// <summary>存活时长(秒)。</summary>
        public float lifetime = 0.5f;

        private float _spawnTime;

        private void Awake()
        {
            _spawnTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (Time.unscaledTime - _spawnTime >= lifetime)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            // 分叉的材质是它独占的实例(见 LightningBolt.SpawnSplit),要跟着一起回收,
            // 否则每次落雷都会留下几份永不释放的材质。
            var sr = GetComponent<LineRenderer>();
            if (sr != null && sr.sharedMaterial != null) Destroy(sr.sharedMaterial);
        }
    }
}
