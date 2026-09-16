// [NT-DIAG] 诊断 dump：NonToon.scshader 到底有没有被导入、叫什么名、导入时报了什么。
//
// 注意：这是**人工阅读的诊断输出**，不是判定性验证。但下面「着色器没被导入」这一条
// 属于硬失败（`Shader.Find` 为 null 会让所有下游验证失去意义），所以那种情况会 exit 1。
// 另外：脚本会**主动强制重导**再返回，所以即使第一次 Shader.Find 是 null 也能救回来。
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTDiag
{
    // [NT-TEST-FIX] 硬编码绝对路径 → 相对当前工程
    private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntdiag.txt"); } }
    // ⚠️ 别把这个常量命名为 Path —— 会遮蔽 System.IO.Path，导致 Path.Combine 编译不过（踩过）
    private const string ShaderAssetPath = "Packages/jp.lilxyzw.nontoon/Shaders/NonToon.scshader";

    public static void Run()
    {
        try { if (File.Exists(Out)) File.Delete(Out); } catch { }
        var sb = new StringBuilder();
        var log = new StringBuilder();
        Application.logMessageReceived += (msg, stack, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Warning)
                log.AppendLine(type + " | " + msg);
        };
        bool ok = false;
        try
        {
            sb.AppendLine("== 运行信息 ==");
            sb.AppendLine("时间 = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("工程 = " + Directory.GetCurrentDirectory());
            sb.AppendLine("Unity = " + Application.unityVersion);
            sb.AppendLine();
            sb.AppendLine("file exists      = " + File.Exists(ShaderAssetPath));
            sb.AppendLine("AssetDatabase path exists = " + (AssetDatabase.LoadMainAssetAtPath(ShaderAssetPath) != null));
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAssetPath);
            sb.AppendLine("LoadAssetAtPath<Shader> = " + (shader == null ? "null" : shader.name));
            sb.AppendLine("Shader.Find(NonToon)    = " + (Shader.Find("NonToon") == null ? "null" : "ok"));

            sb.AppendLine("--- 强制重导 ---");
            AssetDatabase.ImportAsset(ShaderAssetPath, ImportAssetOptions.ForceUpdate);
            shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAssetPath);
            sb.AppendLine("after import = " + (shader == null ? "null" : shader.name));
            var found = Shader.Find("NonToon");
            sb.AppendLine("Shader.Find(NonToon) = " + (found == null ? "null" : "ok"));
            ok = found != null;
            if (shader != null)
            {
                sb.AppendLine("propertyCount = " + shader.GetPropertyCount());
                var messages = ShaderUtil.GetShaderMessages(shader);
                sb.AppendLine("shaderMessages = " + (messages == null ? 0 : messages.Length));
                if (messages != null)
                    foreach (var m in messages) sb.AppendLine("  [" + m.severity + "] " + m.message);
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("!! EXCEPTION: " + e);
        }
        sb.AppendLine();
        sb.AppendLine(ok ? "==== 着色器已导入并可找到 ====" : "==== 着色器仍然找不到（下游验证全部无效） ====");
        File.WriteAllText(Out, sb.ToString() + "\n== 日志 ==\n" + log);
        Debug.Log("[NTDiag] done -> " + Out);
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
