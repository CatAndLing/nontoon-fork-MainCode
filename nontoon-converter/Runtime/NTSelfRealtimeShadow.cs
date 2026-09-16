using UnityEngine;

namespace NonToon
{
    // Realtime self-shadow controller. Unity owns the shadow map; the shader samples it.
    // The former self-rendered linear-depth/CommandBuffer PCSS path was removed because
    // it had no package entry point and was explicitly out of scope.
    [AddComponentMenu("NonToon/Realtime Shadow (PCSS)")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class NTSelfRealtimeShadow : MonoBehaviour
    {
        [Tooltip("Spot light that provides the shadow. Leave empty to find a child Spot automatically.")]
        public Light targetLight;
        [Tooltip("Master switch for the realtime self-shadow rig.")]
        public bool pcssOn = false;
        [Tooltip("Virtual light size used by the shader PCSS filter.")]
        [Range(0f, 512f)] public float lightSize = 64f;
        [Tooltip("Additional softness multiplier used by the shader PCSS filter.")]
        [Range(0f, 2f)] public float softness = 1f;
        [Tooltip("Depth bias in metres used by the shader PCSS filter.")]
        [Range(0f, 0.05f)] public float biasMeters = 0.01f;
        [Tooltip("PCSS quality level: 0 = 8/12, 1 = 12/24, 2 = 16/32, 3 = 24/32 samples.")]
        [Range(0f, 3f)] public float quality = 3f;
        [Tooltip("Fraction of light retained in fully shadowed areas (0 = black).")]
        [Range(0f, 0.95f)] public float shadowFloor = 0.25f;
        [Tooltip("Light intensity driven by this component.")]
        [Range(0f, 30f)] public float lightIntensity = 0.8f;
        [Tooltip("Light colour driven by this component.")]
        public Color lightColor = Color.white;
        [Tooltip("Blend the light hue toward the scene ambient hue at runtime.")]
        [Range(0f, 1f)] public float followAmbient = 0f;

        static readonly int IdRtEnabled = Shader.PropertyToID("_NTRTShadowEnabled");
        static readonly int IdRtQuality = Shader.PropertyToID("_NTRTShadowQuality");
        static readonly int IdRtSoftness = Shader.PropertyToID("_NTRTShadowSoftness");
        static readonly int IdRtFloor = Shader.PropertyToID("_NTRTShadowFloor");
        static readonly int IdRtBias = Shader.PropertyToID("_NTRTShadowBias");
        static readonly int IdRtTanHalf = Shader.PropertyToID("_NTRTShadowTanHalfAngle");
        static readonly int IdRtPos = Shader.PropertyToID("_NTRTShadowLightPos");

        void OnEnable()
        {
            if (targetLight == null) targetLight = GetComponentInChildren<Light>();
            RenderDepth();
        }

        void OnDisable()
        {
            Shader.SetGlobalFloat(IdRtEnabled, 0f);
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(IdRtEnabled, 0f);
        }

        void LateUpdate()
        {
            if (targetLight == null) return;
            RenderDepth();
        }

        void SuspendRig()
        {
            if (targetLight != null && targetLight.enabled) targetLight.enabled = false;
            Shader.SetGlobalFloat(IdRtEnabled, 0f);
        }

        void RestoreRig()
        {
            if (targetLight == null) return;
            if (!targetLight.enabled) targetLight.enabled = true;
            if (targetLight.shadows == LightShadows.None) targetLight.shadows = LightShadows.Hard;
        }

        void PushShadowGlobals()
        {
            if (targetLight == null) return;
            Shader.SetGlobalFloat(IdRtEnabled, 1f);
            Shader.SetGlobalFloat(IdRtQuality, quality);
            Shader.SetGlobalFloat(IdRtSoftness, lightSize * 0.0005f);
            Shader.SetGlobalFloat(IdRtFloor, shadowFloor);
            Shader.SetGlobalFloat(IdRtBias, biasMeters);
            Shader.SetGlobalFloat(IdRtTanHalf, Mathf.Tan(targetLight.spotAngle * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalVector(IdRtPos, targetLight.transform.position);
            if (!Mathf.Approximately(targetLight.intensity, lightIntensity)) targetLight.intensity = lightIntensity;

            var wantColor = lightColor;
            if (followAmbient > 0.001f)
            {
                var ambient = NTAmbient.Read();
                if (ambient.valid) wantColor = Color.Lerp(lightColor, NTAmbient.HueOnly(ambient.linear), followAmbient);
            }
            if (targetLight.color != wantColor) targetLight.color = wantColor;
        }

        /// <summary>
        /// Synchronises the component with Unity's realtime shadow map. The method name is
        /// retained for serialized/editor callers; no custom depth render target is created.
        /// </summary>
        public void RenderDepth()
        {
            if (targetLight == null) return;
            if (!pcssOn) { SuspendRig(); return; }
            RestoreRig();
            PushShadowGlobals();
        }
    }
}
