using System;
using UnityEngine;

namespace VolkenProfiler
{
    /// <summary>
    /// 性能分析器入口,由 <c>TestsBootstrap</c> 调 <see cref="Create"/> 创建(幂等)。
    /// 可见性以 <c>ModSettings.ShowProfiler</c> 为准。
    /// </summary>
    public class ProfilerController : MonoBehaviour
    {
        public static ProfilerController Instance { get; private set; }

        private ProfilerOverlay _overlay;
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

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
