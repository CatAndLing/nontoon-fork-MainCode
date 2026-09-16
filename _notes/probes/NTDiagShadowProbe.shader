// [NT-DIAG] 最小隔离着色器：**只**测"Unity 的实时阴影贴图能不能被我们读出来"。
//
// 为什么需要它：在 ShaderCore 生成的 NonToon 里排查这条通路时，干扰因素太多
// （phase 展开位置、includes 与材质属性的声明顺序、pass 复用、多光源、色调映射…），
// 一轮要几分钟。这个着色器把变量压到最少：
//   · 就是 Unity 的标准 ForwardAdd 写法（AutoLight.cginc 的宏，不做任何改造）
//   · 片段里把若干"能读到的量"直接画成灰度，一眼就能看出哪个可用
//
// 用法：NTDiagShadowProbe 会造一个 球(遮挡)+板(接收)+Spot(Hard) 的场景，
//       用 `_Mode` 切换输出，逐模式抓中心像素。
//
// `_Mode` 的含义（每档都是"这个量到底有没有值"的直接检验）：
//   0 = UNITY_LIGHT_ATTENUATION 的阴影项（Unity 自己的判定，应当 0/1）
//   1 = SampleCmpLevelZero(uv, 0.001)  → 期望恒 1（贴图里什么都比 0.001 远）
//   2 = SampleCmpLevelZero(uv, 0.999)  → 期望恒 0（什么都比 0.999 近）
//   3 = 二分 10 次问出的遮挡深度      → 应当约等于真实的 z_stored
//   4 = 本片元的阴影坐标 z（透视除法后）
//   5 = 阴影坐标 uv.x
//   6 = 阴影坐标 uv.y
//   7 = Texture2D.Load(px).r          → 检验非比较读取是否可用
Shader "NTDiagShadowProbe"
{
    Properties
    {
        _Mode ("Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fwdadd_fullshadows
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wp : TEXCOORD0;
            };
            float _Mode;
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // 照 AutoLight.cginc 自己在 ForwardBase 里的写法算阴影坐标
                // （那里是 `unityShadowCoord4 spotShadowCoord = mul(unity_WorldToShadow[0], float4(worldPos,1))`，
                //   不走 v2f 插值 —— 用 TRANSFER_SHADOW 会因为 fwdbase 不定义 SHADOWS_DEPTH 而报
                //   `invalid subscript '_ShadowCoord'`，踩过）
                float4 sc = mul(unity_WorldToShadow[0], float4(i.wp, 1.0));
                float3 c = sc.xyz / sc.w;
                float v;
                if (_Mode < 0.5) v = UnitySampleShadowmap(sc);
                else if (_Mode < 1.5) v = _ShadowMapTexture.SampleCmpLevelZero(sampler_ShadowMapTexture, c.xy, 0.001);
                else if (_Mode < 2.5) v = _ShadowMapTexture.SampleCmpLevelZero(sampler_ShadowMapTexture, c.xy, 0.999);
                else if (_Mode < 3.5)
                {
                    float lo = 0.0, hi = c.z;
                    for (int k = 0; k < 10; k++)
                    {
                        float mid = 0.5 * (lo + hi);
                        if (_ShadowMapTexture.SampleCmpLevelZero(sampler_ShadowMapTexture, c.xy, mid) > 0.5) lo = mid;
                        else hi = mid;
                    }
                    v = 0.5 * (lo + hi);
                }
                else if (_Mode < 4.5) v = c.z;
                else if (_Mode < 5.5) v = c.x;
                else if (_Mode < 6.5) v = c.y;
                else
                {
                    uint w, h; _ShadowMapTexture.GetDimensions(w, h);
                    int2 px = clamp(int2(c.xy * float2(w, h)), int2(0, 0), int2(w, h) - 1);
                    v = _ShadowMapTexture.Load(int3(px, 0)).r;
                }
                return fixed4(v, v, v, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
