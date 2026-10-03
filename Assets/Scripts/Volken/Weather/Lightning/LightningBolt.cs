using System;
using Assets.Scripts;
using ModApi;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Weather
{
    /// 一道闪电的视觉表现:主干 LineRenderer 逐段"生长" + 分叉 + 落点闪光球/点光源。播完自毁,不池化(雷击间隔秒级)。
    /// 必须由 <see cref="Update"/> 驱动、**不能用协程**:层级 inactive 时 Unity 会静默拒绝启动协程(只打一行警告),
    /// 销毁就再没有别的触发路径 → 闪电永久残留;分叉用整道雷共享的一份材质拷贝(<see cref="SplitMaterial"/>),
    /// **不能**直接用主干材质 —— 主干收尾的 <c>Destroy(_boltMat)</c> 会让还在渲染的分叉变成品红/淡紫。
    /// 残留/紫红根因与打包清单见 docs/proposals/thunder-realism-2026-09-28.md §7 §8。
    public class LightningBolt : MonoBehaviour
    {
        public Camera targetCamera;   // 每道闪电各持一个:改成 static 会在多相机(PIP)下互相覆盖,闪光朝向会串

        // 参数(由 LightningModule 从 VolkenWeatherConfig.LightningSection 灌入)
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

        /// 落雷后回调(延迟播雷声用)。**不带参数** —— 距离必须由调用方按"落点 ↔ 观测者"自己算
        /// (<c>transform.position</c> 是 bolt 的起点、在云里,与观测者无关)。
        public Action OnBoltLanded;

        /// 整道闪电播完(自毁前)回调,**保证最多被调用一次**。
        public Action OnBoltFinished;

        public Vector3 Target { get; private set; }

        // 两个时间量不可混用:_lastActiveTime = 最近一次还在 Update 的时刻(随时间前移),_bornTime = 出生时刻(固定)。
        // 混用会让"僵死判定"退化成"活满 N 秒就杀",正常雷都会被误杀(见 Update)。
        // 两者都用 GamePause.Now(排除暂停时长):否则游戏暂停超过 HardLifetime 就会把这道雷掐掉,而它的雷声恢复后还会响 —— 空响一声雷。
        private const float HardLifetime = 3.5f;       // 秒,自**出生**起算,超过无条件销毁(正常一道雷 <2s 播完)
        private const int FlashCount = 4;              // 亮 25~35ms → 灭 25~35ms 交替;最后一次闪完直接淡出
        private const float MaxHiddenSeconds = 1.5f;   // 连续不可见(自上次 Update 起算)超过它 → 认定不会再恢复 → 销毁;正常切视角远达不到

        // 全部存活闪电:模块停用 / 离开场景时整批清理(见 DestroyAll),防"永久残留"。
        private static readonly System.Collections.Generic.List<LightningBolt> ActiveBolts =
            new System.Collections.Generic.List<LightningBolt>();

        /// <summary>当前参考系纪元:SR2 每次**浮动原点重定位**就 +1(由 <see cref="LightningModule"/> 检测后调
        /// <see cref="DestroyStale"/> 推进)。本道闪电记住出生时的纪元,纪元一变就自毁。</summary>
        public static int FrameIndex { get; private set; }

        /// <summary>推进纪元并清掉属于旧纪元的闪电。纪元只有"相等 / 不相等"两种含义,不做 &lt;/&gt; 比较。
        /// 一行完成"推进 + 清理",免得调用方顺序写错(先清理后推进会把新纪元的雷也清掉)。返回清掉的条数。</summary>
        public static int DestroyStale()
        {
            int killed = 0;
            // 倒序 + 判空:Destroy 是帧末生效,列表里可能有已销毁(== null)的条目
            for (int i = ActiveBolts.Count - 1; i >= 0; i--)
            {
                var b = ActiveBolts[i];
                if (b == null) { ActiveBolts.RemoveAt(i); continue; }
                if (b._bornFrameIndex == FrameIndex) continue;
                b.Finish();
                killed++;
            }
            FrameIndex++;
            return killed;
        }

        public static void DestroyAll()
        {
            // 倒序 + 判空:Destroy 是帧末生效,列表里可能有已销毁(== null)的条目
            for (int i = ActiveBolts.Count - 1; i >= 0; i--)
            {
                var b = ActiveBolts[i];
                if (b == null) { ActiveBolts.RemoveAt(i); continue; }
                b.Release();
                // Destroy 不要求 GameObject 处于激活状态,所以不需要(也不应该)先 SetActive(true)
                UnityEngine.Object.Destroy(b.gameObject);
            }
            ActiveBolts.Clear();
        }

        public static int ActiveCount => ActiveBolts.Count;

        private enum Phase
        {
            Grow,
            Flash,
            Fade,
            Done,
        }

        // 程序化资源全闪电共用:每道雷重建 = 每次多一个网格,每分叉一个渐变。
        private static Mesh _sharedFlashMesh;
        private static Gradient _sharedGradient;

        private LineRenderer _line;
        private Light _light;
        private Transform _flashPlane;
        private Material _boltMat;
        private Material _flashMat;
        private Material _splitMat;   // 全部分叉共用;各分叉独占 = 每次落雷多造 (splits+1)×(arcs-2) 份材质

        // 本道闪电生出的全部分叉,收尾时与主干**同步**销毁(见 Finish)。
        private readonly System.Collections.Generic.List<GameObject> _splits =
            new System.Collections.Generic.List<GameObject>();

        private Phase _phase = Phase.Done;
        private bool _begun;
        private bool _finishedInvoked;
        private bool _released;
        private float _bornTime;
        private float _phaseElapsed;

        // 出生时的参考系纪元(见 FrameIndex):纪元变了 = 这道雷的线段/落点闪光/点光源坐标已经作废
        private int _bornFrameIndex;

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

        /// 创建一道闪电(组件已就位但尚未开播,需再调 <see cref="CastBolt(Vector3, Vector3)"/>)。
        public static LightningBolt Create(Shader boltShader, Shader flashShader, Camera cam)
        {
            var go = new GameObject("VolkenLightningBolt");
            // 刻意不 SetParent:bolt 要当根物体,挂到相机会让相机 inactive 时自毁链一起断掉
            var bolt = go.AddComponent<LightningBolt>();
            bolt.targetCamera = cam;
            bolt.Build(boltShader, flashShader);
            bolt._bornTime = GamePause.Now;
            bolt._bornFrameIndex = FrameIndex;   // 记下这道雷属于哪个参考系纪元
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

            // 主干与落点闪光各用一个单 Pass shader:一个 Material 只用第一个匹配 Pass,共用双 Pass shader
            // 时闪光球会一直跑主干那套逻辑(_CoreWidth/halo 全部失效)。flashShader 缺失时退化共用,免得整块变紫红。
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
            _line.colorGradient = SharedGradient;   // 两端渐隐(shader 会把顶点色乘进颜色)

            // 落点闪光球
            var flashGo = new GameObject("FlashPlane");
            flashGo.transform.SetParent(transform, false);
            _flashPlane = flashGo.transform;
            var mf = flashGo.AddComponent<MeshFilter>();
            mf.sharedMesh = SharedFlashMesh;
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

        /// <summary>落点闪光球网格:全闪电共用的静态资源(每道雷重建一份纯属浪费)。</summary>
        private static Mesh SharedFlashMesh
        {
            get
            {
                if (_sharedFlashMesh == null)
                {
                    _sharedFlashMesh = BuildSphereMesh(8, 12);
                    _sharedFlashMesh.hideFlags = HideFlags.HideAndDontSave;   // 不在场景切换时被回收
                }
                return _sharedFlashMesh;
            }
        }

        /// <summary>主干与分叉共用的颜色渐变(<c>LineRenderer.colorGradient</c> 是拷贝语义,可共享同一份)。</summary>
        private static Gradient SharedGradient
        {
            get
            {
                if (_sharedGradient == null) _sharedGradient = BuildBoltGradient();
                return _sharedGradient;
            }
        }

        /// <summary>主干材质的一次拷贝,供本道闪电的全部分叉共用;统一在 <see cref="DestroySplits"/> 回收。</summary>
        private Material SplitMaterial
        {
            get
            {
                if (_splitMat == null) _splitMat = new Material(_boltMat);
                return _splitMat;
            }
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

        // 单位半径经纬球(只有 uv + position),程序生成以避免依赖内置资源。
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

        public void CastBolt(Vector3 origin, Vector3 target)
        {
            transform.position = origin;
            Target = target;
            CastBolt();
        }

        /// 开播:**不启动协程**,只置状态机第一相,由 <see cref="Update"/> 推进(它是 inactive 时唯一还能跑的东西)。
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

            // 末两段不挂分叉,其余每个节点各生 splits+1 条
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
                _line.SetPosition(arcs - 1, Target);   // 末点钉到落点
                BeginFlash();
            }
        }

        private void BeginFlash()
        {
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam != null && _light != null)
            {
                _light.transform.position = Target;
                _light.transform.LookAt(cam.transform.position, Vector3.up);
            }

            float totalDist = Vector3.Distance(transform.position, Target);
            float flashDiameter = Mathf.Clamp(totalDist * 0.1f, 20f, 4000f);
            if (_flashPlane != null)
            {
                _flashPlane.position = Target;
                _flashPlane.localScale = Vector3.one * flashDiameter;
                _flashPlane.gameObject.SetActive(true);
            }

            // 时刻表:亮 → 灭 交替
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

        /// 每帧按 <c>_phaseElapsed</c> **重算**该亮还是该灭(而不是"翻一次面"),才能容忍 <c>dt</c> 一次跨过多个时间点。
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
                if (_flashState != 0)
                {
                    _flashState = 0;
                    _flashMat.SetFloat("_Intensity", 0f);
                    _light.enabled = false;
                }

                try { OnBoltLanded?.Invoke(); }
                catch (Exception ex) { Mod.Log("Volken:LightningBolt OnBoltLanded threw: " + ex.Message); }

                _fadeTimer = 50f;   // 用 50 当初始强度淡出
                _phase = Phase.Fade;
                _phaseElapsed = 0f;
            }
        }

        /// <summary>暂停期间的"闪光收光"开关(见 <see cref="BeginFlashSuppression"/>)。</summary>
        private bool _flashSuppressed;

        /// <summary>暂停时把闪光的**亮态**收掉,只留静止的线段与闪光球几何。
        /// 为什么必须收:亮/灭的切换**只发生在 <see cref="StepFlash"/> 里**,而它被 <c>!GamePause.IsPaused</c> 挡在门外;
        /// 计时又用 <see cref="GamePause.Now"/>(排除暂停时长),所以暂停中卡在"亮"那一帧的雷:
        /// ① 点光源与加色闪光材质保持全亮的**最终值**;② <c>_phaseElapsed</c> 与 <c>_bornTime</c> 同时停表,<c>HardLifetime</c> 永远不会到期。
        /// 合计 = 一盏**永久**挂在落点、穿地形绘制(ZTest Always)的灯 —— 正是"固定位置的射灯"。暂停本身会冻住动画,
        /// 但"冻住一束强光"不是可接受的表现:闪光本来就只该是一瞬。</summary>
        private void BeginFlashSuppression()
        {
            if (_flashSuppressed || _phase != Phase.Flash) return;
            _flashSuppressed = true;

            if (_flashState != 0)
            {
                _flashState = 0;
                if (_flashMat != null) _flashMat.SetFloat("_Intensity", 0f);
                if (_light != null) _light.enabled = false;
            }
            // 线段压回基础强度:暂停时别留一根"永远最亮"的主干
            if (_boltMat != null) _boltMat.SetFloat("_Intensity", baseIntensity);

            // 留证据:症状(固定位置射灯)若是这条路,日志里就会出现它,且落点/相机距离可复核
            var cam = targetCamera != null ? targetCamera : Camera.main;
            float camDist = cam != null ? Vector3.Distance(cam.transform.position, Target) : -1f;
            Mod.Log($"Volken:LightningBolt pause froze flash — light+additive flash turned off " +
                    $"(landing=({Target.x:F0},{Target.y:F0},{Target.z:F0}) camDist={camDist:F0}m phaseElapsed={_phaseElapsed:F3}s)");
        }

        /// <summary>恢复:清掉抑制标记,并把亮/灭状态标成"未知",让 <see cref="StepFlash"/> 下一次按 <c>_phaseElapsed</c> 重算。
        /// 无条件清(不看 <c>_phase</c>):若暂停期间相位已推进到 Flash 之外,标记也必须归零,否则会锁死后续判断。</summary>
        private void EndFlashSuppression()
        {
            _flashSuppressed = false;
            _flashState = -1;
        }

        private void StepFade(float dt)
        {
            _fadeTimer = Mathf.Lerp(_fadeTimer, 0f, 10f * Mathf.Max(0f, dt));
            if (_boltMat != null) _boltMat.SetFloat("_Intensity", _fadeTimer);

            if (_fadeTimer <= 1f)
            {
                Finish();
            }
        }

        /// 收尾:清理视觉残留 + 回调 + 销毁自身。**保证只执行一次**(靠 <c>Phase.Done</c> 守卫)。
        private void Finish()
        {
            if (_phase == Phase.Done) return;
            _phase = Phase.Done;

            if (_line != null) _line.positionCount = 0;
            if (_flashPlane != null) _flashPlane.gameObject.SetActive(false);
            if (_light != null) _light.enabled = false;

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

        private void DestroySplits()
        {
            for (int i = 0; i < _splits.Count; i++)
            {
                if (_splits[i] != null) Destroy(_splits[i]);
            }
            _splits.Clear();

            // 分叉共用的那**一份**材质在最后回收;漏掉则每次落雷都留下一份永不回收的材质。
            if (_splitMat != null)
            {
                Destroy(_splitMat);
                _splitMat = null;
            }
        }

        private void Release()
        {
            if (_released) return;
            _released = true;
            ActiveBolts.Remove(this);
        }

        /// 一条分叉:8 点、朝"目标方向 + 球内随机 ×500"飞,存活 0.2~0.5s。
        /// 材质**不能**直接用主干那份(主干收尾会 <c>Destroy(_boltMat)</c> → 分叉渲染成品红/淡紫),改用 <see cref="SplitMaterial"/> 的整道雷共享拷贝。
        private void SpawnSplit(Vector3 from)
        {
            var splitGo = new GameObject("Split");
            splitGo.transform.position = from;
            var sr = splitGo.AddComponent<LineRenderer>();
            sr.material = SplitMaterial;
            sr.widthMultiplier = width * 0.5f;
            sr.useWorldSpace = true;
            sr.alignment = LineAlignment.View;
            sr.colorGradient = SharedGradient;
            sr.shadowCastingMode = ShadowCastingMode.Off;
            sr.receiveShadows = false;
            sr.lightProbeUsage = LightProbeUsage.Off;
            sr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            Vector3 toTarget = Target - from;
            if (toTarget.sqrMagnitude < 1e-8f) toTarget = Vector3.down;
            toTarget.Normalize();
            Vector3 targetPos = UnityEngine.Random.insideUnitSphere * 500f + from + toTarget * 500f;

            // 折线点一次性算完(不逐点延迟):视觉差异不可辨,却彻底移除了"协程静默失败"这个故障源。
            const int splitPoints = 7;   // 段数;i = 0..7,共 8 个点
            sr.positionCount = splitPoints + 1;

            //  第 0 个点必须**显式**写:positionCount 只是把新点初始化成 (0,0,0),而 useWorldSpace = true 时
            // (0,0,0) 就是**世界原点**。少这一行 → 每条分叉都从世界原点拉出来;SR2 的浮动原点又把原点重定位到飞船处,
            // 于是 (arcs-3)×(splits+1) 条分叉全部从**观测者**身上呈扇形射出 —— 看上去就是一把"射灯",而不是雷劈。
            sr.SetPosition(0, from);

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

            // 自毁:存活 0.2~0.5s,用独立组件而不是协程
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

        /// 动画与**自毁**的唯一推进点。五条独立销毁/收尾路径(正常播完 / 连续不可见过久 / 硬性寿命上限 / 参考系重定位 / 暂停收光),任一失效都不会留下残留。
        private void Update()
        {
            // 连续不可见时长 = 现在 − **最近一次还在 Update 的时刻**(该时刻随时间前移)。
            // 不要用 _bornTime:那是"总年龄",用它会把"活满 1.5s 就杀"当成僵死判定。
            float now = GamePause.Now;
            float hiddenFor = now - _lastActiveTime;

            if (_phase != Phase.Done)
            {
                // 参考系重定位:本道雷的坐标全在旧世界里(线段是 world space、闪光球/点光源按落点摆好),
                // 留到下个纪元就变成"固定在世界某处的一束亮光/射灯"。见 FrameIndex / LightningModule.BeginFrameGuard。
                if (_bornFrameIndex != FrameIndex)
                {
                    Mod.Log($"Volken:LightningBolt frame recentered mid-bolt (epoch {_bornFrameIndex}→{FrameIndex}, phase={_phase}) — cleaning up");
                    Finish();
                    return;
                }

                if (hiddenFor > MaxHiddenSeconds)
                {
                    Mod.Log($"Volken:LightningBolt hidden for {hiddenFor:F1}s (phase={_phase}) — cleaning up");
                    Finish();
                    return;
                }

                if (!GamePause.IsPaused)   // 暂停时冻住动画,与同样冻住的雷声保持同步
                {
                    EndFlashSuppression();   // 恢复:由 StepFlash 按时间表重算该亮还是该灭
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
                else
                {
                    BeginFlashSuppression();   // 暂停:把"闪光的亮态"收掉(见该方法说明)
                }
            }

            if (_phase == Phase.Done) return;
            if (now - _bornTime > HardLifetime)
            {
                Mod.Log($"Volken:LightningBolt hard lifetime {HardLifetime:F1}s exceeded (phase={_phase}) — cleaning up");
                Finish();
            }
        }

        /// 最近一次"本组件在 Update"的时间戳(随时间前移,与 <c>_bornTime</c> 不同),用于检测自己是否被藏起来。
        private float _lastActiveTime;

        private void LateUpdate()
        {
            // 放 LateUpdate:Update 里若已 Finish,这里就不会再刷新时间戳
            if (_phase != Phase.Done) _lastActiveTime = GamePause.Now;
        }

        private void OnEnable()
        {
            _lastActiveTime = GamePause.Now;
        }

        private void OnDestroy()
        {
            Release();
            DestroySplits();   // 防漏:DestroyAll / 异常路径下也要把分叉带走(幂等)
            if (_boltMat != null) Destroy(_boltMat);
            if (_flashMat != null) Destroy(_flashMat);
            // _sharedFlashMesh 是全闪电共用的静态资源,不在这里销毁
        }
    }

    /// 极简自毁组件:存活 <see cref="lifetime"/> 秒后销毁自己的 GameObject(用途 = 闪电分叉线)。
    /// **刻意不用协程**:协程在 <c>activeInHierarchy == false</c> 时会静默启动失败;Update 天然只在可见时跑,恢复可见后继续计时。
    /// **计时用 <c>GamePause.Now</c>**:分叉是余晖,暂停时不该继续自毁 —— 注意 <c>realtimeSinceStartup</c> 与 <c>unscaledTime</c> 在**游戏暂停**时都照走,都不能用。
    public class SelfDestruct : MonoBehaviour
    {
        public float lifetime = 0.5f;   // 秒

        private float _spawnTime;

        private void Awake()
        {
            _spawnTime = GamePause.Now;
        }

        private void Update()
        {
            if (GamePause.Now - _spawnTime >= lifetime)
            {
                Destroy(gameObject);
            }
        }

        // 刻意没有 OnDestroy:分叉材质由 LightningBolt 统一回收(全部分叉共用一份,按分叉销毁会连累同伴)
    }
}
