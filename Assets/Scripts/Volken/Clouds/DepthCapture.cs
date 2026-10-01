using System;
using UnityEngine;
/// <summary>把 Far-Camera 渲染的 raw depth 转成线性深度(RFloat),并通过 <see cref="OnDepthTextureCreated"/> 广播生成的 RenderTexture。用法:AddComponent&lt;DepthCapture&gt;() 后 Init(farCam)。</summary>

namespace Volken.Clouds
{
    

    public class DepthCapture : MonoBehaviour
    {
        public static event Action<RenderTexture> OnDepthTextureCreated;   // 任何订阅者都会在第一次创建深度纹理后收到

        public static RenderTexture DepthTexture { get; private set; }   // null = 尚未创建

        private Camera _cam;                // 负责渲染深度的摄像机(Far-Camera)
        private Material _depthMat;         // raw depth → 线性 depth
        private bool _initialized = false;  // 防止多次 Init 重复创建

        // 必须在摄像机已在场景中且已 Enable 时调用一次;参数必须是同一台摄像机
        public void Init(Camera farCamera)
        {
            if (_initialized) return;

            _cam = farCamera ?? throw new ArgumentNullException(nameof(farCamera));

            Shader depthShader = Shader.Find("Hidden/DepthLinear");
            if (depthShader == null)
                throw new InvalidOperationException(
                    "Depth linearization shader not found. Make sure the 'DepthLinear' shader is included in the project.");

            _depthMat = new Material(depthShader);
            _initialized = true;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (!_initialized)
            {
                Graphics.Blit(src, dest);   // 未 Init 时直通,防止 NPE
                return;
            }

            if (DepthTexture == null ||   // 分辨率变化则重建,否则重用
                DepthTexture.width  != src.width ||
                DepthTexture.height != src.height)
            {
                if (DepthTexture != null) DepthTexture.Release();

                DepthTexture = new RenderTexture(src.width, src.height, 0,
                                                 RenderTextureFormat.RFloat);
                DepthTexture.enableRandomWrite = true;   // 方便后续 ComputeShader/Blit 读取
                DepthTexture.Create();

                OnDepthTextureCreated?.Invoke(DepthTexture);
            }

            // near/far clip 塞进材质,实际转换计算在 shader 里
            _depthMat.SetVector("_ClipPlanes",
                new Vector2(_cam.nearClipPlane, _cam.farClipPlane));

            // source = null → 直接从摄像机的 depth buffer 读(Unity 自动生成的 _CameraDepthTexture,非线性)
            Graphics.Blit(null, DepthTexture, _depthMat,
                _depthMat.FindPass("LinearDepth"));

            Graphics.Blit(src, dest);
        }

        private void OnDestroy()
        {
            if (DepthTexture != null)
            {
                DepthTexture.Release();
                DepthTexture = null;
            }

            OnDepthTextureCreated = null;   // 避免销毁后仍被事件链引用
        }
    }

}
