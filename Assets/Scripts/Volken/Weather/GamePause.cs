using Assets.Scripts;
using UnityEngine;

namespace Volken.Weather
{
    /// <summary>游戏暂停的唯一判定点,外加一个"暂停期间停表"的真实时钟(任何该在暂停时停住的计时都用 <see cref="Now"/>)。</summary>
    /// <remarks>判据 = Unity <c>Time.timeScale</c> 兜底 + JNO 自己的 <c>ITimeManager.Paused</c>(慢动作/快进是它的另几个字段,不算暂停)。
    /// 累计暂停时长按"两次查询的间隔"懒累加,且**一帧只查一次游戏 API**(闪电分叉可能有上百个自毁组件在查),不需要额外驱动者。</remarks>
    public static class GamePause
    {
        public static bool IsPaused { get { Tick(); return _paused; } }

        /// <summary>真实秒数但**排除累计暂停时长**(单调递增);不要用 <c>Time.realtimeSinceStartup</c> 或 <c>Time.unscaledTime</c>——两者在游戏暂停时都照走。</summary>
        public static float Now { get { Tick(); return Time.realtimeSinceStartup - _pausedTotal; } }

        private static bool _paused;
        private static bool _started;
        private static float _pausedTotal;
        private static float _lastQuery;
        private static int _lastFrame = -1;

        private static void Tick()
        {
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;   // 一帧只查一次游戏 API(分叉组件可能上百个,别每帧打上百次)
            _lastFrame = frame;

            float now = Time.realtimeSinceStartup;
            if (_started && _paused) _pausedTotal += now - _lastQuery;   // 上一次查询到现在都是暂停 → 计入停表
            _started = true;
            _lastQuery = now;
            _paused = Query();
        }

        private static bool Query()
        {
            if (Time.timeScale <= 0.0001f) return true;
            if (RainParticles.StandaloneMode) return false;   // 编辑器预览:不碰游戏单例
            try
            {
                var tm = Game.Instance?.FlightScene?.TimeManager;
                return tm != null && tm.Paused;
            }
            catch { return false; }
        }
    }
}
