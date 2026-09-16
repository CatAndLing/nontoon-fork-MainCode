using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToonTools
{
    // [NT-FEAT 13] 实时光源模式：真的挂一盏 Spot Light 跟随 avatar，阴影因此是**实时**的。
    //
    // 这条路的参考就是 nHaruka 的 PCSS4VRC（真实影システム）：它也是给 avatar 挂一盏聚光灯。
    // 但要注意它的已知代价（他们文档里也写了）：
    //   · 占 VRChat 性能等级的 Lights 计数（PC 上 Lights = 1 → 最高只能 Poor）
    //   · 影子的质量取决于**观看者**的 Shadow Quality 设置，别人关了阴影就看不到
    //   · 多个玩家的这种光会互相叠加，人挤人时会把 avatar 照白（他们叫「白飛び」）
    //   · Quest 端实时光基本不可用
    //
    // 这里能比它多做的两件事：
    //   1) Culling Mask 默认给 PlayerLocal（VRChat 里本地玩家所在的层）→ 不会照亮世界，
    //      也不会照亮别的玩家（对每一端来说，只有"自己的 avatar"在这个层上）。
    //   2) 自动把层级里所有 Renderer 的 Receive Shadows 打开（PCSS4VRC 的 FAQ 里提到
    //      某些 avatar 默认是关的，关了就不出影子）。
    internal static class NTSelfRealtimeLight
    {
        internal const string ChildName = "NTSelfLight (Realtime)";

        internal static Light Sync(NTSelfLight settings, bool log)
        {
            var parent = settings.realtimeLightParent != null ? settings.realtimeLightParent : settings.transform;
            var light = settings.realtimeLight;

            if (light == null)
            {
                var existing = parent.Find(ChildName);
                if (existing != null) light = existing.GetComponent<Light>();
            }
            if (light == null)
            {
                var go = new GameObject(ChildName);
                Undo.RegisterCreatedObjectUndo(go, "Create NonToon realtime self light");
                go.transform.SetParent(parent, false);
                light = go.AddComponent<Light>();
            }

            light.type = LightType.Spot;
            light.color = settings.color;
            light.useColorTemperature = settings.useColorTemperature;
            light.colorTemperature = settings.temperature;
            light.intensity = settings.intensity;
            light.range = settings.lightRange;
            light.spotAngle = settings.spotAngle;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = Mathf.Clamp01(settings.shadowStrength);
            light.shadowBias = settings.shadowBias;
            light.cullingMask = settings.realtimeCullingMask;
            light.renderMode = settings.realtimeRenderMode;
            light.transform.localPosition = Vector3.zero;
            light.transform.localRotation = Quaternion.identity;
            settings.realtimeLight = light;
            EditorUtility.SetDirty(light);

            // 没有 Receive Shadows 就收不到实时阴影 —— 有些 avatar 默认是关的。
            var touched = 0;
            foreach (var renderer in settings.TargetRenderers())
            {
                var changed = false;
                if (!renderer.receiveShadows) { renderer.receiveShadows = true; changed = true; }
                if (renderer.shadowCastingMode != ShadowCastingMode.On) { renderer.shadowCastingMode = ShadowCastingMode.On; changed = true; }
                if (changed) { EditorUtility.SetDirty(renderer); touched++; }
            }

            if (log)
                Debug.Log("[NTSelfLight] 实时光源已同步：" + light.name + "（Spot " + settings.spotAngle.ToString("F0") + "°，范围 "
                    + settings.lightRange.ToString("F2") + "m，CullingMask=" + settings.realtimeCullingMask.value
                    + "），修正了 " + touched + " 个 Renderer 的 Receive Shadows / Cast Shadows。");
            return light;
        }

        // 关掉实时光源（不删除对象，只禁用），并把它从组件引用上摘掉
        internal static void Disable(NTSelfLight settings)
        {
            var light = settings.realtimeLight;
            if (light == null) return;
            light.enabled = false;
            EditorUtility.SetDirty(light);
        }

        internal static List<string> Warnings(NTSelfLight settings)
        {
            var warnings = new List<string>();
            if (settings.realtimeCullingMask.value == 0)
                warnings.Add("Culling Mask 是空的 —— 这盏光不会照到任何东西。");
            else if ((settings.realtimeCullingMask.value & (1 << 10)) == 0)
                warnings.Add("Culling Mask 里没有第 10 层（PlayerLocal）。VRChat 里本地玩家的 avatar 在 PlayerLocal，"
                    + "不选它的话这盏光会去照世界或其他玩家的东西。");
            if (settings.lightRange > 8f)
                warnings.Add("照射范围超过 8m：容易把附近别人的 avatar（他们也在 PlayerLocal 上，是各端各自的本地玩家）照到，"
                    + "而且多盏这种光叠加会把画面照白。");
            return warnings;
        }
    }
}
