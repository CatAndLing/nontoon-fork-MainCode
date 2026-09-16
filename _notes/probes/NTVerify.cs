// [NT-DIAG] 列出 NonToon / NonToonFur 里所有匹配关键词的属性名（不按猜测的完整名探测）。
//
// [NT-TEST-FIX] 原来没有 EnsureShader 兜底，也没删旧产物：包目录刚被"先删后拷"同步过时，
// ScriptedImporter 可能还没跑，`Shader.Find` 返回 null → 报告里只有"未找到"，
// 而退出码照样 0（历史产物 ntverify.txt 就是这样）。现在：强制导入 + 找不到就 exit 1。
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTVerify
{
    private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntverify.txt"); } }

    private static Shader EnsureShader(string name)
    {
        var sh = Shader.Find(name);
        if (sh != null) return sh;
        var fileName = name == "nontoon-fork" ? "NonToon" : "NonToonFur";
        AssetDatabase.ImportAsset("Packages/com.catandling.nontoon/Shaders/" + fileName + ".scshader",
            ImportAssetOptions.ForceUpdate);
        return Shader.Find(name);
    }

    public static void Dump()
    {
        try { if (File.Exists(Out)) File.Delete(Out); } catch { }
        var sb = new StringBuilder();
        string[] shaders = { "nontoon-fork", "nontoon-fork-fur" };
        string[] keys = { "Emission", "Specular", "F0", "Light", "Shadow", "Outline", "Boost", "Parallax", "Direction", "Nearer", "Enable" };

        sb.AppendLine("== 运行信息 ==");
        sb.AppendLine("时间 = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("工程 = " + Directory.GetCurrentDirectory());
        sb.AppendLine();

        int missing = 0;
        foreach (var name in shaders)
        {
            var sh = EnsureShader(name);
            if (sh == null) { sb.AppendLine($"{name}: ❌ 未找到（包没同步进来 / 着色器没导入）\n"); missing++; continue; }

            int n = ShaderUtil.GetPropertyCount(sh);
            var all = new List<string>();
            for (int i = 0; i < n; i++) all.Add(ShaderUtil.GetPropertyName(sh, i));

            sb.AppendLine($"=== {name} : {n} 个属性 ===");
            foreach (var k in keys)
            {
                var hit = all.FindAll(p => p.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                sb.AppendLine($"  --- 含 \"{k}\" 的 {hit.Count} 个 ---");
                foreach (var p in hit) sb.AppendLine("      " + p);
            }
            sb.AppendLine();
        }

        sb.AppendLine(missing == 0 ? "==== 两个着色器都找到了 ====" : "==== 有 " + missing + " 个着色器没找到 ====");
        File.WriteAllText(Out, sb.ToString());
        Debug.Log("[NTVerify] dumped -> " + Out + " (missing=" + missing + ")");
        EditorApplication.Exit(missing == 0 ? 0 : 1);
    }
}
