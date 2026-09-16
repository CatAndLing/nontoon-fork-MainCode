// [NT-DIAG] 目的：确认 batchmode（**不带 -nographics**）能否渲染到 RenderTexture 并回读像素
// —— 这决定自阴影烘焙用「GPU 相机」还是「CPU 软件光栅化」。
//
// 结论（历史实测）：批处理下回读全黑 ⇒ 烘焙走 CPU 光栅化。所以本探针在批处理里
// **预期就是失败**的。既然预期失败，就把失败如实反映成退出码 1（不再打印 ⚠️ 却 exit 0），
// 并且**不要**把它放进发版门禁：它属于"环境能力探测"，不是"产品正确性验证"。
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTGpuProbe
{
    // [NT-TEST-FIX] 硬编码绝对路径 → 相对当前工程
    private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntgpu.txt"); } }

    public static void Run()
    {
        try { if (File.Exists(Out)) File.Delete(Out); } catch { }
        var sb = new StringBuilder();
        bool ok = false;
        GameObject camGo = null;
        GameObject quad = null;
        RenderTexture rt = null;
        try
        {
            sb.AppendLine("== 运行信息 ==");
            sb.AppendLine("时间 = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("工程 = " + Directory.GetCurrentDirectory());
            sb.AppendLine();
            sb.AppendLine("systemInfo.graphicsDeviceType = " + SystemInfo.graphicsDeviceType);
            sb.AppendLine("systemInfo.graphicsShaderLevel = " + SystemInfo.graphicsShaderLevel);

            var unlit = Shader.Find("Unlit/Color");
            sb.AppendLine("Shader.Find(Unlit/Color) = " + (unlit == null ? "null" : "ok"));
            if (unlit == null) { sb.AppendLine("❌ 找不到 Unlit/Color，无法继续"); }
            else
            {
                rt = RenderTexture.GetTemporary(64, 64, 24, RenderTextureFormat.ARGB32);
                camGo = new GameObject("NTGpuProbeCam");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 1f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rt;
                cam.transform.position = Vector3.zero;
                cam.transform.rotation = Quaternion.identity;   // 看向 +Z

                quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.position = new Vector3(0, 0, 2f);
                quad.transform.rotation = Quaternion.Euler(0, 180, 0); // 面向 -Z（朝相机）
                var mat = new Material(unlit);
                mat.SetColor("_Color", Color.white);
                quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                var center = tex.GetPixel(32, 32);
                var corner = tex.GetPixel(2, 2);
                sb.AppendLine("中心像素 = " + center + "（期望接近白）");
                sb.AppendLine("角落像素 = " + corner + "（期望接近黑）");
                ok = center.r > 0.5f && corner.r < 0.5f;
                sb.AppendLine(ok
                    ? "✅ batchmode 下 GPU 渲染 + 回读可用 → 烘焙可以用「正交相机 + 深度着色器」"
                    : "⚠️ 渲染结果不符合预期（批处理下**预期如此** → 烘焙必须走 CPU 光栅化）");
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }
        catch (Exception e)
        {
            sb.AppendLine("!! 异常: " + e);
        }
        finally
        {
            if (quad != null) UnityEngine.Object.DestroyImmediate(quad);
            if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
        sb.AppendLine();
        sb.AppendLine(ok ? "==== GPU 回读可用 ====" : "==== GPU 回读不可用（预期：批处理下如此；不作为发版门禁） ====");
        File.WriteAllText(Out, sb.ToString());
        Debug.Log("[NTGpuProbe] done -> " + Out + " (gpuReadback=" + ok + ")");
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
