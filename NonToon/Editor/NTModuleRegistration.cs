using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace jp.lilxyzw.nontoon
{
    // NonToon 的 ShaderCore 模块注册自愈。
    //
    // 背景：ShaderCore 把「每个着色器启用哪些模块」缓存在
    //   <Project>/ProjectSettings/jp.lilxyzw.shadercore.asset
    // 里，而且**只在首次导入时按着色器目录扫描一次，之后冻结**
    // （见 ShaderCore 的 ProjectSettings.GetShaderModules）。
    // 因此从官方 NonToon 升级过来时，本包新增的模块不会被注册，
    // 结果是**静默失效** —— 着色器上根本没有那些属性，也不报错。
    //
    // 这个脚本在编辑器加载时检查该缓存；若缺少本包要求的模块，
    // 就以外科方式把缺失条目补进对应 shadername 的 modules 列表
    // （不删除缓存、不重置其它设置），然后强制重导着色器。
    // 正常情况下（模块齐全或缓存尚未生成）它是空操作。
    public static class NTModuleRegistration
    {
        private static string SettingsPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../ProjectSettings/jp.lilxyzw.shadercore.asset"));

        // 本包新增、需要注册的模块 uniqueID
        private static readonly string[] RequiredModules =
        {
            "jp.lilxyzw.nontoon.shadowcolor",
            "jp.lilxyzw.nontoon.emission",
            "jp.lilxyzw.nontoon.selflight",
        };

        private static readonly string[] TargetShaders = { "nontoon-fork", "nontoon-fork-fur", "nontoon-fork-twopass" };

        private static readonly string[] ShaderAssetPaths =
        {
            "Packages/com.catandling.nontoon/Shaders/NonToon.scshader",
            "Packages/com.catandling.nontoon/Shaders/NonToonFur.scshader",
            "Packages/com.catandling.nontoon/Shaders/NonToonTwoPass.scshader",
        };

        private static readonly Regex REG_SHADERNAME = new(@"^\s*-\s*shadername:\s*(\S+)\s*$");
        private static readonly Regex REG_MODULES = new(@"^\s*modules:\s*$");
        private static readonly Regex REG_MULTI = new(@"^\s*multiModules:");
        private static readonly Regex REG_ENTRY = new(@"^\s*-\s*(\S+)\s*$");

        [InitializeOnLoadMethod]
        private static void AutoRun()
        {
            Run(false);
        }

        // 供 -executeMethod 调用做确定性验证
        public static void ForceRun()
        {
            Run(true);
        }

        private static void Run(bool verbose)
        {
            NTShaderNameCompatibility.EnsureSupported();
            try
            {
                var path = SettingsPath;
                if (!File.Exists(path))
                {
                    if (verbose) Debug.Log("[NonToon] 模块缓存尚未生成，无需处理：" + path);
                    return;
                }

                var added = AddMissingModules(path, verbose);
                if (added) ReimportShaders();
                else if (verbose) Debug.Log("[NonToon] 模块缓存已完整，无需改动。");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NonToon] ShaderCore 模块注册自愈失败：" + e.Message);
            }
        }

        // 返回是否真的改动了文件
        private static bool AddMissingModules(string path, bool verbose)
        {
            var lines = File.ReadAllLines(path).ToList();
            var changed = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var m = REG_SHADERNAME.Match(lines[i]);
                if (!m.Success) continue;
                if (!TargetShaders.Contains(m.Groups[1].Value)) continue;

                // 定位该条目块内的 modules: 与 multiModules:
                int modulesIdx = -1, multiIdx = -1;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (REG_SHADERNAME.IsMatch(lines[j])) break;      // 到了下一个 shadername
                    if (modulesIdx < 0 && REG_MODULES.IsMatch(lines[j])) modulesIdx = j;
                    if (REG_MULTI.IsMatch(lines[j])) { multiIdx = j; break; }
                }
                if (modulesIdx < 0 || multiIdx < 0)
                {
                    if (verbose) Debug.LogWarning($"[NonToon] 缓存里 {m.Groups[1].Value} 条目结构异常，跳过。");
                    continue;
                }

                var existing = new HashSet<string>();
                for (var k = modulesIdx + 1; k < multiIdx; k++)
                {
                    var e = REG_ENTRY.Match(lines[k]);
                    if (e.Success) existing.Add(e.Groups[1].Value);
                }

                var toAdd = RequiredModules.Where(x => !existing.Contains(x)).ToList();
                if (toAdd.Count == 0) continue;

                // 条目缩进与 modules: 行一致
                var indent = Regex.Match(lines[modulesIdx], @"^\s*").Value;
                lines.InsertRange(multiIdx, toAdd.Select(x => indent + "- " + x));
                changed = true;
                i = multiIdx + toAdd.Count - 1;

                Debug.Log($"[NonToon] 为 {m.Groups[1].Value} 补注册模块：{string.Join(", ", toAdd)}");
            }

            if (!changed) return false;

            File.WriteAllLines(path, lines);
            return true;
        }

        private static void ReimportShaders()
        {
            foreach (var p in ShaderAssetPaths)
                if (File.Exists(p)) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Debug.Log("[NonToon] 已强制重导 NonToon 着色器以应用补注册的模块。");
        }
    }
}
