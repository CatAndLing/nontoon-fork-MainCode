// [NT-FEAT 7 / NT-FEAT 15 / NT-FEAT 18] SelfLight —— 叠加通路（_SelfLightOnly = 0）。
//
// ── 为什么必须区分 pass（0.3.5 修的真 bug）────────────────────────────────────
// birp.hlsl 的 frag 被 **ForwardBase / ForwardAdd / Outline / OutlineAdd 共用**，
// 它们都会调 SCCalculateAllLights → 我们的 customlight 相位。于是"自有光"会在
// **每一个 forward pass 里各加一遍**：地图里有 N 盏像素光时，avatar 的自有光被加 N 次 → 过曝。
// 自有光只在 **base pass** 加一次（Outline 也是 ForwardBase 标签，所以描边同样受光，一致）。
//
// 「只由它照亮」的排他通路在 phase_modifylight.hlsl。
//
// 为什么不做成真实 Unity Light（调研结论，官方出处）：
//   · VRChat 性能等级：PC 上 Excellent / Good / Medium 都要求 Lights = 0，只有 Poor 才允许 1。
//   · 真实光会**照亮周围世界与别的玩家**，做不到"只照自己"。
//   · Quest 端实时光基本不可用。
//   （不在意性能等级的话，工具包里有独立的 Avatar 光源插件，本组件也有「实时光源」模式。）
//
// 自阴影 / 色温 / 环境匹配都在 includes.hlsl 的宏里，与排他通路共用。
#if !defined(UNITY_PASS_FORWARDADD)
if (_UseSelfLight && _SelfLightOnly < 0.5)
{
    half3 selfL;
    NTSELFLIGHT_DIR(selfL, vertex.position)

    half selfShadow = 1;
    NTSELFSHADOW(selfShadow, vertex.position, sd.uv)

    // 把地图的灯"探"出来（环境匹配用）：SH 的 L0 项；光照贴图工程里退化成主光颜色
    half3 ntSelfAmbient = 0;
#if !defined(LIGHTMAP_ON) && UNITY_SHOULD_SAMPLE_SH
    ntSelfAmbient = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
#endif
    half3 ntSelfWorldColor;
    NTSELFLIGHT_WORLD_COLOR(ntSelfWorldColor, ntSelfAmbient)

    half3 selfColor;
    NTSELFLIGHT_COLOR(selfColor, ntSelfWorldColor)

    // [NT-FEAT 18] 阴影颜色：把阴影处的光染成 Shadow Color（默认黑 = 与旧行为完全一致）
    half3 ntSelfShadeTint = lerp(_SelfLightShadowColor.rgb, half3(1, 1, 1), selfShadow);

    half3 selfTerm = selfColor * (_SelfLightIntensity * saturate(dot(sd.N, selfL)) * selfShadow) * ntSelfShadeTint;
    lightSum.color += selfTerm;
}
#endif
