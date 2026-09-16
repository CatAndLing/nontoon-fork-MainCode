using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToonTools
{
    // [NT-FEAT 14] Avatar 光源插件的创建 / 同步 / 自检。
    // 着色器无关：只经营一盏 Unity Light，不碰材质属性。
    internal static class NTAvatarLightCreator
    {
        internal const string ChildName = "NTAvatarLight";
        internal const int PlayerLocalLayer = 10;   // VRChat: PlayerLocal（本地玩家）

        internal static NTAvatarLight Create(GameObject avatarRoot, bool log = true)
        {
            if (avatarRoot == null) return null;

            var existing = avatarRoot.GetComponentInChildren<NTAvatarLight>(true);
            if (existing != null)
            {
                if (log) Debug.Log("[NTAvatarLight] 这个 avatar 已经有光源插件了，改为同步：" + existing.name);
                Sync(existing, log);
                Selection.activeGameObject = existing.gameObject;
                return existing;
            }

            var go = new GameObject(ChildName);
            Undo.RegisterCreatedObjectUndo(go, "创建 Avatar 光源插件");
            go.transform.SetParent(avatarRoot.transform, false);
            var light = go.AddComponent<Light>();
            var component = go.AddComponent<NTAvatarLight>();
            component.target = light;
            Sync(component, log);
            Selection.activeGameObject = go;
            return component;
        }

        internal static Light Sync(NTAvatarLight settings, bool log)
        {
            if (settings == null) return null;
            var light = settings.target;
            if (light == null)
            {
                light = settings.GetComponent<Light>();
                if (light == null) light = settings.gameObject.AddComponent<Light>();
                settings.target = light;
            }

            light.type = LightType.Spot;
            light.color = settings.color;
            light.useColorTemperature = settings.useColorTemperature;
            light.colorTemperature = settings.temperature;
            light.intensity = settings.intensity;
            light.range = settings.range;
            light.spotAngle = settings.spotAngle;
            light.shadows = settings.shadows;
            light.shadowStrength = Mathf.Clamp01(settings.shadowStrength);
            light.shadowBias = settings.shadowBias;
            light.cullingMask = settings.cullingMask;
            light.renderMode = settings.renderMode;
            EditorUtility.SetDirty(light);

            // 光的方向：directionSource 有值就跟随它，否则跟随本节点
            if (settings.directionSource != null)
            {
                light.transform.localPosition = Vector3.zero;
                light.transform.rotation = settings.directionSource.rotation;
            }

            // 没有 Receive Shadows 就收不到实时阴影 —— 有些 avatar 默认是关的。
            var touched = 0;
            foreach (var renderer in settings.transform.root.GetComponentsInChildren<Renderer>(true))
            {
                var changed = false;
                if (!renderer.receiveShadows) { renderer.receiveShadows = true; changed = true; }
                if (renderer.shadowCastingMode == ShadowCastingMode.Off) { renderer.shadowCastingMode = ShadowCastingMode.On; changed = true; }
                if (changed) { EditorUtility.SetDirty(renderer); touched++; }
            }

            if (log)
                Debug.Log("[NTAvatarLight] 光源已同步：" + light.name + "（Spot " + settings.spotAngle.ToString("F0") + "°，范围 "
                    + settings.range.ToString("F2") + "m，CullingMask=" + settings.cullingMask.value + "，阴影 " + settings.shadows + "），"
                    + "修正了 " + touched + " 个 Renderer 的 Receive Shadows / Cast Shadows。");
            return light;
        }

        internal static void Remove(NTAvatarLight settings)
        {
            if (settings == null) return;
            Undo.DestroyObjectImmediate(settings.gameObject);
        }

        // 面板与探针共用：配置上的坑要主动说出来，而不是等用户发现"没影子"
        internal static List<string> Warnings(NTAvatarLight settings)
        {
            var warnings = new List<string>();
            if (settings == null) return warnings;

            if (settings.cullingMask.value == 0)
                warnings.Add("Culling Mask 是空的 —— 这盏光不会照到任何东西。");
            else if ((settings.cullingMask.value & (1 << PlayerLocalLayer)) == 0)
                warnings.Add("Culling Mask 里没有第 10 层（PlayerLocal）。VRChat 里本地玩家的 avatar 在 PlayerLocal，"
                    + "不选它的话这盏光会去照世界或其他玩家的东西。");
            if (settings.range > 8f)
                warnings.Add("照射范围超过 8m：容易照到附近别人的 avatar（每端的本地玩家都在 PlayerLocal 上），"
                    + "而且多盏这种光叠加会把画面照白。");
            if (settings.shadows == LightShadows.None)
                warnings.Add("当前没有开阴影 —— 这盏光只会照亮，不会投影。");
            if (settings.intensity > 4f)
                warnings.Add("光强超过 4：多盏这种光叠加时很容易过曝。");

            // 和 NonToon 的「自有光源」撞车：两者都开就是双份打光
            var doubleLit = settings.transform.root.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && m.HasProperty("_UseSelfLight") && m.GetInteger("_UseSelfLight") != 0)
                .Select(m => m.name)
                .Distinct()
                .ToList();
            if (doubleLit.Count > 0)
                warnings.Add("检测到 " + doubleLit.Count + " 个 NonToon 材质还开着「自有光源」（_UseSelfLight = 1），"
                    + "和这盏实时光叠加会双份打光。请把材质的自有光源关掉，或用 Self Light 组件的「实时光源」模式统一管理。"
                    + "涉及：" + string.Join("、", doubleLit.Take(3)) + (doubleLit.Count > 3 ? " 等" : ""));

            return warnings;
        }
    }
}
