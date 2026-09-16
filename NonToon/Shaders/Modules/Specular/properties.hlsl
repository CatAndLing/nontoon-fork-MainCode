SC_color(_SpecularColor, (0,0,0,1), [], "__Color", "")
SC_float(_SpecularMultiplyAlbedo, 0, [SCRange(0,1)], "__MultiplyAlbedo", "")
SC_uint(_SpecularMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_float(_SpecularF0, 0.04, [SCRange(0,1)], "Specular F0", "")
