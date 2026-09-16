void SCCalculateEnvironmentLight(inout SCLightData lightSum, inout half3 env, inout SCShadingData sd, inout SCCustomData cd, SCVertexData vertex, half4 SHAr, half4 SHAg, half4 SHAb, half4 SHBr, half4 SHBg, half4 SHBb, half4 SHC)
{
    half4 vB = vertex.Head.xyzz * vertex.Head.yzzx;
    // L0 L2 average
    half3 res = float3(SHAr.w,SHAg.w,SHAb.w);
    res += float3(SHBr.z, SHBg.z, SHBb.z) * 0.333333;
    // L1
    half3 l1;
    l1.r = dot(SHAr.rgb, vertex.Head);
    l1.g = dot(SHAg.rgb, vertex.Head);
    l1.b = dot(SHAb.rgb, vertex.Head);
    half3 envF = res + max(l1 * 0.666666, 0);
    half3 envB = res - l1 * 0.666666;
    #if defined(UNITY_COLORSPACE_GAMMA)
        envF = LinearToGammaSpace(envF);
        envB = LinearToGammaSpace(envB);
    #endif
    sd.L = lightSum.direction + SHAr.rgb * 0.333333 + SHAg.rgb * 0.333333 + SHAb.rgb * 0.333333;
    sd.L = dot(sd.L,sd.L) == 0 ? 0 : normalize(sd.L);

    half NdotL = dot(sd.N,sd.L);
    half VdotL = dot(vertex.Head,sd.L);
    half fakerim = saturate((NdotL - VdotL - 0.5) * 2) * saturate(NdotL*3);
    // [NT-FIX 2] urp.hlsl gates the fake environment back-rim by cd.screenrim; BiRP did not,
    // so this environment term showed through occluding geometry. Matches URP now.
    env += envF + saturate(envB - envF) * fakerim * fakerim * cd.screenrim;

    // Rampの影を乗算するので少し明るくしてバランスをとる
    env *= 1.2;
}

void SCCalculateLight(inout SCLightData lightSum, inout SCShadingData sd, inout SCCustomData cd, SCVertexData vertex, SCLightData light)
{
    __SC_PHASE_light__

    lightSum.direction += light.direction * dot(light.color, 0.333333);
    {
        half factor = saturate(dot(light.direction,vertex.Head) * 1 + 0.25);
        // [NT-FIX 1] BiRP was missing the falloff sharpening that urp.hlsl applies here.
        // Without it BiRP lights fall off linearly instead of quadratically, so the same
        // material reads flatter / lower-contrast than in URP. Also affects the `rim`
        // threshold below, which is derived from `factor`.
        factor *= factor;
        half NdotL = dot(sd.N,light.direction);
        half NdotH = dot(sd.N,vertex.Head);
        half mix = (_BacklightRange-NdotH+NdotL)*_BacklightSharpness+0.5;
        half rim = saturate((mix - (factor*0.75+0.25))) * cd.screenrim * NTMaskValue(sd.mask, _BacklightMaskChannel);
        #ifdef OUTLINE
        rim = 0;
        #endif
        light.color *= saturate(factor + rim * rim);
    }
    lightSum.color += light.color;
}

#include "Packages/jp.lilxyzw.shadercore/ShaderLibrary/birp_lighting.hlsl"

half4 frag(v2f i, bool isFront : SV_IsFrontFace) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    SCPositionAndDirection camera = SCGetCameraData();
    SCPositionAndDirection head = SCGetHeadData();
    SCPositionAndDirection headBone = SCGetHeadBoneData();
    SCVertexData vertex = FromPixelInput(i, camera, head, headBone, SCTangentScale(), isFront);
    vertex.shadowOffset = _NTShadowBias * 0.5;

    // Custom Data
    SCCustomData cd = (SCCustomData)0;

    // Screen Space Rimlighting
    cd.screenrim = 0;
    #ifdef OUTLINE
    // [NT-FIX 3] In the outline pass every rim term is discarded (see the `#ifdef OUTLINE rim = 0;`
    // in SCCalculateLight below) and SCCalculateEnvironmentLight never reads cd.screenrim in BiRP,
    // so this block's result is always dead there. It nevertheless cost 8 frame-depth taps per
    // outline pixel - and the outline pass is a full extra draw over every mesh. Skip it and keep
    // the variable valid by using 1 ("nothing occludes me"), the same value the no-depth-texture
    // branch below produces, so any module that reads cd.screenrim still behaves sensibly.
    cd.screenrim = 1;
    #else
    if (SCIsFrameDepthGenerated())
    {
        float random = frac(sin(dot(vertex.uvDepth.xy,float2(12.9898,78.233))) * 36203.4357);
        float2 normalVS = normalize(mul((float3x3)SC_W2V(), vertex.N)).xy * abs(SC_V2P()._m00_m11/vertex.positionRaw.w) * 0.02;
        float maxcount = 8;
        for (int count = 0; count < maxcount; count++)
        {
            float2 uv = vertex.uvDepth + normalVS * (count + random) / maxcount;
            float depth = SCGetFrameDepth(uv);
            cd.screenrim = saturate(cd.screenrim + saturate((depth - vertex.positionRaw.w - 0.2) * 2) / maxcount);
        }
    }
    else
    {
        cd.screenrim = 1;
    }
    #endif

    // Main Texture
    SCShadingData sd;
    sd.L = 0;
    sd.lightColor = 0;
    sd.shadow = 1;
    sd.add = 0;
    sd.postadd = 0;
    sd.uv = vertex.uv[0].xy;
    sd.albedoAlpha = SCSample(_BaseTexture, sampler_BaseTexture, sd.uv);
    sd.mask = SCSample(_SharedMask, sampler_BaseTexture, sd.uv);
    sd.roughness = _Roughness;
    sd.normalMapWithRoughness = _NormalMapWithRoughness;
    sd.N = SCUnpackNormalAndRoughness(SCSample(_NormalMap, sampler_BaseTexture, sd.uv), _NormalScale, sd.roughness, sd.normalMapWithRoughness);
    sd.N_detail = sd.N;
    sd.maskTexture = _SharedMask;
    sd.gradientsTexture = _SharedGradients;

    __SC_PHASE_base__

    sd.albedoAlpha = saturate(sd.albedoAlpha);
    sd.col = sd.albedoAlpha;

    sd.N = normalize(mul(sd.N, vertex.TBN));
    sd.N_detail = normalize(mul(sd.N_detail, vertex.TBN));
    sd.T = normalize(vertex.T - sd.N_detail * dot(sd.N_detail, vertex.T));
    sd.B = normalize(cross(sd.N_detail, sd.T) * vertex.crossDirection * SCTangentScale());

    sd.roughness = saturate(_NormalMapWithRoughness ? sd.roughness : _Roughness);

    half3 dx = ddx(sd.N);
    half3 dy = ddy(sd.N);
    half dxy = max(dot(dx,dx), dot(dy,dy));
    half roughnessGSAA = saturate(dxy / (dxy * 5 + 0.002) * 0.5);

    sd.roughness = max(sd.roughness, roughnessGSAA);

    SCLightData lightSum = (SCLightData)0;
    half3 env = 0;
    SCCalculateAllLights(lightSum, env, sd, cd, vertex, i);

    // [NT-FEAT 3] upstream issue #9: this line used to hardcode the view bias at 1.5, which
    // makes the toon shade direction follow the *camera* far more than the actual light.
    // In a side-lit scene the ramp then calls the camera-facing surface "lit" while the
    // surface that really faces the light is only at the ramp's mid point - and because the
    // direct-light multiplier below is also view-driven (it collapses to ~0.06 when the light
    // is perpendicular to the view), the face that should be lit ends up darker than the one
    // in shadow. Exposed so it can be dialled down; 0 = shade follows the real light,
    // 1.5 = original behaviour (default, nothing changes for existing materials).
    sd.L = normalize(sd.L + vertex.Head * _ShadeDirectionBias + float3(0,1,0));

    // lighting in the VRChat world is not set up correctly
    //sd.lightColor = env + lightSum.color * lerp(0.5, 1, saturate(rcp(dot(lightSum.color,1))));
    // [NT-FEAT 1] lilToon-compatible lighting adjustment, ported from lilToon's
    // LIL_CORRECT_LIGHTCOLOR_* macro (lil_common_macro.hlsl:2081-2083), which is exactly:
    //   clamp(min,max) -> lerp(gray) -> lerp(1.0, AsUnlit)
    // The previous hardcoded `saturate()` was equivalent to a (0,1) clamp, hence the defaults
    // _LightMinLimit = 0 / _LightMaxLimit = 1 reproduce it. Raise Min to lift dark areas
    // (lilToon's own default is 0.05); raise Max above 1 to let highlights exceed white.
    half3 lightColor = env + lightSum.color;
    // [NT-FEAT 27] 亮度上下限 = **按亮度归一化**，不是逐通道 clamp。与 urp.hlsl 保持同步。
    //   逐通道 clamp 的毛病：Min>0 时把每个通道各自推上去 ⇒ 暗部直接变灰
    //   （(0.05,0.02,0.01) 在 Min=0.25 下变成 (0.25,0.25,0.25)），色相和明暗比例一起被拍平 ——
    //   这正是我们先前废弃「菜单抬高 Min」的原因（断点 §4 方向 ⑤）。
    //   改成按亮度缩放：`c *= clamp(luma, lo, hi) / luma` ⇒ 只动亮度，**色相与通道比例逐位不变**。
    //   ⚠️ 默认值必须与上游逐位一致：上游这一行是 `saturate(env + lightSum.color)` = `clamp(c,0,1)`。
    //      而"按亮度"在某个通道 >1 时不会裁剪 ⇒ 默认值下两者**不等价**
    //      ⇒ 所以默认值走原路（逐通道 clamp），**只有用户真的动过旋钮才切到归一化路径**。
    //      两条路径在 lo→0+ / hi→1+ 处连续（luma≤1 时系数恒为 1），不存在跳变。
    //   （写法师承 com.atrinaxu.nontoon.lightlimit 的 phase_modifylight.hlsl，MIT。）
    half3 ntLimited = clamp(lightColor, _LightMinLimit, _LightMaxLimit);
    if (_LightMinLimit != 0.0 || _LightMaxLimit != 1.0)
    {
        // ⚠️ 必须在**未裁剪的** lightColor 上算：旧公式是 `clamp(c, 0, _LightMaxLimit)`,
        //    Max>1（菜单的"增亮"档）时允许 lightColor 超过 1；先 clamp 到 1 会把增亮吃掉。
        half ntLo = min(_LightMinLimit, _LightMaxLimit);
        half ntHi = max(_LightMinLimit, _LightMaxLimit);
        half ntLuma = max(dot(lightColor, half3(0.2126, 0.7152, 0.0722)), 1e-5);
        ntLimited = lightColor * (clamp(ntLuma, ntLo, ntHi) / ntLuma);
    }
    lightColor = ntLimited;
    half lightGray = dot(lightColor, half3(0.333333, 0.333333, 0.333333));
    lightColor = lerp(lightColor, half3(lightGray, lightGray, lightGray), _MonochromeLighting);
    lightColor = lerp(lightColor, 1.0, _AsUnlit);
    sd.lightColor = lightColor;

    __SC_PHASE_modifylight__

    __SC_PHASE_shade__

    __SC_PHASE_reflection__

    __SC_PHASE_add__

    #ifdef NT_FUR
    sd.add += i.customV2f.furVector.z * pow(saturate(1-abs(dot(normalize(sd.N), vertex.V))), _FurRimFresnelPower) * lerp(1, saturate(1-sd.lightColor), _FurRimAntiLight) * _FurRimColor.rgb;
    #endif

    sd.col.rgb += sd.add;
    sd.col.rgb *= sd.lightColor;
    // [NT-FIX 5] HairSpecular and RimLight already attenuate their contribution by sd.shadow;
    // Specular was the only additive module that did not, so highlights stayed at full strength
    // inside cast shadow. sd.shadow is finalised by the Shade phase above (1 = lit, 0 = shadow),
    // so this makes Specular consistent with its sibling modules. Revert by dropping `* sd.shadow`.
    sd.col.rgb += sd.postadd * sd.shadow;

    __SC_PHASE_postpixel__

    #ifdef OUTLINE
    sd.col.rgb *= _OutlineColor.rgb;
    #endif

    #ifdef NT_FUR
    sd.col.a = saturate((SCSampleRepeat(_FurNoiseMask, sd.uv * _FurNoiseTiling).r - i.customV2f.furVector.z * abs(i.customV2f.furVector.z)) * 3);
    if (sd.col.a == 0) discard;
    #else
    if (_RenderingMode == 0)
    {
        sd.col.a = 1;
    }
    else if (_RenderingMode == 1)
    {
        if (_NTDitherTex[uint2(0,0)].a != 0)
        {
            clip(sd.col.a - (_NTDitherTex[uint2(i.pos.xy)%4].r * 255 + 1) / (15+2));
        }
        else
        {
            sd.col.a = saturate((sd.col.a - _Cutoff) / max(fwidth(sd.col.a), 0.0001) + 0.5);
            if (sd.col.a == 0) discard;
        }
    }
    else if (_RenderingMode == 2)
    {
#if defined(NT_FORWARDBACK)
        // [NT-FEAT 26] 两趟透明 · **背面趟**（只有 NonToonTwoPass 的 ForwardBack pass 定义该宏）。
        //   与 lilToon 同构（lil_pass_forward_normal.hlsl:406-409 / LIL_TRANSPARENT_PRE）：
        //     fd.col *= _PreColor;  clip(fd.col.a - _PreCutoff);
        //   ★ 用**自己的** _PreCutoff（lilToon 默认 0.5），不是 _Cutoff。
        //     实测 age 材质 _Cutoff=0.136 / _PreCutoff=0.5 ⇒ 背面趟只画 alpha≥0.5 的那部分物体。
        //     沿用 _Cutoff 会让背面趟覆盖整个物体（实测混合落地率 0.80 vs lilToon 0.43）。
        sd.col *= _PreColor;
        clip(sd.col.a - _PreCutoff);
#else
        // [NT-FIX 8] Transparent: 保留 alpha 供混合，不裁剪。
#endif
    }
    #endif

    UNITY_APPLY_FOG(i.fogCoord, sd.col);
    return sd.col;
}
