SC_uint(_UseSelfLight, 0, [SCInHeader][SCToggle], "Self Light", "")

SC_Box
SC_uint(_SelfLightId, 0, [SCEnum(Any, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15)], "Accept Light ID", "")
SC_color(_SelfLightColor, (1,1,1,1), [], "Color", "")
SC_uint(_SelfLightUseTemperature, 0, [SCToggle], "Use Color Temperature", "")
SC_float(_SelfLightTemperature, 6500, [SCRange(1000,20000)], "Color Temperature (K)", "")
SC_float(_SelfLightIntensity, 1, [SCRange(0,8)], "Intensity", "")
SC_float4(_SelfLightDirection, (0,0,1,0), [SCVector3], "Direction (world, toward light)", "")
SC_uint(_SelfLightOnly, 0, [SCToggle], "Only This Light", "")
SC_float(_SelfLightBlockAmbient, 0, [SCRange(0,1)], "Block World Ambient", "")
SC_BoxEnd

SC_Box
SC_float(_SelfLightMatchAmbient, 0, [SCRange(0,1)], "Match World Light Color", "")
SC_float(_SelfLightMatchDirection, 0, [SCRange(0,1)], "Match World Light Direction", "")
SC_BoxEnd

SC_Box
SC_float(_SelfLightShadowStrength, 1, [SCRange(0,1)], "Shadow Strength", "")
SC_color(_SelfLightShadowColor, (0,0,0,1), [], "Shadow Color", "")
SC_uint(_SelfLightPCSS, 1, [SCToggle], "PCSS Soft Shadow", "")
SC_uint(_SelfLightPCSSQuality, 0, [SCEnum(Low, 0, Medium, 1, High, 2, Ultra, 3)], "PCSS Quality", "")
SC_float(_SelfLightSoftness, 0.5, [SCRange(0,1)], "Softness", "")
SC_float(_SelfLightDensity, 1, [SCRange(0,1)], "Shadow Density", "")
SC_float(_SelfLightClamp, 0, [SCRange(0,1)], "Shadow Clamp", "")
SC_float(_SelfLightDistance, 10, [SCRange(0,50)], "Shadow Distance", "")
SC_float(_SelfLightShadowBias, 0.02, [SCRange(0,0.2)], "Shadow Bias", "")
SC_Texture2D(_SelfLightReceiveMask, "white", [], "Receive Mask", "")
SC_uint(_SelfLightReceiveMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_float(_SelfLightReceiveMaskStrength, 0, [SCRange(0,1)], "Receive Mask Strength", "")
SC_Texture2D(_SelfLightShadowMask, "white", [], "Shadow Strength Mask", "")
SC_uint(_SelfLightShadowMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_float(_SelfLightShadowMaskStrength, 0, [SCRange(0,1)], "Shadow Mask Strength", "")
SC_BoxEnd

SC_Box
SC_Texture2D(_SelfLightShadowMap, "white", [], "Shadow Map (custom)", "")
SC_float(_SelfLightShadowTexels, 256, [SCRange(64,2048)], "Shadow Texels", "")
SC_float4(_SelfLightOrigin, (0,0,0,0), [SCVector3], "Map Origin", "")
SC_float4(_SelfLightRight, (1,0,0,0), [SCVector3], "Map Right", "")
SC_float4(_SelfLightUp, (0,1,0,0), [SCVector3], "Map Up", "")
SC_float4(_SelfLightForward, (0,0,1,0), [SCVector3], "Map Forward", "")
SC_float(_SelfLightHalfX, 1, [], "Map Half Width", "")
SC_float(_SelfLightHalfY, 1, [], "Map Half Height", "")
SC_float(_SelfLightNear, 0, [], "Map Near", "")
SC_float(_SelfLightFar, 1, [], "Map Far", "")
SC_BoxEnd
