using System;
using System.IO;
using Assets.Scripts;

namespace Volken.Core
{
    /// <summary>预设名会成为配置文件名的一部分:这里统一做合法性检查与坏文件隔离,避免路径穿越和静默覆盖。</summary>
    internal static class ConfigFileNames
    {
        /// <summary>是否可安全用作配置文件名主名;拒绝空名、路径分隔符、非法字符、<c>.</c>/<c>..</c> 与结尾点空格。</summary>
        public static bool IsValid(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 32 || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|') return false;
            }
            char last = name[name.Length - 1];
            return last != '.' && last != ' ';
        }

        /// <summary>把无法读取的旧文件改名为 <c>.broken</c> 保留现场(绝不删除玩家文件);成功返回 true。</summary>
        public static bool TryQuarantine(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

                string targetPath = filePath + ".broken";
                for (int i = 2; File.Exists(targetPath) && i < 1000; i++)
                {
                    targetPath = filePath + ".broken" + i;
                }
                File.Move(filePath, targetPath);
                Mod.Log("Config file quarantined: {0} -> {1}", filePath, targetPath);
                return true;
            }
            catch (Exception e)
            {
                Mod.Log("Config file quarantine failed for '{0}': {1}", filePath, e.Message);
                return false;
            }
        }
    }
}
