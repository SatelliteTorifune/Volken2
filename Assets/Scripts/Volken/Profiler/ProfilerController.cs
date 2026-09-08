using System;
using Assets.Packages.DevConsole;
using UnityEngine;

namespace VolkenProfiler
{
    /// <summary>
    /// 性能分析器入口(独立命名空间 <c>Volken.Profiler</c>)。
    /// 单例:由 <c>Mod.OnModLoaded()</c> 调用 <see cref="Create"/> 创建,
    /// 负责承载叠加层并注册开发控制台命令:
    ///   VolkenProfiler          —— 开关叠加层
    ///   VolkenProfiler.Capture  —— 开始/结束录制帧数据并导出 CSV
    /// 可见性同步自 <see cref="Assets.Scripts.ModSettings.ShowProfiler"/>。
    /// </summary>
    public class ProfilerController : MonoBehaviour
    {
        public static ProfilerController Instance { get; private set; }

        private ProfilerOverlay _overlay;
        private bool _commandsRegistered;
        private bool _visible;

        /// <summary>创建单例(幂等)。</summary>
        public static ProfilerController Create()
        {
            if (Instance != null)
            {
                return Instance;
            }

            var go = new GameObject("VolkenProfiler");
            DontDestroyOnLoad(go);
            return go.AddComponent<ProfilerController>();
        }

        public bool IsVisible => _visible;

        private void Awake()
        {
            Instance = this;

            var overlayGo = new GameObject("Overlay");
            overlayGo.transform.SetParent(transform, false);
            _overlay = overlayGo.AddComponent<ProfilerOverlay>();
            _overlay.gameObject.SetActive(false); // 默认隐藏,隐藏时零开销

            RegisterCommands();
        }

        private void Update()
        {
            // 以 ModSettings.ShowProfiler 为唯一事实来源,双向保持一致
            bool desired = _visible;
            try
            {
                var s = Assets.Scripts.ModSettings.Instance?.ShowProfiler;
                desired = s != null && s.Value;
            }
            catch
            {
                // 设置尚未就绪时保持当前状态
            }

            if (desired != _visible)
            {
                SetVisible(desired);
            }
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_overlay != null)
            {
                _overlay.gameObject.SetActive(visible);
            }
        }

        public void Toggle()
        {
            bool next = !_visible;
            try
            {
                var s = Assets.Scripts.ModSettings.Instance?.ShowProfiler;
                if (s != null)
                {
                    s.Value = next;
                }
            }
            catch
            {
                // 设置不可用时只切本地状态
            }

            SetVisible(next);
        }

        private void RegisterCommands()
        {
            if (_commandsRegistered)
            {
                return;
            }

            _commandsRegistered = true;
            try
            {
                DevConsoleApi.RegisterCommand("VolkenProfiler", Toggle);
                DevConsoleApi.RegisterCommand("VolkenProfiler.Capture", ToggleCapture);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Volken.Profiler] failed to register console commands: " + ex);
            }
        }

        private void ToggleCapture()
        {
            var session = _overlay != null ? _overlay.Session : null;
            if (session == null)
            {
                return;
            }

            if (session.CaptureActive)
            {
                string path = session.FinishCapture();
                if (path != null)
                {
                    Debug.Log("[Volken.Profiler] capture saved: " + path);
                }
                else
                {
                    Debug.Log("[Volken.Profiler] capture ended (no samples)");
                }
            }
            else
            {
                session.BeginCapture();
                Debug.Log("[Volken.Profiler] capture started (max " + session.CaptureLimit + " frames)");
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
