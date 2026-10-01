using System;
using Assets.Scripts;
using ModApi;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Weather
{
    /// 一道闪电的视觉表现:主干 LineRenderer 逐段"生长" + 分叉 + 落点闪光球/点光源。播完自毁,不池化(雷击间隔秒级)。
    /// 必须由 <see cref="Update"/> 驱动、**不能用协程**:层级 inactive 时 Unity 会静默拒绝启动协程(只打一行警告),
    /// 销毁就再没有别的触发路径 → 闪电永久残留;分叉必须各自持有自己的材质实例,否则主干 <c>Destroy(_boltMat)</c> 后渲染成品红/淡紫。
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
        private const float HardLifetime = 3.5f;       // 秒,自**出生**起算,超过无条件销毁(正常一道雷 <2s 播完)
        private const int FlashCount = 4;              // 亮 25~35ms → 灭 25~35ms 交替;最后一次闪完直接淡出
        private const float MaxHiddenSeconds = 1.5f;   // 连续不可见(自上次 Update 起算)超过它 → 认定不会再恢复 → 销毁;正常切视角远达不到

        // 全部存活闪电:模块停用 / 离开场景时整批清理(见 DestroyAll),防"永久残留"。
        private static readonly System.Collections.Generic.List<LightningBolt> ActiveBolts =
            new System.Collections.Generic.List<LightningBolt>();

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

        private LineRenderer _line;
        private Light _light;
        private Transform _flashPlane;
        private Material _boltMat;
        private Material _flashMat;
        private Mesh _flashMesh;

        // 本道闪电生出的全部分叉,收尾时与主干**同步**销毁(见 Finish)。
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

        /// 创建一道闪电(组件已就位但尚未开播,需再调 <see cref="CastBolt(Vector3, Vector3)"/>)。
        public static LightningBolt Create(Shader boltShader, Shader flashShader, Camera cam)
        {
            var go = new GameObject("VolkenLightningBolt");
            // 刻意不 SetParent:bolt 要当根物体,挂到相机会让相机 inactive 时自毁链一起断掉
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
            _line.colorGradient = BuildBoltGradient();   // 两端渐隐(shader 会把顶点色乘进颜色)

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
                var go = _splits[i];
                if (go == null) continue;

                // 材质是每个分叉独占的实例,可以就地销毁;不销毁则每次落雷都漏几份永不回收的材质。
                var sr = go.GetComponent<LineRenderer>();
                if (sr != null && sr.sharedMaterial != null) Destroy(sr.sharedMaterial);

                Destroy(go);
            }
            _splits.Clear();
        }

        private void Release()
        {
            if (_released) return;
            _released = true;
            ActiveBolts.Remove(this);
        }

        /// 一条分叉:8 点、朝"目标方向 + 球内随机 ×500"飞,存活 0.2~0.5s。
        /// **必须各自持有自己的材质实例**(<c>new Material(_boltMat)</c>):共享主干材质时,主干收尾的 <c>Destroy(_boltMat)</c> 会让分叉渲染成品红/淡紫。
        private void SpawnSplit(Vector3 from)
        {
            var splitGo = new GameObject("Split");
            splitGo.transform.position = from;
            var sr = splitGo.AddComponent<LineRenderer>();
            sr.material = new Material(_boltMat);   // 独占实例,不可共享(见上)
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

            Vector3 toTarget = Target - from;
            if (toTarget.sqrMagnitude < 1e-8f) toTarget = Vector3.down;
            toTarget.Normalize();
            Vector3 targetPos = UnityEngine.Random.insideUnitSphere * 500f + from + toTarget * 500f;

            // 折线点一次性算完(不逐点延迟):视觉差异不可辨,却彻底移除了"协程静默失败"这个故障源。
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

        /// 动画与**自毁**的唯一推进点。三条独立销毁路径(正常播完 / 连续不可见过久 / 硬性寿命上限),任一失效都不会留下残留。
        private void Update()
        {
            // 连续不可见时长 = 现在 − **最近一次还在 Update 的时刻**(该时刻随时间前移)。
            // 不要用 _bornTime:那是"总年龄",用它会把"活满 1.5s 就杀"当成僵死判定。
            float hiddenFor = Time.realtimeSinceStartup - _lastActiveTime;

            if (_phase != Phase.Done)
            {
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

            if (_phase == Phase.Done) return;
            if (Time.realtimeSinceStartup - _bornTime > HardLifetime)
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

    /// 极简自毁组件:存活 <see cref="lifetime"/> 秒后销毁自己的 GameObject(用途 = 闪电分叉线)。
    /// **刻意不用协程**:协程在 <c>activeInHierarchy == false</c> 时会静默启动失败;Update 天然只在可见时跑,恢复可见后继续计时。
    /// **计时用 <c>Time.unscaledTime</c> 而非 <c>realtimeSinceStartup</c>**:后者在失焦/暂停时仍前进 —— 分叉是余晖,暂停时不该继续自毁。
    public class SelfDestruct : MonoBehaviour
    {
        public float lifetime = 0.5f;   // 秒

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
            // 分叉的材质是它独占的实例(见 LightningBolt.SpawnSplit),要跟着一起回收,否则每次落雷都留下几份材质。
            var sr = GetComponent<LineRenderer>();
            if (sr != null && sr.sharedMaterial != null) Destroy(sr.sharedMaterial);
        }
    }
}
