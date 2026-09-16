// [NT-FEAT 15 / NT-FEAT 18] 「只由自有光源照亮」的排他通路 —— 地图没灯 / 环境光乱来时用。
//
// ── 为什么放在 modifylight 而不是 customlight ────────────────────────────────
//   SCCalculateAllLights:
//       env += lightmap;  env += vertexLighting;
//       __SC_PHASE_customlight__            ← 早期版本在这里写 env = 0
//       SCCalculateEnvironmentLight(...)     ← 这里又 env += envF + ...; env *= 1.2;
//   SH 环境光是在 customlight **之后**才累加进 env 的，在那儿清零等于白清：
//   环境光 / 光照贴图 / 顶点光依然漏进来，而且 sd.L（渐变方向）被 SH 污染
//   —— 同一颗 avatar 换个地图就换一副渐变。
//   __SC_PHASE_modifylight__ 在 `sd.lightColor = env + lightSum.color` 之后、shade 之前，
//   到这里才真正"一锤定音"。
//
// ── 为什么要分 pass（0.3.5 修的真 bug）──────────────────────────────────────
//   同一个 frag 被 ForwardBase 与 ForwardAdd 共用：
//     · base pass：接管 sd.lightColor（丢掉 env 与世界主光），并接管 sd.L
//     · 附加光 pass：既然"只由它照亮"，世界光的**附加贡献必须为 0**（否则 N 盏附加光又把世界光加回来）
//   用 UNITY_PASS_FORWARDADD 区分（URP 不定义它 → 走 base 分支，URP 只有一个 forward pass）。
if (_UseSelfLight && _SelfLightOnly > 0.5)
{
#if defined(UNITY_PASS_FORWARDADD)
    sd.lightColor = 0;
#else
    half3 ntSelfOnlyL;
    NTSELFLIGHT_DIR(ntSelfOnlyL, vertex.position)

    half ntSelfOnlyShadow = 1;
    NTSELFSHADOW(ntSelfOnlyShadow, vertex.position, sd.uv)

    // 顺便把地图的灯"探"出来（色温 / 环境匹配用），但**不**让它参与着色
    half3 ntSelfAmbient = 0;
#if !defined(LIGHTMAP_ON) && UNITY_SHOULD_SAMPLE_SH
    ntSelfAmbient = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
#endif
    half3 ntSelfOnlyWorldColor;
    NTSELFLIGHT_WORLD_COLOR(ntSelfOnlyWorldColor, ntSelfAmbient)
    half3 ntSelfOnlyColor;
    NTSELFLIGHT_COLOR(ntSelfOnlyColor, ntSelfOnlyWorldColor)

    // [NT-FEAT 18] 阴影颜色：把阴影处的光染成 Shadow Color（默认黑 = 与旧行为完全一致）
    half3 ntSelfShadeTint = lerp(_SelfLightShadowColor.rgb, half3(1, 1, 1), ntSelfOnlyShadow);

    half3 ntSelfOnlyTerm = ntSelfOnlyColor * (_SelfLightIntensity * saturate(dot(sd.N, ntSelfOnlyL)) * ntSelfOnlyShadow) * ntSelfShadeTint;

    // 与主通路一致的后处理链（clamp → 单色 → 无光照），保证切换开关时观感一致
    half3 ntSelfOnlyFinal = clamp(ntSelfOnlyTerm, _LightMinLimit, _LightMaxLimit);
    half ntSelfOnlyGray = dot(ntSelfOnlyFinal, half3(0.333333, 0.333333, 0.333333));
    ntSelfOnlyFinal = lerp(ntSelfOnlyFinal, half3(ntSelfOnlyGray, ntSelfOnlyGray, ntSelfOnlyGray), _MonochromeLighting);
    ntSelfOnlyFinal = lerp(ntSelfOnlyFinal, 1.0, _AsUnlit);
    sd.lightColor = ntSelfOnlyFinal;

    // 渐变（Shade / ShadowColor 用 sd.L）的方向也换成我们自己的，
    // 否则 SCCalculateEnvironmentLight 里那句 `sd.L = lightSum.direction + SH*0.333` 会把
    // 地图环境光的颜色方向混进来，ramp 就会随地图变。
    sd.L = normalize(ntSelfOnlyL + vertex.Head * _ShadeDirectionBias + float3(0, 1, 0));

    // 后面的 reflection 相位（高光 / 环境反射）会读 env，这里一并清掉，
    // 否则"只由它照亮"的 avatar 还是会反射地图的天空盒。
    env = 0;
#endif
}

// [NT-FEAT 19] 屏蔽环境光（但保留世界直射光）：
//   把已经算进 sd.lightColor 的那部分环境光扣掉，并把 env 清掉（连带关掉环境反射）。
//   这是在 clamp / 单色 / AsUnlit 之后做的，属于近似；想"完全不依赖地图"请用「只由它照亮」。
#if !defined(UNITY_PASS_FORWARDADD)
if (_UseSelfLight && _SelfLightOnly < 0.5 && _SelfLightBlockAmbient > 0.001)
{
    sd.lightColor = max(0, sd.lightColor - env * saturate(_SelfLightBlockAmbient));
    env *= (1.0 - saturate(_SelfLightBlockAmbient));
}
#endif
