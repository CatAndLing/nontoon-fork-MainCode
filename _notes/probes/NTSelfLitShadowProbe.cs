// [NT-VERIFY 0.5.0] ④「自带光照与阴影」组件的验证装置。
//
// 要钉死三件事：
//   A. 虚拟光烘焙 == 用真 Light 指定同一方向烘焙（逐像素一致）—— 证明"不需要场景里的灯"是真等价，
//      而不是另写了一套会漂移的实现。
//   B. 一键写入的材质属性正确：自有光开关/方向/强度、亮度上下限、烘焙基与深度图、PCSS 开关与档位；
//      并且 Int 属性是**真的**写进去了（SetInteger 的坑）。
//   C. 关掉自阴影时：强度写 0、贴图引用被清掉；不烘焙时沿用上次的贴图。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTSelfLitShadowProbe
{
    const string OUT = "ntselflitshadow.txt";
    const string Folder = "Assets/NonToonSelfLight";
    static readonly StringBuilder Sb = new StringBuilder();
    static int Fail, Pass;
    static void L(string s) { Sb.AppendLine(s); Debug.Log("[NT-④] " + s); }
    static void Ok(string s) { Pass++; L("  ✅ " + s); }
    static void Bad(string s) { Fail++; L("  ❌ " + s); }
    static void Info(string s) { L("  ·  " + s); }
    static void Check(bool c, string what, string detail = "")
    {
        if (c) Ok(what + (detail.Length > 0 ? " → " + detail : ""));
        else Bad(what + (detail.Length > 0 ? " → " + detail : ""));
    }

    static Type FindType(string n)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var t in ts) if (t.Name == n) return t;
        }
        return null;
    }

    public static void Run()
    {
        try { if (File.Exists(OUT)) File.Delete(OUT); } catch { }
        L("== 运行信息 ==");
        L("时间  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        L("工程  = " + Directory.GetCurrentDirectory());
        L("");
        try { Body(); }
        catch (Exception e) { Bad("探针异常：" + e); }
        L("");
        L(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + "）====");
        File.WriteAllText(OUT, Sb.ToString());
        Debug.Log("[NTSelfLitShadowProbe] done -> " + OUT + " (fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        // ---------------- 0. 类型在不在 ----------------
        var compType = FindType("NTSelfLitShadow");
        var writerType = FindType("NTSelfLightWriter");
        var editorType = FindType("NTSelfLitShadowEditor");
        Check(compType != null, "找到运行时组件 NTSelfLitShadow");
        Check(writerType != null, "找到共用的写入器 NTSelfLightWriter");
        Check(editorType != null, "找到面板 NTSelfLitShadowEditor");
        if (compType == null || writerType == null || editorType == null) return;

        var shader = Shader.Find("nontoon-fork");
        Check(shader != null, "找到 NonToon 着色器");
        if (shader == null) return;

        // ---------------- 1. 搭场景 ----------------
        var root = GameObject.Find("NTSelfLitProbe");
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
        var avatar = new GameObject("NTSelfLitProbe");

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(avatar.transform, false);
        head.transform.localScale = Vector3.one * 0.3f;              // 直径 0.3 m ≈ 一颗头
        UnityEngine.Object.DestroyImmediate(head.GetComponent<Collider>());

        var tuft = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tuft.name = "Tuft";                                          // 一撮头发：在头前面 5 cm
        tuft.transform.SetParent(avatar.transform, false);
        tuft.transform.localScale = Vector3.one * 0.04f;
        tuft.transform.position = new Vector3(0, 0, -0.20f);
        UnityEngine.Object.DestroyImmediate(tuft.GetComponent<Collider>());

        var mat = new Material(shader) { name = "NTSelfLitProbeMat" };
        head.GetComponent<MeshRenderer>().sharedMaterial = mat;
        tuft.GetComponent<MeshRenderer>().sharedMaterial = mat;

        var comp = avatar.AddComponent(compType);

        // 用反射设字段（探针不引用包内类型，保持和其它探针一致的写法）
        void Set(string f, object v) { compType.GetField(f).SetValue(comp, v); }
        object Get(string f) { return compType.GetField(f).GetValue(comp); }
        Set("targetRoot", avatar.transform);
        Set("includeChildren", true);
        Set("yaw", 25f);
        Set("pitch", 40f);
        Set("intensity", 1f);
        Set("onlyThisLight", true);
        Set("raiseBrightnessFloor", true);
        Set("minLimit", 0.35f);
        Set("maxLimit", 0.9f);
        Set("enableShadow", true);
        Set("shadowResolution", 128);
        Set("pcss", true);
        Set("receiveMask", null);
        Set("receiveMaskStrength", 0f);

        // ---------------- 2. A. 虚拟光烘焙 vs 真 Light 同方向 ----------------
        L("== A. 虚拟光烘焙 == 真 Light 同方向烘焙 ==");
        var dir = (Vector3)compType.GetProperty("LightDirection").GetValue(comp);
        var fwd = (Vector3)compType.GetProperty("LightForward").GetValue(comp);
        Info(string.Format("组件算出：光从 ({0:F3}, {1:F3}, {2:F3}) 来；传播方向 ({3:F3}, {4:F3}, {5:F3})",
            dir.x, dir.y, dir.z, fwd.x, fwd.y, fwd.z));

        // 期望方向（yaw=25, pitch=40）
        var expect = new Vector3(Mathf.Sin(25f * Mathf.Deg2Rad) * Mathf.Cos(40f * Mathf.Deg2Rad),
                                 Mathf.Sin(40f * Mathf.Deg2Rad),
                                 Mathf.Cos(25f * Mathf.Deg2Rad) * Mathf.Cos(40f * Mathf.Deg2Rad)).normalized;
        Check(Vector3.Dot(dir, expect) > 0.9999f, "角度→方向 的换算正确", Vector3.Dot(dir, expect).ToString("F5"));

        // 真 Light：位置放在远处、朝向 = 组件的传播方向
        var lightGo = new GameObject("NTSelfLitProbeLight");
        lightGo.transform.position = -fwd * 5f;
        lightGo.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;

        var bakerType = FindType("NTSelfShadowBaker");
        Check(bakerType != null, "找到 NTSelfShadowBaker");
        if (bakerType == null) return;
        // 注意：烘焙器与写入器都是 internal → 反射必须带 NonPublic（踩过：默认绑定找不到 internal 方法）
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var bakeLight = bakerType.GetMethod("Bake", Any, null,
            new[] { typeof(Light), typeof(IEnumerable<Renderer>), typeof(int), typeof(string) }, null);
        var bakeDir = bakerType.GetMethod("Bake", Any, null,
            new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(string), typeof(IEnumerable<Renderer>), typeof(int), typeof(string) }, null);
        Check(bakeLight != null, "烘焙器有 Light 入口（② 用）");
        Check(bakeDir != null, "烘焙器有方向入口（④ 用，虚拟光）");
        if (bakeLight == null || bakeDir == null) return;

        var renderers = new List<Renderer> { head.GetComponent<MeshRenderer>(), tuft.GetComponent<MeshRenderer>() };
        object resLight = null, resDir = null;
        try { resLight = bakeLight.Invoke(null, new object[] { light, renderers, 128, Folder + "/probeLight" }); }
        catch (Exception e) { Bad("Light 入口烘焙抛异常：" + (e.InnerException ?? e).Message); }
        try { resDir = bakeDir.Invoke(null, new object[] { fwd, Vector3.Cross(Vector3.up, fwd).normalized, Vector3.Cross(fwd, Vector3.Cross(Vector3.up, fwd).normalized).normalized, "probeDir", renderers, 128, Folder + "/probeDir" }); }
        catch (Exception e) { Bad("方向入口烘焙抛异常：" + (e.InnerException ?? e).Message); }
        Check(resLight != null && resDir != null, "两次烘焙都成功");
        if (resLight == null || resDir == null) return;

        var rt = resLight.GetType();
        var depthL = (float[])rt.GetField("depth").GetValue(resLight);
        var depthD = (float[])rt.GetField("depth").GetValue(resDir);
        Check(depthL.Length == depthD.Length, "两次烘焙分辨率一致", depthL.Length + " / " + depthD.Length);
        var maxDiff = 0f; var differ = 0;
        for (var i = 0; i < Mathf.Min(depthL.Length, depthD.Length); i++)
        {
            var d = Mathf.Abs(depthL[i] - depthD[i]);
            if (d > maxDiff) maxDiff = d;
            if (d > 1f / 255f) differ++;
        }
        Check(maxDiff <= 1f / 255f, "虚拟光烘焙结果与真 Light 同方向**逐像素一致**",
            string.Format("最大差 {0:F5}（{1}/{2} 个像素超过 1/255）", maxDiff, differ, depthL.Length));

        var basisL = ((Vector3)rt.GetField("forward").GetValue(resLight));
        var basisD = ((Vector3)rt.GetField("forward").GetValue(resDir));
        Check(Vector3.Dot(basisL, basisD) > 0.9999f, "两次烘焙的光轴一致",
            Vector3.Dot(basisL, basisD).ToString("F5"));

        // ---------------- 3. B. 一键写入材质 ----------------
        L("");
        L("== B. 一键写入材质 ==");
        var collect = editorType.GetMethod("CollectTargetMaterials", Any, null, new[] { compType }, null);
        var bakeAndApply = editorType.GetMethod("BakeAndApply", Any, null, new[] { compType, typeof(List<Material>) }, null);
        var applyOnly = editorType.GetMethod("Apply", Any, null,
            new[] { compType, typeof(List<Material>), FindType("NTSelfShadowBaker").GetNestedType("Result", BindingFlags.Public | BindingFlags.NonPublic) }, null);
        Check(collect != null, "面板有材质收集");
        Check(bakeAndApply != null, "面板有「一键配置并烘焙」的实现");
        Check(applyOnly != null, "面板有「只同步参数」的实现");
        if (collect == null || bakeAndApply == null || applyOnly == null) return;

        var mats = (System.Collections.IList)collect.Invoke(null, new object[] { comp });
        Check(mats != null && mats.Count == 1, "收集到 1 个 NonToon 材质", mats == null ? "null" : mats.Count.ToString());

        bakeAndApply.Invoke(null, new object[] { comp, mats });

        Check(mat.HasProperty("_UseSelfLight") && mat.GetInteger("_UseSelfLight") == 1, "写了 _UseSelfLight = 1（Int → SetInteger）");
        Check(mat.GetInteger("_SelfLightOnly") == 1, "写了 _SelfLightOnly = 1（只由它照亮）");
        Check(Mathf.Abs(mat.GetFloat("_LightMinLimit") - 0.35f) < 1e-4f, "写了亮度下限 0.35",
            mat.GetFloat("_LightMinLimit").ToString("F3"));
        Check(Mathf.Abs(mat.GetFloat("_LightMaxLimit") - 0.9f) < 1e-4f, "写了亮度上限 0.9",
            mat.GetFloat("_LightMaxLimit").ToString("F3"));
        var mdir = mat.GetVector("_SelfLightDirection");
        Check(Vector3.Dot(new Vector3(mdir.x, mdir.y, mdir.z), dir) > 0.9999f, "写了光的方向（指向光源）",
            string.Format("({0:F3},{1:F3},{2:F3})", mdir.x, mdir.y, mdir.z));
        Check(mat.GetInteger("_SelfLightPCSS") == 1, "写了 PCSS 开关（Int → SetInteger）");
        Check(mat.GetInteger("_SelfLightPCSSQuality") == 0, "写了 PCSS 档位 = 低",
            mat.GetInteger("_SelfLightPCSSQuality").ToString());

        var map = mat.GetTexture("_SelfLightShadowMap");
        Check(map != null, "材质上有烘焙出来的深度图", map == null ? "null" : map.name);
        var texels = mat.GetFloat("_SelfLightShadowTexels");
        Check(Mathf.Abs(texels - 128f) < 1e-3f, "Shadow Texels 与烘焙分辨率一致", texels.ToString("F0"));
        var far = mat.GetFloat("_SelfLightFar");
        var halfX = mat.GetFloat("_SelfLightHalfX");
        Info(string.Format("烘焙基：halfX={0:F4} halfY={1:F4} far={2:F4}（头尺度 0.3 m 应≈0.16 / 0.42）",
            halfX, mat.GetFloat("_SelfLightHalfY"), far));
        Check(halfX > 0.10f && halfX < 0.25f, "包围盒半宽符合头尺度", halfX.ToString("F4"));
        Check(far > 0.3f && far < 0.6f, "包围盒深度符合头尺度", far.ToString("F4"));

        var baked = (Texture2D)compType.GetField("bakedShadowMap").GetValue(comp);
        Check(baked != null && baked == map, "组件记住了这次烘焙的贴图");

        // ---------------- 4. C. 关掉自阴影 / 不烘焙 ----------------
        L("");
        L("== C. 关掉自阴影 / 只同步参数 ==");
        Set("enableShadow", false);
        bakeAndApply.Invoke(null, new object[] { comp, mats });   // 关掉后不应该再烘焙（也不该留贴图引用）
        Check(Mathf.Abs(mat.GetFloat("_SelfLightShadowStrength")) < 1e-4f, "关掉自阴影 ⇒ 强度写 0",
            mat.GetFloat("_SelfLightShadowStrength").ToString("F3"));
        Check(mat.GetTexture("_SelfLightShadowMap") == null, "关掉自阴影 ⇒ 材质的贴图引用被清掉（不白占显存）");

        Set("enableShadow", true);
        Set("minLimit", 0.5f);
        applyOnly.Invoke(null, new object[] { comp, mats, null });   // 不烘焙，沿用上次的贴图
        Check(Mathf.Abs(mat.GetFloat("_LightMinLimit") - 0.5f) < 1e-4f, "「只同步参数」会写新的亮度下限",
            mat.GetFloat("_LightMinLimit").ToString("F3"));
        Check(mat.GetTexture("_SelfLightShadowMap") == baked, "「只同步参数」沿用上次烘焙的贴图");

        // ---------------- 5. 与 ② 的冲突检测 ----------------
        L("");
        L("== D. 与 ② 的冲突检测 ==");
        var lightCompType = FindType("NTSelfLight");
        Check(lightCompType != null, "找到 ② NTSelfLight（应存在，未被改动）");
        if (lightCompType != null)
        {
            var old = avatar.AddComponent(lightCompType);
            var conflicts = editorType.GetMethod("FindConflicts", BindingFlags.NonPublic | BindingFlags.Static);
            Check(conflicts != null, "面板有冲突检测");
            if (conflicts != null)
            {
                var list = (System.Collections.IList)conflicts.Invoke(null, new object[] { comp });
                Check(list != null && list.Count > 0, "同时挂 ② 和 ④ 时给出警告", list == null ? "null" : list.Count + " 条");
            }
            UnityEngine.Object.DestroyImmediate(old);
        }

        // 清理
        UnityEngine.Object.DestroyImmediate(avatar);
        UnityEngine.Object.DestroyImmediate(lightGo);
        UnityEngine.Object.DestroyImmediate(mat);
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
    }
}
