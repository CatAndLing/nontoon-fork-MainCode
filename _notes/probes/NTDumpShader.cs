// [NT-DIAG] 把 ShaderCore **生成后的** NonToon 源码 dump 出来，用于定位 HLSL 编译错误。
//
// 用途：`_verify-A.log` 里的 `Shader error ... at line NNNNN (on d3d11)` 里的行号是
// **生成源码**的行号，不是我们手写文件的行号。要看清那一行到底是什么，就得拿生成源码。
// ShaderCore 的 SCShaderImporter 会把生成结果作为 **TextAsset 子资产**塞进 .scshader，
// 所以 `AssetDatabase.LoadAllAssetsAtPath` 就能取到（等价于 Inspector 上的 "Output Text"）。
//
// 输出：ntshader-source.txt（全文）+ 控制台打印含 NTRTS 的行号区间。
// 这是**人工阅读**用的诊断工具，永远 exit 0。
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTDumpShader
{
    const string ShaderPath = "Packages/com.catandling.nontoon/Shaders/NonToon.scshader";
    const string FurPath    = "Packages/com.catandling.nontoon/Shaders/NonToonFur.scshader";

    public static void Run()
    {
        var sb = new StringBuilder();
        Dump(ShaderPath, sb);
        Dump(FurPath, sb);
        var outPath = Path.Combine(Directory.GetCurrentDirectory(), "ntshader-source.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("[NTDumpShader] -> " + outPath);
        EditorApplication.Exit(0);
    }

    static void Dump(string assetPath, StringBuilder sb)
    {
        sb.AppendLine("############################################################");
        sb.AppendLine("# " + assetPath);
        sb.AppendLine("############################################################");
        if (!File.Exists(assetPath)) { sb.AppendLine("(不存在)"); return; }
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        var text = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<TextAsset>().FirstOrDefault();
        if (text == null) { sb.AppendLine("(没有 TextAsset 子资产 —— 导入失败？)"); return; }

        var lines = text.text.Replace("\r\n", "\n").Split('\n');
        sb.AppendLine("总行数 = " + lines.Length);
        sb.AppendLine();
        sb.AppendLine("== 含 NTRTS 的行（我们的实时 PCSS 相位） ==");
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("NTRTS")) sb.AppendLine((i + 1) + ": " + lines[i]);
        sb.AppendLine();
        sb.AppendLine("== 含 _ShadowMapTexture 的行 ==");
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("_ShadowMapTexture")) sb.AppendLine((i + 1) + ": " + lines[i]);
        sb.AppendLine();
        sb.AppendLine("== 全文 ==");
        for (int i = 0; i < lines.Length; i++) sb.AppendLine((i + 1) + ": " + lines[i]);
    }
}
