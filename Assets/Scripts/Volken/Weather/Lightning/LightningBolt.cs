using System;
using Assets.Scripts;
using ModApi;
using UnityEngine;
using UnityEngine.Rendering;

namespace Volken.Weather
{
    /// <summary>主干、分叉与落点闪光的生命周期由激活组件的 Update 推进,结束后销毁。</summary>
    /// 分叉共用独立材质,由本对象统一回收;计龄使用排除游戏暂停的 GamePause.Now。
    /// 实现记录见 docs/archive/thunder-realism-2026-09-28.md §7、§8。
    public class LightningBolt : MonoBehaviour
    {
        public Camera targetCamera;   // 每道闪电各持一个:改成 static 会在多相机(PIP)下互相覆盖,闪光朝向会串

        public float flashIntensity = 50f;
        public float flashPlaneIntensity = 10f;
        public int arcs = 20;
        public float arcLength = 1f;
        public float arcVariation = 1f;
        public float inaccuracy = 0.5f;
        public int splits = 4;
        public float width = 10f;
        public float baseIntensity = 1f;
        public float lightIntensity = 1f;
        public float lightRange = 8000f;

        /// <summary>落点回调;雷声距离由调用方按 Target 与观测者位置计算。</summary>
        public Action OnBoltLanded;

        /// <summary>结束回调,自毁前最多调用一次。</summary>
        public Action OnBoltFinished;

        public Vector3 Target { get; private set; }

        private const float HardLifetime = 3.5f;       // 秒,自出生计龄,游戏暂停不计时
        private const int FlashCount = 4;              // 亮 25~35ms → 灭 25~35ms 交替;最后一次闪完直接淡出
        private const float MaxHiddenSeconds = 1.5f;   // 秒,距最近活跃时间超过此值时清理

        private static readonly System.Collections.Generic.List<LightningBolt> ActiveBolts =
            new System.Collections.Generic.List<LightningBolt>();

        /// <summary>参考帧纪元;出生纪元不同的闪电会在 Update 中清理。</summary>
        public static int FrameIndex { get; private set; }

        /// <summary>清理已过期闪电并推进纪元;其余闪电在下一次 Update 检查失效。返回本次清理数量。</summary>
        public static int DestroyStale()
        {
            int killed = 0;
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

        /// <summary>模块停用或离开场景时销毁全部闪电,包括未激活的对象。</summary>
        public static void DestroyAll()
        {
            for (int i = ActiveBolts.Count - 1; i >= 0; i--)
            {
                var b = ActiveBolts[i];
                if (b == null) { ActiveBolts.RemoveAt(i); continue; }
                b.Release();
                UnityEngine.Object.Destroy(b.gameObject);
            }
            ActiveBolts.Clear();
        }

        public static int ActiveCount => ActiveBolts.Count;

        // 只选当前参考系内最强的亮脉冲;所有相机消费同一光源,不增加 Unity Light。
        public static bool TryGetCloudLight(float intensity, float range, out Vector4 positionRange, out Vector4 colorIntensity)
        {
            positionRange = colorIntensity = Vector4.zero;
            if (range <= 0f || float.IsNaN(range) || float.IsInfinity(range)) return false;
            float strongest = 0f;
            for (int i = ActiveBolts.Count - 1; i >= 0; i--)
            {
                var bolt = ActiveBolts[i];
                if (bolt == null || !bolt.isActiveAndEnabled || bolt._released ||
                    bolt._bornFrameIndex != FrameIndex || bolt._phase != Phase.Flash ||
                    bolt._flashState != 1 || bolt._flashSuppressed) continue;
                float energy = intensity * bolt.flashIntensity / 50f;
                if (float.IsNaN(energy) || float.IsInfinity(energy)) continue;
                energy = Mathf.Min(energy, 1000f);
                if (energy <= strongest) continue;
                Vector3 pos = bolt.transform.position;
                if (float.IsNaN(pos.sqrMagnitude) || float.IsInfinity(pos.sqrMagnitude)) continue;
                strongest = energy;
                positionRange = new Vector4(pos.x, pos.y, pos.z, range);
                colorIntensity = new Vector4(0.85f, 0.9f, 1f, strongest);
            }
            return strongest > 0f;
        }

        private enum Phase
        {
            Grow,
            Flash,
            Fade,
            Done,
        }

        // 网格与渐变跨闪电共享,不由单个闪电销毁。
        private static Mesh _sharedFlashMesh;
        private static Gradient _sharedGradient;

        private LineRenderer _line;
        private Light _light;
        private Transform _flashPlane;
        private Material _boltMat;
        private Material _flashMat;
        private Material _splitMat;   // 本道闪电的全部分叉共用

        private readonly System.Collections.Generic.List<GameObject> _splits =
            new System.Collections.Generic.List<GameObject>();

        private Phase _phase = Phase.Done;
        private bool _begun;
        private bool _finishedInvoked;
        private bool _released;
        // 出生时间固定,用于寿命上限;_lastActiveTime 随活跃帧更新。
        private float _bornTime;
        private float _phaseElapsed;

        // 浮动原点重定位后,旧纪元的帧空间坐标失效。
        private int _bornFrameIndex;

        private int _arcIndex;
        private float _arcTimer;
        private float _arcInterval;
        private Vector3 _lastPoint;
        private float _arcDist;

        private float[] _flashTimestamps;
        private int _flashState = -1;   // -1 = 未设置;0 = 灭;1 = 亮

        private float _fadeTimer;

        /// <summary>创建独立根对象;调用 CastBolt 后开始动画,不随相机层级停用。</summary>
        public static LightningBolt Create(Shader boltShader, Shader flashShader, Camera cam)
        {
            var go = new GameObject("VolkenLightningBolt");
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

            // 落点使用独立单 Pass shader;缺失时回退主干 shader。
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

        private static Gradient SharedGradient
        {
            get
            {
                if (_sharedGradient == null) _sharedGradient = BuildBoltGradient();
                return _sharedGradient;
            }
        }

        /// <summary>分叉共用的主干材质拷贝,由 DestroySplits 统一回收。</summary>
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

        /// <summary>设置动画初始状态;对象激活时由 Update 推进。</summary>
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
            _light.intensity = lightIntensity;
            _light.range = lightRange;
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

            _flashTimestamps = new float[FlashCount * 2];
            float t = 0f;
            for (int i = 0; i < FlashCount; i++)
            {
                _flashTimestamps[i * 2] = t;                                        // 亮
                t += UnityEngine.Random.Range(0.025f, 0.035f);
                _flashTimestamps[i * 2 + 1] = t;                                    // 灭
                if (i < FlashCount - 1)
                {
                    t += UnityEngine.Random.Range(0.025f, 0.035f);
                }
            }

            _phase = Phase.Flash;
            _phaseElapsed = 0f;
            _flashState = -1;
            StepFlash(0f);
        }

        /// <summary>按累计时间重算亮灭状态,允许一帧跨过多个时间点。</summary>
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

            float flashTotal = _flashTimestamps[_flashTimestamps.Length - 1];
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

        private bool _flashSuppressed;

        /// <summary>游戏暂停时关闭点光源和加色闪光,保留静止几何与基础亮度。</summary>
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
            if (_boltMat != null) _boltMat.SetFloat("_Intensity", baseIntensity);

            var cam = targetCamera != null ? targetCamera : Camera.main;
            float camDist = cam != null ? Vector3.Distance(cam.transform.position, Target) : -1f;
            Mod.Log($"Volken:LightningBolt pause froze flash — light+additive flash turned off " +
                    $"(landing=({Target.x:F0},{Target.y:F0},{Target.z:F0}) camDist={camDist:F0}m phaseElapsed={_phaseElapsed:F3}s)");
        }

        /// <summary>恢复后按冻结的时间表重算亮灭。</summary>
        private void EndFlashSuppression()
        {
            if (!_flashSuppressed) return;
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

        /// <summary>清理视觉与分叉、触发一次结束回调并销毁自身;重复调用安全。</summary>
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

        /// <summary>创建 8 点分叉,使用共享分叉材质并独立计龄。</summary>
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

            const int splitPoints = 7;   // 段数;i = 0..7,共 8 个点
            sr.positionCount = splitPoints + 1;

            // world-space 线段必须显式写起点,避免连到参考帧原点。
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

        /// <summary>推进动画,并检查纪元、最近活跃时间与总寿命;游戏暂停时收光。</summary>
        private void Update()
        {
            float now = GamePause.Now;
            float hiddenFor = now - _lastActiveTime;

            if (_phase != Phase.Done)
            {
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

        // 最近活跃时间使用 GamePause.Now,与出生时间分别维护。
        private float _lastActiveTime;

        private void LateUpdate()
        {
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
        }
    }

    /// <summary>分叉按 GamePause.Now 计龄,游戏暂停期间不推进;材质由 LightningBolt 回收。</summary>
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

    }
}
