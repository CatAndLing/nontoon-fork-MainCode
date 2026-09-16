{
    if(_UseEmission)
    {
        half4 ntEEmission = _EmissionColor;
        ntEEmission *= SCSample(_EmissionMap, sampler_BaseTexture, sd.uv);
        ntEEmission *= SCSample(_EmissionBlendMask, sampler_BaseTexture, sd.uv);
        ntEEmission.rgb = lerp(ntEEmission.rgb, ntEEmission.rgb * sd.albedoAlpha.rgb, _EmissionMainStrength);

        half3 ntEDst = sd.col.rgb;
        half3 ntESrc = ntEEmission.rgb;
        half3 ntEAdd = ntEDst + ntESrc;
        half3 ntEMul = ntEDst * ntESrc;
        half3 ntEOut = ntESrc;
        if(_EmissionBlendMode == 1) ntEOut = ntEAdd;
        else if(_EmissionBlendMode == 2) ntEOut = max(ntEAdd - ntEMul, ntEDst);
        else if(_EmissionBlendMode == 3) ntEOut = ntEMul;

        half ntEBlend = _EmissionBlend * ntEEmission.a;
        sd.col.rgb = lerp(ntEDst, ntEOut, ntEBlend);
    }
}
