
SC_Box
SC_uint(_ShadowColorEnable, 0, [SCToggle], "Enable", "")
SC_color(_ShadowColor, (0.82,0.76,0.85,1.0), [], "Shadow Color", "")
SC_Texture2D(_ShadowColorTex, "black", [], "Shadow Color", "")
SC_float(_ShadowBorder, 0.5, [SCRange(0,1)], "Border", "")
SC_float(_ShadowBlur, 0.1, [SCRange(0,1)], "Blur", "")
SC_BoxEnd

SC_Box
SC_color(_Shadow2ndColor, (0.68,0.66,0.79,1), [], "2nd Color", "")
SC_Texture2D(_Shadow2ndColorTex, "black", [], "2nd Color", "")
SC_float(_Shadow2ndBorder, 0.15, [SCRange(0,1)], "2nd Border", "")
SC_float(_Shadow2ndBlur, 0.1, [SCRange(0,1)], "2nd Blur", "")
SC_BoxEnd

SC_Box
SC_color(_Shadow3rdColor, (0,0,0,0), [], "3rd Color", "")
SC_Texture2D(_Shadow3rdColorTex, "black", [], "3rd Color", "")
SC_float(_Shadow3rdBorder, 0.25, [SCRange(0,1)], "3rd Border", "")
SC_float(_Shadow3rdBlur, 0.1, [SCRange(0,1)], "3rd Blur", "")
SC_BoxEnd

SC_Box
SC_float(_ShadowStrength, 1, [SCRange(0,1)], "Strength", "")
SC_float(_ShadowBorderRange, 0.08, [SCRange(0,1)], "Border Range", "")
SC_color(_ShadowBorderColor, (1,0.1,0,1), [], "Border Color", "")
SC_float(_ShadowMainStrength, 0, [SCRange(0,1)], "Contrast", "")
SC_float(_AAStrength, 1, [SCRange(0,4)], "AA Strength", "")
SC_uint(_ShadowStrengthMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_BoxEnd
SC_Box
SC_uint(_ShadowMaskEnable, 0, [SCToggle], "Use Shadow Masks", "")
SC_Texture2D(_ShadowStrengthMask, "white", [], "Shadow Strength Mask", "")
SC_Texture2D(_ShadowBorderMask, "white", [], "Shadow Border Mask", "")
SC_Texture2D(_ShadowBlurMask, "white", [], "Shadow Blur Mask", "")
SC_BoxEnd
