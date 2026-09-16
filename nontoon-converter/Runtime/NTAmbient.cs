// [NT-FEAT 21] 环境光取样 + 色温换算。
//
// 为什么放在 Runtime（而不是 Editor）：
//   ⑥ 的实时灯要在**运行时**跟随地图环境光（暖色地图里自己的灯不能是冷光），
//   而编辑器工具又要给出"这个地图的环境色温大约是多少"的读数。
//   两边**必须是同一份实现**，否则探针算出来的色温和实际生效的颜色会对不上
//   —— 这正是本项目反复踩过的"两处各算一遍、然后悄悄漂移"的坑。
//
// 两个空间的约定（别搞混，这是最容易错的地方）：
//   · `RenderSettings.ambient*` 是**线性**空间（Linear 色彩空间工程里 Unity 直接给线性值）。
//   · 着色器里的 `NT_SELFLIGHT_KELVIN`（Tanner Helland 表）算出来的 RGB **被当作线性光色
//     直接相乘**（`_SelfLightColor.rgb *= kelvin`）。所以"反推色温"必须在**同一个空间**里比：
//     把环境色和 Kelvin→RGB 的**线性解释**都按通道和归一化后比色度，亮度不参与。
//   · 给人看的色板才转成 sRGB（`Mathf.LinearToGammaSpace`）。
//
// 反推**不用** McCamy 之类的近似公式，而是**在我们自己的正向上做搜索**
//   —— 保证"选出来的色温在着色器里渲出来最接近环境色"，而不是"最接近某个教科书画布"。
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToon
{
    public static class NTAmbient
    {
        // 与 `NonToon/Shaders/Modules/SelfLight/properties.hlsl`
        // 的 `_SelfLightTemperature` [SCRange(1000,20000)] 保持一致。
        public const float KelvinMin = 1000f;
        public const float KelvinMax = 20000f;

        public struct Sample
        {
            /// <summary>线性空间的环境色（已乘 ambientIntensity）。</summary>
            public Color linear;
            /// <summary>sRGB 空间的环境色（只用于界面显示）。</summary>
            public Color display;
            /// <summary>线性亮度（Rec.709）。</summary>
            public float luminance;
            /// <summary>环境光模式的文字说明。</summary>
            public string source;
            /// <summary>亮度太低时为 false —— 此时"环境色温"没有意义（黑没有色温）。</summary>
            public bool valid;
        }

        public struct Fit
        {
            /// <summary>拟合出的黑体色温（K）。</summary>
            public float kelvin;
            /// <summary>归一化色度残差：0 = 环境色正好落在黑体轨迹上。</summary>
            public float error;
            /// <summary>残差的绿/品红分量：&gt;0 偏绿，&lt;0 偏品红（黑体色温表达不了这个方向）。</summary>
            public float tint;
            /// <summary>误差小于此值才认为"色温能表达这个环境色"。</summary>
            public bool good;
        }

        // 六轴平均用的缓存 —— ⚠️ **不能每帧 new**（`SphericalHarmonicsL2.Evaluate`
        // 会往数组里写，运行时每帧分配数组就是每帧一次 GC）。
        static readonly Vector3[] ProbeDirs =
        {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
        };
        static readonly Color[] ProbeResults = new Color[6];

        /// <summary>读当前场景的环境光（线性、已含强度）。</summary>
        public static Sample Read()
        {
            Color linear;
            string source;
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Flat:
                    linear = RenderSettings.ambientLight;
                    source = "Flat（单色环境光）";
                    break;
                case AmbientMode.Trilight:
                    // 天空占大半：角色朝上的面收得最多，这是"看起来是什么颜色"的主导项。
                    linear = RenderSettings.ambientSkyColor * 0.5f
                           + RenderSettings.ambientEquatorColor * 0.3f
                           + RenderSettings.ambientGroundColor * 0.2f;
                    source = "Trilight（天/地平/地三色，0.5/0.3/0.2 加权）";
                    break;
                default:
                    linear = AverageProbe(RenderSettings.ambientProbe);
                    source = "Skybox / Custom（环境探针 SH，6 轴平均）";
                    break;
            }

            float intensity = Mathf.Max(0f, RenderSettings.ambientIntensity);
            linear = new Color(linear.r * intensity, linear.g * intensity, linear.b * intensity, 1f);

            var s = new Sample
            {
                linear = linear,
                display = new Color(
                    Mathf.LinearToGammaSpace(Mathf.Clamp01(linear.r)),
                    Mathf.LinearToGammaSpace(Mathf.Clamp01(linear.g)),
                    Mathf.LinearToGammaSpace(Mathf.Clamp01(linear.b)), 1f),
                luminance = Luminance(linear),
                source = source + "，强度 " + intensity.ToString("F2"),
            };
            // 亮度阈值：低到这个程度时色相基本是噪声（HDR 天空的 SH 在暗场景里也会抖）。
            s.valid = s.luminance > 1e-4f;
            return s;
        }

        static Color AverageProbe(SphericalHarmonicsL2 probe)
        {
            probe.Evaluate(ProbeDirs, ProbeResults);
            Color sum = Color.black;
            for (int i = 0; i < ProbeResults.Length; i++) sum += ProbeResults[i];
            return sum / ProbeResults.Length;
        }

        public static float Luminance(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

        // ────────────────────────────────────────────────────────────────────
        // 正向：色温 → RGB。**必须与着色器宏 `NT_SELFLIGHT_KELVIN` 逐字一致**
        // （`NonToon/Shaders/Modules/SelfLight/includes.hlsl`），否则预览和实机不一样。
        // ────────────────────────────────────────────────────────────────────
        public static Color KelvinToColor(float kelvin)
        {
            float t = Mathf.Clamp(kelvin, 1000f, 40000f) / 100f;
            float r, g, b;
            if (t <= 66f) r = 255f;
            else r = 329.698727446f * Mathf.Pow(Mathf.Max(t - 60f, 1e-3f), -0.1332047592f);
            if (t <= 66f) g = 99.4708025861f * Mathf.Log(Mathf.Max(t, 1f)) - 161.1195681661f;
            else g = 288.1221695283f * Mathf.Pow(Mathf.Max(t - 60f, 1e-3f), -0.0755148492f);
            if (t >= 66f) b = 255f;
            else if (t <= 19f) b = 0f;
            else b = 138.5177312231f * Mathf.Log(Mathf.Max(t - 10f, 1e-3f)) - 305.0447927307f;
            return new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f), 1f);
        }

        /// <summary>通道和归一化 —— 抹掉亮度，只留色度。</summary>
        static Color Chromaticity(Color c)
        {
            float sum = c.r + c.g + c.b;
            if (sum < 1e-6f) return new Color(1f / 3f, 1f / 3f, 1f / 3f, 1f);
            return new Color(c.r / sum, c.g / sum, c.b / sum, 1f);
        }

        /// <summary>把一个线性环境色拟合成黑体色温（搜索我们自己的正向曲线，保证与实机一致）。</summary>
        public static Fit FitKelvin(Color linearAmbient)
        {
            var target = Chromaticity(linearAmbient);
            float bestK = 6500f, bestErr = float.MaxValue;
            Color best = Color.white;

            // 粗扫 10K 一档（1900 次，编辑器/偶发调用完全够用），再 1K 细化。
            for (float k = KelvinMin; k <= KelvinMax; k += 10f)
                Refine(k, target, ref bestK, ref bestErr, ref best);
            float lo = Mathf.Max(KelvinMin, bestK - 10f), hi = Mathf.Min(KelvinMax, bestK + 10f);
            for (float k = lo; k <= hi; k += 1f)
                Refine(k, target, ref bestK, ref bestErr, ref best);

            // 绿/品红残差：黑体轨迹上"绿-品红"这一轴恒为 0，所以残差投影到该轴 = 色温表达不了的部分。
            float targetTint = target.g - (target.r + target.b) * 0.5f;
            float bestTint = best.g - (best.r + best.b) * 0.5f;

            var fit = new Fit
            {
                kelvin = Mathf.Round(bestK),
                error = Mathf.Sqrt(bestErr),
                tint = targetTint - bestTint,
                good = Mathf.Sqrt(bestErr) < 0.02f,
            };
            return fit;
        }

        static void Refine(float k, Color target, ref float bestK, ref float bestErr, ref Color best)
        {
            var c = Chromaticity(KelvinToColor(k));
            float dr = c.r - target.r, dg = c.g - target.g, db = c.b - target.b;
            float e = dr * dr + dg * dg + db * db;
            if (e < bestErr) { bestErr = e; bestK = k; best = c; }
        }

        /// <summary>把颜色归一化成"最亮通道 = 1"的**纯色相**色 —— 用于只跟色相、不动亮度的场合。</summary>
        public static Color HueOnly(Color c)
        {
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (m < 1e-5f) return Color.white;
            return new Color(c.r / m, c.g / m, c.b / m, 1f);
        }

        /// <summary>把拟合结果说成人话。</summary>
        public static string Describe(Sample s, Fit fit)
        {
            if (!s.valid)
                return "环境光几乎为黑（线性亮度 " + s.luminance.ToString("F5") + "）⇒ 没有可用的色温。\n"
                     + "地图这么黑的话，用 ③ 的亮度下限防煤，别指望色温探测。";

            var text = "环境光：" + s.source + "\n"
                     + "线性 " + Fmt(s.linear) + "    sRGB " + Fmt(s.display)
                     + "    亮度 " + s.luminance.ToString("F3") + "\n"
                     + "拟合色温 ≈ **" + fit.kelvin.ToString("F0") + " K**"
                     + "（色度残差 " + fit.error.ToString("F4") + "）\n";

            if (fit.good)
                text += "判定：**这个环境色基本落在黑体轨迹上** ⇒ 写色温就够，观感会一致。\n";
            else if (fit.tint > 0.02f)
                text += "判定：环境色**比黑体轨迹更绿**（残差 " + fit.tint.ToString("F3") + "）"
                      + "—— 常见于树林/大片植被、或偏绿的 HDR 天空。\n"
                      + "色温表达不了「绿」这个方向，建议用「匹配环境色」而不是色温。\n";
            else if (fit.tint < -0.02f)
                text += "判定：环境色**比黑体轨迹更品红**（残差 " + fit.tint.ToString("F3") + "）"
                      + "—— 常见于霓虹/紫调夜景。建议用「匹配环境色」。\n";
            else
                text += "判定：误差偏大但绿-品红分量小，多为**低饱和**环境色；写色温或匹配环境色都行。\n";

            text += "\n⚠️ 注意：这里读的是**当前打开的场景**。VRChat 里地图光照是别人定的，"
                  + "所以「匹配环境色」（运行时跟随）比「写死色温」更保险。";
            return text;
        }

        static string Fmt(Color c)
        {
            return "(" + c.r.ToString("F3") + ", " + c.g.ToString("F3") + ", " + c.b.ToString("F3") + ")";
        }
    }
}
