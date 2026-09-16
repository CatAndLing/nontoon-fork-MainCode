using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 20] 生成「游戏内可调的光照强度」所需的资产。
    //
    // ── 为什么不能用运行时脚本 ──────────────────────────────────────────────
    // VRChat 官方文档《Allowed Avatar Components》明确写着：
    //   "Any component on the following list can be used in VRChat.
    //    Other components or custom scripts won't work in VRChat and may stop you from uploading your avatar."
    // 白名单里只有 Animator / Animation / Light / Renderer 这类，**没有自定义脚本**。
    // 所以"玩家在游戏里拉一下就把自己调亮"只能靠：
    //   Animator 的 Float 参数（由 Expression Menu 的 Radial Puppet 控制，值域 0.0–1.0）
    //   → BlendTree 在两条 AnimationClip 之间混合
    //   → AnimationClip 直接给 renderer 的 material 属性打曲线（material._SelfLightIntensity）
    // 全都是白名单内的东西，Quest 也能用，且**不需要往 avatar 上挂我们自己的脚本**。
    //
    // ── 为什么全走反射 ──────────────────────────────────────────────────────
    // 本包坚持零 VRCSDK 依赖（没装 SDK 的人也能用着色器与转换器）。
    // 所以这里只按类型名/字段名去找 VRC 的类型，找不到就退回"打印手动步骤"。
    //
    // 参数代价（官方文档）：Float = 8 bit 同步内存；VRChat 最多 256 bit 自定义同步参数。
    internal static class NTVrcParameterBuilder
    {
        internal sealed class Result
        {
            public readonly List<string> Done = new List<string>();
            public readonly List<string> Manual = new List<string>();
            public readonly List<string> Errors = new List<string>();
            // [NT-FEAT 22] standalone 模式下生成的独立控制器（交给 Modular Avatar 合并）
            public AnimatorController Controller;
            public string ControllerPath;
            public readonly List<string> GeneratedAssets = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
            public string Summary()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in Done) sb.AppendLine("  ✅ " + s);
                foreach (var s in Manual) sb.AppendLine("  ⚠️ 需要手动：" + s);
                foreach (var s in Errors) sb.AppendLine("  ❌ " + s);
                return sb.ToString();
            }
        }

        internal const string DefaultFolder = "Assets/NonToonLightAdjuster";
        internal const string LayerPrefix = "NonToon ";
        // 默认驱动「亮度下限」——这是 LLC（LightLimitChanger）那一系的思路：
        //   NonToon 与 lilToon 的亮度公式完全一致：RGB = clamp(RGB, _LightMinLimit, _LightMaxLimit)
        //   而 NonToon 的 _LightMinLimit **默认是 0**，所以全黑地图里就是一块煤。
        //   把它抬高（lilToon 官方建议 VRChat 里 0.0~0.2）就能防煤，且**零采样代价、不必开自有光**。
        internal const string DefaultProp = "_LightMinLimit";

        // ---------------------------------------------------------------- 反射工具
        private static Type FindType(params string[] fullNames)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var n in fullNames)
                {
                    var t = asm.GetType(n, false);
                    if (t != null) return t;
                }
            return null;
        }

        private static object ParseEnum(Type enumType, string name)
        {
            if (enumType == null) return null;
            try { return Enum.Parse(enumType, name, true); } catch { return null; }
        }

        private static FieldInfo F(Type t, string n)
        {
            return t?.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        private static object Get(object o, string n) { return F(o?.GetType(), n)?.GetValue(o); }
        private static void Set(object o, string n, object v)
        {
            var f = F(o?.GetType(), n);
            if (f == null) throw new MissingFieldException((o?.GetType().Name ?? "null") + "." + n);
            f.SetValue(o, v);
        }

        // ---------------------------------------------------------------- 主流程
        // [NT-FEAT 22] 2026-09-17 用户决定：**需要菜单的功能一律走 Modular Avatar 声明式**。
        //
        // ⛔ 原来的 `standalone = false` 分支会**直接改用户的资产**：
        //    · 往用户的 FX 控制器里加层（`GetFxController`）
        //    · 写用户的 `ExpressionParameters`
        //    · 写用户的 `ExpressionsMenu`，甚至改 `baseAnimationLayers` 的 FX 指派
        //    实机事故就是它造成的：在用户的 `Torao_FXLayer.controller` 里留下一个
        //    `defaultWeight = 0` 的**死层**（+ `NT_Light` 参数 + 菜单项）⇒ ⑤ 的"游戏内亮度"
        //    永远拉不动，而且用户完全不知道自己的资产被动过。
        //    ⇒ **整条回退路径已删除**。没装 MA 就明确报错、什么都不做，绝不碰用户资产。
        internal static Result Build(
            GameObject avatarRoot,
            List<Renderer> renderers,
            string paramName,
            float intensityAtZero,
            float intensityAtOne,
            string folder,
            string propName = null)
        {
            var r = new Result();
            if (avatarRoot == null) { r.Errors.Add("没有指定 avatar 根节点"); return r; }
            if (renderers == null || renderers.Count == 0) { r.Errors.Add("没有找到可用于动画的 Renderer"); return r; }
            if (string.IsNullOrEmpty(paramName)) { r.Errors.Add("参数名不能为空"); return r; }
            if (string.IsNullOrEmpty(folder)) folder = DefaultFolder;
            if (string.IsNullOrEmpty(propName)) propName = DefaultProp;
            EnsureFolder(folder);

            // 1) Animator 参数 + 两条曲线剪辑 + 一层 BlendTree（**独立控制器**）
            BuildAnimator(avatarRoot, renderers, paramName, intensityAtZero, intensityAtOne, folder, propName, r);

            r.Done.Add("独立模式：只生成资产，avatar 的动画层 / 参数 / 菜单交给 Modular Avatar 在构建期合并"
                     + "（**你的 FX 控制器 / ExpressionParameters / ExpressionsMenu 不会被改动**）");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return r;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            var leaf = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(string.IsNullOrEmpty(parent) ? "Assets" : parent, leaf);
        }

        // ---------------------------------------------------------------- Animator
        private static void BuildAnimator(
            GameObject avatarRoot, List<Renderer> renderers, string paramName,
            float atZero, float atOne, string folder, string propName, Result r)
        {
            var tag = propName.TrimStart('_');
            var layerName = LayerPrefix + tag;
            // ⚠️ AnimationClip 资产必须用 .anim 扩展名：用 .clip 时 Unity 会拒绝创建
            // （日志：CreateAsset() should not be used to create a file of type 'clip'）—— 实测踩过。
            var lowPath = folder + "/" + Sanitize(paramName) + "_" + tag + "_低.anim";
            var highPath = folder + "/" + Sanitize(paramName) + "_" + tag + "_高.anim";

            var low = CreatePropClip(lowPath, avatarRoot, renderers, atZero, propName);
            var high = CreatePropClip(highPath, avatarRoot, renderers, atOne, propName);
            if (low == null || high == null) { r.Errors.Add("创建 AnimationClip 失败"); return; }
            r.GeneratedAssets.Add(lowPath);
            r.GeneratedAssets.Add(highPath);
            r.Done.Add("生成曲线剪辑：" + Path.GetFileName(lowPath) + "（" + propName + " = " + atZero + "）、"
                       + Path.GetFileName(highPath) + "（" + atOne + "），绑定 "
                       + renderers.Count + " 个 Renderer 的 material." + propName);

            // FX 控制器：**永远新建独立控制器**（绝不碰用户已有的控制器 —— 这是 MA 声明的合并路径）
            var fxPath = folder + "/" + Sanitize(paramName) + "_NonToonFX.controller";
            if (File.Exists(fxPath)) AssetDatabase.DeleteAsset(fxPath);   // 可重复运行
            var fx = AnimatorController.CreateAnimatorControllerAtPath(fxPath);
            r.Controller = fx;
            r.ControllerPath = fxPath;
            r.GeneratedAssets.Add(fxPath);
            r.Done.Add("新建独立 FX AnimatorController：" + fxPath + "（由 Modular Avatar 合并进 FX 层）");

            // Float 参数（没有才加）
            if (!fx.parameters.Any(p => p.name == paramName))
            {
                fx.AddParameter(paramName, AnimatorControllerParameterType.Float);
                r.Done.Add("Animator 参数 +Float " + paramName);
            }
            else r.Done.Add("Animator 参数 " + paramName + " 已存在（复用）");

            // 生成用的层：同名先删（保证可重复运行）
            for (int i = fx.layers.Length - 1; i >= 0; i--)
                if (fx.layers[i].name == layerName) fx.RemoveLayer(i);

            fx.AddLayer(layerName);
            var layer = fx.layers[fx.layers.Length - 1];
            // ⛔⛔ **必须是 1f**：`AnimatorController.AddLayer(string)` 这个重载建出来的层
            //   `defaultWeight = 0` ⇒ 该层运行时**零贡献** ⇒ 游戏内那个径向**完全拉不动**。
            //   （实机 NDMF 烘焙里铁证：`layer 'NonToon LightMinLimit' w=0`。）
            //   这是"⑤ 亮度调整：构建时固定能用、游戏内不可调"的真正原因。
            //   `AnimatorControllerLayer` 在 UnityEditor.Animations 里是 class，正常改字段即可生效；
            //   这里仍然显式写回一次，避免吃 API 实现细节的亏。
            layer.defaultWeight = 1f;
            {
                var layers = fx.layers;
                layers[layers.Length - 1] = layer;
                fx.layers = layers;
            }
            var sm = layer.stateMachine;
            var bt = new BlendTree
            {
                name = paramName + "_blend",
                blendType = BlendTreeType.Simple1D,
                blendParameter = paramName,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(bt, fx);
            bt.AddChild(low, 0f);
            bt.AddChild(high, 1f);

            var st = sm.AddState("亮度");
            st.motion = bt;
            st.writeDefaultValues = false;
            sm.defaultState = st;
            EditorUtility.SetDirty(fx);
            r.Done.Add("新增 Animator 层「" + layerName + "」：BlendTree 按 " + paramName + " 在两条曲线间混合（0 → "
                       + atZero + "，1 → " + atOne + "）");
        }

        private static AnimationClip CreatePropClip(string path, GameObject root, List<Renderer> renderers, float value, string propName)
        {
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var clip = new AnimationClip { frameRate = 60f, legacy = false };
            var curve = AnimationCurve.Constant(0f, 1f / 60f, value);
            foreach (var rd in renderers)
            {
                if (rd == null) continue;
                // material.<属性名> 曲线：这是 VRChat 支持的材质属性动画写法
                var rel = AnimationUtility.CalculateTransformPath(rd.transform, root.transform);
                var binding = EditorCurveBinding.FloatCurve(rel, rd.GetType(), "material." + propName);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
            AssetDatabase.CreateAsset(clip, path);
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

    }
}
