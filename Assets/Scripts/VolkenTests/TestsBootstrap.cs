using System;
using UnityEngine;

namespace Volken.Tests
{
    /// <summary>
    /// 测试/开发工具的唯一自发入口:启动时起一个隐藏物体,创建开发工具(性能剖析覆盖层),然后自毁。
    /// 控制台命令只在 <c>Mod.RegisterCommands</c> 注册(这里不再注册任何命令)。
    /// **正式代码对测试文件夹零引用** —— 删掉整个 <c>VolkenTests</c> 文件夹,mod 仍能编译并正常运行。
    /// </summary>
    public static class TestsBootstrap
    {
        /// <summary>是否已成功注册(避免重复)。</summary>
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

        /// <summary>创建开发工具;返回 true = 全部完成(调用方随后自毁)。</summary>
        internal static bool TryRegister()
        {
            // 编辑器里什么都不做(编辑器要的是雨预览台)。
            if (Application.isEditor)
            {
                _profilerCreated = true;
                _registered = true;
                return true;
            }

            // 性能剖析覆盖层(只依赖 Unity API;Create 幂等)
            if (!_profilerCreated)
            {
                try
                {
                    VolkenProfiler.ProfilerController.Create();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[VolkenTests] profiler create: " + ex.Message);
                }
                _profilerCreated = true;   // 失败也别每帧重试刷屏
            }

            if (!_registered)
            {
                _registered = true;
                UnityEngine.Debug.Log("[VolkenTests] 开发工具就绪(性能剖析覆盖层)");
            }
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
            }
        }
    }
}
