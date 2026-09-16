// [NT-TEST] 只存在于验证工程。
//
// 复现并验证「ShaderCore 内置键（__*）不汉化」这个问题：
//   用户工程里的 ShaderCore 0.1.12 只带 en-US.po / ja-JP.po，没有 zh-Hans.po。
//   L10n.L() 对 __ 键先查 coreTransration，而 LoadLocalizationDirect 找不到 <语言>.po 时会
//   回落 en-US.po → __ 键在 core 表里命中英文并直接返回，我们自己包的 po 轮不到。
//
// 步骤：
//   ① 语言设成 zh-Hans
//   ② **负向对照**：把 ShaderCore 的 zh-Hans.po 挪走 → __Texture 必须是英文（复现 bug）
//   ③ 调 NonToon 的 NTShaderCoreLocalization.Ensure() 补文件
//   ④ 再查 → __Texture 必须是「贴图」，并且我们自己的键（Shadow Bias）仍是中文
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTL10nProbe
{
    // [NT-TEST-FIX] 硬编码绝对路径 → 相对当前工程，避免换工程跑时覆盖验证工程的报告
    private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntl10n.txt"); } }
    private const string Po = "Packages/jp.lilxyzw.shadercore/lang/zh-Hans.po";
    private const string Bak = Po + ".probebak";

    private static readonly StringBuilder Sb = new StringBuilder();
    private static int Fail;

    public static void Run()
    {
        try
        {
            SetLanguage("zh-Hans");
            Sb.AppendLine("Language 设为            = " + GetLanguage());

            // ② 负向对照：把 ShaderCore 自己的 zh-Hans.po 挪走
            var hadOwn = File.Exists(Po);
            Sb.AppendLine("ShaderCore 原本带 zh-Hans.po = " + hadOwn);
            if (hadOwn && File.Exists(Bak)) File.Delete(Bak);
            if (hadOwn) File.Move(Po, Bak);
            if (hadOwn) AssetDatabase.Refresh();

            Reload();
            var beforeTexture = L("__Texture");
            var beforeModules = L("__SelectModules");
            var beforeOurKey = L("Shadow Bias");
            Sb.AppendLine();
            Sb.AppendLine("== ② 负向对照（补全之前） ==");
            Sb.AppendLine("  L(__Texture)       = " + beforeTexture);
            Sb.AppendLine("  L(__SelectModules) = " + beforeModules);
            Sb.AppendLine("  L(Shadow Bias)     = " + beforeOurKey + "  ← 我们自己的键（走着色器表）");
            Check("没补文件时 __ 键确实是英文（bug 复现出来）", IsAscii(beforeTexture) && beforeTexture == "Texture", beforeTexture);

            // ③ 补全
            Sb.AppendLine();
            Sb.AppendLine("== ③ 调 NTShaderCoreLocalization.Ensure() ==");
            var ensured = InvokeEnsure();
            Sb.AppendLine("  Ensure() 返回 = " + ensured);
            Sb.AppendLine("  文件已生成    = " + File.Exists(Po));
            Check("补全后 zh-Hans.po 存在", File.Exists(Po), Po);

            Reload();
            var afterTexture = L("__Texture");
            var afterModules = L("__SelectModules");
            var afterRoughness = L("__Roughness");
            var afterCutoff = L("__Cutoff");
            var afterSharedMask = L("__SharedMask");
            var afterSharedGradients = L("__SharedGradients");
            var afterNormalMap = L("__NormalMap");
            var afterRoughInNormal = L("__NormalMapWithRoughness");
            var afterMain = L("__Main");
            var afterRenderingMode = L("__RenderingMode");
            var afterCreateTexture = L("__CreateTexture");
            var afterOurKey = L("Shadow Bias");

            Sb.AppendLine();
            Sb.AppendLine("== ④ 补全之后（用户截图里那些英文项） ==");
            Sb.AppendLine("  L(__Main)                   = " + afterMain);
            Sb.AppendLine("  L(__RenderingMode)          = " + afterRenderingMode);
            Sb.AppendLine("  L(__SelectModules)          = " + afterModules);
            Sb.AppendLine("  L(__Texture)                = " + afterTexture);
            Sb.AppendLine("  L(__SharedMask)             = " + afterSharedMask);
            Sb.AppendLine("  L(__SharedGradients)        = " + afterSharedGradients);
            Sb.AppendLine("  L(__NormalMap)              = " + afterNormalMap);
            Sb.AppendLine("  L(__NormalMapWithRoughness) = " + afterRoughInNormal);
            Sb.AppendLine("  L(__Roughness)              = " + afterRoughness);
            Sb.AppendLine("  L(__Cutoff)                 = " + afterCutoff);
            Sb.AppendLine("  L(__CreateTexture)          = " + afterCreateTexture);
            Sb.AppendLine("  L(Shadow Bias)              = " + afterOurKey);

            Check("__Main 变中文", !IsAscii(afterMain), afterMain);
            Check("__RenderingMode 变中文", !IsAscii(afterRenderingMode), afterRenderingMode);
            Check("__SelectModules 变中文", !IsAscii(afterModules), afterModules);
            Check("__Texture 变中文（截图里的 Textrue）", !IsAscii(afterTexture), afterTexture);
            Check("__SharedMask 变中文", !IsAscii(afterSharedMask), afterSharedMask);
            Check("__SharedGradients 变中文", !IsAscii(afterSharedGradients), afterSharedGradients);
            Check("__NormalMap 变中文", !IsAscii(afterNormalMap), afterNormalMap);
            Check("__NormalMapWithRoughness 变中文", !IsAscii(afterRoughInNormal), afterRoughInNormal);
            Check("__Roughness 变中文", !IsAscii(afterRoughness), afterRoughness);
            Check("__Cutoff 变中文", !IsAscii(afterCutoff), afterCutoff);
            Check("__CreateTexture 变中文", !IsAscii(afterCreateTexture), afterCreateTexture);
            Check("自己的键（Shadow Bias）也仍是中文", afterOurKey == "阴影偏移", afterOurKey);

            // 再跑一次 Ensure：已存在时不能覆盖
            var beforeSecond = File.ReadAllText(Po);
            InvokeEnsure();
            Check("已存在时不覆盖（幂等）", File.ReadAllText(Po) == beforeSecond, "文件内容未变");

            // 上游原本自带 zh-Hans.po 的场景：把它放回去，Ensure 必须让位
            File.Delete(Po);
            if (hadOwn) File.Move(Bak, Po);
            AssetDatabase.Refresh();
            var kept = InvokeEnsure();
            Check("上游自带 zh-Hans.po 时不覆盖", hadOwn ? File.ReadAllText(Po).Contains("诺圣晴空") || !File.ReadAllText(Po).Contains("[NT-L10N]") : true,
                hadOwn ? "保留了上游文件" : "（上游本来就没有，跳过该项）");
            Sb.AppendLine("  Ensure() 返回值 = " + kept);

            // 清理：这次真的删掉（验证工程用不着留着），恢复原状
            if (!hadOwn && File.Exists(Po)) File.Delete(Po);
            if (File.Exists(Bak)) File.Delete(Bak);
            AssetDatabase.Refresh();
        }
        catch (Exception e)
        {
            Sb.AppendLine("!! PROBE EXCEPTION: " + e);
            Fail++;
        }

        Sb.AppendLine();
        Sb.AppendLine(Fail == 0 ? "==== 全部通过 ====" : "==== 有 " + Fail + " 项不符 ====");
        File.WriteAllText(Out, Sb.ToString());
        Debug.Log("[NTL10nProbe] done -> " + Out + " (fail=" + Fail + ")");
        // [NT-TEST-FIX] Environment.ExitCode 在 Unity 2022.3 batchmode 下实测被忽略（NTExitProbe 验过）
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    // ShaderCore 的 SCL10n 是 public，但 Assembly-CSharp-Editor 没有引用它的 asmdef，
    // 所以这里走反射（探针专用，不影响发布包）。
    private static void LoadTable(string path)
    {
        FindType("jp.lilxyzw.shadercore.SCL10n")?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { path });
    }

    private static string L(string key)
    {
        return FindType("jp.lilxyzw.shadercore.SCL10n")?.GetMethod("L", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { key }) as string;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok) Fail++;
        Sb.AppendLine("  " + (ok ? "[OK]" : "[FAIL]") + " " + name + " -> " + detail);
    }

    private static bool IsAscii(string s)
    {
        return !string.IsNullOrEmpty(s) && s.All(c => c < 128);
    }

    // 让 ShaderCore 重新读表
    private static void Reload()
    {
        var l10n = FindType("jp.lilxyzw.shadercore.L10n");
        l10n?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        var shader = Shader.Find("NonToon");
        var path = shader != null ? AssetDatabase.GetAssetPath(shader) : null;
        LoadTable(path);
    }

    private static string GetLanguage()
    {
        var settings = FindType("jp.lilxyzw.shadercore.Settings");
        var instance = settings?.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
        return settings?.GetField("language", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string;
    }

    private static void SetLanguage(string code)
    {
        var settings = FindType("jp.lilxyzw.shadercore.Settings");
        var instance = settings?.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
        settings?.GetField("language", BindingFlags.Public | BindingFlags.Instance)?.SetValue(instance, code);
        FindType("jp.lilxyzw.shadercore.L10n")?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
    }

    private static object InvokeEnsure()
    {
        var type = FindType("jp.lilxyzw.nontoon.NTShaderCoreLocalization");
        if (type == null) { Sb.AppendLine("  !! 找不到 NTShaderCoreLocalization"); Fail++; return null; }
        return type.GetMethod("Ensure", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { true });
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }
}
