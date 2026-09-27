using UnityEngine;
using UnityEngine.Rendering;
/// <summary>
/// Captures the far camera's linearized depth into a render texture for the cloud pipeline.
///
/// Uses a CommandBuffer instead of OnRenderImage: merely having an OnRenderImage hook on the
/// far camera forces it through an intermediate render target + resolve, which subtly changes
/// its output and draws a visible single-pixel seam line where the near camera's coverage ends
/// (at the near camera's far clip plane, ~10 km). A command buffer leaves the camera's normal
/// render path untouched.
///
/// 多相机支持:farDepthTex / maxFarDepth 都是【实例成员】(不再是 static)。每对(主视角 / PIP)
/// 相机各自持有自己的远相机深度纹理 —— 否则多台额外相机会在同一个 static 纹理上互相 Release/
/// 重建,导致命令缓冲写入已释放纹理(报错/云消失)。
/// </summary>

namespace Volken.Clouds
{
    

    public class FarCameraScript : MonoBehaviour
    {
        public float maxFarDepth;
        public RenderTexture farDepthTex;

        private Camera _cam;
        // dedicated material instance: its "clipPlanes" uniform must hold the FAR camera's planes,
        // while the shared cloud material gets overwritten with the near camera's planes every frame
        private Material _depthMat;
        private CommandBuffer _commandBuffer;
        private const CameraEvent CaptureEvent = CameraEvent.AfterForwardOpaque;

        /// <summary>本脚本挂载的相机。</summary>
        public Camera Camera => _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.depthTextureMode |= DepthTextureMode.Depth;
            _depthMat = new Material(Volken.Core.VolkenMod.Instance.MainLayer?.material?.shader);
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

            // recreate resources on resolution changes
            if (farDepthTex == null || !farDepthTex.IsCreated() ||
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
            // restore the camera's own target so the rest of the frame renders normally
            _commandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
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
