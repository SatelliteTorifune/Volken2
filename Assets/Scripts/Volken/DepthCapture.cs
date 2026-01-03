using System;
using UnityEngine;

/// <summary>
/// 把 Far‑Camera（即游戏视图的远端相机）渲染的 **raw depth** 转换为 **线性深度(RFloat)**，
/// 并把生成的 RenderTexture 通过事件广播，让云渲染系统可以直接读取。
///
/// 用法示例（在 Volken.OnSceneLoaded）：
/// ---------------------------------------------------------------
/// var farCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera.FarCamera;
/// var depthComp = farCam.gameObject.AddComponent<DepthCapture>();
/// depthComp.Init(farCam);               // 只需要在场景创建后调用一次
/// ---------------------------------------------------------------
/// </summary>
public class DepthCapture : MonoBehaviour
{
    // -----------------------------------------------------------------
    // 事件：任何订阅者都会在第一次创建深度纹理后立即收到
    // -----------------------------------------------------------------
    public static event Action<RenderTexture> OnDepthTextureCreated;

    // -----------------------------------------------------------------
    // 公开的只读属性，方便外部直接读取（如果不想走事件也可以直接取）
    // -----------------------------------------------------------------
    public static RenderTexture DepthTexture { get; private set; }

    // -----------------------------------------------------------------
    // 私有成员
    // -----------------------------------------------------------------
    private Camera _cam;                // 负责渲染深度的摄像机（Far‑Camera）
    private Material _depthMat;         // 用来把 raw depth → 线性 depth
    private bool _initialized = false; // 防止多次 Init 产生重复创建

    // -----------------------------------------------------------------
    // 公共初始化接口（在 Volken.OnSceneLoaded 中调用）
    // -----------------------------------------------------------------
    /// <summary>
    /// 必须在摄像机已经在场景中且已 Enable 的情况下调用一次。
    /// 参数必须是同一台摄像机（通常是 FlightScene 中的 FarCamera）。
    /// </summary>
    public void Init(Camera farCamera)
    {
        if (_initialized) return;          // 防止重复初始化

        _cam = farCamera ?? throw new ArgumentNullException(nameof(farCamera));

        // “Hidden/DepthLinear” 是下面附带的最小化 shader（只做 linear‑depth 转换）。
        Shader depthShader = Shader.Find("Hidden/DepthLinear");
        if (depthShader == null)
            throw new InvalidOperationException(
                "Depth linearization shader not found. Make sure the 'DepthLinear' shader is included in the project.");

        _depthMat = new Material(depthShader);
        _initialized = true;
    }

    // -----------------------------------------------------------------
    // Unity 渲染回调：
    //   src  – 只读的摄影机颜色缓冲（我们不管它）
    //   dest – 渲染到屏幕的最终图像（保持不变）
    // -----------------------------------------------------------------
    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (!_initialized)
        {
            // 未 Init 时直接把画面传过去，防止 NPE。
            Graphics.Blit(src, dest);
            return;
        }

        // ----------- 1) 生成（或重用）线性深度纹理 ------------
        // 若分辨率变化则重新创建，若已经创建且大小匹配则直接重用。
        if (DepthTexture == null ||
            DepthTexture.width  != src.width ||
            DepthTexture.height != src.height)
        {
            // 释放旧纹理（如果有的话）
            if (DepthTexture != null) DepthTexture.Release();

            DepthTexture = new RenderTexture(src.width, src.height, 0,
                                             RenderTextureFormat.RFloat);
            DepthTexture.enableRandomWrite = true; // 方便后面 ComputeShader/Blit 读取
            DepthTexture.Create();

            // 把新纹理广播给所有订阅者（云渲染器等）
            OnDepthTextureCreated?.Invoke(DepthTexture);
        }

        // ----------- 2) 把摄像机的 raw depth 转成线性深度 ----------
        // 在材质里把 near/far clip 直接塞进去，shader 负责实际计算。
        _depthMat.SetVector("_ClipPlanes",
            new Vector2(_cam.nearClipPlane, _cam.farClipPlane));

        // 将 Unity 自动生成的 _CameraDepthTexture（非线性）写入我们的 RFloat 纹理。
        // 这里不使用 src，直接把 null 当成 “从摄像机的 depth buffer 读取”
        Graphics.Blit(null, DepthTexture, _depthMat,
            _depthMat.FindPass("LinearDepth")); // Pass 名在 shader 中叫 LinearDepth

        // ----------- 3) 把原始颜色缓冲直接写回屏幕 ----------
        Graphics.Blit(src, dest);
    }

    // -----------------------------------------------------------------
    // 保证在对象销毁时释放 RenderTexture，防止内存泄漏
    // -----------------------------------------------------------------
    private void OnDestroy()
    {
        if (DepthTexture != null)
        {
            DepthTexture.Release();
            DepthTexture = null;
        }

        // 让外部可以自行清理（如果还有人持有引用的话）
        OnDepthTextureCreated = null;
    }
}
