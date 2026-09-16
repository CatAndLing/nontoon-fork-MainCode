SC_color(_RimLightColor, (0,0,0,1), [], "__Color", "")
SC_float(_RimLightMultiplyAlbedo, 0, [SCRange(0,1)], "__MultiplyAlbedo", "")
SC_float4(_RimLightRange, (0.6,0.9,0,0), [SCMinMax(0,1)], "__Range", "")
SC_uint(_RimLightMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
