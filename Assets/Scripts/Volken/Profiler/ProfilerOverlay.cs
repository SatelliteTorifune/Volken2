using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace VolkenProfiler
{
    /// <summary>
    /// GPU 焦点性能叠加层(独立命名空间 <c>VolkenProfiler</c>)。
    /// 纯代码构建的 uGUI 覆盖层,重点展示 GPU 帧时间 / 渲染线程 / Present 等待 / 瓶颈判断;
    /// 仅展示 <see cref="ProfilerSession"/> 采集的 GPU 相关数据。
    /// </summary>
    public class ProfilerOverlay : MonoBehaviour
    {
        public static ProfilerOverlay Instance { get; private set; }

        private const float RefreshInterval = 0.25f; // 与游戏内置 FPS 覆盖层刷新节奏一致

        private ProfilerSession _session;
        private Text _text;
        private float _refreshTimer;

        /// <summary>数据采集器(懒加载)。</summary>
        public ProfilerSession Session => _session ?? (_session = new ProfilerSession());

        private void Awake()
        {
            Instance = this;
            BuildUi();
        }

        private void OnEnable()
        {
            // 显示时重置,让数据从本次打开开始(与游戏内置一致)
            Session.Reset();
        }

        private void Update()
        {
            // 每帧喂一次数据(Overlay 隐藏时 GameObject 被禁用,Update 不会执行,零开销)
            Session.Tick(Time.unscaledDeltaTime);

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer >= RefreshInterval)
            {
                _refreshTimer = 0f;
                RefreshText();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void BuildUi()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000; // 盖在游戏 UI 之上

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // 让面板可接收指针事件(复用游戏全局 EventSystem,不新建)
            canvasGo.AddComponent<GraphicRaycaster>();

            // --- 半透明背景 ---
            var bgGo = new GameObject("Background", typeof(RectTransform));
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 1f);   // 左上角
            bgRect.anchorMax = new Vector2(0f, 1f);
            bgRect.pivot = new Vector2(0f, 1f);
            bgRect.anchoredPosition = new Vector2(12f, -12f);
            bgRect.sizeDelta = new Vector2(470f, 250f);

            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.55f);
            bgImage.raycastTarget = true; // 背景接收点击/拖拽

            // 拖拽组件(鼠标按住面板背景拖动)
            bgGo.AddComponent<PanelDrag>();

            // --- 文本 ---
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(bgGo.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 10f);
            textRect.offsetMax = new Vector2(-10f, -10f);

            _text = textGo.AddComponent<Text>();
            _text.font = LoadFont();
            _text.fontSize = 16;
            _text.lineSpacing = 1f;
            _text.color = Color.white;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            _text.supportRichText = false;
            _text.text = "Volken Profiler (GPU)\nwaiting for data...";
        }

        private void RefreshText()
        {
            if (_text == null)
            {
                return;
            }

            var s = Session.BuildSnapshot();
            var sb = new StringBuilder(384);

            sb.AppendLine("Volken Profiler (GPU)");
            sb.Append("FPS ").Append(s.Fps.ToString("F0"));
            sb.Append("  frame ").Append(s.FrameMs.ToString("F1")).Append(" ms");
            sb.AppendLine();

            if (s.HasFrameTiming)
            {
                sb.Append("GPU ").Append(s.GpuFrameMs.ToString("F1")).Append(" ms");
                sb.Append(" (").Append(s.GpuSharePercent.ToString("F0")).Append("% of frame)");
                sb.Append("  render ").Append(s.CpuRenderThreadFrameMs.ToString("F1")).Append(" ms");
                sb.AppendLine();
                sb.Append("present-wait ").Append(s.PresentWaitMs.ToString("F1")).Append(" ms");
                sb.AppendLine();
                sb.Append("bottleneck: ").AppendLine(s.Bottleneck);
            }
            else
            {
                sb.AppendLine("GPU timing: n/a (FrameTimingManager unavailable)");
            }

            // 当前云渲染配置(GPU 开销的主要来源)
            if (!string.IsNullOrEmpty(s.CloudRenderInfo))
            {
                sb.Append("cloud  ").AppendLine(s.CloudRenderInfo);
            }

            // GPU 静态信息
            if (!string.IsNullOrEmpty(s.GpuName))
            {
                sb.Append("GPU ").Append(s.GpuName).Append("  [").Append(s.GraphicsApi).Append("]");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(s.GpuApiVersion))
            {
                sb.Append("API ").Append(s.GpuApiVersion);
                if (s.GpuMemoryMb > 0)
                {
                    sb.Append("  VRAM ").Append(s.GpuMemoryMb).Append(" MB");
                }
                sb.AppendLine();
            }
            if (s.MaxTextureSize > 0 || s.SupportsCompute)
            {
                sb.Append("max tex ").Append(s.MaxTextureSize);
                sb.Append("  compute ").Append(s.SupportsCompute ? "yes" : "no");
                sb.AppendLine();
            }
            if (s.RefreshRateHz > 0)
            {
                sb.Append("vsync ").Append(s.VsyncCount);
                sb.Append(" @ ").Append(s.RefreshRateHz).Append("Hz");
                sb.Append("  target FPS ").Append(s.TargetFrameRate > 0 ? s.TargetFrameRate.ToString() : "unlimited");
                sb.AppendLine();
            }

            _text.text = sb.ToString();
        }

        private static Font LoadFont()
        {
            try
            {
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font != null)
                {
                    return font;
                }
            }
            catch
            {
                // 回退到系统字体
            }

            try
            {
                return Font.CreateDynamicFontFromOSFont("Arial", 16);
            }
            catch
            {
                return null;
            }
        }
    }
}
