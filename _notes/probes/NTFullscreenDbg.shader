// [NT-DIAG] 全屏 debug 着色器：一次性把"附加光与阴影"的所有中间量画出来。
//
// 为什么需要它：在 NonToon 的 phase 里打 debug 输出要经过
//   材质属性 → include 全局量 → phase → light.color → 色调映射 → 后处理
// 太多层，前面几轮反复被"到底哪一层把值吃掉了"困住。
// 这个着色器**只保留 ForwardAdd + Unity 的阴影宏**，其余全部去掉，
// 于是"能不能读阴影贴图""阴影系数是多少"这些问题的答案就是画面本身。
//
// 用法：NTFullscreenDbgRun 用 Graphics.Blit + 该材质逐 _Mode 出图并读回中心像素。
// 场景必须有一盏开阴影的 Spot（Blit 也会走光照 pass）。
Shader "NTFullscreenDbg"
{
    Properties { _Mode ("Mode", Float) = 0 }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdadd_fullshadows
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wp : TEXCOORD0;
                float2 uv : TEXCOORD1;
                LIGHTING_COORDS(2, 3)
            };
            float _Mode;
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = v.uv;
                TRANSFER_VERTEX_TO_FRAGMENT(o)
                return o;
            }
            // Mode:
            //  1 light.color.r / _LightColor0.r（本轮踩的坑：这是 0/1 吗？）
            //  2 上面那个 × spotAttenuation 反推出来的"阴影系数"
            //  3 二分问出的遮挡深度
            //  4 片元深度
            //  5 SampleCmpLevelZero(uv, 0.999)  ← 应恒 0
            //  6 SampleCmpLevelZero(uv, 0.001)  ← 应恒 1
            //  7 Unity 的 UNITY_LIGHT_ATTENUATION（= 衰减 × 阴影）
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_LIGHT_ATTENUATION(atten, i, i.wp);
                float4 sc = mul(unity_WorldToShadow[0], float4(i.wp, 1.0));
                float3 c = sc.xyz / sc.w;
                float ratio = _LightColor0.r > 1e-6 ? (atten / _LightColor0.r) : 0.0;

                float v;
                if (_Mode < 1.5) v = ratio;
                else if (_Mode < 2.5)
                {
                    // 用 AutoLight 自己的两个 inline 函数反推非阴影衰减
                    unityShadowCoord4 lc = mul(unity_WorldToLight, unityShadowCoord4(i.wp, 1));
                    float spot = (lc.z > 0) * UnitySpotCookie(lc) * UnitySpotAttenuate(lc.xyz);
                    v = spot > 1e-4 ? saturate(atten / (_LightColor0.r * spot)) : 0.0;
                }
                else if (_Mode < 3.5)
                {
                    float lo = 0.0, hi = c.z;
                    for (int k = 0; k < 10; k++)
                    {
                        float mid = 0.5 * (lo + hi);
                        if (UNITY_SAMPLE_SHADOW(_ShadowMapTexture, float4(c.xy, mid, 1)) > 0.5) lo = mid;
                        else hi = mid;
                    }
                    v = 0.5 * (lo + hi);
                }
                else if (_Mode < 4.5) v = c.z;
                else if (_Mode < 5.5) v = UNITY_SAMPLE_SHADOW(_ShadowMapTexture, float4(c.xy, 0.999, 1));
                else v = UNITY_SAMPLE_SHADOW(_ShadowMapTexture, float4(c.xy, 0.001, 1));
                return fixed4(v, v, v, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
