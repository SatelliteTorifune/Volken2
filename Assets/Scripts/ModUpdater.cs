using System;
using System.Collections;
using System.Text.RegularExpressions;
using ModApi;
using ModApi.Ui;
using UnityEngine;
using UnityEngine.Networking;

namespace Assets.Scripts
{
    /// 通用 Mod 更新检查 + 提醒弹窗:双通道取"网站最新版本"(GitHub Releases API 的 tag_name 为主,raw version.txt 兜底),
    /// 网站最新 &gt; 本地且玩家没点过"不再提醒" → 进主菜单后弹三按钮弹窗(下载更新 / 稍后再说 / 不再提醒)。
    /// **不阻塞主线程**:网络等待全在协程里(UnityWebRequest + 总看门狗 15s);每次游戏会话只检查一次(static 防抖)。
    public class ModUpdater
    {
        // 通道1:GitHub Releases API,取 JSON 里的 "tag_name" 即最新版本号(前导 v 会被忽略)。
        public const string LatestVersionUrl =
            "https://api.github.com/repos/SatelliteTorifune/Volken2/releases/latest";

        public const string DownloadUrl =
            "https://github.com/SatelliteTorifune/Volken2/releases/latest";

        // 通道2(兜底):仓库根目录 version.txt 的 raw 直链,内容就是版本号;留空("")则禁用兜底通道。
        public const string VersionFileUrl =
            "https://raw.githubusercontent.com/SatelliteTorifune/Volken2/main/version.txt";

        private const string SkippedVersionPrefKey = "Volken.UpdateReminder.SkippedVersion";   // "不再提醒"记住的版本(key 带 Mod 名防冲突)

        private static bool _startedThisSession;   // 一次游戏会话只检查一次(static 跨实例)

        private Version _localVersion;
        private ModUpdaterHost _host;

        /// 启动一次更新检查(每次游戏会话最多一次,重复调用忽略)。**立即返回**:不发起也不等待任何网络请求。
        public void CheckForUpdate()
        {
            try
            {
                if (_startedThisSession) return;
                _startedThisSession = true;

                _localVersion = Mod.Instance.ModVersion;
                if (_localVersion == null)
                {
                    Mod.Log("更新检查跳过——ModVersion 为空");
                    return;
                }

                if (string.IsNullOrWhiteSpace(LatestVersionUrl))
                {
                    // URL 被清空(调试)则只打日志,不弹窗
                    Mod.Log("更新检查——LatestVersionUrl 未配置,当前版本 {0}", _localVersion);
                    return;
                }

                // 非 MonoBehaviour,要一个隐藏 GameObject 当协程宿主
                if (_host == null)
                {
                    var go = new GameObject("VolkenUpdateReminder");
                    GameObject.DontDestroyOnLoad(go);
                    _host = go.AddComponent<ModUpdaterHost>();
                    _host.Owner = this;
                }
            }
            catch (Exception ex)
            {
                Mod.Log("更新检查初始化失败: {0}", ex);
            }
        }

        public IEnumerator FetchRoutine()
        {
            // 总看门狗:整个"等最新版本号"的过程必须在 deadline 前结束(Time.realtimeSinceStartup 不受暂停影响)。
            // UnityWebRequest.timeout 只管单个请求,这里管整体等待。
            const float totalTimeoutSeconds = 15f;
            var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;

            Version latest = null;
            var got = false;

            yield return TryFetchVersion(LatestVersionUrl, deadline, v => { latest = v; got = true; }, () => { });

            // 通道2(兜底):API 限流 / 断网 / 无 release 时回退 version.txt,前提是还没到看门狗期限
            if (!got && Time.realtimeSinceStartup < deadline)
            {
                Mod.Log("更新检查——API 通道不可用,回退到 version.txt");
                yield return TryFetchVersion(VersionFileUrl, deadline, v => { latest = v; got = true; }, () => { });
            }

            if (!got || latest == null)
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Mod.Log("更新检查——等待版本号超时(>{0}s),本次跳过提醒", totalTimeoutSeconds);
                else
                    Mod.Log("更新检查——所有通道均失败,本次跳过提醒");
                yield break;
            }

            Mod.Log("更新检查——当前 {0},网站最新 {1}", _localVersion, latest);
            if (latest <= _localVersion) yield break; // 已是最新,不打扰

            if (Version.TryParse(PlayerPrefs.GetString(SkippedVersionPrefKey, ""), out var skipped)
                && latest <= skipped)
            {
                Mod.Log("更新检查——版本 {0} 已被用户跳过提醒", latest);
                yield break;
            }

            // 等进入主菜单再弹,避免在飞行/设计场景打断玩家(想换场景就改 InMenuScene 判定)。
            while (Game.Instance == null || !Game.Instance.SceneManager.InMenuScene)
            {
                yield return null;
            }

            ShowUpdateDialog(latest);
        }

        /// 抓取 URL 并解析版本号;超过 deadline 主动 Abort(onSuccess / onFail 由两个通道共用)。
        private IEnumerator TryFetchVersion(string url, float deadline, Action<Version> onSuccess, Action onFail)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                // GitHub 的 URL(API 与 raw)都要求非空 User-Agent,否则返回 403
                request.SetRequestHeader("User-Agent", "VolkenUpdater/1.0");

                // 逐帧轮询 isDone(而非 `yield return request.SendWebRequest()`)才能每帧检查看门狗并主动 Abort;不阻塞主线程。
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                    {
                        request.Abort();
                        Mod.Log("更新检查——总等待超时,已中断请求 {0}", url);
                        onFail?.Invoke();
                        yield break;
                    }
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Mod.Log("更新检查——请求失败 {0}: {1}", url, request.error);
                    onFail?.Invoke();
                    yield break;
                }

                if (TryParseLatestVersion(request.downloadHandler.text, out var version))
                {
                    onSuccess?.Invoke(version);
                }
                else
                {
                    Mod.Log("更新检查——无法解析 {0} 的版本,原文: {1}", url, request.downloadHandler.text);
                    onFail?.Invoke();
                }
            }
        }

        /// 解析版本号:兼容纯文本("0.7" / "v0.7.1")、Releases JSON 的 "tag_name" 与 {"version":"0.7"}。
        private static bool TryParseLatestVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();

            if (s.StartsWith("{") || s.StartsWith("["))
            {
                var match = Regex.Match(s, "\"(?:tag_name|version)\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) s = match.Groups[1].Value.Trim();
            }

            // 去掉前导 v/V,再取第一段形如 "数字.数字..." 的内容(抗 HTML / 换行干扰)
            s = Regex.Replace(s, "^[vV]", "");
            s = Regex.Match(s, @"\d+(?:\.\d+){1,3}").Value;

            return Version.TryParse(s, out version);
        }

        private void ShowUpdateDialog(Version latest)
        {
            try
            {
                if (Game.Instance?.UserInterface == null) return;

                var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.ThreeButtons, null, true);
                if (dialog == null) return;

                dialog.MessageText = string.Format(
                    "{0}\n\n{1}\n{2}",
                    Locale.GetString("Volken.UI.UpdateAvailable"),
                    string.Format(Locale.GetString("Volken.UI.UpdateNewVersion"), latest),
                    string.Format(Locale.GetString("Volken.UI.UpdateCurrentVersion"), _localVersion));
                dialog.OkayButtonText = Locale.GetString("Volken.UI.UpdateDownload");
                dialog.MiddleButtonText = Locale.GetString("Volken.UI.UpdateLater");
                dialog.CancelButtonText = Locale.GetString("Volken.UI.UpdateDismiss");

                dialog.OkayClicked += d =>
                {
                    d.Close();
                    if (!string.IsNullOrEmpty(DownloadUrl))
                        Application.OpenURL(DownloadUrl);
                    else
                        Mod.Log("更新下载页 DownloadUrl 未配置");
                };

                // 稍后再说:仅关闭,下次启动仍会提醒
                dialog.MiddleClicked += d => d.Close();

                // 不再提醒:记住跳过的版本,出现更新版本前不再弹
                dialog.CancelClicked += d =>
                {
                    PlayerPrefs.SetString(SkippedVersionPrefKey, latest.ToString());
                    PlayerPrefs.Save();
                    d.Close();
                };
            }
            catch (Exception ex)
            {
                Mod.Log("更新提醒弹窗失败: {0}", ex);
            }
        }

        public class ModUpdaterHost : MonoBehaviour
        {
            public ModUpdater Owner;

            private void Start()
            {
                if (Owner != null)
                {
                    StartCoroutine(Owner.FetchRoutine());
                }
            }

            // 宿主被销毁(场景切换/重载)时立刻停协程,不留悬挂的网络等待
            private void OnDestroy()
            {
                StopAllCoroutines();
            }
        }
    }
}

// 注:Mod.Log 受设置项 ModSettings.ShowDevLog 控制(关掉开发日志就不打印),签名为 (string format, params object[] args)。
