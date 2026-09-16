struct SCCustomData
{
    half screenrim;
};

// [NT-FEAT 6] 共有マスクのチャンネル取得（反転つき）。upstream issue #10 に対応。
// チャンネル選択プロパティは 8 択の enum になった:
//   0-3 = R / G / B / A、4-7 = 1-R / 1-G / 1-B / 1-A
// 0-3 は従来と完全に同じ値なので、既存マテリアルの見た目は変わらない。
// 引数を float で受けるのは、SC_uint が生成する型（int/uint/float のいずれでも）を
// 暗黙変換でそのまま受けられるようにするため。
half NTMaskValue(half4 mask, float channel)
{
    int c = (int)channel;
    half value = mask[c & 3];
    return (c & 4) ? (1.0 - value) : value;
}

void SCVertexMorph(inout SCVertexData vertex, SCPositionAndDirection camera, SCPositionAndDirection head, SCPositionAndDirection headBone)
{
    __SC_PHASE_morph__
}

void SCVertexPost(inout SCVertexData vertex, SCPositionAndDirection camera, SCPositionAndDirection head, SCPositionAndDirection headBone, float3 L = 0)
{
    #ifdef OUTLINE
    float3 outlineN = vertex.color.rgb * 2.0 - 1.0;
    if(!_OutlineWidth || dot(outlineN,outlineN) < 1e-6f) vertex.position = 0.0/0.0;
    outlineN = _OutlineFromVertexColor ? mul(outlineN, vertex.TBN) : vertex.N;
    float offset = _OutlineFromVertexColor ? _OutlineZOffset * vertex.color.a : _OutlineZOffset;
    vertex.position += outlineN * _OutlineWidth * 0.01 - vertex.V * offset;
    #endif

    __SC_PHASE_postvertex__

    float bias = _NTShadowBias;
    #if defined(UNIVERSAL_PIPELINE_CORE_INCLUDED)
    bias *= 0.5;
    #endif
    //bias *= saturate(vertex.position.y + 0.3);
    #if defined(SHADOWS_DEPTH)
        if(UNITY_MATRIX_P._m33 == 0.0) bias = 0;
    #endif
    vertex.position -= L * bias * saturate(dot(L,normalize(_WorldSpaceCameraPos.xyz - vertex.position)) * 2 + 1);
}

void SCPixelClip(v2f i, bool isFront, float bitangentDir)
{
    SCPositionAndDirection camera = SCGetCameraData();
    SCPositionAndDirection head = SCGetHeadData();
    SCPositionAndDirection headBone = SCGetHeadBoneData();
    SCVertexData vertex = FromPixelInput(i, camera, head, headBone, bitangentDir, isFront);
    vertex.shadowOffset = _NTShadowBias;

    // Main Texture
    SCShadingData sd;
    sd.shadow = 1;
    sd.add = 0;
    sd.postadd = 0;
    sd.uv = vertex.uv[0].xy;
    sd.albedoAlpha = SCSample(_BaseTexture, sampler_BaseTexture, sd.uv);
    sd.mask = SCSample(_SharedMask, sampler_BaseTexture, sd.uv);
    sd.roughness = _Roughness;
    sd.N = half3(0,0,1);
    sd.N_detail = half3(0,0,1);
    sd.maskTexture = _SharedMask;
    sd.gradientsTexture = _SharedGradients;

    __SC_PHASE_base__

    sd.albedoAlpha = saturate(sd.albedoAlpha);

    sd.col = sd.albedoAlpha;

    #ifdef NT_FUR
    sd.col.a = saturate((SCSampleRepeat(_FurNoiseMask, sd.uv * _FurNoiseTiling).r - i.customV2f.furVector.z * abs(i.customV2f.furVector.z)) * 3);
    if (sd.col.a < 1) discard;
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
        // [NT-FEAT 26] 两趟透明 · **背面趟**（只在 NonToonTwoPass 的 ForwardBack pass 里定义该宏）。
        //   与 lilToon 同构：lil_pass_forward_normal.hlsl:406-409 在 Transparent 分支里
        //     #if defined(LIL_TRANSPARENT_PRE)   fd.col *= _PreColor; clip(fd.col.a - _PreCutoff);
        //   ★ 关键在于它用的是**自己的** _PreCutoff，而不是 _Cutoff。
        //     实测 lilToon 的 age 材质：_Cutoff = 0.136 而 _PreCutoff = 0.5
        //     ⇒ 背面趟**只画 alpha ≥ 0.5 的那部分物体**，所以它只覆盖物体的一部分。
        //     这正是 lilToon 两趟观感的来源（实测"第二次混合落地率" ≈ 0.43）。
        //   ⚠️ 若这里沿用 _Cutoff（0.136），背面趟会覆盖整个物体，
        //     混合量比 lilToon 多出近一倍（实测 0.80）—— 这正是修复前的行为。
        //   ⚠️ 与 lilToon 的次序差异：它在**光照之前**乘 _PreColor，我们在光照之后。
        //     _PreColor 为白（默认值，也是实测材质的值）时两者完全等价；
        //     非白 _PreColor 时要还原 lilToon 语义需把这一步前移（已知限制，未实现）。
        sd.col *= _PreColor;
        clip(sd.col.a - _PreCutoff);
#else
        // [NT-FIX 8] Transparent: 保留 alpha 交给硬件混合，不做任何裁剪。
        // 原先这里 clip(sd.col.a - _Cutoff) 会让 Transparent 退化成 Cutout。
#endif
    }
    // [NT-FIX 8] 同上：删掉二次 dither 裁剪。
    #endif
}

#ifdef NT_FUR
void SCCustomV2FFunc(inout v2f o, SCVertexData vertex, SCPositionAndDirection camera, SCPositionAndDirection head, SCPositionAndDirection headBone)
{
    SCCustomV2F customV2f = (SCCustomV2F)0;
    customV2f.furVector = vertex.T * _FurVector.x + vertex.B * _FurVector.y + vertex.N * _FurVector.z;
    o.customV2f = customV2f;
}
#endif
