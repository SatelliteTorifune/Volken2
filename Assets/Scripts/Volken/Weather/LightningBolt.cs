using System;
using System.Collections;
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

        /// <summary>落雷后回调(用于延迟播放雷声)。参数 = 落点到相机的距离(米)。</summary>
        public Action<float> OnBoltLanded;

        /// <summary>整道闪电播完(自毁前)回调。</summary>
        public Action OnBoltFinished;

        public Vector3 Target { get; private set; }

        private LineRenderer _line;
        private Light _light;
        private Transform _flashPlane;
        private Material _boltMat;
        private Material _flashMat;
        private Mesh _flashMesh;

        private bool _fadeOut;
        private float _fadeTimer;
        private bool _playing;
        private Coroutine _playRoutine;

        // ================= 构建 =================

        /// <summary>
        /// 创建一道闪电。返回的组件已就位但尚未开播(调用 <see cref="CastBolt(Vector3)"/>)。
        /// </summary>
        /// <param name="boltShader">闪电 shader(按 Pass 名取 "Bolt" / "Flash")。</param>
        /// <param name="cam">用于闪光朝向的相机(可为 null,退化为 Camera.main)。</param>
        public static LightningBolt Create(Transform parent, Shader boltShader, Camera cam)
        {
            var go = new GameObject("VolkenLightningBolt");
            if (parent != null) go.transform.SetParent(parent, false);
            var bolt = go.AddComponent<LightningBolt>();
            bolt.targetCamera = cam;
            bolt.Build(boltShader);
            return bolt;
        }

        private void Build(Shader boltShader)
        {
            if (boltShader == null)
            {
                Mod.Log("Volken:LightningBolt: bolt shader is null — bolt will be invisible");
                return;
            }

            // 注意:Shader 上**没有** FindPass(那是 Material 的方法)。这里也不需要它 ——
            // LineRenderer / MeshRenderer 会自动选子着色器里第一个匹配的 pass,
            // 而本 shader 的两个 pass 分属不同材质(LightningBolt.shader 的 Bolt / Flash),
            // 各自只有一个 pass 可用,不需要手动指定索引。
            _boltMat = new Material(boltShader);
            _boltMat.SetFloat("_Intensity", baseIntensity);
            _boltMat.SetColor("_Color", new Color(0.85f, 0.9f, 1f, 1f));

            _flashMat = new Material(boltShader);
            _flashMat.SetFloat("_Intensity", 0f);
            _flashMat.SetColor("_Color", new Color(0.8f, 0.85f, 1f, 1f));
            _flashMat.SetFloat("_CoreWidth", 0.35f);

            // 主干
            _line = gameObject.AddComponent<LineRenderer>();
            _line.material = _boltMat;
            _line.positionCount = 1;
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

        public void CastBolt()
        {
            if (_line == null || _playing) return;
            _playing = true;
            _line.positionCount = 1;
            _playRoutine = StartCoroutine(CreateLightningBolt());
        }

        /// <summary>
        /// 主干生长 → 4 次闪光 → 进入淡出(与 SP2 <c>Enviro.Lightning.CreateLightningBolt</c> 1:1)。
        /// </summary>
        private IEnumerator CreateLightningBolt()
        {
            _light.enabled = false;
            _line.widthMultiplier = width;
            _boltMat.SetFloat("_Intensity", baseIntensity);
            _line.SetPosition(0, transform.position);
            _line.positionCount = 2;
            _line.SetPosition(1, transform.position);

            Vector3 lastPoint = transform.position;
            float totalDist = Vector3.Distance(transform.position, Target);
            float arcDist = totalDist / Mathf.Max(1, arcs);

            for (int i = 1; i < arcs; i++)
            {
                _boltMat.SetFloat("_Intensity", UnityEngine.Random.Range(0f, 2f));
                _line.positionCount = i + 1;

                Vector3 dir = Target - lastPoint;
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
                dir.Normalize();
                Vector3 next = Randomize(dir, inaccuracy);
                next *= UnityEngine.Random.Range(arcLength * arcVariation, arcLength) * arcDist;
                next += lastPoint;
                _line.SetPosition(i, next);

                // 原版:i < arcs - 2 的分叉点各生 splits+1 条
                if (i < arcs - 2 && splits >= 0)
                {
                    for (int j = 0; j <= splits; j++)
                    {
                        StartCoroutine(CreateSplit(next));
                    }
                }

                lastPoint = next;
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.001f, 0.005f));
            }

            _line.SetPosition(arcs - 1, Target);

            // 落点点光源朝向相机(原版:myLight.transform.LookAt(EnviroManager.instance.Camera...))
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam != null)
            {
                _light.transform.position = Target;
                _light.transform.LookAt(cam.transform.position, Vector3.up);
            }

            // 落点闪光球定位 + 缩放(直径 = 1/10 落距,给一个可见但不荒谬的闪光范围)
            float flashDiameter = Mathf.Clamp(totalDist * 0.1f, 20f, 4000f);
            _flashPlane.position = Target;
            _flashPlane.localScale = Vector3.one * flashDiameter;
            _flashPlane.gameObject.SetActive(true);

            // ==== 4 次闪烁(每次"亮 25~35ms → 灭 25~35ms") ====
            for (int flash = 0; flash < 4; flash++)
            {
                _boltMat.SetFloat("_Intensity", flashIntensity);
                _flashMat.SetFloat("_Intensity", flashPlaneIntensity);
                _light.enabled = true;
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.025f, 0.035f));

                _boltMat.SetFloat("_Intensity", baseIntensity);
                _flashMat.SetFloat("_Intensity", 0f);
                _light.enabled = false;
                // 原版最后一次闪完没有熄灭间隔,直接进淡出
                if (flash < 3)
                {
                    yield return new WaitForSeconds(UnityEngine.Random.Range(0.025f, 0.035f));
                }
            }

            // 通知外部(雷声):距离越远,雷声延迟越久
            try { OnBoltLanded?.Invoke(Vector3.Distance(transform.position, Target)); }
            catch (Exception ex) { Mod.Log("Volken:LightningBolt OnBoltLanded threw: " + ex.Message); }

            _fadeTimer = 50f;   // 原版:用 50 当初始强度淡出
            _fadeOut = true;
        }

        /// <summary>
        /// 一条分叉(对应 SP2 <c>Enviro.Lightning.CreateSplit</c>):8 点、朝"目标方向 + 球内随机 ×500"飞,
        /// 存活 0.2~0.5s。
        /// </summary>
        private IEnumerator CreateSplit(Vector3 from)
        {
            var splitGo = new GameObject("Split");
            splitGo.transform.SetParent(transform, false);
            splitGo.transform.position = from;
            var sr = splitGo.AddComponent<LineRenderer>();
            sr.material = _boltMat;
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

            Vector3 lastPoint = from;
            float dist = Vector3.Distance(from, targetPos);
            float arcDist = dist / 7f;

            for (int i = 1; i < 8; i++)
            {
                sr.positionCount = i + 1;
                Vector3 dir = targetPos - lastPoint;
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
                dir.Normalize();
                Vector3 next = Randomize(dir, inaccuracy);
                next *= UnityEngine.Random.Range(1f, 1.5f) * arcDist;
                next += lastPoint;
                sr.SetPosition(i, next);
                lastPoint = next;
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.004f, 0.006f));
            }
            sr.SetPosition(7, targetPos);

            // 分叉比主干活得久一点(原版 0.2~0.5s),之后销毁。
            // 用 Destroy 而不是原版的 DestroyImmediate:运行时 DestroyImmediate 会打断批次。
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.2f, 0.5f));
            if (splitGo != null) Destroy(splitGo);
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

        private void Update()
        {
            if (!_fadeOut) return;

            _fadeTimer = Mathf.Lerp(_fadeTimer, 0f, 10f * Time.deltaTime);   // 原版:10*dt
            if (_boltMat != null) _boltMat.SetFloat("_Intensity", _fadeTimer);

            if (_fadeTimer <= 1f)
            {
                _fadeOut = false;
                if (_line != null) _line.positionCount = 1;
                if (_flashPlane != null) _flashPlane.gameObject.SetActive(false);
                if (_light != null) _light.enabled = false;
                try { OnBoltFinished?.Invoke(); }
                catch (Exception ex) { Mod.Log("Volken:LightningBolt OnBoltFinished threw: " + ex.Message); }
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (_playRoutine != null) StopAllCoroutines();
            if (_boltMat != null) Destroy(_boltMat);
            if (_flashMat != null) Destroy(_flashMat);
            if (_flashMesh != null) Destroy(_flashMesh);
        }
    }
}
