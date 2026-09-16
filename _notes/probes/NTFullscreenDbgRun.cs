// [NT-DIAG] 配合 NTFullscreenDbg.shader：一个 quad + 一盏 Spot，逐模式读回像素。
// 永远 exit 0（人工阅读）。
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTFullscreenDbgRun
{
    const int S = 64;
    static readonly StringBuilder Sb = new StringBuilder();

    public static void Run()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        QualitySettings.shadowDistance = 8f;
        QualitySettings.shadowResolution = ShadowResolution.Low;
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            if (l.type == LightType.Directional) l.enabled = false;

        var shader = Shader.Find("NTFullscreenDbg");
        Sb.AppendLine("shader = " + (shader == null ? "NULL" : shader.name));
        if (shader == null) { Write(); return; }

        // 遮挡物：球在光轴上，接收面就用这个 quad（z=2）
        var occ = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        occ.transform.position = new Vector3(0, 0, 0.9f);
        occ.transform.localScale = Vector3.one * 0.32f;

        var lg = new GameObject("spot");
        var li = lg.AddComponent<Light>();
        li.type = LightType.Spot; li.spotAngle = 70f; li.range = 6f; li.intensity = 1f;
        li.renderMode = LightRenderMode.ForcePixel; li.shadows = LightShadows.Hard;
        li.shadowBias = 0.0005f; li.shadowNormalBias = 0.02f; li.cullingMask = ~0;
        lg.transform.position = new Vector3(0, 0, -0.6f);
        lg.transform.rotation = Quaternion.identity;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.position = new Vector3(0, 0, 2.0f);
        quad.transform.localScale = new Vector3(1f, 1f, 1f);
        UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
        var mr = quad.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var camGo = new GameObject("cam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = 0.55f;
        cam.nearClipPlane = 0.01f; cam.farClipPlane = 50f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.transform.position = new Vector3(0, 0, -2.5f);
        cam.transform.rotation = Quaternion.identity;

        for (var mode = 1; mode <= 6; mode++)
        {
            var mat = new Material(shader);
            mat.SetFloat("_Mode", mode);
            mr.sharedMaterial = mat;
            var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32); rt.Create();
            var prev = RenderTexture.active;
            cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, S, S), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            var row = new StringBuilder();
            for (var x = 0; x < S; x += 4) row.Append(x + ":" + px[(S / 2) * S + x].r + " ");
            Sb.AppendLine("mode " + mode + " 中线：" + row);
            Sb.AppendLine("   center=" + px[(S / 2) * S + S / 2].r + "  corner=" + px[S * 4 + 4].r);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(mat);
        }
        Write();
        EditorApplication.Exit(0);
    }

    static void Write()
    {
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "ntfulldbg.txt"), Sb.ToString());
        Debug.Log("[NTFullscreenDbgRun]\n" + Sb);
    }
}
