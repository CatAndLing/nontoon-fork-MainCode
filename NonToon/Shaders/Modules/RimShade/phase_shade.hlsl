if(_RimShadeGradientIndex >= 0)
{
    half ntRimShadeMask = NTMaskValue(sd.mask, _RimShadeMaskChannel);
    sd.col.rgb *= SCSampleClamp(sd.gradientsTexture, float2(1 - (ntRimShadeMask - dot(vertex.N,vertex.Head) * ntRimShadeMask), 0.5), _RimShadeGradientIndex).rgb;
}
