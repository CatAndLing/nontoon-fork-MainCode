SC_uint(_RenderingMode, 0, [NTRenderingMode], "__RenderingMode", "")
SC_Texture2D(_BaseTexture, "white", [SCMainTexture], "__Texture", "")
SC_SamplerState(sampler_BaseTexture)
SC_Texture2D(_SharedMask, "white", [SCMask], "__SharedMask", "")
SC_Texture2DArray(_SharedGradients, "white", [SCGradients], "__SharedGradients", "")
SC_float(_NormalScale, 1, [SCCache][SCRange(-10,10)], "", "")
SC_Texture2D(_NormalMap, "bump", [], "__NormalMap", "")
SC_uint(_NormalMapWithRoughness, 0, [SCToggle], "__NormalMapWithRoughness", "")
SC_float(_Roughness, 0.5, [SCRange(0.002,1)], "__Roughness", "")
SC_float(_Cutoff, 0.5, [SCRange(-0.001,1.001)], "__Cutoff", "")
SC_float(_NTShadowBias, 0.1, [], "Shadow Bias", "")
SC_Texture2D(_NTDitherTex, "black", [], "Dither", "")

SC_Box
SC_color(_OutlineColor, (0.6,0.45,0.55,1), [], "Outline Color", "")
SC_float(_OutlineWidth, 0.1, [], "Outline Width", "")
SC_float(_OutlineZOffset, 0.0, [], "Outline Z Offset", "")
SC_uint(_OutlineFromVertexColor, 0, [SCToggle], "From Vertex Color", "")
SC_BoxEnd

SC_Box
SC_float(_BacklightSharpness, 1.5, [], "Backlight Sharpness", "")
SC_float(_BacklightRange, 0.75, [], "Backlight Range", "")
SC_uint(_BacklightMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_BoxEnd

SC_Box
SC_float(_ShadeDirectionBias,  1.5,[SCRange(0,3)], "Shade Direction Bias",  "")
SC_float(_LightMinLimit,       0, [SCRange(0,1)],  "Light Min Limit",       "")
SC_float(_LightMaxLimit,       1, [SCRange(0,10)], "Light Max Limit",       "")
SC_float(_MonochromeLighting,  0, [SCRange(0,1)],  "Monochrome Lighting",   "")
SC_float(_AsUnlit,             0, [SCRange(0,1)],  "As Unlit",              "")
SC_BoxEnd
SC_color(_PreColor, (1,1,1,1), [], "Pre Color", "")
SC_float(_PreCutoff, 0.5, [SCRange(-0.001,1.001)], "Pre Cutoff", "")
