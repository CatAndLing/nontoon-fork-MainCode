if (_Enable)
{
    // [NT-FEAT 5] Sampling the matcap with vertex.Head (the midpoint of both eyes - ShaderCore
    // makes it stereo-stable on purpose) gives both eyes the identical UV, so the reflection
    // looks painted onto the mesh instead of sitting on it. vertex.V is the true per-eye view
    // direction. 0 = original head-based sampling, 1 = full stereo parallax (default).
    half3 N_VD = normalize(lerp(vertex.Head, vertex.V, _VRParallaxStrength));
    half3 B_VD = normalize(float3(0,1,0) - N_VD * N_VD.y * 0.9);
    half3 T_VD = cross(N_VD, B_VD);
    half3x3 TBN_VD = float3x3(T_VD, B_VD, N_VD);
    half2 uvMat = mul(TBN_VD, sd.N).xy * 0.5 + 0.5;
    half2 uvMat_Detail = mul(TBN_VD, sd.N_detail).xy * 0.5 + 0.5;
    sd.col.rgb *= lerp(1, SCSampleClamp(_MatCapMultiply, lerp(uvMat, uvMat_Detail, _MatCapMultiplyDetail)).rgb * _MatCapMultiplyColor.rgb, NTMaskValue(sd.mask, _MatCapMultiplyMaskChannel));
    sd.add += SCSampleClamp(_MatCapAdd, lerp(uvMat, uvMat_Detail, _MatCapAddDetail)).rgb * _MatCapAddColor.rgb * NTMaskValue(sd.mask, _MatCapAddMaskChannel);
}
