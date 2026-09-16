using UnityEngine;
using UnityEngine.Rendering;

namespace NonToonTools
{
    // [NT-REFACTOR 0.5.0] SelfLight 材质属性写入的**唯一实现**。
    //
    // 为什么单独抽出来：现在有两个组件会写同一批属性 —— ② `NTSelfLight`（手动模式：光源来自场景里的 Light）
    // 与 ④ `NTSelfLitShadow`（自带光照与阴影：光源是组件上的虚拟方向）。两份实现必然漂移，
    // 所以写入逻辑只留这一份，两个面板都调它。
    //
    // 硬规则（踩过的坑）：NonToon 的 `SC_uint` / ShaderLab `Integer` 属性**必须用 SetInteger**，
    // 用 SetFloat / SetInt 写是**静默无效**的（`_ShadowColorEnable` 那次就是因此写不进去）。
    internal static class NTSelfLightWriter
    {
        internal struct Settings
        {
            public bool enabled;            // _UseSelfLight
            public bool onlyThisLight;      // _SelfLightOnly
            public Color color;
            public bool useTemperature;
            public float temperature;
            public float intensity;
            public Vector3 towardLight;     // 世界空间、**指向光源**（与材质属性同名同义）；零向量时跳过

            public bool setLimits;          // 是否同时写亮度上下限（④ 用；② 不写，保持旧行为）
            public float minLimit;          // _LightMinLimit
            public float maxLimit;          // _LightMaxLimit

            public bool matchAmbient;
            public float matchAmbientAmount;
            public float matchDirectionAmount;

            public bool pcss;
            public int quality;
            public float softness;
            public float density;
            public float clamp;
            public float distance;
            public float bias;
            public float shadowStrength;

            public Texture2D receiveMask;
            public int receiveMaskChannel;
            public float receiveMaskStrength;

            public int lightId;             // 0 = 不限制
            public bool stampId;
        }

        internal static void Write(Material material, Settings s, NTSelfShadowBaker.Result baked)
        {
            if (material == null) return;

            SetNumber(material, "_UseSelfLight", s.enabled ? 1f : 0f);
            SetNumber(material, "_SelfLightOnly", s.onlyThisLight ? 1f : 0f);
            material.SetColor("_SelfLightColor", s.color);
            SetNumber(material, "_SelfLightIntensity", s.intensity);
            if (s.towardLight.sqrMagnitude > 1e-8f)
                material.SetVector("_SelfLightDirection", s.towardLight.normalized);

            SetNumber(material, "_SelfLightUseTemperature", s.useTemperature ? 1f : 0f);
            SetNumber(material, "_SelfLightTemperature", s.temperature);
            SetNumber(material, "_SelfLightMatchAmbient", s.matchAmbientAmount);
            SetNumber(material, "_SelfLightMatchDirection", s.matchDirectionAmount);

            SetNumber(material, "_SelfLightPCSS", s.pcss ? 1f : 0f);
            SetNumber(material, "_SelfLightPCSSQuality", s.quality);
            SetNumber(material, "_SelfLightSoftness", s.softness);
            SetNumber(material, "_SelfLightDensity", s.density);
            SetNumber(material, "_SelfLightClamp", s.clamp);
            SetNumber(material, "_SelfLightDistance", s.distance);
            SetNumber(material, "_SelfLightShadowBias", s.bias);
            SetNumber(material, "_SelfLightShadowStrength", s.shadowStrength);

            if (s.setLimits)
            {
                SetNumber(material, "_LightMinLimit", s.minLimit);
                SetNumber(material, "_LightMaxLimit", s.maxLimit);
            }

            if (s.stampId && s.lightId != 0) SetNumber(material, "_SelfLightId", s.lightId);

            if (material.HasProperty("_SelfLightReceiveMask"))
            {
                material.SetTexture("_SelfLightReceiveMask", s.receiveMask);
                SetNumber(material, "_SelfLightReceiveMaskChannel", s.receiveMaskChannel);
                SetNumber(material, "_SelfLightReceiveMaskStrength",
                    s.receiveMask == null ? 0f : s.receiveMaskStrength);
            }

            if (baked != null && baked.map != null) WriteBaked(material, baked);
        }

        // 把烘焙结果的基与深度图写进材质（② 与 ④ 共用）
        internal static void WriteBaked(Material material, NTSelfShadowBaker.Result baked)
        {
            if (material == null || baked == null || baked.map == null) return;
            material.SetTexture("_SelfLightShadowMap", baked.map);
            material.SetVector("_SelfLightOrigin", baked.origin);
            material.SetVector("_SelfLightRight", baked.right);
            material.SetVector("_SelfLightUp", baked.up);
            material.SetVector("_SelfLightForward", baked.forward);
            SetNumber(material, "_SelfLightHalfX", baked.halfX);
            SetNumber(material, "_SelfLightHalfY", baked.halfY);
            SetNumber(material, "_SelfLightNear", baked.near);
            SetNumber(material, "_SelfLightFar", baked.far);
            SetNumber(material, "_SelfLightShadowTexels", baked.resolution);
        }

        // 只把已存在的阴影贴图重新挂上（"只同步参数"路径）
        internal static void WriteExistingMap(Material material, Texture2D map)
        {
            if (material != null && map != null) material.SetTexture("_SelfLightShadowMap", map);
        }

        // Int 属性走 SetInteger，其余走 SetFloat —— 这一条不能省（见文件头）
        internal static void SetNumber(Material material, string property, float value)
        {
            if (material == null || string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
            var index = material.shader.FindPropertyIndex(property);
            if (index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Int)
                material.SetInteger(property, Mathf.RoundToInt(value));
            else material.SetFloat(property, value);
        }

        // 由 forward（光线传播方向）造一组正交基（烘焙器要求 right/up 与 forward 正交）
        internal static void OrthonormalBasis(Vector3 forward, out Vector3 right, out Vector3 up)
        {
            forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
            var reference = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
            right = Vector3.Cross(reference, forward).normalized;
            up = Vector3.Cross(forward, right).normalized;
        }
    }
}
