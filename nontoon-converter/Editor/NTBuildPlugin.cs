using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(NonToonTools.NTBuildPlugin))]

namespace NonToonTools
{
    // Build-only: never call menu installers, SaveAssets, or the legacy asset migration here.
    public sealed class NTBuildPlugin : Plugin<NTBuildPlugin>
    {
        public override string QualifiedName => "com.catandling.nontoon-converter";
        public override string DisplayName => "NonToon Tools";

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                .BeforePlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Replay NonToon authoring settings", Replay);
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .WithRequiredExtension(typeof(AnimatorServicesContext), sequence =>
                    sequence.Run("Align NonToon build material queues", AlignQueues));
        }

        internal sealed class BuildState
        {
            public BuildState() { }
            public readonly Dictionary<Material, Material> Copies = new Dictionary<Material, Material>();
            public readonly HashSet<string> Warnings = new HashSet<string>();
        }

        internal static void AlignQueues(BuildContext context)
        {
            var state = context.GetState<BuildState>();
            var index = context.Extension<AnimatorServicesContext>().AnimationIndex;
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            var materials = renderers.SelectMany(r => r.sharedMaterials)
                .Concat(index.GetPPtrReferencedObjects.OfType<Material>()).Where(m => m != null)
                .Select(m => state.Copies.TryGetValue(m, out var copy) ? copy : m).Distinct().ToArray();
            int queues = 0;
            foreach (var m in materials)
            {
                if (!NTRenderQueueFix.IsNonToonMaterial(m) || !m.HasProperty("_RenderingMode")) continue;
                int want = NTRenderQueueFix.QueueFor(m.GetInteger("_RenderingMode"));
                if (!NTRenderQueueFix.IsModeDefaultQueue(m.renderQueue) || RawQueue(m) == want) continue;
                Copy(state, m).renderQueue = want;
                queues++;
            }
            foreach (var rd in renderers)
                rd.sharedMaterials = rd.sharedMaterials.Select(m => m != null && state.Copies.TryGetValue(m, out var copy) ? copy : m).ToArray();
            // NDMF owns these virtual clips: source controllers and clips are never edited.
            index.RewriteObjectCurves(obj => obj is Material m && state.Copies.TryGetValue(m, out var copy) ? copy : obj);
            Debug.Log($"[NonToon NDMF] Queue pass complete: materials={materials.Length}, queues={queues}", context.AvatarRootObject);
        }

        internal static void Replay(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var state = context.GetState<BuildState>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var writes = new Dictionary<Material, Dictionary<string, float>>();
            var settings = root.GetComponentsInChildren<NTLightAdjust>(true);
            foreach (var s in settings)
            {
                if (s.mode != NTLightAdjust.Mode.FixedAtBuild)
                {
                    // Generate() installs the unified menu; it does NOT write initialValue to materials.
                    if (!root.GetComponentsInChildren<Component>(true).Any(c => c != null &&
                        c.GetType() == NTModularAvatarBridge.MergeAnimatorType &&
                        (Field(c, "animator") as RuntimeAnimatorController)?.name == "NTLightMenu"))
                        Warn(state, "ADJUSTABLE_NOT_INSTALLED", "⑤ 游戏内可调没有 MA MergeAnimator；请先在编辑器生成光影菜单。", s);
                    continue;
                }
                if (s.targetRoot != null && !Inside(root, s.targetRoot))
                    throw new InvalidOperationException("NonToon ⑤ targetRoot 指向构建 avatar 外部：" + s.name);
                foreach (var rd in s.TargetRenderers())
                foreach (var m in rd.sharedMaterials)
                {
                    if (m == null || !m.HasProperty(s.EffectiveProperty)) continue;
                    if (!writes.TryGetValue(m, out var values))
                        writes[m] = values = new Dictionary<string, float>();
                    if (values.TryGetValue(s.EffectiveProperty, out var previous) && previous != s.fixedValue)
                        throw new InvalidOperationException("NonToon ⑤ 多个组件对同一共享材质属性指定了不同值：" + m.name + "/" + s.EffectiveProperty);
                    values[s.EffectiveProperty] = s.fixedValue;
                }
            }

            int properties = 0;
            foreach (var m in renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
            {
                writes.TryGetValue(m, out var values);
                bool changed = values != null && values.Any(v => ReadNumber(m, v.Key) != ExpectedNumber(m, v.Key, v.Value));
                if (!changed) continue;
                var copy = Copy(state, m);
                if (values != null)
                    foreach (var v in values) { NTSelfLightWriter.SetNumber(copy, v.Key, v.Value); properties++; }
            }
            foreach (var rd in renderers)
            {
                var mats = rd.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && state.Copies.TryGetValue(mats[i], out var copy))
                    { mats[i] = copy; changed = true; }
                if (changed) rd.sharedMaterials = mats;
            }

            var lights = root.GetComponentsInChildren<NTAvatarLight>(true);
            foreach (var s in lights)
            {
                if ((s.target != null && !Inside(root, s.target.transform)) ||
                    (s.directionSource != null && !Inside(root, s.directionSource)))
                    throw new InvalidOperationException("NonToon ① 光源引用指向构建 avatar 外部：" + s.name);
                // Same fields as NTAvatarLightCreator.Sync; constrain renderer scope to this avatar.
                var light = s.target != null ? s.target : s.GetComponent<Light>();
                if (light == null) light = s.gameObject.AddComponent<Light>();
                s.target = light;
                light.type = LightType.Spot;
                light.color = s.color;
                light.useColorTemperature = s.useColorTemperature;
                light.colorTemperature = s.temperature;
                light.intensity = s.intensity;
                light.range = s.range;
                light.spotAngle = s.spotAngle;
                light.shadows = s.shadows;
                light.shadowStrength = Mathf.Clamp01(s.shadowStrength);
                light.shadowBias = s.shadowBias;
                light.cullingMask = s.cullingMask;
                light.renderMode = s.renderMode;
                if (s.directionSource != null)
                {
                    light.transform.localPosition = Vector3.zero;
                    light.transform.rotation = s.directionSource.rotation;
                }
                foreach (var rd in renderers)
                {
                    rd.receiveShadows = true;
                    if (rd.shadowCastingMode == ShadowCastingMode.Off) rd.shadowCastingMode = ShadowCastingMode.On;
                }
            }
            WarnLegacy(root, state);
            // These two components have no runtime behaviour; retain their Light/MA products.
            foreach (var s in settings) Object.DestroyImmediate(s);
            foreach (var s in lights) Object.DestroyImmediate(s);
            Debug.Log($"[NonToon NDMF] Replay complete: properties={properties}, lights={lights.Length}, authoringRemoved={settings.Length + lights.Length}", root);
        }

        static bool Inside(GameObject root, Transform t) => t == root.transform || t.IsChildOf(root.transform);
        static int RawQueue(Material material) => new SerializedObject(material).FindProperty("m_CustomRenderQueue").intValue;
        static float ExpectedNumber(Material m, string p, float v) =>
            m.shader.GetPropertyType(m.shader.FindPropertyIndex(p)) == ShaderPropertyType.Int ? Mathf.RoundToInt(v) : v;
        static float ReadNumber(Material m, string p) =>
            m.shader.GetPropertyType(m.shader.FindPropertyIndex(p)) == ShaderPropertyType.Int ? m.GetInteger(p) : m.GetFloat(p);

        static Material Copy(BuildState state, Material original)
        {
            if (state.Copies.TryGetValue(original, out var copy)) return copy;
            if (state.Copies.ContainsValue(original)) return original;
            copy = new Material(original) { name = original.name + " (NonToon build)" };
            ObjectRegistry.RegisterReplacedObject(original, copy);
            state.Copies.Add(original, copy);
            return copy;
        }

        static object Field(object obj, string name) => obj?.GetType().GetField(name)?.GetValue(obj);
        static bool LegacyParameter(object p) => Convert.ToString(Field(p, "name")) == "NT_Light";

        static void WarnLegacy(GameObject root, BuildState state)
        {
            foreach (var desc in root.GetComponents<Component>().Where(c => c != null && c.GetType().Name == "VRCAvatarDescriptor"))
            {
                if (Field(desc, "baseAnimationLayers") is IEnumerable layers)
                    foreach (var layer in layers)
                    {
                        if (Convert.ToString(Field(layer, "type")) != "FX") continue;
                        var runtime = Field(layer, "animatorController") as RuntimeAnimatorController;
                        if (runtime is AnimatorOverrideController over) runtime = over.runtimeAnimatorController;
                        if (runtime is AnimatorController ctrl && (ctrl.layers.Any(l => l.name.StartsWith(NTVrcParameterBuilder.LayerPrefix, StringComparison.Ordinal)) ||
                            ctrl.parameters.Any(p => p.name == "NT_Light")))
                            Warn(state, "LEGACY_FX", "FX 中有旧 NonToon 层/NT_Light。不能仅凭名称确认归属，未自动删除；请检查并迁移为 MA 声明。", ctrl);
                    }
                if (Field(Field(desc, "expressionParameters"), "parameters") is IEnumerable parameters && parameters.Cast<object>().Any(LegacyParameter))
                    Warn(state, "LEGACY_PARAMETERS", "ExpressionParameters 引用了 NT_Light；未改写用户资产，请检查是否为旧版残留。", desc);
                WarnMenu(Field(desc, "expressionsMenu"), state, new HashSet<object>());
            }
        }

        static void WarnMenu(object menu, BuildState state, HashSet<object> visited)
        {
            if (menu == null || !visited.Add(menu) || !(Field(menu, "controls") is IEnumerable controls)) return;
            foreach (var control in controls)
            {
                if (LegacyParameter(Field(control, "parameter")) ||
                    (Field(control, "subParameters") is IEnumerable ps && ps.Cast<object>().Any(LegacyParameter)))
                    Warn(state, "LEGACY_MENU", "ExpressionsMenu 引用了 NT_Light（含径向 subParameters）；未改写用户资产，请检查是否为旧版残留。", menu as Object);
                WarnMenu(Field(control, "subMenu"), state, visited);
            }
        }

        static void Warn(BuildState state, string code, string message, Object obj)
        {
            if (state.Warnings.Add(code + ":" + (obj != null ? obj.GetInstanceID() : 0)))
                Debug.LogWarning("[NonToon NDMF " + code + "] " + message, obj);
        }
    }
}
