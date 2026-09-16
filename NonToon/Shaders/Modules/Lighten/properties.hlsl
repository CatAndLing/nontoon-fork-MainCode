SC_float(_LightBoost, 1, [SCRange(0,10)], "Light Boost", "")
SC_uint(_LightBoostMaskChannel, 3, [SCEnum(R, 0, G, 1, B, 2, A, 3, 1-R, 4, 1-G, 5, 1-B, 6, 1-A, 7)], "__MaskChannel", "")
SC_uint(_LightBoostAsEmission, 0, [SCToggle], "As Emission", "")
