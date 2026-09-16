SC_uint(_Enable, 0, [SCInHeader][SCToggle][SCConstValue(1,pixel)], "", "")

SC_float(_VRParallaxStrength, 1, [SCRange(0,1)], "VR Parallax Strength", "")

SC_Box
SC_color(_MatCapMultiplyColor, (1,1,1,1), [SCCache], "", "")
SC_Texture2D(_MatCapMultiply, "white", [], "MatCap (Multiply)", "")
SC_float(_MatCapMultiplyDetail, 1, [SCRange(0,1)], "Detail Normal", "")
SC_uint(_MatCapMultiplyMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_BoxEnd

SC_Box
SC_color(_MatCapAddColor, (1,1,1,1), [SCCache], "", "")
SC_Texture2D(_MatCapAdd, "black", [], "MatCap (Add)", "")
SC_float(_MatCapAddDetail, 1, [SCRange(0,1)], "Detail Normal", "")
SC_uint(_MatCapAddMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_BoxEnd
