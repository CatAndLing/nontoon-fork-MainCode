SC_Box
SC_int(_ShadeGradientIndex, -1, [SCGradientSelect], "__GradientIndex", "")
SC_float4(_ShadeGradientRange, (0,1,0,0), [SCMinMax(0,1)], "__Range", "")
SC_BoxEnd

SC_Box
SC_uint(_SDFType, 0, [SCEnum(__Disable, 0, SDF, 1, ShadeOffset, 2)], "SDF Type", "")
SC_Texture2D(_SDFMap, "white", [], "SDF Map", "")
SC_float(_SDFBlendVertical, 0, [SCRange(0,1)], "Blend Vertical Light Vector", "")
SC_BoxEnd

SC_Box
SC_float(_ShadeSharpen, 0, [SCRange(0,1)], "Shadow Sharpen", "")
SC_float(_ShadeAmbientAmount, 0, [SCRange(0,1)], "Ambient Tint Amount", "")
SC_color(_ShadeAmbientTint, (1,1,1,1), [], "Ambient Tint", "")
SC_BoxEnd
