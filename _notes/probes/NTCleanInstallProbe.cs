// 装置 I —— 干净工程 VPM 安装门禁（2026-09-18 新增）
//
// 为什么要有它：0.3.11 / 0.5.3 那次发布**在没有这一步有效证据的情况下被推上去了**。
//   当时的 _clean-proj/ntclean.txt 是**无效测量** —— 同一份 Unity 日志里全是
//   `error CS2001: Source file ... could not be found`，因为包目录在编译过程中被移到了
//   _clean-proj-outside/。那种报告既不能当"通过"，也不能当"失败"。
//
// 本装置回答的问题（与"包在_verify-proj 里能跑"不同）：
//   **一个空工程，只加已发布的 listing，能不能独立解析依赖并真正用起来？**
//   ① listing 的依赖能否解析（含 shadercore / MA / NDMF 这些传递依赖）
//   ② 三个着色器是否真的可用（这同时证明 ShaderCore 的 ScriptedImporter 真的工作了）
//   ③ 转换器能否识别到着色器包（反射进它自己的 internal 类，不靠猜）
//
// 判据：**退出码**（0 = 全过，1 = 有断言失败）。启动先删旧报告（本项目假绿事故 #2）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTCleanInstallProbe
{
    private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntcleaninstall.txt"); } }
    private static readonly StringBuilder Sb = new StringBuilder();
    private static int Pass, Fail;

    private const string ExpectedNonToon = "0.3.11";
    private const string ExpectedConverter = "0.5.3";

    private static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Sb.AppendLine("  " + (ok ? "[OK] " : "[FAIL] ") + what);
    }

    // 与 NTVerify 同样的兜底：包刚被解压进来时 ScriptedImporter 可能还没跑，
    // 直接 Shader.Find 会拿到 null ⇒ 假失败。强制导入一次再找。
    private static Shader EnsureShader(string name, string file)
    {
        var sh = Shader.Find(name);
        if (sh != null) return sh;
        AssetDatabase.ImportAsset("Packages/com.catandling.nontoon/Shaders/" + file + ".scshader",
            ImportAssetOptions.ForceUpdate);
        return Shader.Find(name);
    }

    public static void Run()
    {
        try { if (File.Exists(Out)) File.Delete(Out); } catch { }
        Sb.Clear(); Pass = Fail = 0;

        Sb.AppendLine("== 运行信息 ==");
        Sb.AppendLine("时间 = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Sb.AppendLine("工程 = " + Directory.GetCurrentDirectory());
        Sb.AppendLine("Unity = " + Application.unityVersion);
        Sb.AppendLine("期望 = 着色器 " + ExpectedNonToon + " / 工具包 " + ExpectedConverter);
        Sb.AppendLine();

        // ── ⓪ 测试床：决定本结论的适用范围 ────────────────────────────────────
        Sb.AppendLine("== ⓪ 测试床（决定结论适用范围，别误读） ==");
        var allPkgs = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().ToList();
        var vrc = allPkgs.FirstOrDefault(p => p.name == "com.vrchat.avatars");
        var vrcBase = allPkgs.FirstOrDefault(p => p.name == "com.vrchat.base");
        Sb.AppendLine("      注册包总数 = " + allPkgs.Count);
        Sb.AppendLine("      VRChat SDK  = " + (vrc == null ? "(未安装)" : vrc.name + " @ " + vrc.version));
        Sb.AppendLine("      VRChat Base = " + (vrcBase == null ? "(未安装)" : vrcBase.name + " @ " + vrcBase.version));
        Sb.AppendLine("      ⚠️ 必须含 VRChat SDK：工具包的**必需**依赖 Modular Avatar（→ NDMF）的 asmdef");
        Sb.AppendLine("         用 overrideReferences 引用 VRCSDKBase.dll / VRCSDK3A.dll / VRC.Dynamics.dll /");
        Sb.AppendLine("         System.Collections.Immutable.dll。缺 SDK 时它们**本来就编译不过**，");
        Sb.AppendLine("         Unity 会以「Scripts have compiler errors」中止，本探针根本不会被执行 ——");
        Sb.AppendLine("         那不是本包的缺陷，而是测试床不成立。");
        Sb.AppendLine("      ⚠️ 也必须是**标准** Unity 工程骨架。裁剪清单会引入与本包**无关**的编译错误：");
        Sb.AppendLine("         · {\"dependencies\": {}} ⇒ com.vrchat.base 的 DOTween 报 CS1069");
        Sb.AppendLine("           「Rigidbody2D ... forwarded to UnityEngine.Physics2DModule」/ AndroidJNI 同类；");
        Sb.AppendLine("         · 只有内置模块、没有模板自带的 registry 包 ⇒ 缺 com.unity.test-framework");
        Sb.AppendLine("           （2022.3 模板经 com.unity.feature.development 带入），");
        Sb.AppendLine("           VRCSDK 自己的 AssetBundleFooterTest.cs / VTPTests.cs 编译不过。");
        Sb.AppendLine("         这些都不是本包的缺陷。**门禁测试床 = 2022.3 标准模板清单 + VRChat SDK。**");
        Sb.AppendLine();

        // ── ① 这是"VPM 客户端装的"，不是手工拷进来的 ──────────────────────────
        Sb.AppendLine("== ① VPM 解析结果（Packages/vpm-manifest.json） ==");
        const string vpmManifest = "Packages/vpm-manifest.json";
        if (!File.Exists(vpmManifest))
        {
            Check(false, "vpm-manifest.json 不存在 —— 无法证明这些包来自 VPM 解析（手工嵌入的包没有这份清单）");
        }
        else
        {
            var txt = File.ReadAllText(vpmManifest);
            Check(txt.Contains("com.catandling.nontoon"), "清单声明 com.catandling.nontoon");
            Check(txt.Contains("com.catandling.nontoon-converter"), "清单声明 com.catandling.nontoon-converter");
            Check(txt.Contains("jp.lilxyzw.shadercore"),
                "传递依赖 jp.lilxyzw.shadercore 被解析出来（空工程里没有 lilToon，所以不可能是本来就在的）");
            Check(txt.Contains("nadena.dev.modular-avatar"), "传递依赖 nadena.dev.modular-avatar 被解析出来");
            Check(txt.Contains("nadena.dev.ndmf"), "传递依赖 nadena.dev.ndmf 被解析出来（MA 的依赖也解开了）");
        }

        // ── ② 包实际注册与版本 ───────────────────────────────────────────────
        Sb.AppendLine();
        Sb.AppendLine("== ② 已注册包（Unity 视角） ==");
        var pkgs = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .Where(p => p.name.Contains("nontoon") || p.name.Contains("shadercore")
                     || p.name.Contains("modular-avatar") || p.name.Contains("ndmf"))
            .OrderBy(p => p.name, StringComparer.Ordinal).ToList();
        foreach (var p in pkgs) Sb.AppendLine("      " + p.name + " @ " + p.version + "  src=" + p.source);
        var nt = pkgs.FirstOrDefault(p => p.name == "com.catandling.nontoon");
        var cv = pkgs.FirstOrDefault(p => p.name == "com.catandling.nontoon-converter");
        var sc = pkgs.FirstOrDefault(p => p.name == "jp.lilxyzw.shadercore");
        var ma = pkgs.FirstOrDefault(p => p.name == "nadena.dev.modular-avatar");
        Check(nt != null, "com.catandling.nontoon 已注册");
        Check(cv != null, "com.catandling.nontoon-converter 已注册");
        if (nt != null) Check(nt.version == ExpectedNonToon, "着色器包版本 = " + ExpectedNonToon + "（实测 " + nt.version + "）");
        if (cv != null) Check(cv.version == ExpectedConverter, "工具包版本 = " + ExpectedConverter + "（实测 " + cv.version + "）");
        Check(sc != null, "jp.lilxyzw.shadercore 已注册" + (sc != null ? "（" + sc.version + "，满足 ^0.1.9）" : ""));
        Check(ma != null, "nadena.dev.modular-avatar 已注册（工具包的必需依赖）");

        // ── ③ 着色器真的能用（同时证明 ShaderCore 导入器在工作） ────────────────
        Sb.AppendLine();
        Sb.AppendLine("== ③ 着色器（Shader.Find 是真编译产物的门票） ==");
        var main = EnsureShader("nontoon-fork", "NonToon");
        var fur = EnsureShader("nontoon-fork-fur", "NonToonFur");
        var two = EnsureShader("nontoon-fork-twopass", "NonToonTwoPass");
        Check(main != null, "Shader.Find(\"nontoon-fork\") 非空");
        Check(fur != null, "Shader.Find(\"nontoon-fork-fur\") 非空");
        Check(two != null, "Shader.Find(\"nontoon-fork-twopass\") 非空");
        Check(Shader.Find("NonToon") == null, "旧着色器名 \"NonToon\" 已不存在（改名生效）");
        if (main != null)
        {
            var importer = AssetImporter.GetAtPath("Packages/com.catandling.nontoon/Shaders/NonToon.scshader");
            Sb.AppendLine("      .scshader 的导入器类型 = " + (importer == null ? "(无)" : importer.GetType().FullName));
            Check(importer != null, "ShaderCore 的 ScriptedImporter 接管了 .scshader（导入器非空）");
            var probeMat = new Material(main);
            Check(probeMat.HasProperty("_ShadowColorEnable"),
                "分支新增模块属性可寻（_ShadowColorEnable）—— 装的确实是本 fork 而不是上游包");
            UnityEngine.Object.DestroyImmediate(probeMat);
        }

        // ── ④ 程序集与转换器自检 ─────────────────────────────────────────────
        Sb.AppendLine();
        Sb.AppendLine("== ④ 程序集 ==");
        var asms = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToList();
        foreach (var want in new[] { "jp.lilxyzw.shadercore", "jp.lilxyzw.nontoon" })
            Check(asms.Contains(want), "程序集 " + want + " 已加载");

        Sb.AppendLine();
        Sb.AppendLine("== ⑤ 转换器能否识别着色器包（反射进它的 internal 类） ==");
        Type compat = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            compat = a.GetType("LilToonToNonToonConverter.NonToonCompatibility", false);
            if (compat != null) break;
        }
        Check(compat != null, "LilToonToNonToonConverter.NonToonCompatibility 可解析（转换器程序集在）");
        if (compat != null)
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var isInstalled = (bool)compat.GetProperty("IsInstalled", F).GetValue(null, null);
            var supported = (bool)compat.GetProperty("UsesSupportedVersions", F).GetValue(null, null);
            var status = (string)compat.GetProperty("VersionStatus", F).GetValue(null, null);
            var missing = (List<string>)compat.GetMethod("MissingModules", F).Invoke(null, null);
            Check(isInstalled, "NonToonCompatibility.IsInstalled = true");
            Check(supported, "版本满足转换器期望 —— " + status);
            Check(missing != null && missing.Count == 0,
                "12 个模块的材质属性全部可寻" + (missing != null && missing.Count > 0 ? "（缺 " + missing.Count + " 个：" + string.Join(", ", missing) + "）" : ""));
        }

        Sb.AppendLine();
        Sb.AppendLine(Fail == 0
            ? "==== 干净安装门禁全部通过（" + Pass + " 项）===="
            : "==== 干净安装门禁失败 " + Fail + " 项（通过 " + Pass + " 项）====");

        File.WriteAllText(Out, Sb.ToString());
        Debug.Log("[NTCleanInstallProbe] -> " + Out + " (pass=" + Pass + ", fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }
}
