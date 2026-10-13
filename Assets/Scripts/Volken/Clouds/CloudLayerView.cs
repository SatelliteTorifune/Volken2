using System;
using UnityEngine;
/// <summary>
/// 单个 (CloudRenderer 实例 × CloudLayer) 的渲染状态:属于【某个相机】的那一份渲染目标 + 时序/TSS 状态 + 云空间重投影状态。
///  每份只属于一台相机、与其它相机(主视角 / PIP)完全隔离,多相机同帧互不踩坏 RT / 历史 / 时序状态。
/// 全局配置/噪声/材质与风、自转累积量留在 CloudLayer(所有相机共享)。
/// </summary>

namespace Volken.Clouds
{

    public class CloudLayerView
    {
        public readonly CloudLayer layer;

        public RenderTexture cloudTex;              // 低清 raymarch 颜色(MRT 0)
        public RenderTexture cloudDepthTex;         // 本帧云面距离(MRT 1, RFloat)
        public RenderTexture cloudMVTex;            // MRT 2:RG=UV 位移,B=上一相机云距离×1e-5
        public readonly RenderBuffer[] cloudTargets = new RenderBuffer[3];
        public readonly RenderBuffer[] lightningTargets = new RenderBuffer[4];
        public RenderTexture lightningTex;         // 当前帧云内雷光,不进入基础云颜色历史。
        internal readonly CloudLightningCache lightningCache = new CloudLightningCache();
        public RenderTexture upscaledCloudTex;      // 全清上采样结果
        public RenderTexture historyTex;            // 全清历史颜色
        public RenderTexture historyDepthTex;       // 全清场景深度历史
        public RenderTexture historyCloudDepthTex;  // 全清云面距离历史
        public RenderTexture orbitCloudTex;         // 轨道云(2D 壳着色)输出,Composite 按 _OrbitFade 混合

        public int frameNumber;                     // 距上次重建/配置变更的帧计数;0 = 冷启动
        public int[] temporalSequence;              // 当前 upscale 格网的采样序列(缓存,格网变化时重建)
        public int currentUpX = -1;
        public int currentUpY = -1;
        public int currentTemporal = -1;
        public int currentW = -1;                   // 上次创建 RT 时的渲染尺寸(像素)
        public int currentH = -1;
        public float currentResolutionScale = -1f;
        public float currentOrbitRes = -1f;         // 轨道云当前分辨率缩放(签名,变化触发 RT 重建)
        public int currentShaderRevision = -1;
        public RenderTextureFormat currentColorFormat = (RenderTextureFormat)(-1);
        public bool currentLightningEnabled;
        private int _lastRenderFrame = -1;

        public Matrix4x4 prevViewProjMat;
        public Matrix4x4 prevWorldToCameraMat;
        public float prevCloudAngle = float.NaN;    // 上一帧的云转角相位;NaN = 尚无历史,回退纯世界空间重投影

        public float orbitFade;                     // 本帧海拔淡入因子 0..1(CloudRenderer 每帧写入)
        public bool orbitOnlyLastFrame;             // 上一帧是否纯 2D(进入纯 2D 时清时序历史,防切回残影)

        public CloudLayerView(CloudLayer layer)
        {
            this.layer = layer;
        }

        public bool IsCreated => Ready(cloudTex) && Ready(cloudDepthTex) && Ready(cloudMVTex) &&
            Ready(upscaledCloudTex) && Ready(historyTex) &&
            Ready(historyDepthTex) && Ready(historyCloudDepthTex) && Ready(orbitCloudTex) &&
            (!currentLightningEnabled || Ready(lightningTex));

        private static bool Ready(RenderTexture rt) => rt != null && rt.IsCreated();

        internal static bool HasRecenterMotion(double positionDelta, double velocityDelta) =>
            !(positionDelta <= 0.001 && velocityDelta <= 0.001);

        public void SynchronizeShaderRevision()
        {
            if (currentShaderRevision == layer.ShaderRevision) return;
            ClearHistory();
            currentShaderRevision = layer.ShaderRevision;
        }

        public void PrepareFrame(int frame)
        {
            if (_lastRenderFrame < 0 || frame > _lastRenderFrame + 1) ClearHistory();
            _lastRenderFrame = frame;
            SynchronizeShaderRevision();
        }

        /// <summary>
        /// 按【本相机输出尺寸】(renderW×renderH)创建全部 RT。
        ///  不能再按 Screen.width/Height —— 额外相机的输出远小于屏幕,会造出大小错配的中间纹理(云不显示)。
        /// </summary>
        public void CreateRenderTextures(int renderW, int renderH)
        {
            if (layer?.config == null) return;

            frameNumber = 0;              // RT 重建 → 历史失效 → 冷启动全步进
            _lastRenderFrame = -1;
            temporalSequence = null;
            prevCloudAngle = float.NaN;   // 相位随重建作废,首帧回退纯世界空间重投影

            // TSS 开:低清 raymarch + 全清时序累积;TSS 关:全清 + 运动残影混合。历史一律全清。
            float scale = Mathf.Max(0.1f, currentResolutionScale);
            bool tss = layer.config.useTemporalUpscale;
            int upX = Mathf.Max(1, layer.config.upscaleX);
            int upY = Mathf.Max(1, layer.config.upscaleY);
            Vector2Int cloudRes = tss
                ? new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(renderW * scale / upX)), Mathf.Max(1, Mathf.RoundToInt(renderH * scale / upY)))
                : new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(renderW * scale)), Mathf.Max(1, Mathf.RoundToInt(renderH * scale)));

            currentColorFormat = CloudRenderer.CloudColorFormat;
            cloudTex = CreateRT(cloudRes.x, cloudRes.y, currentColorFormat, "CloudTex" + layer.layerIndex, 16);
            cloudDepthTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.RFloat, "CloudDepthTex" + layer.layerIndex);
            cloudMVTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGBHalf, "CloudMVTex" + layer.layerIndex);
            cloudDepthTex.filterMode = FilterMode.Point;
            cloudMVTex.filterMode = FilterMode.Point;
            cloudTargets[0] = cloudTex.colorBuffer;
            cloudTargets[1] = cloudDepthTex.colorBuffer;
            cloudTargets[2] = cloudMVTex.colorBuffer;
            currentLightningEnabled = CloudRenderer.CloudLightningEnabled;
            if (currentLightningEnabled)
            {
                var lightFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                    ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                lightningTex = CreateRT(cloudRes.x, cloudRes.y, lightFormat, "CloudLightningTex" + layer.layerIndex);
                Array.Copy(cloudTargets, lightningTargets, 3);
                lightningTargets[3] = lightningTex.colorBuffer;
            }

            upscaledCloudTex = CreateRT(renderW, renderH, currentColorFormat, "UpscaledCloudTex" + layer.layerIndex);
            historyTex = CreateRT(renderW, renderH, currentColorFormat, "HistoryTex" + layer.layerIndex);
            historyDepthTex = CreateRT(renderW, renderH, RenderTextureFormat.RFloat, "HistoryDepthTex" + layer.layerIndex);
            historyCloudDepthTex = CreateRT(renderW, renderH, RenderTextureFormat.RFloat, "HistoryCloudDepthTex" + layer.layerIndex);
            historyCloudDepthTex.filterMode = FilterMode.Point;

            // 轨道云(2D 壳着色):按 orbitResolutionScale 降分辨率渲染,Composite 时双线性软化
            float orbitRes = Mathf.Clamp(layer.config.orbitResolutionScale, 0.1f, 1f);
            orbitCloudTex = CreateRT(
                Mathf.Max(1, Mathf.RoundToInt(renderW * orbitRes)),
                Mathf.Max(1, Mathf.RoundToInt(renderH * orbitRes)),
                RenderTextureFormat.ARGB32, "OrbitCloudTex" + layer.layerIndex);
            currentOrbitRes = orbitRes;

            currentUpX = upX;
            currentUpY = upY;
            currentTemporal = tss ? 1 : 0;
            currentW = renderW;
            currentH = renderH;
            ClearHistory();
        }

        public void ReleaseRenderTextures()
        {
            ReleaseRT(ref cloudTex);
            ReleaseRT(ref upscaledCloudTex);
            ReleaseRT(ref historyTex);
            ReleaseRT(ref historyDepthTex);
            ReleaseRT(ref cloudDepthTex);
            ReleaseRT(ref historyCloudDepthTex);
            ReleaseRT(ref cloudMVTex);
            ReleaseRT(ref lightningTex);
            lightningCache.Dispose();
            ReleaseRT(ref orbitCloudTex);
        }

        // 清空时序历史(颜色/场景深度/云面距离)→ Upscale 的 validHist 全 0 → 全走本帧新鲜 raymarch
        public void ClearHistory()
        {
            lightningCache.Invalidate();
            frameNumber = 0;
            prevCloudAngle = float.NaN;
            var prevActive = RenderTexture.active;
            ClearRT(historyTex);
            ClearRT(historyDepthTex);
            ClearRT(historyCloudDepthTex);
            RenderTexture.active = prevActive;
        }

        private void ClearRT(RenderTexture rt)
        {
            if (rt != null && rt.IsCreated()) { RenderTexture.active = rt; GL.Clear(true, true, Color.clear); }
        }

        private static RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt, string name, int depthBits = 0)
        {
            var rt = new RenderTexture(Mathf.Max(1, w), Mathf.Max(1, h), depthBits, fmt);
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(rt);
                else UnityEngine.Object.DestroyImmediate(rt);
            }
            rt = null;
        }
    }

}
