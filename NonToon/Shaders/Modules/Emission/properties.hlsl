SC_Box
SC_uint(_UseEmission, 0, [SCToggle], "Emission", "")
SC_color(_EmissionColor, (1,1,1,1), [], "Color", "")
SC_Texture2D(_EmissionMap, "white", [], "Emission Map", "")
SC_float(_EmissionBlend, 1, [SCRange(0,1)], "Blend", "")
SC_Texture2D(_EmissionBlendMask, "white", [], "Blend Mask", "")
SC_float(_EmissionMainStrength, 0, [SCRange(0,1)], "Main Strength", "")
SC_uint(_EmissionBlendMode, 1, [SCEnum(Normal, 0, Add, 1, Screen, 2, Multiply, 3)], "Blend Mode", "")
SC_BoxEnd
