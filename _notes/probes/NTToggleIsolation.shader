// 隔离实验用的最小着色器：模仿 ShaderCore 对 SC_uint 的生成结果
//   ShaderLab 属性类型 = Integer，HLSL uniform = uint
// 目的：判定「Material.SetInt 写 Integer 属性 + shader 里 uint uniform」到底通不通。
// 通了 → 问题在 NonToon 别处；不通 → SC_uint 模块开关全都是死的（严重）。
Shader "NTToggleIsolation"
{
    Properties
    {
        _T ("Toggle T", Integer) = 0
        _T2 ("Toggle T2", Integer) = 0
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
            #include "UnityCG.cginc"

            uint _T;
            uint _T2;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // _T=1 → 红；否则绿。_T2=1 时叠加蓝，用来验证第二个开关
                fixed3 c = _T ? fixed3(1, 0, 0) : fixed3(0, 1, 0);
                if (_T2) c += fixed3(0, 0, 1);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
