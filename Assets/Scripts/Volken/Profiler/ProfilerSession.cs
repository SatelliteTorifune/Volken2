using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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

    /// <summary>录制导出用的单帧样本(GPU 焦点)。</summary>
    public struct FrameSample
    {
        public double Time;
        public float FrameMs;
        public float Fps;
        public float GpuMs;
        public float RenderMs;
        public float PresentMs;
    }

    /// <summary>
    /// 帧数据采集器(GPU 焦点,独立命名空间 <c>VolkenProfiler</c>)。
    /// 每帧采集:帧率/帧耗时(EMA)、GPU 帧时间、渲染线程耗时、Present 等待、瓶颈判断;
    /// 可录制一段帧数据并导出 CSV。不采集 CPU 细节与内存。
    /// </summary>
    public sealed class ProfilerSession
    {
        public const int DefaultCaptureLimit = 1800; // 约 30s @ 60fps
        private const float SmoothingFactor = 0.1f;  // 与游戏内置 FpsMonitor 一致

        private readonly FrameTiming[] _frameTimings = new FrameTiming[8];
        private float _cpuMainThreadFrameMs;   // 仅用于瓶颈判断,不对外展示
        private float _cpuRenderThreadFrameMs;
        private float _presentWaitMs;
        private float _gpuFrameMs;
        private bool _hasFrameTiming;

        private float _smoothedMs;
        private float _lastFrameMs;

        private readonly List<FrameSample> _capture = new List<FrameSample>();
        private bool _captureActive;
        private int _captureLimit = DefaultCaptureLimit;
        private double _elapsed;

        public bool CaptureActive => _captureActive;
        public int CaptureLimit { get => _captureLimit; set => _captureLimit = Mathf.Max(1, value); }

        /// <summary>每帧调用一次(由 Overlay 的 Update 驱动)。</summary>
        public void Tick(float unscaledDeltaTime)
        {
            _elapsed += unscaledDeltaTime;

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

            // 3) 录制
            if (_captureActive)
            {
                var snap = BuildSnapshot();
                _capture.Add(new FrameSample
                {
                    Time = _elapsed,
                    FrameMs = _lastFrameMs,   // CSV 记录原始帧耗时,而非平滑值
                    Fps = snap.Fps,
                    GpuMs = snap.GpuFrameMs,
                    RenderMs = snap.CpuRenderThreadFrameMs,
                    PresentMs = snap.PresentWaitMs,
                });
                if (_capture.Count >= _captureLimit)
                {
                    FinishCapture();
                }
            }
        }

        /// <summary>开始录制帧数据。</summary>
        public void BeginCapture()
        {
            _capture.Clear();
            _captureActive = true;
        }

        /// <summary>结束录制并导出 CSV,返回文件路径;失败返回以 "ERR:" 开头的错误信息。</summary>
        public string FinishCapture()
        {
            _captureActive = false;
            if (_capture.Count == 0)
            {
                return null;
            }

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(_capture.Count * 56 + 128);
            sb.AppendLine("time_s,frame_ms,fps,gpu_ms,render_ms,present_ms");
            foreach (var s in _capture)
            {
                sb.Append(s.Time.ToString("F3", inv)).Append(',');
                sb.Append(s.FrameMs.ToString("F2", inv)).Append(',');
                sb.Append(s.Fps.ToString("F1", inv)).Append(',');
                sb.Append(s.GpuMs.ToString("F2", inv)).Append(',');
                sb.Append(s.RenderMs.ToString("F2", inv)).Append(',');
                sb.Append(s.PresentMs.ToString("F2", inv));
                sb.AppendLine();
            }

            string path = Path.Combine(Application.persistentDataPath,
                "VolkenProfilerGPU_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            try
            {
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                _capture.Clear();
                return "ERR: " + ex.Message;
            }

            _capture.Clear();
            return path;
        }

        /// <summary>构建 GPU 焦点快照。</summary>
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

        /// <summary>
        /// 读取当前云的 GPU 开销相关配置(分辨率 / TSS / 采样格网)。
        /// 非飞行或 Volken 尚未初始化时返回 null。
        /// </summary>
        private static string BuildCloudRenderInfo()
        {
            try
            {
                var volken = Volken.Instance;
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
            _capture.Clear();
            _captureActive = false;
        }

        /// <summary>
        /// 粗略判断瓶颈:比较 GPU 帧时间 / 渲染线程 / 主线程与整帧耗时的占比。
        /// 玩家构建里 Unity 不公开 draw call 等细节,这是能拿到的最接近的 GPU 占用判断。
        /// </summary>
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
