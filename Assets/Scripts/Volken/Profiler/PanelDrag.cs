using UnityEngine;
using UnityEngine.EventSystems;

namespace VolkenProfiler
{
    /// <summary>
    /// 让 GPU 性能面板可拖动(挂在面板背景上,独立命名空间 <c>VolkenProfiler</c>)。
    /// 复用游戏已有的全局 EventSystem,不新建,避免输入冲突;
    /// 只需面板所在 Canvas 带有 GraphicRaycaster 即可收到指针事件。
    /// </summary>
    public class PanelDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        private const float EdgeMargin = 40f; // 拖动时面板至少保留在屏幕内的边缘留白(px)

        private RectTransform _panel;
        private RectTransform _canvas;
        private Vector2 _pointerOffset;

        private void Awake()
        {
            _panel = transform as RectTransform;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                _canvas = canvas.transform as RectTransform;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_panel == null)
            {
                return;
            }

            // 点击时把面板提到最前
            _panel.SetAsLastSibling();

            // 记录鼠标在面板本地坐标中的抓取偏移(面板 pivot 在左上角,原点即面板左上角)
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _panel, eventData.position, eventData.pressEventCamera, out _pointerOffset);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_panel == null || _canvas == null)
            {
                return;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvas, eventData.position, eventData.pressEventCamera, out Vector2 localPointer))
            {
                return;
            }

            // 期望的面板左上角(画布本地空间) = 指针位置 - 抓取偏移
            _panel.localPosition = ClampToCanvas(localPointer - _pointerOffset);
        }

        /// <summary>把面板左上角夹在画布范围内,保证面板不整体拖出屏幕。</summary>
        private Vector2 ClampToCanvas(Vector2 pos)
        {
            Vector2 canvasSize = _canvas.rect.size;
            Vector2 panelSize = _panel.rect.size;

            float margin = Mathf.Min(EdgeMargin, Mathf.Min(canvasSize.x, canvasSize.y) * 0.25f);

            // 面板 pivot = 左上角(0,1),pos 即面板左上角在画布本地空间的位置(画布本地原点在中心)
            float minX = -canvasSize.x * 0.5f + margin;
            float maxX = canvasSize.x * 0.5f - margin - panelSize.x;
            float minY = -canvasSize.y * 0.5f + margin + panelSize.y;
            float maxY = canvasSize.y * 0.5f - margin;

            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
            return pos;
        }
    }
}
