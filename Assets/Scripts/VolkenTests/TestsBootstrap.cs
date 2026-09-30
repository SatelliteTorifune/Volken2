using System;
using UnityEngine;
using Assets.Packages.DevConsole;

namespace Volken.Tests
{
    /// <summary>
    /// **测试/开发工具的单独入口**（整个 <c>Assets/Scripts/VolkenTests</c> 文件夹的唯一"自发起点"）。
    ///
    /// 【为什么要这个文件】正式代码（<c>Mod.cs</c> / <c>VolkenMod.cs</c> / <c>WeatherPanel.cs</c> …）
    ///   **对测试文件夹零引用** —— 删掉整个 <c>VolkenTests</c> 文件夹，mod 仍能编译并正常运行。
    ///   原本写在 `Mod.RegisterCommands()` 里的"测试探针命令注册"和 `ProfilerController.Create()`，
    ///   现在收拢到这里自发注册（用 <see cref="RuntimeInitializeOnLoadMethodAttribute"/> 起一个隐藏物体，
    ///   等游戏/开发者控制台就绪后注册一次，然后自毁）。
    ///
    /// 【本文件夹内容】
    ///   · <c>RainPreview.cs</c>     —— 编辑器内雨预览台（自由飞 + IMGUI 参数面板，按 Play 即可迭代）
    ///   · <c>RainAxisProbe.cs</c>   —— Phase 1 雨丝构轴对照探针（命令 <c>volkenRainAxis*</c>）
    ///   · <c>NoiseVisualizer.cs</c> · <c>RaymarchDebug.cs</c> —— 体积云噪声/步进可视化
    ///   · <c>Profiler/</c>          —— 性能剖析覆盖层（命令 <c>VolkenProfiler*</c>，自身注册）
    ///
    /// 【想彻底不打包进游戏】删掉 <c>VolkenTests</c> 文件夹即可（Unity 会把它从编译中移除）。
    /// </summary>
    public static class TestsBootstrap
    {
        /// <summary>是否已成功注册（避免重复）。</summary>
        private static bool _registered;
        private static bool _profilerCreated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (_registered) return;
            try
            {
                var go = new GameObject("VolkenTestsBootstrap");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<BootstrapRunner>();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[VolkenTests] bootstrap spawn failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 把测试用命令/工具注册进运行时。返回 true = 全部完成（调用方随后自毁）。
        /// ⚠️ 剖析器的创建**不依赖**控制台命令注册是否成功（两件事分开,互不拖累）。
        /// </summary>
        internal static bool TryRegister()
        {
            // 编辑器里**什么都不做**:`DevConsoleApi` 属于游戏包,在编辑器里注册命令同样可能触发
            // 游戏侧半初始化(刷 CelestialDatabase/SceneManager 之类的无关报错)。
            // 编辑器里要的是"雨预览台"(RainPreview),不需要控制台命令。
            if (Application.isEditor)
            {
                _profilerCreated = true;
                _registered = true;
                return true;
            }

            // ① 性能剖析覆盖层(只依赖 Unity API;Create 幂等)
            if (!_profilerCreated)
            {
                try
                {
                    VolkenProfiler.ProfilerController.Create();
                    _profilerCreated = true;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[VolkenTests] profiler create: " + ex.Message);
                    _profilerCreated = true;   // 别每帧重试刷屏
                }
            }

            if (_registered) return true;

            // ② Phase 1 构轴对照探针的命令(控制台可能还没就绪 → 返回 false 让调用方稍后重试)
            try
            {
                DevConsoleApi.RegisterCommand("volkenRainAxis", RainAxisProbe.DiagStatus);
                DevConsoleApi.RegisterCommand<int>("volkenRainAxisOn", RainAxisProbe.SetEnabled);
                DevConsoleApi.RegisterCommand<int>("volkenRainAxisMode", RainAxisProbe.SetMode);
                DevConsoleApi.RegisterCommand<float, float, float>("volkenRainAxisVec", RainAxisProbe.SetAirDir);
                DevConsoleApi.RegisterCommand<int>("volkenRainAxisCount", RainAxisProbe.SetCount);
            }
            catch
            {
                return false;   // 控制台还没就绪 → 下一帧再试
            }

            _registered = true;
            UnityEngine.Debug.Log("[VolkenTests] 测试命令已注册: volkenRainAxis / volkenRainAxisOn / volkenRainAxisMode / " +
                                  "volkenRainAxisVec / volkenRainAxisCount(+ VolkenProfiler*)");
            return true;
        }
    }

    /// <summary>重试器:等游戏/控制台就绪后注册一次,然后销毁自己。</summary>
    internal class BootstrapRunner : MonoBehaviour
    {
        private int _frames;

        private void Update()
        {
            _frames++;
            if (_frames < 30) return;                       // 给游戏/控制台几帧启动时间
            if (TestsBootstrap.TryRegister())
            {
                Destroy(gameObject);
                return;
            }
            if (_frames == 1800)                            // 30 秒还没注册上 → 明确告警一次(不再无限重试)
            {
                UnityEngine.Debug.LogWarning("[VolkenTests] 控制台命令注册失败(DevConsole 未就绪?) —— " +
                                             "RainPreview/Profiler 不受影响,但 volkenRainAxis* 命令可能不可用");
                Destroy(gameObject);
            }
        }
    }
}
