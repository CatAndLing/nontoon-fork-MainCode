{
    // [NT-FEAT 2] lilToon shadow colour (1st / 2nd / 3rd), faithful port.
    //
    // Runs inside the `shade` phase. Inject order within a phase is by module name
    // (SCShaderImporter sorts modules via SCModule.CompareTo -> SCPhase.CompareTo, which falls
    // through to name.CompareTo), and "Shade" < "ShadowColor", so this runs AFTER the
    // gradient-ramp Shade module. ("RimShade" < "Shade" < "ShadowColor".)
    // `afters`/`befores` cannot express this: SCModule.FromFile computes a phase de-duplication
    // set and then never uses it, so declaring a phase in the .scmodule JSON injects this file
    // TWICE. Name ordering is the only reliable lever.
    //
    // Wrapped in { } and every local ntS-prefixed, because phase files are textually spliced into
    // frag() next to other modules' phase files.
    //
    // ── Why this is exact rather than approximate ─────────────────────────────────────────────
    // lilToon ends with:  directCol = albedo * lightColor
    //                     indirectCol *= lightColor
    //                     fd.col.rgb = lerp(indirectCol, directCol, lns.x)
    // NonToon instead multiplies sd.col.rgb by sd.lightColor AFTER this phase (birp.hlsl / urp.hlsl
    // do `sd.col.rgb *= sd.lightColor`). Since lightColor >= 0 (it is clamped by _LightMinLimit),
    // both the final lerp and lilToon's `min(indirectCol, directCol)` factor cleanly:
    //     lerp(a*L, b*L, t) == lerp(a, b, t) * L
    //     min(a*L, b*L)     == min(a, b) * L
    // so computing everything on pre-light values here and letting NonToon apply lightColor
    // afterwards reproduces lilToon's result exactly.
    if(_ShadowColorEnable)
    {
        half3 ntSAlbedo = sd.col.rgb;

        // (B) ramp input is a half-Lambert N·L - lil_common_frag.hlsl:946-952
        half ntSLit = saturate(dot(sd.N, sd.L) * 0.5 + 0.5);

        // [NT-FEAT 11] lilToon 的逐像素阴影遮罩，通道语义与 lilToon 完全一致：
        //   _ShadowStrengthMask.r → 阴影强度；_ShadowBlurMask.rgb → 第 1/2/3 层 blur；
        //   _ShadowBorderMask.rgb → 第 1/2/3 层渐变输入。
        // _ShadowMaskEnable = 0（默认）时一个采样都不做，行为与旧版一致。
        // 贴图默认 white ⇒ 即使打开遮罩但没指定贴图，也不会把阴影弄没。
        half ntSMaskStrength = 1;
        half ntSMaskBorder1 = 1;
        half ntSMaskBorder2 = 1;
        half ntSMaskBorder3 = 1;
        half ntSBorder1 = saturate(_ShadowBorder);
        half ntSBorder2 = saturate(_Shadow2ndBorder);
        half ntSBorder3 = saturate(_Shadow3rdBorder);
        half ntSBlur1 = _ShadowBlur;
        half ntSBlur2 = _Shadow2ndBlur;
        half ntSBlur3 = _Shadow3rdBlur;
        if (_ShadowMaskEnable)
        {
            ntSMaskStrength = SCSample(_ShadowStrengthMask, sampler_BaseTexture, sd.uv).r;
            half3 ntSMaskBlur = SCSample(_ShadowBlurMask, sampler_BaseTexture, sd.uv).rgb;
            half3 ntSMaskBorder = SCSample(_ShadowBorderMask, sampler_BaseTexture, sd.uv).rgb;
            ntSBlur1 = saturate(ntSBlur1 * ntSMaskBlur.r);
            ntSBlur2 = saturate(ntSBlur2 * ntSMaskBlur.g);
            ntSBlur3 = saturate(ntSBlur3 * ntSMaskBlur.b);
            ntSMaskBorder1 = ntSMaskBorder.r;
            ntSMaskBorder2 = ntSMaskBorder.g;
            ntSMaskBorder3 = ntSMaskBorder.b;
        }

        // (F) toon shaping - lil_common_functions.hlsl:64-69 (the LIL_ANTIALIAS_MODE != 0 branch):
        //   borderMin = saturate(border - blur*0.5 - borderRange)
        //   borderMax = saturate(border + blur*0.5)
        //   saturate((value - borderMin) / saturate(borderMax - borderMin + fwidth(value)*aascale))
        // Inlined as statements because a phase file cannot define functions.
        half ntSAA = fwidth(ntSLit) * _AAStrength;

        half ntSMin1 = saturate(ntSBorder1 - ntSBlur1 * 0.5);
        half ntSMax1 = saturate(ntSBorder1 + ntSBlur1 * 0.5);
        half ntSLit1 = saturate((ntSLit * ntSMaskBorder1 - ntSMin1) / saturate(ntSMax1 - ntSMin1 + ntSAA));

        // lns.w: a second tooning of the 1st factor, widened by _ShadowBorderRange
        // (lil_common_frag.hlsl:1021 / :1031). Drives only the _ShadowBorderColor gradation.
        half ntSMinW = saturate(ntSBorder1 - ntSBlur1 * 0.5 - _ShadowBorderRange);
        half ntSLitW = saturate((ntSLit * ntSMaskBorder1 - ntSMinW) / saturate(ntSMax1 - ntSMinW + ntSAA));

        half ntSMin2 = saturate(ntSBorder2 - ntSBlur2 * 0.5);
        half ntSMax2 = saturate(ntSBorder2 + ntSBlur2 * 0.5);
        half ntSLit2 = saturate((ntSLit * ntSMaskBorder2 - ntSMin2) / saturate(ntSMax2 - ntSMin2 + ntSAA));

        half ntSMin3 = saturate(ntSBorder3 - ntSBlur3 * 0.5);
        half ntSMax3 = saturate(ntSBorder3 + ntSBlur3 * 0.5);
        half ntSLit3 = saturate((ntSLit * ntSMaskBorder3 - ntSMin3) / saturate(ntSMax3 - ntSMin3 + ntSAA));

        // (H) shadowmix is captured BEFORE strength is applied (lil_common_frag.hlsl:629). NonToon's
        // Specular / RimLight / HairSpecular consume sd.shadow, so keep it meaningful here - this
        // also makes them behave when the gradient-ramp Shade module is off, which is the normal
        // configuration when using shadow colours instead of a ramp.
        sd.shadow *= ntSLit1;

        // (H) _ShadowStrength applies to the 1st level ONLY (lil_common_frag.hlsl:648/1066);
        // the 2nd/3rd "strength" is their colour alpha, handled below.
        half ntSStrength = _ShadowStrength * NTMaskValue(sd.mask, _ShadowStrengthMaskChannel) * ntSMaskStrength;
        ntSLit1 = lerp(1.0, ntSLit1, ntSStrength);

        // (I) colour textures blend over the albedo by their own alpha; default "black" has a = 0,
        // so a lerp by 0 leaves the albedo untouched.
        half4 ntSTex1 = SCSample(_ShadowColorTex,    sampler_BaseTexture, sd.uv);
        half4 ntSTex2 = SCSample(_Shadow2ndColorTex, sampler_BaseTexture, sd.uv);
        half4 ntSTex3 = SCSample(_Shadow3rdColorTex, sampler_BaseTexture, sd.uv);

        // (J) 1st level is the BASE shade colour - albedo (or texture) times _ShadowColor.rgb.
        // Note _ShadowColor.a is not read anywhere in lilToon.
        half3 ntSIndirect = lerp(ntSAlbedo, ntSTex1.rgb, ntSTex1.a) * _ShadowColor.rgb;

        // (J) levels 2 and 3 lerp on top with the inverted-alpha composite
        //   weight = _ShadowNthColor.a - lns.n * _ShadowNthColor.a  ==  a * (1 - lns.n)
        // so the weight rises as the ramp factor falls, saturating at `a` when lns.n == 0.
        // _Shadow3rdColor.a defaults to 0, which is how lilToon disables the 3rd level.
        half3 ntSCol2 = lerp(ntSAlbedo, ntSTex2.rgb, ntSTex2.a) * _Shadow2ndColor.rgb;
        ntSIndirect = lerp(ntSIndirect, ntSCol2, _Shadow2ndColor.a * (1 - ntSLit2));

        half3 ntSCol3 = lerp(ntSAlbedo, ntSTex3.rgb, ntSTex3.a) * _Shadow3rdColor.rgb;
        ntSIndirect = lerp(ntSIndirect, ntSCol3, _Shadow3rdColor.a * (1 - ntSLit3));

        // (K) contrast, cap, then border-colour gradation.
        ntSIndirect = lerp(ntSIndirect, ntSIndirect * ntSAlbedo, _ShadowMainStrength);
        ntSIndirect = min(ntSIndirect, ntSAlbedo);
        // Deliberately NOT saturated: lilToon allows a _ShadowBorderColor component > 1 to
        // extrapolate past the lit colour (default (1,0.1,0,1) gives a red-tinted terminator).
        ntSIndirect = lerp(ntSIndirect, ntSAlbedo, ntSLitW * _ShadowBorderColor.rgb);

        // Final mix, factored as described in the header comment.
        sd.col.rgb = lerp(ntSIndirect, ntSAlbedo, ntSLit1);
    }
}
