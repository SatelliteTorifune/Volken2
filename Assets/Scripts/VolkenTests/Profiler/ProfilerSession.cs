using UnityEngine;

namespace VolkenProfiler
{
    
    /// <summary>GPU 焦点性能快照(只读数据)。</summary>
    public struct ProfilerSnapshot
    {
        public float Fps;                    // 当前帧率(指数滑动平均,与游戏内置一致)
        public float FrameMs;                // 当前帧耗时(平滑)
        public bool HasFrameTiming;          // FrameTimingManager 是否可用
        public float GpuFrameMs;             // 最近一帧 GPU 帧时间
        public float CpuRenderThreadFrameMs; // 渲染线程帧时间
        public float PresentWaitMs;          // 主线程等 Present(垂直同步)的时间
        public float GpuSharePercent;        // GPU 帧时间占整帧预算的百分比(gpu/frame*100)
        public string Bottleneck;            // 瓶颈提示:GPU / RENDER / CPU / mixed
        public string GpuName;               // GPU 型号(SystemInfo.graphicsDeviceName)
        public string GpuVendor;             // GPU 厂商(SystemInfo.graphicsDeviceVendor)
        public string GpuApiVersion;         // 图形 API 版本(SystemInfo.graphicsDeviceVersion)
        public int GpuMemoryMb;              // 显存总量 MB(SystemInfo.graphicsMemorySize)
        public int MaxTextureSize;           // 最大纹理尺寸(SystemInfo.maxTextureSize)
        public bool SupportsCompute;         // 是否支持 Compute Shader
        public string GraphicsApi;           // 图形 API 类型(SystemInfo.graphicsDeviceType)
        public int VsyncCount;               // QualitySettings.vSyncCount
        public float RefreshRateHz;          // 当前刷新率(FrameTimingManager.GetVSyncsPerSecond)
        public int TargetFrameRate;          // Application.targetFrameRate(0 = 不限)
        public string CloudRenderInfo;       // 当前云渲染配置(分辨率/TSS/采样格网),非飞行或读取失败时为 null
    }

    /// <summary>
    /// 帧数据采集器(GPU 焦点):帧率/帧耗时、GPU 帧时间、渲染线程耗时、Present 等待与瓶颈判断。
    /// </summary>
    public sealed class ProfilerSession
    {
        private const float SmoothingFactor = 0.1f;  // 与游戏内置 FpsMonitor 一致

        private readonly FrameTiming[] _frameTimings = new FrameTiming[8];
        private float _cpuMainThreadFrameMs;   // 仅用于瓶颈判断,不对外展示
        private float _cpuRenderThreadFrameMs;
        private float _presentWaitMs;
        private float _gpuFrameMs;
        private bool _hasFrameTiming;

        private float _smoothedMs;
        private float _lastFrameMs;

        /// <summary>每帧调用一次(由 Overlay 的 Update 驱动)。</summary>
        public void Tick(float unscaledDeltaTime)
        {
            // 1) 当前帧耗时(指数滑动平均,与游戏内置一致)
            if (unscaledDeltaTime > 0f)
            {
                float frameMs = unscaledDeltaTime * 1000f;
                _lastFrameMs = frameMs;
                _smoothedMs = _smoothedMs > 0f
                    ? _smoothedMs + (frameMs - _smoothedMs) * SmoothingFactor
                    : frameMs;
            }

            // 2) GPU / 渲染线程 / Present 帧时间
            CaptureFrameTimings();
        }

        public ProfilerSnapshot BuildSnapshot()
        {
            float refreshHz = 0f;
            if (_hasFrameTiming)
            {
                try { refreshHz = FrameTimingManager.GetVSyncsPerSecond(); }
                catch { refreshHz = 0f; }
            }

            return new ProfilerSnapshot
            {
                Fps = _smoothedMs > 0f ? 1000f / _smoothedMs : 0f,
                FrameMs = _smoothedMs,
                HasFrameTiming = _hasFrameTiming,
                GpuFrameMs = _gpuFrameMs,
                CpuRenderThreadFrameMs = _cpuRenderThreadFrameMs,
                PresentWaitMs = _presentWaitMs,
                GpuSharePercent = (_smoothedMs > 0f && _hasFrameTiming) ? _gpuFrameMs / _smoothedMs * 100f : 0f,
                Bottleneck = ComputeBottleneck(_smoothedMs),
                GpuName = SystemInfo.graphicsDeviceName,
                GpuVendor = SystemInfo.graphicsDeviceVendor,
                GpuApiVersion = SystemInfo.graphicsDeviceVersion,
                GpuMemoryMb = SystemInfo.graphicsMemorySize,
                MaxTextureSize = SystemInfo.maxTextureSize,
                SupportsCompute = SystemInfo.supportsComputeShaders,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                VsyncCount = QualitySettings.vSyncCount,
                RefreshRateHz = refreshHz,
                TargetFrameRate = Application.targetFrameRate,
                CloudRenderInfo = BuildCloudRenderInfo(),
            };
        }

        /// <summary>读取当前云的 GPU 开销相关配置(分辨率 / TSS / 采样格网);非飞行或 Volken 未初始化时返回 null。</summary>
        private static string BuildCloudRenderInfo()
        {
            try
            {
                var volken = Volken.Clouds.VolkenClouds.Instance;
                if (volken == null)
                {
                    return null;
                }

                var layer = volken.MainLayer;
                if (layer == null || layer.config == null)
                {
                    return null;
                }

                var c = layer.config;
                return "res " + c.resolutionScale.ToString("0.00") +
                       " | TSS " + (c.useTemporalUpscale ? "on" : "off") +
                       " | grid " + c.upscaleX + "x" + c.upscaleY;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>重置(Overlay 显示时调用,镜像游戏内置行为)。</summary>
        public void Reset()
        {
            _smoothedMs = 0f;
            _lastFrameMs = 0f;
            _hasFrameTiming = false;
            _gpuFrameMs = 0f;
            _cpuMainThreadFrameMs = 0f;
            _cpuRenderThreadFrameMs = 0f;
            _presentWaitMs = 0f;
        }

        /// <summary>瓶颈判断:某项占整帧耗时 ≥85% 才判定为该瓶颈。</summary>
        private string ComputeBottleneck(float frameMs)
        {
            if (!_hasFrameTiming || frameMs <= 0f)
            {
                return "n/a";
            }

            float gpu = _gpuFrameMs;
            float render = _cpuRenderThreadFrameMs;
            float main = _cpuMainThreadFrameMs;

            float gpuShare = gpu / frameMs;
            float renderShare = render / frameMs;
            float mainShare = main / frameMs;

            if (gpuShare >= 0.85f && gpuShare >= renderShare && gpuShare >= mainShare)
            {
                return "GPU";
            }
            if (renderShare >= 0.85f && renderShare >= mainShare)
            {
                return "RENDER";
            }
            if (mainShare >= 0.85f)
            {
                return "CPU";
            }
            return "mixed";
        }

        private void CaptureFrameTimings()
        {
            _hasFrameTiming = false;
            _cpuMainThreadFrameMs = 0f;
            _cpuRenderThreadFrameMs = 0f;
            _presentWaitMs = 0f;
            _gpuFrameMs = 0f;
            try
            {
                if (!FrameTimingManager.IsFeatureEnabled())
                {
                    return;
                }

                FrameTimingManager.CaptureFrameTimings();
                uint copied = FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings);
                if (copied == 0)
                {
                    return;
                }

                FrameTiming latest = _frameTimings[0];
                // 官方文档:FrameTiming.*FrameTime 单位为 ms,无需换算
                _cpuMainThreadFrameMs = (float)latest.cpuMainThreadFrameTime;
                _cpuRenderThreadFrameMs = (float)latest.cpuRenderThreadFrameTime;
                _presentWaitMs = (float)latest.cpuMainThreadPresentWaitTime;
                _gpuFrameMs = (float)latest.gpuFrameTime;
                _hasFrameTiming = true;
            }
            catch
            {
                // 平台不支持或 API 受限时静默关闭该数据源
            }
        }
    }
}
