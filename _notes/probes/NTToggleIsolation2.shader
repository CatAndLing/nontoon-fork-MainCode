// 隔离实验 2：找出**哪条写入路径**能让着色器里的整数 uniform 真的拿到值。
//   _P1: ShaderLab Integer + HLSL uint   ← 试 SetInt / SetFloat / SetInteger / SerializedObject
//   _P2: ShaderLab Int     + HLSL int    ← 试 SetInt / SetFloat
//   _P3: ShaderLab Float   + HLSL float  ← 对照组（这个必须通）
//   _P4: ShaderLab Range   + HLSL float  ← 对照组
// 输出：把每个属性是否"着色器真的读到非 0"编码成颜色，探针解出来。
Shader "NTToggleIsolation2"
{
    Properties
    {
        _P1 ("P1 Integer/uint", Integer) = 0
        _P2 ("P2 Int/int", Int) = 0
        _P3 ("P3 Float/float", Float) = 0
        _P4 ("P4 Range/float", Range(0, 1)) = 0
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

            // 故意用 4 个独立 uniform，互不干扰
            uint  _P1;
            int   _P2;
            float _P3;
            float _P4;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed3 c = fixed3(0, 0, 0);
                if (_P1 != 0) c.r = 1;
                if (_P2 != 0) c.g = 1;
                if (_P3 != 0) c.b = 1;
                c.r += _P4 * 0.5;   // P4 走红色半档，便于与 P1 区分
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
