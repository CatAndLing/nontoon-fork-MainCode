using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace LilToonToNonToonConverter
{
    // [NT-L10N] 工具包自己的本地化层。
    //
    // 为什么不复用 ShaderCore 的 L10n：它的 L() 只会查「当前正在编辑的着色器/模块」的翻译表，
    // 而我们要查的是工具包自己的表；它的 Settings 类是 internal，拿不到 language 字段（这里用反射拿）。
    //
    // 设计：C# 里保留英文原文当 key，显示时过一层 L()。
    //   - 命中 lang/zh-Hans.po → 中文
    //   - 未命中 → 原样返回英文（这就是「英文 fallback」）
    internal static class NTL10n
    {
        private static Dictionary<string, string> table;
        private static bool loaded;

        internal static bool IsChinese
        {
            get
            {
                var lang = ShaderCoreLanguage();
                if (!string.IsNullOrEmpty(lang)) return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                return CultureInfo.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ShaderCore 的 Settings 是 internal 的 ScriptableSingleton，用反射读它的 language。
        // 拿不到就回落到系统区域（ShaderCore 的默认值同样是 CurrentCulture.Name）。
        private static string ShaderCoreLanguage()
        {
            try
            {
                var type = FindType("jp.lilxyzw.shadercore.Settings");
                if (type == null) return null;
                var instance = type.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                if (instance == null) return null;
                return type.GetField("language", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) as string;
            }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        internal static string L(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            if (!IsChinese) return english;
            if (!loaded)
            {
                loaded = true;
                table = LoadTable();
            }
            return table != null && table.TryGetValue(english, out var translated) && !string.IsNullOrEmpty(translated) ? translated : english;
        }

        // 模板：po 里存 "{0}" 形式的英文模板，命中后按当前区域格式化
        internal static string F(string english, params object[] args)
        {
            return string.Format(L(english), args);
        }

        private static Dictionary<string, string> LoadTable()
        {
            var result = new Dictionary<string, string>();
            foreach (var path in CandidatePoPaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    Parse(path, result);
                    return result;
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[NTL10n] 读取翻译失败：" + path + " / " + e.Message); }
            }
            return result;
        }

        private static List<string> CandidatePoPaths()
        {
            var paths = new List<string>();
            // 1) VPM / UPM 包安装位置
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NTL10n).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                    paths.Add(Path.Combine(info.resolvedPath, "lang", "zh-Hans.po"));
            }
            catch { }

            // 2) 直接放在 Assets 下的开发态：按脚本自身位置找 lang/
            try
            {
                var guid = AssetDatabase.FindAssets("NTL10n t:MonoScript");
                if (guid.Length > 0)
                {
                    var scriptPath = AssetDatabase.GUIDToAssetPath(guid[0]);
                    var dir = Path.GetDirectoryName(scriptPath);
                    paths.Add(Path.Combine(dir, "..", "lang", "zh-Hans.po").Replace('\\', '/'));
                }
            }
            catch { }

            // 3) 兜底：固定包名
            paths.Add(Path.Combine(Application.dataPath, "..", "Packages", "com.123cy321.nontoon-converter", "lang", "zh-Hans.po"));
            return paths;
        }

        // 与 ShaderCore 的 POParser 保持同样宽松的格式约定（允许 # 注释、忽略续行）
        private static void Parse(string path, Dictionary<string, string> destination)
        {
            var regId = new Regex("^\\s*msgid\\s*\"(.*)\"\\s*$");
            var regStr = new Regex("^\\s*msgstr\\s*\"(.*)\"\\s*$");
            var key = "";
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("\"") || line.StartsWith("#")) continue;
                var id = regId.Match(line);
                if (id.Success) { key = id.Groups[1].Value; continue; }
                var str = regStr.Match(line);
                if (str.Success) { destination[key] = str.Groups[1].Value; continue; }
            }
        }
    }
}
