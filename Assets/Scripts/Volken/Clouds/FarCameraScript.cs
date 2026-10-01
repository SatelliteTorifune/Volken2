using UnityEngine;
using UnityEngine.Rendering;
/// <summary>
/// 用 CommandBuffer 抓取远相机的线性化深度到 RT(供云渲染管线使用)。
/// ⚠️ 不要改用 OnRenderImage:远相机上挂该钩子会被迫走中间 RT + resolve,并在近相机远裁剪面(~10 km)处画出可见的像素缝线。
/// ⚠️ farDepthTex / maxFarDepth 是【实例成员】:每对(主视角 / PIP)相机各自持有,否则多相机会互相 Release/重建同一纹理。
/// </summary>

namespace Volken.Clouds
{
    

    public class FarCameraScript : MonoBehaviour
    {
        public float maxFarDepth;
        public RenderTexture farDepthTex;

        private Camera _cam;
        // 独立材质实例:它的 "clipPlanes" 必须始终是远相机的裁剪面,而共享的云材质每帧会被近相机的裁剪面覆盖
        private Material _depthMat;
        private CommandBuffer _commandBuffer;
        private const CameraEvent CaptureEvent = CameraEvent.AfterForwardOpaque;

        public Camera Camera => _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.depthTextureMode |= DepthTextureMode.Depth;
            _depthMat = new Material(Volken.Clouds.VolkenClouds.Instance.MainLayer?.material?.shader);
        }

        private void OnEnable()
        {
            RebuildResources();
        }

        private void OnDisable()
        {
            RemoveCommandBuffer();
        }

        private void OnPreRender()
        {
            maxFarDepth = _cam.farClipPlane;

            if (farDepthTex == null || !farDepthTex.IsCreated() ||   // 分辨率变化时重建
                farDepthTex.width != _cam.pixelWidth || farDepthTex.height != _cam.pixelHeight)
            {
                RebuildResources();
            }

            _depthMat.SetVector("clipPlanes", new Vector2(_cam.nearClipPlane, _cam.farClipPlane));
        }

        private void RebuildResources()
        {
            if (_cam == null || _depthMat == null)
            {
                return;
            }

            RemoveCommandBuffer();

            if (farDepthTex != null)
            {
                farDepthTex.Release();
            }

            farDepthTex = new RenderTexture(_cam.pixelWidth, _cam.pixelHeight, 0, RenderTextureFormat.RFloat);
            farDepthTex.Create();

            _commandBuffer = new CommandBuffer { name = "Volken Far Depth Capture" };
            _commandBuffer.Blit(BuiltinRenderTextureType.None, farDepthTex, _depthMat, _depthMat.FindPass("FarDepth"));
            _commandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);   // 恢复相机自身的渲染目标
            _cam.AddCommandBuffer(CaptureEvent, _commandBuffer);
        }

        private void RemoveCommandBuffer()
        {
            if (_commandBuffer != null)
            {
                if (_cam != null)
                {
                    _cam.RemoveCommandBuffer(CaptureEvent, _commandBuffer);
                }
                _commandBuffer.Release();
                _commandBuffer = null;
            }
        }

        private void OnDestroy()
        {
            RemoveCommandBuffer();

            if (farDepthTex != null)
            {
                farDepthTex.Release();
                farDepthTex = null;
            }
        }
    }

}
