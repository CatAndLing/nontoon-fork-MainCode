// [NT-FEAT 20] ⑥ 实时自阴影：**我们自己渲染**的阴影深度图（replacement shader）。
//
// ── 为什么要自己渲染深度 ───────────────────────────────────────────────────
// Unity 的逐光源阴影贴图在 D3D11 上只给出**比较采样器**
// （`UNITY_DECLARE_SHADOWMAP` → `Texture2D_float` + `SamplerComparisonState`），
// 比较采样只返回 0/1，拿不到遮挡物深度 ⇒ PCSS 的 blocker 搜索**做不了**。
// 实测把其余路全部排除了（详见 `_notes/续接-实时PCSS实现.md` §9.6/§9.10）：
//   · `Texture2D.Load` 读原始深度 → **恒为 0**
//   · 自声明 `SamplerState` + `SampleLevel` → `no matching 3 parameter intrinsic`
//   · `Shader.GetGlobalTexture("_ShadowMapTexture")` → 只解析出 4×4 占位符
//   · `Shader.SetGlobalTexture` 自绑 → 拿不到句柄
// ⇒ 唯一可靠的办法：**自己渲一张**。
//
// ── 输出约定（与内核严格一致）──────────────────────────────────────────────
//   存 **线性深度**：`d = (到光源距离 - near) / (far - near)`，范围 0..1、**越大越远**。
//   这样内核里不需要再做 reversed-Z 归一化，直接可用。
//   没有几何的地方保持 1.0（远）⇒ blocker 搜索会自然忽略。
//
// 用法：由 `NTSelfRealtimeShadow` 挂到一台临时相机上，用
//   `cam.SetReplacementShader(Shader.Find("NonToon/NTRTDepth"), "NTRTDepthPass")`
//   渲染到 RenderTexture，再把它绑到材质的 `_NTRTShadowMapRaw`。
Shader "NonToon/NTRTDepth"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "NTRTDepthPass" = "On" }   // SetReplacementShader 用这个 tag 匹配
            // ⚠️⚠️ 2026-09-16 实测定位：**不能用深度缓冲做最近面归约**。
            //   平台是 `usesReversedZBuffer`（近=1、远=0），而我们习惯写的
            //   `ZTest LEqual` 在 reversed-Z 下语义正好相反：
            //   实测"先画接收面、再画球"时，**球的片元被深度测试拒绝** ——
            //   深度图里只剩接收面，球整个消失（`BOTH` 与 `ONLY PLANE` 逐像素相同）。
            //   这会让 PCSS 的 blocker 搜索永远搜不到遮挡物，表现成"没有半影"。
            //   修法：**彻底不用深度缓冲**，改用硬件的 **min 混合**做最近面归约：
            //     `Blend One One / BlendOp Min` ⇒ dst = min(src, dst)
            //   背景清成 1.0（远），任何几何写进去的都是更小的距离 ⇒ 最终得到
            //   "到光源最近表面"的线性距离图。与 ZTest / reversed-Z / 平台惯例全部无关。
            //   配套：Cull Off（正反两面都要参与 min）、ZWrite Off、ZTest Always。
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One
            BlendOp Min
            ColorMask R
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float dist : TEXCOORD0; };

            float _NTRTDepthNear;
            float _NTRTDepthFar;
            float3 _NTRTDepthLightPos;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.dist = distance(wpos, _NTRTDepthLightPos);
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                float d = (i.dist - _NTRTDepthNear) / max(_NTRTDepthFar - _NTRTDepthNear, 1e-4);
                return saturate(d);
            }
            ENDCG
        }
    }
    Fallback Off
}
