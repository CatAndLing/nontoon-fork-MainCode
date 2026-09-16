// [NT-DIAG] 配合 NTDiagShadowProbe.shader：造"遮挡+接收+Spot(Hard)"场景，
// 逐模式抓中心像素，直接回答"Unity 的阴影贴图到底能不能被我们读出来"。
// 永远 exit 0（人工阅读的诊断输出）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTDiagShadowRun
{
    const int S = 256;
    static readonly StringBuilder Sb = new StringBuilder();

    public static void Run()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.fog = false;
        QualitySettings.shadowDistance = 8f;
        QualitySettings.shadowResolution = ShadowResolution.Low;
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            if (l.type == LightType.Directional) l.enabled = false;

        var shader = Shader.Find("NTDiagShadowProbe");
        Sb.AppendLine("shader = " + (shader == null ? "NULL（没导入？）" : shader.name));
        if (shader == null) { Write(); return; }

        for (var mode = 0; mode <= 7; mode++)
        {
            var px = Shot(shader, mode);
            // 取画面中心与"阴影外"的一点作对照
            var sb = new StringBuilder();
            foreach (var (x, y) in new[] { (S / 2, S / 2), (S / 2 + 60, S / 2), (S / 2, S / 2 + 60), (20, 20) })
                sb.Append("(" + x + "," + y + ")=" + px[y * S + x].r + " ");
            Sb.AppendLine("mode " + mode + " : " + sb);
            // 顺带把中间一行的亮度剖面记下来，看"画面到底长什么样"
            if (mode == 0 || mode == 4)
            {
                var rowSb = new StringBuilder();
                for (var x = 0; x < S; x += 16) rowSb.Append(x + ":" + px[(S / 2) * S + x].r + " ");
                Sb.AppendLine("   mode " + mode + " 中线剖面：" + rowSb);
            }
        }
        // ★ 决定性对照：把接收面的**投影**关掉再拍一次 mode 0/4
        foreach (var noCast in new[] { false, true })
        {
            var px = Shot(shader, 4, noCast);
            var rowSb = new StringBuilder();
            for (var x = 0; x < S; x += 16) rowSb.Append(x + ":" + px[(S / 2) * S + x].r + " ");
            Sb.AppendLine("★ 接收面投影=" + (noCast ? "关" : "开") + " (mode 4 = 片元深度) 中线剖面：" + rowSb);
        }
        Write();
        EditorApplication.Exit(0);
    }

    static void Write()
    {
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "ntdiagshadow.txt"), Sb.ToString());
        Debug.Log("[NTDiagShadowRun]\n" + Sb);
    }

    static Color32[] Shot(Shader shader, int mode, bool receiverNoCast = true)
    {
        var alive = new List<GameObject>();
        var mat = new Material(shader);
        mat.SetFloat("_Mode", mode);

        var recv = GameObject.CreatePrimitive(PrimitiveType.Cube);
        recv.transform.position = new Vector3(0, 0, 2.0f);
        recv.transform.localScale = new Vector3(1f, 1f, 0.02f);
        recv.GetComponent<MeshRenderer>().sharedMaterial = mat;
        recv.GetComponent<MeshRenderer>().shadowCastingMode = receiverNoCast ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
        alive.Add(recv);

        var occ = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        occ.transform.position = new Vector3(0, 0, 0.9f);
        occ.transform.localScale = Vector3.one * 0.32f;
        occ.GetComponent<MeshRenderer>().sharedMaterial = mat;
        alive.Add(occ);

        var lg = new GameObject("spot");
        var li = lg.AddComponent<Light>();
        li.type = LightType.Spot; li.spotAngle = 70f; li.range = 6f; li.intensity = 1f;
        li.renderMode = LightRenderMode.ForcePixel; li.shadows = LightShadows.Hard;
        li.shadowBias = 0.0005f; li.shadowNormalBias = 0.02f; li.cullingMask = ~0;
        lg.transform.position = new Vector3(0, 0, -0.6f);
        lg.transform.rotation = Quaternion.identity;
        alive.Add(lg);

        var camGo = new GameObject("cam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = 0.55f;
        cam.nearClipPlane = 0.01f; cam.farClipPlane = 50f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.transform.position = new Vector3(0, 0, -2.5f);
        cam.transform.rotation = Quaternion.identity;
        alive.Add(camGo);

        var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32); rt.Create();
        var prev = RenderTexture.active;
        cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, S, S), 0, 0); tex.Apply();
        RenderTexture.active = prev;
        var px = tex.GetPixels32();

        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(mat);
        foreach (var g in alive) if (g != null) UnityEngine.Object.DestroyImmediate(g);
        return px;
    }
}
