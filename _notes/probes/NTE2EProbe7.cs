// E2E 探针 v7 —— 验证「本包自己的 lang 目录也补当前语言」这个修复
// 场景：全新安装、中文 Windows 默认语言 = zh-CN、本包只带 zh-Hans.po。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTE2EProbe7
{
    const string OUT = "e2e-report7.txt";
    static readonly StringBuilder sb = new StringBuilder();
    static int pass, fail;
    static void L(string s) { sb.AppendLine(s); Debug.Log("[E2E7] " + s); }
    static void Ok(string s) { pass++; L("  [OK]   " + s); }
    static void Bad(string s) { fail++; L("  [FAIL] " + s); }
    static void Check(bool c, string s) { if (c) Ok(s); else Bad(s); }
    static void Info(string s) { L("  [info] " + s); }

    static bool HasCJK(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }
    static Type T(string q, string asm)
    {
        var t = Type.GetType(q + ", " + asm);
        if (t != null) return t;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var x in ts) if (x.Name == q.Split('.').Last()) return x;
        }
        return null;
    }

    public static void Run()
    {
        try { Body(); } catch (Exception e) { Bad("探针异常 " + e); }
        L("");
        L(fail == 0 ? "==== v7 全部通过（" + pass + "）====" : "==== v7 失败 " + fail + " 项（通过 " + pass + "）====");
        try { File.WriteAllText(OUT, sb.ToString()); } catch { }
        EditorApplication.Exit(fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        L("=== E2E 探针 v7 === " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var langDirs = new List<string>();
        string pkg = "Packages/com.catandling.nontoon";
        langDirs.Add(pkg + "/Shaders/lang");
        foreach (var d in Directory.GetDirectories(pkg + "/Shaders/Modules"))
        {
            var l = d.Replace('\\', '/') + "/lang";
            if (Directory.Exists(l)) langDirs.Add(l);
        }
        Info("本包共有 " + langDirs.Count + " 个 lang 目录");

        // 【修复前状态】探针运行前，有多少目录带 zh-CN.po
        int beforeCn = langDirs.Count(d => File.Exists(d + "/zh-CN.po"));
        int beforeHans = langDirs.Count(d => File.Exists(d + "/zh-Hans.po"));
        Info($"运行前：带 zh-Hans.po 的 {beforeHans} 个，带 zh-CN.po 的 {beforeCn} 个");

        // 语言
        var settingsType = T("jp.lilxyzw.shadercore.Settings", "jp.lilxyzw.shadercore");
        var inst = settingsType?.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
        var langField = settingsType?.GetField("language", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var current = langField?.GetValue(inst) as string;
        Info("当前 ShaderCore 语言 = " + current);
        Check(current == "zh-CN", "当前语言是 zh-CN（中文 Windows 的默认值，也是用户开箱即见的状态）");

        // 测量函数
        var l10nType = T("jp.lilxyzw.shadercore.L10n", "jp.lilxyzw.shadercore");
        var scl10nType = T("jp.lilxyzw.shadercore.SCL10n", "jp.lilxyzw.shadercore");
        var clearM = l10nType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
        var loadM = scl10nType?.GetMethod("Load", new[] { typeof(string) });
        var Lm = scl10nType?.GetMethod("L", new[] { typeof(string) });

        var sh = Shader.Find("nontoon-fork");
        var mat = new Material(sh);
        var props = MaterialEditor.GetMaterialProperties(new UnityEngine.Object[] { mat });
        var raw = props.Select(p => p.displayName).ToArray();

        Func<int> measure = () =>
        {
            clearM?.Invoke(null, null);
            var contexts = new List<string> { pkg + "/Shaders/NonToon.scshader" };
            var reg = "ProjectSettings/jp.lilxyzw.shadercore.asset";
            if (File.Exists(reg))
                contexts.AddRange(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(reg), "jp\\.lilxyzw\\.nontoon\\.[a-z0-9]+")
                    .Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value).Distinct());
            var best = new HashSet<string>();
            foreach (var ctx in contexts)
            {
                try { loadM?.Invoke(null, new object[] { ctx }); } catch { }
                foreach (var d in raw)
                {
                    var v = (string)Lm?.Invoke(null, new object[] { d });
                    if (v != d && HasCJK(v)) best.Add(d);
                }
            }
            return best.Count;
        };

        int before = measure();
        Info($"【修复前基准】当前文件布局下，165 条里显示中文的 {before} 条");

        // 触发自愈（真实流程里由 [InitializeOnLoadMethod] + delayCall 自动调用）
        var fixType = T("jp.lilxyzw.nontoon.NTShaderCoreLocalization", "jp.lilxyzw.nontoon");
        Check(fixType != null, "找到 NTShaderCoreLocalization");
        var ensure = fixType?.GetMethod("Ensure", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(ensure != null, "找到 Ensure 方法");
        try { var r = ensure.Invoke(null, new object[] { true }); Info("Ensure(true) 返回 " + r); }
        catch (Exception e) { Bad("Ensure 抛异常：" + (e.InnerException?.Message ?? e.Message)); }

        int afterCn = langDirs.Count(d => File.Exists(d + "/zh-CN.po"));
        Info($"运行后：带 zh-CN.po 的 {afterCn} 个（补了 {afterCn - beforeCn} 个）");
        Check(afterCn == langDirs.Count, $"所有 {langDirs.Count} 个 lang 目录都有了 zh-CN.po");

        int after = measure();
        Info($"【修复后 / 语言=zh-CN】显示中文的 {after} 条（此前 {before} 条）");
        Check(after > before + 80, $"中文覆盖率显著提升（{before} → {after}）");

        // 同一个工程状态下再测一次 zh-Hans，用来区分「修复的极限」和「语言码本身的影响」
        langField?.SetValue(inst, "zh-Hans");
        int hans = measure();
        Info($"【同一状态下 / 语言=zh-Hans】显示中文的 {hans} 条");
        langField?.SetValue(inst, "zh-CN");
        clearM?.Invoke(null, null);
        Info($"→ 两者差值 {hans - after} 条（若为 0，说明补文件已完全等价于原生 zh-Hans）");
        Check(hans - after <= 2, $"补齐 <语言>.po 后的覆盖率与原生 zh-Hans 一致（差 {hans - after}）");

        // 顺带确认 core 键也还在
        loadM?.Invoke(null, new object[] { pkg + "/Shaders/NonToon.scshader" });
        var tex = (string)Lm?.Invoke(null, new object[] { "__Texture" });
        Info("L(\"__Texture\") = " + tex);
        Check(HasCJK(tex), "ShaderCore 核心键仍是中文");

        UnityEngine.Object.DestroyImmediate(mat);
    }
}
