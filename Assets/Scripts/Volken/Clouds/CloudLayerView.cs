using System;
using UnityEngine;
/// <summary>
/// 单个 (CloudRenderer 实例 × CloudLayer) 的渲染状态:属于【某个相机】的那一份渲染目标 +
/// 时序/TSS 状态 + 云空间重投影状态。
///
/// 关键设计:每一份 CloudLayerView 只属于一个 CloudRenderer 实例(一台相机),与其它相机
/// (主视角、PIP 等额外摄像机)完全隔离。这是让"额外摄像机也能正确渲染体积云"、且多台
/// 相机同帧渲染时互不踩坏渲染目标/历史/时序状态的根基。
///
/// CloudLayer 只保留全局配置/噪声/材质与风、自转累积量(全球共享);凡是"跟某台相机走"
/// 的状态都放在这里。
/// </summary>

namespace Volken.Clouds
{

    public class CloudLayerView
    {
        public readonly CloudLayer layer;

        // === 渲染目标(本相机独享) ===
        public RenderTexture cloudTex;              // 低清 raymarch 颜色(MRT 0)
        public RenderTexture cloudDepthTex;         // 本帧云面距离(MRT 1, RFloat)
        public RenderTexture cloudMVTex;            // 本帧新鲜格运动矢量(MRT 2, RG)
        public RenderTexture cloudMVDilatedTex;     // 3×3 膨胀后的运动矢量(供同帧 Upscale 使用)
        public RenderTexture upscaledCloudTex;      // 全清上采样结果
        public RenderTexture historyTex;            // 全清历史颜色
        public RenderTexture historyDepthTex;       // 全清场景深度历史
        public RenderTexture historyCloudDepthTex;  // 全清云面距离历史
        public RenderTexture orbitCloudTex;         // 轨道云(2D 壳着色)输出,Composite 按 _OrbitFade 混合

        // === 时序/TSS 状态(本相机独享) ===
        public int frameNumber;                     // 距上次重建/配置变更的帧计数;0 = 冷启动
        public int[] temporalSequence;              // 当前 upscale 格网的采样序列(缓存,格网变化时重建)
        public int currentUpX = -1;
        public int currentUpY = -1;
        public int currentTemporal = -1;
        public int currentW = -1;                   // 上次创建 RT 时的渲染尺寸(像素)
        public int currentH = -1;
        public float currentResolutionScale = -1f;
        public float currentOrbitRes = -1f;         // 轨道云当前分辨率缩放(签名,变化触发 RT 重建)

        // === 云空间重投影状态(本相机独享) ===
        public Matrix4x4 prevViewProjMat;
        public float prevCloudAngle = float.NaN;    // 方案 C §5:云空间重投影用——上一帧的云转角相位

        // === 轨道云淡入(本相机独享) ===
        public float orbitFade;                     // 本帧海拔淡入因子 0..1(CloudRenderer 每帧写入)
        public bool orbitOnlyLastFrame;             // 上一帧是否纯 2D(进入纯 2D 时清时序历史,防切回残影)

        public CloudLayerView(CloudLayer layer)
        {
            this.layer = layer;
        }

        public bool IsCreated => cloudTex != null && cloudTex.IsCreated();

        /// <summary>
        /// 按【本相机输出尺寸】(renderW×renderH,即 OnRenderImage 的 source 尺寸)创建全部 RT。
        /// 注意:不能再按 Screen.width/Height —— 额外摄像机的输出(如 PIP 窗口 RT)远小于屏幕,
        /// 用屏幕尺寸会造出大小错配的中间纹理(upscaled/history 与 composite 目标不一致 → 云不显示)。
        /// </summary>
        public void CreateRenderTextures(int renderW, int renderH)
        {
            if (layer?.config == null) return;

            // RT 重建 → 历史失效 → 冷启动全步进
            frameNumber = 0;
            temporalSequence = null;
            prevCloudAngle = float.NaN;   // 云转角相位随重建作废,首帧回退纯世界空间重投影

            // KSA 完整结构:
            //   TSS 开 → cloudRes = 低清(全清/格网),每帧全量 raymarch 低清;上采样在全清做时序累积。
            //   TSS 关 → cloudRes = 全清(现状基线),上采样=运动残影混合。
            //   历史一律全清(时序混合在上采样/全清层面采样历史)。
            float scale = Mathf.Max(0.1f, currentResolutionScale);
            bool tss = layer.config.useTemporalUpscale;
            int upX = Mathf.Max(1, layer.config.upscaleX);
            int upY = Mathf.Max(1, layer.config.upscaleY);
            Vector2Int cloudRes = tss
                ? new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(renderW * scale / upX)), Mathf.Max(1, Mathf.RoundToInt(renderH * scale / upY)))
                : new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(renderW * scale)), Mathf.Max(1, Mathf.RoundToInt(renderH * scale)));

            cloudTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGB32, "CloudTex" + layer.layerIndex, 16);
            cloudDepthTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.RFloat, "CloudDepthTex" + layer.layerIndex);
            cloudMVTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGBHalf, "CloudMVTex" + layer.layerIndex);
            cloudMVDilatedTex = CreateRT(cloudRes.x, cloudRes.y, RenderTextureFormat.ARGBHalf, "CloudMVDilatedTex" + layer.layerIndex);

            upscaledCloudTex = CreateRT(renderW, renderH, RenderTextureFormat.ARGB32, "UpscaledCloudTex" + layer.layerIndex);
            historyTex = CreateRT(renderW, renderH, RenderTextureFormat.ARGB32, "HistoryTex" + layer.layerIndex);
            historyDepthTex = CreateRT(renderW, renderH, RenderTextureFormat.RFloat, "HistoryDepthTex" + layer.layerIndex);
            historyCloudDepthTex = CreateRT(renderW, renderH, RenderTextureFormat.RFloat, "HistoryCloudDepthTex" + layer.layerIndex);

            // 轨道云(2D 壳着色):按 orbitResolutionScale 降分辨率渲染 + Composite 双线性软化
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
            ReleaseRT(ref cloudMVDilatedTex);
            ReleaseRT(ref orbitCloudTex);
        }

        /// <summary>
        /// 清空时序历史(颜色/场景深度/云面距离),使 Upscale 的 validHist 全 0 → 全走本帧新鲜 raymarch。
        /// </summary>
        public void ClearHistory()
        {
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
            if (rt != null && rt.IsCreated()) rt.Release();
            rt = null;
        }
    }

}
