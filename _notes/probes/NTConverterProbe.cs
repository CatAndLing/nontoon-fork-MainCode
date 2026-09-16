// [NT-TEST] 只在验证工程里存在（_verify-proj 的包副本），**不进发布包**。
// 端到端跑「假 lilToon 材质 → NonToon 材质」，覆盖三种渲染模式 + 阴影颜色模块 + VRC 反射剥离。
// 重点验证：Int 属性（_RenderingMode / _SrcBlend / _DstBlend / _AlphaToMask / _ShadowColorEnable）
// 在转换后是否**真的写进去了** —— 用 SetFloat 写这些属性是静默无效的。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NonToonTools;
using Object = UnityEngine.Object;

namespace LilToonToNonToonConverter
{
    public static class NTConverterProbe
    {
        // [NT-TEST-FIX] 报告路径原来是**硬编码的 _verify-proj 绝对路径**：换任何别的工程跑都会
        // 悄悄覆盖验证工程的那份报告（本机已实测发生）。改成相对当前工程根目录，报告永远属于本次运行。
        private static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntprobe.txt"); } }
        private const string Dir = "Assets/NTProbe";
        private const string ShaderPath = Dir + "/lilToon.shader";
        // [NT-FIX 34] 专用变体：模式**编在着色器名里**，`_TransparentMode` 属性恒为 0。
        private const string GemShaderPath = Dir + "/lilToonGem.shader";


        // [NT-TEST] 保证着色器已导入：先删后拷包目录后，Unity 可能还没跑 ScriptedImporter，
        // 这时 Shader.Find 会返回 null（不是代码问题）。强制导入一次即可。
        internal static Shader EnsureShader(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader != null) return shader;
            var fileName = shaderName == "nontoon-fork" ? "NonToon"
                : shaderName == "nontoon-fork-fur" ? "NonToonFur"
                : shaderName == "nontoon-fork-twopass" ? "NonToonTwoPass"
                : throw new ArgumentException("Unknown fork shader: " + shaderName);
            var path = "Packages/com.catandling.nontoon/Shaders/" + fileName + ".scshader";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Shader.Find(shaderName);
        }

        private static readonly StringBuilder Sb = new StringBuilder();
        private static int Fail;

        public static void Run()
        {
            // [NT-TEST-FIX] 先删掉上一轮产物：否则本轮到不了写文件那一步（崩溃 / 被 kill / 类名写错）时，
            // 磁盘上留着的是**上一轮的「==== 全部通过 ====」**，人工读 txt 就会假绿。
            try { if (File.Exists(Out)) File.Delete(Out); } catch { }
            try
            {
                Sb.AppendLine("== 运行信息 ==");
                Sb.AppendLine("时间     = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Sb.AppendLine("工程     = " + Directory.GetCurrentDirectory());
                Sb.AppendLine("Unity    = " + Application.unityVersion);
                Sb.AppendLine();
                Sb.AppendLine("== 兼容性（窗口顶部显示的内容） ==");
                Sb.AppendLine("IsInstalled            = " + NonToonCompatibility.IsInstalled);
                Sb.AppendLine("NonToon / ShaderCore   = " + NonToonCompatibility.NonToonVersion + " / " + NonToonCompatibility.ShaderCoreVersion);
                Sb.AppendLine("UsesSupportedVersions  = " + NonToonCompatibility.UsesSupportedVersions);
                Sb.AppendLine("MissingModules         = [" + string.Join(", ", NonToonCompatibility.MissingModules()) + "]");

                EnsureFakeShader();

                // lilToon _TransparentMode: 0=不透明 1=镂空 2=半透明
                // ⛔ [NT-FIX 32] **队列期望已随策略反转，这是有意的，不是为了让门禁变绿。**
                //    转换器现在**照搬源材质的生效队列**，不再按渲染模式规范化。合成源材质
                //    这里显式给出"lilToon 现实中的队列"（实测：`age`/`face_transparent` = 2460、
                //    `bagbelt` = 2450、不透明件 = 2000）⇒ 产物必须**逐字保留**。
                //    旧断言是 `半透明 → 3000`，那是已被推翻的 `[NT-FIX 9]` 策略。
                Case("不透明", 0, 0, 1f, 0f, 2000);
                Case("镂空", 1, 1, 1f, 0f, 2450);
                Case("半透明", 2, 2, 5f, 10f, 2460);
                // 作者**手动覆盖**队列的真实案例：`tail_Crystal` 被作者设成 3005（"保证最后画"），
                // 而它源 shader 声明的是 2900(Gem)。旧策略会把它碾成 3000。
                Case("半透明+手设3005", 2, 2, 5f, 10f, 3005, 3005);

                Sb.AppendLine();
                Sb.AppendLine("== Int 属性写入诊断（整型属性到底能不能写进去） ==");
                var probeMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/NTProbe/NonToonConverted/probe_" + "半透明" + "_NonToon.mat");
                if (probeMat != null)
                {
                    DumpProp(probeMat, "_RenderingMode");
                    DumpProp(probeMat, "_SrcBlend");
                    DumpProp(probeMat, "_DstBlend");
                    DumpProp(probeMat, "_AlphaToMask");
                    DumpProp(probeMat, "_ShadowColorEnable");
                    DumpProp(probeMat, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex");
                    Sb.AppendLine("  -- 直接写入测试 --");
                    probeMat.SetInteger("_SrcBlend", 5);
                    Sb.AppendLine("  SetInteger(_SrcBlend,5) → " + probeMat.GetInteger("_SrcBlend"));
                    probeMat.SetFloat("_SrcBlend", 1f);
                    Sb.AppendLine("  SetFloat(_SrcBlend,1)   → " + probeMat.GetInteger("_SrcBlend"));
                    probeMat.SetInteger("_ShadowColorEnable", 1);
                    Sb.AppendLine("  SetInteger(_ShadowColorEnable,1) → " + probeMat.GetInteger("_ShadowColorEnable"));
                }
                else Sb.AppendLine("  找不到半透明产物，跳过");

                Sb.AppendLine();
                Sb.AppendLine("== 材质检测：拖入 Project 里的模型资源（fbx / prefab） ==");
                RunDetectionCase();

                Sb.AppendLine();
                Sb.AppendLine("== 分支特性 1:1 映射（发光 / lilToon 光照调整 / 受光方向） ==");
                RunForkFeatureCase();

                Sb.AppendLine();
                Sb.AppendLine("== SelfLight 自阴影烘焙（纯 CPU 光线投射） ==");
                RunSelfLightBakeCase();

                Sb.AppendLine();
                Sb.AppendLine("== 自诊断（lilToon 在不在都要说清楚，并列出实际看到的着色器） ==");
                // ⚠️ 这里原先写死 `!AnyLilToon`（假设验证工程**没装** lilToon）。
                // 2026-09-18 全盘门禁抓到它变红，而**不是产品回归**：上一轮全绿时该断言是
                // `→ False`，本轮 AnyLilToon=True —— 因为 MA 批做装置 F 时把依赖复制进了
                // `_verify-proj`，连带装上了 `jp.lilxyzw.liltoon`（`sync-verify.sh` 只管我们自己的
                // 两个包，**不管第三方**⇒ 测试床会漂移）。
                // ⇒ 断言的语义应是"**检测结果与实际安装情况一致**"（这才是它的名字所声称的），
                //    而不是"必须没装"。这样它在两种测试床上都成立，且**更强**（验的是机制而非环境）。
                var lilToonPkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/jp.lilxyzw.liltoon/package.json");
                var lilToonInstalled = lilToonPkg != null;
                Sb.AppendLine("  LilToonCompatibility.AnyLilToon = " + LilToonCompatibility.AnyLilToon
                    + "（实际安装 " + (lilToonInstalled ? "有" : "无") + " lilToon）");
                Check("lilToon 在位检测与实际安装一致", LilToonCompatibility.AnyLilToon == lilToonInstalled,
                    "AnyLilToon=" + LilToonCompatibility.AnyLilToon + " / 实际安装=" + lilToonInstalled);
                // 测试床成分**写进报告**：否则第三方包的增删会静默改变结论（本次就是这样被发现的）
                {
                    var names = new[] { "jp.lilxyzw.liltoon", "nadena.dev.modular-avatar", "nadena.dev.ndmf",
                                        "com.anatawa12.avatar-optimizer", "com.vrchat.avatars", "jp.lilxyzw.shadercore" };
                    var present = new List<string>();
                    foreach (var n in names)
                        if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + n + "/package.json") != null) present.Add(n);
                    Sb.AppendLine("  测试床（第三方，sync-verify 不管）：" + (present.Count > 0 ? string.Join(" / ", present) : "（无）"));
                }
                var standardMaterial = new Material(Shader.Find("Standard"));
                var lilToonShapedMaterial = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath));
                var describe = typeof(LilToonToNonToonConverterWindow).GetMethod("DescribeFoundShaders", BindingFlags.NonPublic | BindingFlags.Static);
                var shown = (string)describe.Invoke(null, new object[] { new Object[] { standardMaterial, lilToonShapedMaterial } });
                Sb.AppendLine("  诊断输出示例：" + shown);
                Check("诊断能同时列出非 lilToon 与 lilToon 着色器", shown.Contains("Standard") && shown.Contains("lilToon"), shown);
                Object.DestroyImmediate(standardMaterial);
                Object.DestroyImmediate(lilToonShapedMaterial);

                Sb.AppendLine();
                Sb.AppendLine("== VRC PipelineManager 反射剥离 ==");
                var pmType = FindType("VRC.Core.PipelineManager");
                Sb.AppendLine("type = " + (pmType == null ? "NOT FOUND（非 VRChat 工程，正常）" : pmType.FullName));
                var go = new GameObject("NTVrcProbe");
                try
                {
                    if (pmType != null)
                    {
                        var pm = go.AddComponent(pmType);
                        var f = pmType.GetField("blueprintId", BindingFlags.Public | BindingFlags.Instance);
                        Sb.AppendLine("blueprintId 是字段 = " + (f != null));
                        var mi = typeof(LilToonToNonToonConverterWindow).GetMethod("TryDetachBlueprintId", BindingFlags.NonPublic | BindingFlags.Static);
                        Sb.AppendLine("TryDetachBlueprintId 找得到 = " + (mi != null));
                        if (f != null)
                        {
                            f.SetValue(pm, "avtr_probe_123");
                            if (mi != null) mi.Invoke(null, new object[] { go });
                            var after = f.GetValue(pm) as string;
                            Check("blueprintId 被清空", after == "", "\"" + after + "\"");
                        }
                    }
                }
                finally { Object.DestroyImmediate(go); }

                Sb.AppendLine();
                Sb.AppendLine("== v0.3.0 新特性：SelfLight PCSS / lilToon 逐像素阴影遮罩 / 完整汉化 ==");
                RunV030Case();

                Sb.AppendLine();
                Sb.AppendLine("== v0.3.1 新特性：PCSS 画质档位 / 实时光源模式 ==");
                RunV031Case();

                Sb.AppendLine();
                Sb.AppendLine("== v0.4.3 新特性：独立的 Avatar 光源插件（着色器无关） ==");
                RunV043Case();

                Sb.AppendLine();
                Sb.AppendLine("== v0.3.4：性能预算（NonToon 的目标是低消耗） ==");
                RunV034Case();

                Sb.AppendLine();
                Sb.AppendLine("== v0.3.5：色温 / 环境匹配 / 面板内部属性隐藏 ==");
                RunV035Case();

                RunDiagnosticsCase();

                Sb.AppendLine();
                Sb.AppendLine(Fail == 0 ? "==== 全部通过 ====" : "==== 有 " + Fail + " 项不符（见上面 ❌） ====");
            }
            catch (Exception e)
            {
                Sb.AppendLine("!! PROBE EXCEPTION: " + e);
                Fail++;
            }
            File.WriteAllText(Out, Sb.ToString());
            Debug.Log("[NTConverterProbe] done -> " + Out + " (fail=" + Fail + ")");
            // [NT-TEST-FIX] 原来这里**完全没有退出码**：装置 B 无论失败多少项，批处理进程都 exit 0，
            // 命令行/CI 完全看不见。注意必须用 EditorApplication.Exit —— Environment.ExitCode
            // 在 Unity 2022.3 batchmode 下**实测被忽略**（本机 NTExitProbe 验证：设 7 仍得 0）。
            EditorApplication.Exit(Fail == 0 ? 0 : 1);
        }

        /// <summary>[NT-FIX 32] 合成源材质在"现实中"会有的队列 —— 与 lilToon 的实测值一致。</summary>
        private static int DefaultLilQueue(float lilTransparentMode)
        {
            var m = Mathf.RoundToInt(lilTransparentMode);
            return m == 1 ? 2450 : m >= 2 ? 2460 : 2000;
        }

        private static void Case(string label, float lilTransparentMode, int expectMode, float expectSrcBlend, float expectDstBlend, int expectQueue, int sourceQueue = -1)
        {
            Sb.AppendLine();
            Sb.AppendLine("== 用例：" + label + "（lilToon _TransparentMode = " + lilTransparentMode + "） ==");
            var mat = CreateFakeMaterial(label, lilTransparentMode,
                sourceQueue < 0 ? DefaultLilQueue(lilTransparentMode) : sourceQueue);
            Sb.AppendLine("  源材质队列 = " + mat.renderQueue);
            var entry = LilToonMaterialConverter.Convert(mat, new ConversionOptions { OverwriteExisting = true });
            Sb.AppendLine("Severity = " + entry.Severity);
            foreach (var m in entry.Messages)
                if (m.StartsWith("WARNING", StringComparison.Ordinal) || m.StartsWith("MISSING", StringComparison.Ordinal) || m.Contains("rendering mode"))
                    Sb.AppendLine("  MSG  " + m);
            if (!string.IsNullOrEmpty(entry.OutputPath) == false)
            {
                Sb.AppendLine("  ❌ 没有产生输出材质");
                Fail++;
                foreach (var d in entry.Diagnostics) Sb.AppendLine("  DIAG " + d);
                return;
            }
            var o = AssetDatabase.LoadAssetAtPath<Material>(entry.OutputPath);
            if (o == null) { Sb.AppendLine("  ❌ 产物加载失败 " + entry.OutputPath); Fail++; return; }

            Check("_RenderingMode", o.HasProperty("_RenderingMode") && Val(o, "_RenderingMode") == expectMode, Val(o, "_RenderingMode") + " 期望 " + expectMode);
            Check("_SrcBlend", Mathf.Approximately(Val(o, "_SrcBlend"), expectSrcBlend), Val(o, "_SrcBlend") + " 期望 " + expectSrcBlend);
            Check("_DstBlend", Mathf.Approximately(Val(o, "_DstBlend"), expectDstBlend), Val(o, "_DstBlend") + " 期望 " + expectDstBlend);
            Check("_AlphaToMask", Val(o, "_AlphaToMask") == (expectMode == 1 ? 1 : 0), Val(o, "_AlphaToMask") + " 期望 " + (expectMode == 1 ? 1 : 0));
            Check("renderQueue", o.renderQueue == expectQueue, o.renderQueue + " 期望 " + expectQueue);
            // ── 阴影路径：[NT-FIX 33 / 2026-09-18] **默认走 ShadowColor 模块**（= lilToon 阴影的逐行忠实移植），
            //    Shade 的梯度 ramp 必须**关**（`_ShadeGradientIndex = -1`）──
            // 这一组期望与 [NT-FIX 28] 那一版**正好相反**，是**有意的策略切换**，不是"把断言改绿"。
            // 依据（`_notes/素材扫描-范围锁定.md` §6.4，直读源码 + 真实素材实测）：
            //   · 上游 Shade **ramp 是一维颜色查找**，结构上：① 承载不了 `fwidth` 抗锯齿；
            //     ② 烘焙器只建模 lns.x/.y/.z 三层色带，**没有烘 `lnB × _ShadowBorderColor` 渐变项**
            //     （`lnB` 是唯一使用 `_ShadowBorderRange` 的项）⇒ 那 4 项在 ramp 路径下**全部 inert**；
            //   · `ShadowColor/phase_shade.hlsl:86-88` 自述：ramp 关闭是「the normal configuration
            //     when using shadow colours instead of a ramp」⇒ fork 原本的设计配置就是这样；
            //   · 两条路径都会**整段赋值** `sd.col.rgb` ⇒ 同时活着则 ShadowColor 覆盖 ramp（白烘）。
            // ⇒ 所以除了"谁开谁关"，还要断言**被恢复的 4 项 lilToon 机制真的 1:1 搬到了产物上**。
            Check("_ShadowColorEnable = 1（阴影走 ShadowColor 模块）",
                  Val(o, "_ShadowColorEnable") == 1, Val(o, "_ShadowColorEnable") + " 期望 1");
            Check("ShadeGradientIndex = -1（Shade ramp 必须关，否则两条路径同时活着、ramp 白烘）",
                  Mathf.RoundToInt(Val(o, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex")) == -1,
                  Val(o, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex") + " 期望 -1");

            // ── [NT-FIX 33] 被恢复的 lilToon 机制：源值必须 1:1 出现在产物上 ──
            // 源（CreateFakeMaterial）：BorderRange 0.1 / MainStrength 0.2 / Border 0.42 / Blur 0.07
            //                            2ndBorder 0.17 / 3rdBorder 0.26 / AAStrength 1.3
            Check("_ShadowBorderRange 1:1（lilToon 0.1；ramp 路径下这一项是 inert 的）",
                  Mathf.Abs(Val(o, "_ShadowBorderRange") - 0.1f) < 1e-3f, Val(o, "_ShadowBorderRange") + " 期望 0.1");
            Check("_ShadowMainStrength 1:1（lilToon 0.2；= Contrast）",
                  Mathf.Abs(Val(o, "_ShadowMainStrength") - 0.2f) < 1e-3f, Val(o, "_ShadowMainStrength") + " 期望 0.2");
            Check("_ShadowBorder / _ShadowBlur 1:1（0.42 / 0.07）",
                  Mathf.Abs(Val(o, "_ShadowBorder") - 0.42f) < 1e-3f && Mathf.Abs(Val(o, "_ShadowBlur") - 0.07f) < 1e-3f,
                  Val(o, "_ShadowBorder") + " / " + Val(o, "_ShadowBlur") + " 期望 0.42 / 0.07");
            Check("_Shadow2ndBorder / _Shadow3rdBorder 1:1（0.17 / 0.26）",
                  Mathf.Abs(Val(o, "_Shadow2ndBorder") - 0.17f) < 1e-3f && Mathf.Abs(Val(o, "_Shadow3rdBorder") - 0.26f) < 1e-3f,
                  Val(o, "_Shadow2ndBorder") + " / " + Val(o, "_Shadow3rdBorder") + " 期望 0.17 / 0.26");
            Check("_AAStrength 1:1（1.3；ramp 路径结构上无法承载 fwidth 抗锯齿）",
                  Mathf.Abs(Val(o, "_AAStrength") - 1.3f) < 1e-3f, Val(o, "_AAStrength") + " 期望 1.3");
            // ⚠️ 中性化**仍然生效**（[NT-FIX 26]/[NT-FIX 22] 复核后保留）：它与 MA 菜单
            //    `NT_ShadowStrength` **默认 1**（⇒ VRChat 里 `_ShadowStrength` 被驱动到 1.0，
            //    而 lilToon 作者设的是 0.10）耦合。撤掉它必须先解决菜单默认值 —— 那是另一项决定。
            Check("_ShadowColor 已中性化（三通道相等；源是粉紫 0.25/0.20/0.30）",
                  o.HasProperty("_ShadowColor")
                      && Mathf.Abs(o.GetColor("_ShadowColor").r - o.GetColor("_ShadowColor").b) < 1e-3f
                      && Mathf.Abs(o.GetColor("_ShadowColor").g - o.GetColor("_ShadowColor").b) < 1e-3f,
                  o.HasProperty("_ShadowColor") ? o.GetColor("_ShadowColor").ToString() : "<不存在>");
            Check("_ShadowStrength 有可见下限（[NT-FIX 27]，源 0.8 应保留）",
                  o.HasProperty("_ShadowStrength") && o.GetFloat("_ShadowStrength") >= 0.8f - 0.001f,
                  o.HasProperty("_ShadowStrength") ? o.GetFloat("_ShadowStrength").ToString("0.###") : "<不存在>");
            Check("_BaseTexture 已生成", o.HasProperty("_BaseTexture") && o.GetTexture("_BaseTexture") != null, o.HasProperty("_BaseTexture") && o.GetTexture("_BaseTexture") != null ? o.GetTexture("_BaseTexture").name : "<空>");
            Sb.AppendLine("  输出：" + entry.OutputPath);
        }

        // [NT-FIX 34/35] 诊断覆盖测试：**每一条新警告都必须有一处"确实该报警"的输入**，
        // 否则"加了警告"这件事本身没有被验证过。
        // 本项目反复出现的同一类故障就是"结论对、判据够不到"（[NT-FIX 30]、[NT-FIX 34]），
        // 所以诊断本身也要有测试。
        private static void RunDiagnosticsCase()
        {
            Sb.AppendLine();
            Sb.AppendLine("== [NT-FIX 34/35] 转换诊断覆盖 ==");

            // ① 环境反射：NonToon **零反射代码**（全包 grep "Reflect|Cubemap" = 0 命中），必须明确报警
            var normal = CreateFakeMaterial("diag_reflection", 0f);
            var e1 = LilToonMaterialConverter.Convert(normal, new ConversionOptions { OverwriteExisting = true });
            Check("环境反射未复现 → 有明确 WARNING",
                  HasMsg(e1, "no cubemap/probe environment reflection"),
                  FirstMsg(e1, "cubemap/probe"));
            // ⚠️ 判据必须**只在镜面映射那条消息里出现**。第一版用 "direct-light specular only"，
            //    结果匹配到的是**环境反射警告**（它也含 "direct-light specular term"）⇒
            //    断言"因为错误的理由通过"了。用只有该条消息才有的子串。
            Check("镜面映射的日志不再宣称'反射已转换'（改为 direct-light specular only）",
                  HasMsg(e1, "reflection colour to NonToon Specular"),
                  FirstMsg(e1, "reflection colour to NonToon Specular"));
            Check("环境反射警告与镜面映射日志是**两条独立**消息，都要出现",
                  HasMsg(e1, "reflection colour to NonToon Specular") && HasMsg(e1, "no cubemap/probe environment reflection"),
                  "镜面消息=" + HasMsg(e1, "reflection colour to NonToon Specular")
                  + " / 环境消息=" + HasMsg(e1, "no cubemap/probe environment reflection"));

            // ② Gem/Refraction：专用变体的 `_TransparentMode` **属性就是 0**、模式编在**着色器名**里
            //    ⇒ 旧判据 `transparentMode >= 3` 永远不触发（实测 tail_Crystal.mat 转换 severity=Success、零警告）
            var gemShader = AssetDatabase.LoadAssetAtPath<Shader>(GemShaderPath);
            var gemPath = Dir + "/probe_diag_gem.mat";
            var gemMat = AssetDatabase.LoadAssetAtPath<Material>(gemPath);
            if (gemMat == null) { gemMat = new Material(gemShader); AssetDatabase.CreateAsset(gemMat, gemPath); }
            gemMat.shader = gemShader;
            gemMat.SetFloat("_TransparentMode", 0f);   // ← 故意：属性是 0，模式只在着色器名里
            gemMat.SetFloat("_UseShadow", 1f);
            gemMat.SetFloat("_UseReflection", 0f);
            EditorUtility.SetDirty(gemMat);
            AssetDatabase.SaveAssets();
            var e2 = LilToonMaterialConverter.Convert(gemMat, new ConversionOptions { OverwriteExisting = true });
            Check("Gem 专用变体（_TransparentMode 属性 = 0）也会报'被近似成透明'",
                  HasMsg(e2, "gem/refraction"), FirstMsg(e2, "gem"));

            // ③ 通用「不支持」警告：**只在真开了那几个开关时**才该触发
            //    （实测本工程 `_UseGlitter`/`_UseEmission2nd`/`_UseParallax` = **0/47 全关**）
            var glit = CreateFakeMaterial("diag_glitter", 0f);
            glit.SetFloat("_UseGlitter", 1f);
            EditorUtility.SetDirty(glit);
            AssetDatabase.SaveAssets();
            var e3 = LilToonMaterialConverter.Convert(glit, new ConversionOptions { OverwriteExisting = true });
            Check("真开了 _UseGlitter 时通用「不支持」警告会触发",
                  HasMsg(e3, "Glitter"), FirstMsg(e3, "Glitter"));
            glit.SetFloat("_UseGlitter", 0f);
            EditorUtility.SetDirty(glit);
            AssetDatabase.SaveAssets();

            // ⑤ [NT-FIX 36] `_BackfaceForceShadow`：NonToon 的 sd 里**没有 facing 等价字段**
            //    （全仓 grep "facing" 只命中一处注释）⇒ 无法转换，必须如实报警而不是静默丢弃。
            Check("_BackfaceForceShadow 未转换 → 有明确 WARNING",
                  HasMsg(e1, "Backface Force Shadow") && HasMsg(e1, "no back-face flag equivalent"),
                  FirstMsg(e1, "Backface Force Shadow"));

            // ④ [NT-FIX 35b] **不变式**：源关闭阴影时，两条路径都必须关干净。
            //    ⚠️ 诚实记录：当前转换流程里目标材质总是**新建**的（`_ShadeGradientIndex` 默认就是 -1），
            //    所以这条**不是**在复现一个已发生的 bug，而是把这个不变式钉成回归护栏
            //    （审计第 7 轮 a 指出：若目标模板/复用资产残留合法索引，"源无阴影"的材质会被 ramp 着色）。
            var off = CreateFakeMaterial("diag_shadowoff", 0f);
            off.SetFloat("_UseShadow", 0f);
            EditorUtility.SetDirty(off);
            AssetDatabase.SaveAssets();
            var e4 = LilToonMaterialConverter.Convert(off, new ConversionOptions { OverwriteExisting = true });
            var o4 = string.IsNullOrEmpty(e4.OutputPath) ? null : AssetDatabase.LoadAssetAtPath<Material>(e4.OutputPath);
            Check("源 _UseShadow=0 → _ShadowColorEnable=0（不得开 ShadowColor）",
                  o4 != null && Val(o4, "_ShadowColorEnable") == 0,
                  o4 == null ? "<无产物>" : Val(o4, "_ShadowColorEnable") + " 期望 0");
            Check("源 _UseShadow=0 → ShadeGradientIndex=-1（不得残留 ramp 索引）",
                  o4 != null && Mathf.RoundToInt(Val(o4, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex")) == -1,
                  o4 == null ? "<无产物>" : Val(o4, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex") + " 期望 -1");
            off.SetFloat("_UseShadow", 1f);
            EditorUtility.SetDirty(off);
            AssetDatabase.SaveAssets();
        }

        private static bool HasMsg(ConversionEntry e, string needle)
        {
            for (int i = 0; i < e.Messages.Count; i++)
                if (e.Messages[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string FirstMsg(ConversionEntry e, string needle)
        {
            for (int i = 0; i < e.Messages.Count; i++)
                if (e.Messages[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return e.Messages[i];
            return "<没有匹配的消息>";
        }

        private static string StrI(Material m, string p) { return m.HasProperty(p) ? m.GetInteger(p).ToString() : "<不存在>"; }

        // 关键：按属性的真实类型取值。Int 属性用 GetInteger、Float 属性用 GetFloat。
        // 用错读取器会永远读到 0（我第一次就是这么误判 _SrcBlend 的）。
        private static float Val(Material m, string p)
        {
            if (m == null || !m.HasProperty(p)) return float.NaN;
            var idx = m.shader.FindPropertyIndex(p);
            if (idx >= 0 && m.shader.GetPropertyType(idx) == UnityEngine.Rendering.ShaderPropertyType.Int)
                return m.GetInteger(p);
            return m.GetFloat(p);
        }

        // [NT-TEST] 「把模型拖进窗口」的检测用例：Project 里的模型资源（fbx / prefab）
        // 往往**不带**材质，材质挂在场景实例上。旧的 CollectMaterials 只查资源依赖 → 0 个材质
        // → 转换按钮变灰 → 用户以为"窗口里没有转换按钮"。
        private static void RunDetectionCase()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var matScene = CreateFakeMaterial("A_scene", 0f);
            var matPrefab = CreateFakeMaterial("B_prefab", 0f);

            // 甲：Prefab 自带材质，场景实例上覆盖成另一个材质
            var a = GameObject.CreatePrimitive(PrimitiveType.Quad);
            a.name = "NTProbeA";
            a.GetComponent<MeshRenderer>().sharedMaterial = matPrefab;
            var prefabA = PrefabUtility.SaveAsPrefabAsset(a, Dir + "/NTProbeA.prefab");
            Object.DestroyImmediate(a);
            var instanceA = (GameObject)PrefabUtility.InstantiatePrefab(prefabA);
            instanceA.GetComponent<MeshRenderer>().sharedMaterial = matScene;
            DumpDetection("Prefab 自带材质 + 场景覆盖", prefabA, matScene);

            // 乙：Prefab **不带**材质（等价于没内嵌材质的 fbx —— 用户遇到的就是这种）
            var b = GameObject.CreatePrimitive(PrimitiveType.Quad);
            b.name = "NTProbeB";
            b.GetComponent<MeshRenderer>().sharedMaterial = null;
            var prefabB = PrefabUtility.SaveAsPrefabAsset(b, Dir + "/NTProbeB.prefab");
            Object.DestroyImmediate(b);
            var instanceB = (GameObject)PrefabUtility.InstantiatePrefab(prefabB);
            instanceB.GetComponent<MeshRenderer>().sharedMaterial = matScene;
            DumpDetection("Prefab 不带材质 + 场景实例有材质", prefabB, matScene);
        }

        private static void DumpDetection(string label, GameObject asset, Material expect)
        {
            var path = asset == null ? "" : AssetDatabase.GetAssetPath(asset);
            Sb.AppendLine("  [" + label + "]");
            var item = (Object)asset;
            Sb.AppendLine("      isGameObject=" + (item is GameObject) + " isMaterial=" + (item is Material)
                + " isValidFolder=" + AssetDatabase.IsValidFolder(path)
                + " 走「场景对象」分支=" + (item is GameObject && string.IsNullOrEmpty(path)));
            var deps = string.IsNullOrEmpty(path) ? new string[0] : AssetDatabase.GetDependencies(path, true);
            foreach (var d in deps.Where(x => x.EndsWith(".mat")))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(d);
                Sb.AppendLine("      dep=" + d + " → load=" + (m == null ? "null" : m.name + "@" + m.shader.name) + " IsLilToon=" + (m != null && LilToonMaterialConverter.IsLilToon(m)));
            }
            var method = typeof(LilToonToNonToonConverterWindow).GetMethod(
                "CollectMaterials",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(IEnumerable<Object>) },
                null);
            var direct = ((IEnumerable<Material>)method.Invoke(null, new object[] { new Object[] { expect } })).ToList();
            Sb.AppendLine("      直接用材质调用 → " + direct.Count + " 个（期望 1）");
            var found = ((IEnumerable<Material>)method.Invoke(null, new object[] { new Object[] { asset } })).ToList();
            Sb.AppendLine("      用模型资源调用 → " + found.Count + " 个");
            Check(label + " 能取到场景实例上的材质", found.Contains(expect), found.Contains(expect) ? "命中 " + expect.name : "没命中（按钮会变灰）");
        }

        // [NT-TEST] 本分支多出来的能力是否被转换器真正利用：发光走原生 Emission 模块（1:1），
        // lilToon 的光照调整同名同义直接搬，受光方向置 0 以贴近 lilToon 的「跟光」行为。
        private static void RunForkFeatureCase()
        {
            var mat = CreateFakeMaterial("fork_features", 0f);
            mat.SetFloat("_UseEmission", 1f);
            mat.SetColor("_EmissionColor", new Color(0.2f, 0.6f, 0.9f, 1f));
            mat.SetFloat("_EmissionBlend", 0.75f);
            mat.SetFloat("_EmissionMainStrength", 0.3f);
            mat.SetFloat("_EmissionBlendMode", 1f);
            mat.SetTexture("_EmissionMap", MakeTexture("NTProbe_EmissionMap"));
            mat.SetTexture("_EmissionBlendMask", MakeTexture("NTProbe_EmissionBlendMask"));
            mat.SetFloat("_AsUnlit", 0.25f);
            mat.SetFloat("_LightMinLimit", 0.06f);
            mat.SetFloat("_LightMaxLimit", 1.4f);
            mat.SetFloat("_MonochromeLighting", 0.5f);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            var entry = LilToonMaterialConverter.Convert(mat, new ConversionOptions { OverwriteExisting = true });
            Sb.AppendLine("  Severity = " + entry.Severity + " → " + entry.OutputPath);
            foreach (var m in entry.Messages)
                if (m.Contains("emission") || m.Contains("lighting") || m.Contains("Shade Direction")) Sb.AppendLine("    MSG  " + m);

            var o = AssetDatabase.LoadAssetAtPath<Material>(entry.OutputPath);
            if (o == null) { Check("产物存在", false, "<null>"); return; }
            Check("Emission 模块被打开（_UseEmission=1，且能活过 CreateAsset）", Val(o, "_UseEmission") == 1, Val(o, "_UseEmission").ToString());
            Check("_EmissionColor 1:1", o.HasProperty("_EmissionColor") && Vector4.Distance(o.GetColor("_EmissionColor"), new Color(0.2f, 0.6f, 0.9f, 1f)) < 0.01f, o.HasProperty("_EmissionColor") ? o.GetColor("_EmissionColor").ToString() : "<无>");
            Check("_EmissionBlend = 0.75", Mathf.Abs(Val(o, "_EmissionBlend") - 0.75f) < 0.001f, Val(o, "_EmissionBlend").ToString("0.###"));
            Check("_EmissionMainStrength = 0.3", Mathf.Abs(Val(o, "_EmissionMainStrength") - 0.3f) < 0.001f, Val(o, "_EmissionMainStrength").ToString("0.###"));
            Check("_EmissionBlendMode = 1", Val(o, "_EmissionBlendMode") == 1, Val(o, "_EmissionBlendMode").ToString());
            Check("_EmissionMap 贴图已拷", o.HasProperty("_EmissionMap") && o.GetTexture("_EmissionMap") != null, o.HasProperty("_EmissionMap") && o.GetTexture("_EmissionMap") != null ? o.GetTexture("_EmissionMap").name : "<空>");
            Check("_EmissionBlendMask 贴图已拷", o.HasProperty("_EmissionBlendMask") && o.GetTexture("_EmissionBlendMask") != null, o.HasProperty("_EmissionBlendMask") && o.GetTexture("_EmissionBlendMask") != null ? o.GetTexture("_EmissionBlendMask").name : "<空>");
            Check("_AsUnlit 1:1", Mathf.Abs(Val(o, "_AsUnlit") - 0.25f) < 0.001f, Val(o, "_AsUnlit").ToString("0.###"));
            Check("_LightMinLimit 1:1", Mathf.Abs(Val(o, "_LightMinLimit") - 0.06f) < 0.001f, Val(o, "_LightMinLimit").ToString("0.###"));
            Check("_LightMaxLimit 1:1", Mathf.Abs(Val(o, "_LightMaxLimit") - 1.4f) < 0.001f, Val(o, "_LightMaxLimit").ToString("0.###"));
            Check("_MonochromeLighting 1:1", Mathf.Abs(Val(o, "_MonochromeLighting") - 0.5f) < 0.001f, Val(o, "_MonochromeLighting").ToString("0.###"));
            Check("_ShadeDirectionBias 置 0（跟光）", Mathf.Abs(Val(o, "_ShadeDirectionBias")) < 0.001f, Val(o, "_ShadeDirectionBias").ToString("0.###"));
            Check("没为发光浪费 SharedMask 通道", o.GetTexture("_SharedMask") == null, o.GetTexture("_SharedMask") == null ? "未生成（正确）" : "生成了（会占用通道）");
        }

        private static Texture2D MakeTexture(string name)
        {
            var path = Dir + "/" + name + ".png";
            var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (loaded != null) return loaded;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) tex.SetPixel(x, y, Color.white);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // [NT-TEST] 自阴影烘焙：一块地面 + 一块悬在上方的遮挡物。
        // 关键断言：遮挡物正下方的像素记录到的深度必须比空地更"近"（即阴影成立）。
        private static void RunSelfLightBakeCase()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGo = new GameObject("NTSelfLightProbe");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 从上往下照（forward = -Y）

            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            floor.name = "floor";
            floor.transform.position = Vector3.zero;
            floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            floor.transform.localScale = new Vector3(2f, 2f, 1f);

            var blocker = GameObject.CreatePrimitive(PrimitiveType.Quad);
            blocker.name = "blocker";
            blocker.transform.position = new Vector3(0f, 0.5f, 0f);
            blocker.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            blocker.transform.localScale = new Vector3(0.5f, 0.5f, 1f);

            var settings = lightGo.AddComponent<NTSelfLight>();
            settings.lightSource = light;

            var renderers = new Renderer[] { floor.GetComponent<Renderer>(), blocker.GetComponent<Renderer>() };
            var result = NTSelfShadowBaker.Bake(light, renderers, 64, "Assets/NTProbe/SelfLight");
            Sb.AppendLine("  网格=" + result.meshCount + " 像素=" + result.texels + " 命中=" + result.hits
                + " 半宽=" + result.halfX.ToString("F3") + " 深度=" + result.far.ToString("F3")
                + " 贴图=" + (result.map != null ? result.map.name : "<null>"));
            Check("烘焙出贴图", result.map != null, result.map != null ? result.map.name : "<null>");
            Check("射线确实打到几何体", result.hits > 0, result.hits + " 像素命中");

            if (result.depth != null && result.depth.Length == 64 * 64)
            {
                var below = result.depth[32 * 64 + 32];  // 遮挡物正下方
                var corner = result.depth[4 * 64 + 4];   // 空地
                Sb.AppendLine("  遮挡物下方深度=" + below.ToString("F3") + "  空地深度=" + corner.ToString("F3"));
                Check("遮挡物下方记录到更近的深度（自阴影成立）", below < corner - 0.001f, below.ToString("F3") + " < " + corner.ToString("F3"));
            }
            else Check("拿到深度缓冲", false, "长度不对");

            Object.DestroyImmediate(lightGo);
        }

        // [NT-TEST] v0.3.0：SelfLight v2（类 PCSS）、ShadowColor 逐像素阴影遮罩、工具包完整汉化。
        private static void RunV030Case()
        {
            var selfLightProps = new[]
            {
                "_SelfLightPCSS", "_SelfLightSoftness", "_SelfLightDensity", "_SelfLightClamp",
                "_SelfLightDistance", "_SelfLightShadowTexels", "_SelfLightReceiveMask",
                "_SelfLightReceiveMaskChannel", "_SelfLightReceiveMaskStrength"
            };
            var maskProps = new[] { "_ShadowMaskEnable", "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask" };
            foreach (var name in new[] { "nontoon-fork", "nontoon-fork-fur" })
            {
                var shader = Shader.Find(name);
                if (shader == null) { Check(name + " 着色器存在", false, "<null>"); continue; }
                var missSelf = selfLightProps.Where(p => shader.FindPropertyIndex(p) < 0).ToArray();
                var missMask = maskProps.Where(p => shader.FindPropertyIndex(p) < 0).ToArray();
                Check(name + " SelfLight v2 属性齐全", missSelf.Length == 0, missSelf.Length == 0 ? selfLightProps.Length + "/" + selfLightProps.Length : "缺 " + string.Join(", ", missSelf));
                Check(name + " 逐像素阴影遮罩属性齐全", missMask.Length == 0, missMask.Length == 0 ? maskProps.Length + "/" + maskProps.Length : "缺 " + string.Join(", ", missMask));
            }

            // 汉化：ShaderCore 的 lang/*.po 就是材质面板显示名的来源，直接查关键 key
            CheckPoKeys("com.catandling.nontoon", "Shaders/lang/zh-Hans.po",
                new[] { "ZWrite", "Cull", "Outline Offset Factor", "Outline Ref", "ShadowColor", "Emission", "Copy", "From Shader" });
            CheckPoKeys("com.catandling.nontoon", "Shaders/Modules/SelfLight/lang/zh-Hans.po",
                new[] { "PCSS Soft Shadow", "Shadow Distance", "Receive Mask", "Softness" });
            CheckPoKeys("com.catandling.nontoon", "Shaders/Modules/ShadowColor/lang/zh-Hans.po",
                new[] { "Use Shadow Masks", "Shadow Strength Mask", "Shadow Border Mask", "Shadow Blur Mask" });
            CheckPoKeys("com.catandling.nontoon-converter", "lang/zh-Hans.po",
                new[] { "Last conversion", "Converting lilToon materials" });

            Sb.AppendLine("  NTL10n.IsChinese = " + NTL10n.IsChinese + "（当前区域 " + System.Globalization.CultureInfo.CurrentCulture.Name + "）");
            Sb.AppendLine("  L(\"Last conversion\") = " + NTL10n.L("Last conversion"));
            Check("NTL10n 英文 fallback 生效", NTL10n.L("__NTL10N_MISSING_KEY__") == "__NTL10N_MISSING_KEY__", NTL10n.L("__NTL10N_MISSING_KEY__"));
            var formatted = NTL10n.F("Success: {0}, Warnings: {1}, Unsupported: {2}, Errors: {3}", 1, 2, 3, 4);
            Check("报告模板可格式化且含计数", formatted.Contains("2") && formatted.Contains("4"), formatted);
            if (NTL10n.IsChinese) Check("中文表命中（Last conversion）", NTL10n.L("Last conversion") == "上次转换", NTL10n.L("Last conversion"));

            // lilToon 三张逐像素阴影遮罩 → NonToon ShadowColor 模块
            var mat = CreateFakeMaterial("v030_masks", 0f);
            mat.SetTexture("_ShadowStrengthMask", MakeTexture("NTProbe_ShadowStrengthMask"));
            mat.SetTexture("_ShadowBorderMask", MakeTexture("NTProbe_ShadowBorderMask"));
            mat.SetTexture("_ShadowBlurMask", MakeTexture("NTProbe_ShadowBlurMask"));
            mat.SetTexture("_ShadowBorderMask", MakeTexture("NTProbe_ShadowBorderMask"));
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            var entry = LilToonMaterialConverter.Convert(mat, new ConversionOptions { OverwriteExisting = true });
            var o = string.IsNullOrEmpty(entry.OutputPath) ? null : AssetDatabase.LoadAssetAtPath<Material>(entry.OutputPath);
            Check("遮罩用例产出了材质", o != null, entry.OutputPath);
            if (o != null)
            {
                Check("_ShadowMaskEnable 已被打开", Val(o, "_ShadowMaskEnable") == 1, Val(o, "_ShadowMaskEnable").ToString());
                Check("_ShadowStrengthMask 贴图已拷", o.GetTexture("_ShadowStrengthMask") != null, o.GetTexture("_ShadowStrengthMask") != null ? o.GetTexture("_ShadowStrengthMask").name : "<空>");
                Check("_ShadowBorderMask 贴图已拷", o.GetTexture("_ShadowBorderMask") != null, o.GetTexture("_ShadowBorderMask") != null ? o.GetTexture("_ShadowBorderMask").name : "<空>");
                Check("_ShadowBlurMask 贴图已拷", o.GetTexture("_ShadowBlurMask") != null, o.GetTexture("_ShadowBlurMask") != null ? o.GetTexture("_ShadowBlurMask").name : "<空>");
            }
            foreach (var m in entry.Messages)
                if (m.Contains("shadow mask") || m.Contains("遮罩")) Sb.AppendLine("    MSG  " + m);
        }

        private static void CheckPoKeys(string package, string relative, string[] keys)
        {
            var path = "Packages/" + package + "/" + relative;
            if (!File.Exists(path)) { Check(relative + " 存在", false, path); return; }
            var text = File.ReadAllText(path);
            var missing = keys.Where(k => !text.Contains("msgid \"" + k + "\"")).ToArray();
            var count = text.Split(new[] { "msgid " }, StringSplitOptions.None).Length - 1;
            Check(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path) + " 关键 key 齐全（共 " + count + " 条）",
                missing.Length == 0, missing.Length == 0 ? string.Join(", ", keys) : "缺 " + string.Join(", ", missing));
        }

        // [NT-TEST] v0.3.1：PCSS 画质档位属性 + 实时光源模式（真的挂 Spot Light）。
        private static void RunV031Case()
        {
            foreach (var name in new[] { "nontoon-fork", "nontoon-fork-fur" })
            {
                var shader = Shader.Find(name);
                if (shader == null) { Check(name + " 存在", false, "<null>"); continue; }
                Check(name + " 有 _SelfLightPCSSQuality", shader.FindPropertyIndex("_SelfLightPCSSQuality") >= 0, "画质档位属性");
            }

            // 实时光源：搭一个假的 avatar 层级
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("NTAvatarRoot");
            var body = GameObject.CreatePrimitive(PrimitiveType.Quad);
            body.name = "body";
            body.transform.SetParent(root.transform, false);
            var bodyRenderer = body.GetComponent<Renderer>();
            bodyRenderer.receiveShadows = false;                       // 故意先关掉，看工具会不会打开
            bodyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var settings = root.AddComponent<NTSelfLight>();
            settings.mode = NTSelfLightMode.RealtimeLight;
            settings.color = new Color(0.9f, 0.8f, 1f, 1f);
            settings.intensity = 2f;
            settings.spotAngle = 42f;
            settings.lightRange = 2.5f;
            settings.realtimeCullingMask = 1 << 10;
            settings.shadowStrength = 0.7f;

            var light = NTSelfRealtimeLight.Sync(settings, false);
            Check("实时光源被创建", light != null, light == null ? "<null>" : light.name);
            if (light != null)
            {
                Check("类型是 Spot", light.type == LightType.Spot, light.type.ToString());
                Check("开了软阴影", light.shadows == LightShadows.Soft, light.shadows.ToString());
                Check("强度 2.0", Mathf.Abs(light.intensity - 2f) < 0.001f, light.intensity.ToString("0.###"));
                Check("角度 42", Mathf.Abs(light.spotAngle - 42f) < 0.001f, light.spotAngle.ToString("0.###"));
                Check("范围 2.5", Mathf.Abs(light.range - 2.5f) < 0.001f, light.range.ToString("0.###"));
                Check("CullingMask = 第 10 层（PlayerLocal）", light.cullingMask == (1 << 10), light.cullingMask.ToString());
                Check("阴影强度 0.7", Mathf.Abs(light.shadowStrength - 0.7f) < 0.001f, light.shadowStrength.ToString("0.###"));
                Check("挂在组件所在节点下", light.transform.parent == root.transform, light.transform.parent == null ? "<null>" : light.transform.parent.name);
                Check("组件记住了这盏光", settings.realtimeLight == light, settings.realtimeLight == null ? "<null>" : "ok");
            }
            Check("Renderer 的 Receive Shadows 被打开", bodyRenderer.receiveShadows, bodyRenderer.receiveShadows.ToString());
            Check("Renderer 的 Cast Shadows 被打开", bodyRenderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On, bodyRenderer.shadowCastingMode.ToString());
            foreach (var w in NTSelfRealtimeLight.Warnings(settings)) Sb.AppendLine("    WARN " + w);
            Check("合法配置下没有警告", NTSelfRealtimeLight.Warnings(settings).Count == 0, "warnings=" + NTSelfRealtimeLight.Warnings(settings).Count);

            // 反例：Culling Mask 没选 PlayerLocal 时必须给出警告
            settings.realtimeCullingMask = 1 << 0;
            Check("没选 PlayerLocal 时给出警告", NTSelfRealtimeLight.Warnings(settings).Count > 0, "warnings=" + NTSelfRealtimeLight.Warnings(settings).Count);

            // 清理
            Object.DestroyImmediate(root);
        }

        // [NT-TEST] v0.4.3：Avatar 光源插件 —— 一键创建一盏"只照自己"的实时光，且不碰材质。
        private static void RunV043Case()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("NTAvatarLightRoot");
            var body = GameObject.CreatePrimitive(PrimitiveType.Quad);
            body.name = "body";
            body.transform.SetParent(root.transform, false);
            var renderer = body.GetComponent<Renderer>();
            renderer.receiveShadows = false;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 材质用的是 NonToon（但插件是着色器无关的，必须一个属性都不动）
            var mat = new Material(EnsureShader("nontoon-fork"));
            if (mat.HasProperty("_UseSelfLight")) mat.SetInteger("_UseSelfLight", 0);
            renderer.sharedMaterial = mat;

            var plugin = NTAvatarLightCreator.Create(root, false);
            Check("一键创建出光源插件", plugin != null, plugin == null ? "<null>" : plugin.name);
            var light = plugin != null ? plugin.target : null;
            Check("光源组件被生成", light != null, light == null ? "<null>" : light.name);
            if (light != null)
            {
                Check("类型 Spot", light.type == LightType.Spot, light.type.ToString());
                Check("软阴影", light.shadows == LightShadows.Soft, light.shadows.ToString());
                Check("CullingMask = 第 10 层 PlayerLocal", light.cullingMask == (1 << 10), light.cullingMask.ToString());
                Check("默认角度 60", Mathf.Abs(light.spotAngle - 60f) < 0.001f, light.spotAngle.ToString("0.###"));
                Check("默认范围 3m", Mathf.Abs(light.range - 3f) < 0.001f, light.range.ToString("0.###"));
                Check("默认光强 1.5", Mathf.Abs(light.intensity - 1.5f) < 0.001f, light.intensity.ToString("0.###"));
                Check("挂在 avatar 根节点下", light.transform.parent == root.transform, light.transform.parent == null ? "<null>" : light.transform.parent.name);
            }
            Check("Renderer 的 Receive Shadows 被打开", renderer.receiveShadows, renderer.receiveShadows.ToString());
            Check("Renderer 的 Cast Shadows 被打开", renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On, renderer.shadowCastingMode.ToString());
            Check("着色器无关：没有动材质属性", mat.GetInteger("_UseSelfLight") == 0, "_UseSelfLight=" + mat.GetInteger("_UseSelfLight"));
            Check("合法配置无警告", NTAvatarLightCreator.Warnings(plugin).Count == 0, "warnings=" + NTAvatarLightCreator.Warnings(plugin).Count);

            // 反例 1：Culling Mask 没选 PlayerLocal
            plugin.cullingMask = 1 << 0;
            Check("没选 PlayerLocal 时警告", NTAvatarLightCreator.Warnings(plugin).Any(w => w.Contains("PlayerLocal")), NTAvatarLightCreator.Warnings(plugin).Count + " 条");
            plugin.cullingMask = 1 << 10;

            // 反例 2：材质还开着 NonToon 的「自有光源」→ 双份打光
            mat.SetInteger("_UseSelfLight", 1);
            Check("检测到双份打光时警告", NTAvatarLightCreator.Warnings(plugin).Any(w => w.Contains("双份打光")), NTAvatarLightCreator.Warnings(plugin).Count + " 条");
            mat.SetInteger("_UseSelfLight", 0);

            // 重复创建不应产生第二盏灯
            var again = NTAvatarLightCreator.Create(root, false);
            Check("重复创建是幂等的（不会多一盏灯）", root.GetComponentsInChildren<Light>(true).Length == 1 && again == plugin,
                root.GetComponentsInChildren<Light>(true).Length + " 盏灯");

            Object.DestroyImmediate(mat);
            Object.DestroyImmediate(root);
        }

        // [NT-TEST] v0.3.4：把"默认消耗"钉死 —— 默认必须是最省的那一档。
        private static void RunV034Case()
        {
            var shader = EnsureShader("nontoon-fork");
            if (shader == null) { Check("NonToon 存在", false, "<null>"); return; }
            // Int/Range 混着，直接建一个材质读默认值（GetPropertyDefaultFloatValue 对 Int 会抛异常）
            var fresh = new Material(shader);

            Check("_UseSelfLight 默认关闭（模块启用但零成本）", Val(fresh, "_UseSelfLight") == 0f, Val(fresh, "_UseSelfLight").ToString());
            Check("_SelfLightShadowStrength 默认 1", Mathf.Abs(Val(fresh, "_SelfLightShadowStrength") - 1f) < 0.001f, Val(fresh, "_SelfLightShadowStrength").ToString("0.###"));
            Check("_SelfLightPCSSQuality 默认 0 = 低（20 次采样，不是 36）", Val(fresh, "_SelfLightPCSSQuality") == 0f, Val(fresh, "_SelfLightPCSSQuality").ToString());
            Check("_SelfLightReceiveMaskStrength 默认 0（不指定遮罩不采样）", Val(fresh, "_SelfLightReceiveMaskStrength") == 0f, Val(fresh, "_SelfLightReceiveMaskStrength").ToString());
            Check("_ShadowMaskEnable 默认 0（三张遮罩共 0 次采样）", Val(fresh, "_ShadowMaskEnable") == 0f, Val(fresh, "_ShadowMaskEnable").ToString());
            Check("_UseEmission 默认 0", Val(fresh, "_UseEmission") == 0f, Val(fresh, "_UseEmission").ToString());

            Sb.AppendLine("  每像素自阴影采样预算：");
            foreach (var q in new[] { NTPCSSQuality.Low, NTPCSSQuality.Medium, NTPCSSQuality.High, NTPCSSQuality.Ultra })
            {
                var t = NTSelfLightEditor.PCSSBodyTaps(q);
                Sb.AppendLine("    " + q + "：" + t.Item1 + " blocker + " + t.Item2 + " PCF = " + (t.Item1 + t.Item2) + " 次/像素");
            }
            var low = NTSelfLightEditor.PCSSBodyTaps(NTPCSSQuality.Low);
            Check("低档 = 8+12 = 20 次", low.Item1 == 8 && low.Item2 == 12 && low.Item1 + low.Item2 == 20, (low.Item1 + low.Item2) + " 次");
            var ultra = NTSelfLightEditor.PCSSBodyTaps(NTPCSSQuality.Ultra);
            Check("极高 = 32+64 = 96 次", ultra.Item1 == 32 && ultra.Item2 == 64 && ultra.Item1 + ultra.Item2 == 96, (ultra.Item1 + ultra.Item2) + " 次");

            var settings = new GameObject("NTBudgetProbe").AddComponent<NTSelfLight>();
            settings.pcss = true;
            settings.pcssQuality = NTPCSSQuality.Low;
            var text = NTSelfLightEditor.ShadowBudgetText(settings);
            Sb.AppendLine("  面板读数：" + text.Replace("\n", " / "));
            Check("面板读数为低档算出 20 次", text.Contains("20 次") && !text.Contains("21"), text.Replace("\n", " "));
            settings.pcss = false;
            Check("关掉 PCSS 时读数为 1 次", NTSelfLightEditor.ShadowBudgetText(settings).Contains("1 次"), NTSelfLightEditor.ShadowBudgetText(settings));
            Object.DestroyImmediate(settings.gameObject);
        }


        // [NT-TEST] v0.3.5：色温 + 环境匹配 + UI（内部属性不上面板）。
        private static void RunV035Case()
        {
            var shader = EnsureShader("nontoon-fork");
            if (shader == null) { Check("NonToon 存在", false, "<null>"); return; }
            var fresh = new Material(shader);

            // 新属性与默认值（默认必须"不改变行为"）
            foreach (var p in new[] { "_SelfLightUseTemperature", "_SelfLightTemperature", "_SelfLightMatchAmbient", "_SelfLightMatchDirection" })
                Check("有属性 " + p, shader.FindPropertyIndex(p) >= 0, "index=" + shader.FindPropertyIndex(p));
            Check("_SelfLightUseTemperature 默认 0（不开色温）", Val(fresh, "_SelfLightUseTemperature") == 0f, Val(fresh, "_SelfLightUseTemperature").ToString());
            Check("_SelfLightTemperature 默认 6500K（中性白）", Mathf.Abs(Val(fresh, "_SelfLightTemperature") - 6500f) < 1f, Val(fresh, "_SelfLightTemperature").ToString("0"));
            Check("_SelfLightMatchAmbient 默认 0（不匹配世界光）", Val(fresh, "_SelfLightMatchAmbient") == 0f, Val(fresh, "_SelfLightMatchAmbient").ToString());
            Check("_SelfLightMatchDirection 默认 0（用自己的方向）", Val(fresh, "_SelfLightMatchDirection") == 0f, Val(fresh, "_SelfLightMatchDirection").ToString());

            // UI：这 10 个是"自备阴影贴图"要用到的映射参数，必须**可见**（0.3.6 起从 SCHide 放出来）
            var mapProps = new[]
            {
                "_SelfLightShadowMap", "_SelfLightShadowTexels", "_SelfLightOrigin", "_SelfLightRight",
                "_SelfLightUp", "_SelfLightForward", "_SelfLightHalfX", "_SelfLightHalfY", "_SelfLightNear", "_SelfLightFar"
            };
            var stillHidden = new List<string>();
            foreach (var p in mapProps)
            {
                var i = shader.FindPropertyIndex(p);
                if (i < 0) { stillHidden.Add(p + "(缺失)"); continue; }
                var attrs = shader.GetPropertyAttributes(i);
                if (attrs != null && attrs.Any(a => a == "SCHide")) stillHidden.Add(p);
            }
            Check("阴影贴图映射参数全部可见（可自备贴图，共 " + mapProps.Length + " 个）", stillHidden.Count == 0,
                stillHidden.Count == 0 ? "10/10 可见" : "仍隐藏：" + string.Join(", ", stillHidden));

            // 用户实际要调的那些必须**不**隐藏
            var visible = new[] { "_SelfLightColor", "_SelfLightIntensity", "_SelfLightUseTemperature", "_SelfLightTemperature",
                                  "_SelfLightMatchAmbient", "_SelfLightMatchDirection", "_SelfLightPCSSQuality", "_SelfLightShadowStrength" };
            var wronglyHidden = new List<string>();
            foreach (var p in visible)
            {
                var i = shader.FindPropertyIndex(p);
                if (i < 0) { wronglyHidden.Add(p + "(缺失)"); continue; }
                var attrs = shader.GetPropertyAttributes(i);
                if (attrs != null && attrs.Any(a => a == "SCHide")) wronglyHidden.Add(p);
            }
            Check("用户可调属性没被误隐藏", wronglyHidden.Count == 0, wronglyHidden.Count == 0 ? "8/8 可见" : string.Join(", ", wronglyHidden));

            // 组件侧：色温/匹配字段存在且默认不改变行为
            var go = new GameObject("NTTempProbe");
            var comp = go.AddComponent<NTSelfLight>();
            Check("组件有 色温 / 匹配世界光 字段",
                comp.useColorTemperature == false && Mathf.Abs(comp.temperature - 6500f) < 1f && comp.matchWorldColor == 0f && comp.matchWorldDirection == 0f,
                "temp=" + comp.temperature.ToString("0") + " matchColor=" + comp.matchWorldColor + " matchDir=" + comp.matchWorldDirection);
            var av = go.AddComponent<NTAvatarLight>();
            Check("Avatar 光源插件也有色温字段", av.useColorTemperature == false && Mathf.Abs(av.temperature - 6500f) < 1f, av.temperature.ToString("0") + "K");

            Object.DestroyImmediate(go);
            Object.DestroyImmediate(fresh);
        }

        private static void DumpProp(Material m, string p)
        {
            if (!m.HasProperty(p)) { Sb.AppendLine("  " + p + " : <材质上不存在>"); return; }
            var idx = m.shader.FindPropertyIndex(p);
            var type = idx >= 0 ? m.shader.GetPropertyType(idx).ToString() : "(FindPropertyIndex = -1)";
            Sb.AppendLine("  " + p + " : type=" + type + "  GetInteger=" + m.GetInteger(p) + "  GetFloat=" + m.GetFloat(p) + "  Val=" + Val(m, p));
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (!ok) Fail++;
            Sb.AppendLine("  " + (ok ? "✅" : "❌") + " " + name + " → " + detail);
        }

        private static Material CreateFakeMaterial(string label, float transparentMode, int renderQueue = -1)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            var path = Dir + "/probe_" + label + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = shader;
            mat.SetFloat("_TransparentMode", transparentMode);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_UseShadow", 1f);
            mat.SetColor("_Color", new Color(0.9f, 0.8f, 0.7f, 1f));
            mat.SetColor("_ShadowColor", new Color(0.25f, 0.20f, 0.30f, 1f));
            mat.SetColor("_Shadow2ndColor", new Color(0.40f, 0.35f, 0.45f, 1f));
            mat.SetColor("_Shadow3rdColor", new Color(0.10f, 0.10f, 0.15f, 1f));
            mat.SetColor("_ShadowBorderColor", new Color(1f, 0.1f, 0f, 1f));
            mat.SetFloat("_ShadowBorder", 0.42f);
            mat.SetFloat("_ShadowBlur", 0.07f);
            mat.SetFloat("_Shadow2ndBorder", 0.17f);
            mat.SetFloat("_Shadow2ndBlur", 0.08f);
            mat.SetFloat("_Shadow3rdBorder", 0.26f);
            mat.SetFloat("_Shadow3rdBlur", 0.09f);
            mat.SetFloat("_ShadowStrength", 0.8f);
            mat.SetFloat("_ShadowBorderRange", 0.1f);
            mat.SetFloat("_ShadowMainStrength", 0.2f);
            mat.SetFloat("_AAStrength", 1.3f);
            // [NT-FIX 35] 环境反射：让合成源材质**确实启用**环境反射，否则
            // 「环境反射被静默丢弃」这条新诊断在装置 B 里根本走不到（= 空测）。
            // lilToon 的 `_UseReflection` 覆盖两件事：直接光镜面高光（能映）与环境反射（不能映）。
            mat.SetFloat("_UseReflection", 1f);
            mat.SetFloat("_ApplySpecular", 1f);
            mat.SetFloat("_Reflectance", 0.5f);
            mat.SetFloat("_ApplyReflection", 1f);
            // [NT-FIX 36] `_BackfaceForceShadow`：NonToon 的 sd 里没有 facing 等价字段 ⇒
            // 只能如实报警。这里设一个非零值，让那条警告在装置 B 里**真的被走到**。
            mat.SetFloat("_BackfaceForceShadow", 0.5f);
            // [NT-FIX 32] 让合成源材质带上"现实中会有的队列"，否则 `[NT-FIX 32]` 的
            // 「照搬源队列」这条路径在装置 B 里根本不会被走到（等于空测）。
            if (renderQueue >= 0) mat.renderQueue = renderQueue;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        private static void EnsureFakeShader()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "NTProbe");
            // 注意：必须**内容变化就重写**。只判断"文件是否存在"的话，
            // 假着色器会被上一次运行缓存住，新加的属性不会生效 → 测出来全是假失败。
            var needsWrite = true;
            if (File.Exists(ShaderPath)) needsWrite = File.ReadAllText(ShaderPath) != FakeShaderSource;
            if (needsWrite)
            {
                File.WriteAllText(ShaderPath, FakeShaderSource);
                AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);
            }
            Sb.AppendLine("fake lilToon shader = " + (AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) != null ? "OK" : "加载失败")
                + (needsWrite ? "（已按新内容重写）" : "（内容未变，复用）"));

            // [NT-FIX 34] 同内容的 **Gem 命名变体**：只有着色器名不同、`_TransparentMode` 仍是 0。
            // 专门用来验证 [NT-FIX 34] 修好的那条"够不到的判据"。
            var gemNeedsWrite = true;
            if (File.Exists(GemShaderPath)) gemNeedsWrite = File.ReadAllText(GemShaderPath) != GemShaderSource;
            if (gemNeedsWrite)
            {
                File.WriteAllText(GemShaderPath, GemShaderSource);
                AssetDatabase.ImportAsset(GemShaderPath, ImportAssetOptions.ForceUpdate);
            }
            Sb.AppendLine("fake lilToon **Gem 变体**着色器 = "
                + (AssetDatabase.LoadAssetAtPath<Shader>(GemShaderPath) != null ? "OK" : "加载失败")
                + (gemNeedsWrite ? "（已重写）" : "（复用）"));
        }

        private static readonly string GemShaderSource =
            FakeShaderSource.Replace("\"lilToon (probe)\"", "\"Hidden/lilToonGem (probe)\"");

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private const string FakeShaderSource = @"
Shader ""lilToon (probe)""
{
    Properties
    {
        _Color (""Color"", Color) = (1,1,1,1)
        _MainTex (""MainTex"", 2D) = ""white"" {}
        _TransparentMode (""TransparentMode"", Float) = 0
        _Cutoff (""Cutoff"", Range(0,1)) = 0.5
        _Cull (""Cull"", Float) = 2
        _ZWrite (""ZWrite"", Float) = 1
        _UseShadow (""UseShadow"", Float) = 0
        _ShadowColor (""Shadow Color"", Color) = (0.5,0.5,0.5,1)
        _Shadow2ndColor (""2nd Color"", Color) = (0.7,0.7,0.7,1)
        _Shadow3rdColor (""3rd Color"", Color) = (0,0,0,0)
        _ShadowBorderColor (""Border Color"", Color) = (1,0,0,1)
        _ShadowColorTex (""Shadow Color Tex"", 2D) = ""black"" {}
        _Shadow2ndColorTex (""2nd Color Tex"", 2D) = ""black"" {}
        _Shadow3rdColorTex (""3rd Color Tex"", 2D) = ""black"" {}
        _ShadowBorder (""Border"", Range(0,1)) = 0.5
        _ShadowBlur (""Blur"", Range(0,1)) = 0.1
        _Shadow2ndBorder (""2nd Border"", Range(0,1)) = 0.15
        _Shadow2ndBlur (""2nd Blur"", Range(0,1)) = 0.1
        _Shadow3rdBorder (""3rd Border"", Range(0,1)) = 0.25
        _Shadow3rdBlur (""3rd Blur"", Range(0,1)) = 0.1
        _ShadowStrength (""Strength"", Range(0,1)) = 1
        _ShadowBorderRange (""Border Range"", Range(0,1)) = 0.08
        _ShadowMainStrength (""Main Strength"", Range(0,1)) = 0
        _AAStrength (""AA Strength"", Range(0,4)) = 1
        _ShadowStrengthMask (""Shadow Strength Mask"", 2D) = ""white"" {}
        _ShadowBorderMask (""Shadow Border Mask"", 2D) = ""white"" {}
        _ShadowBlurMask (""Shadow Blur Mask"", 2D) = ""white"" {}
        _UseEmission (""UseEmission"", Float) = 0
        _EmissionColor (""Emission Color"", Color) = (1,1,1,1)
        _EmissionMap (""Emission Map"", 2D) = ""white"" {}
        _EmissionBlend (""Emission Blend"", Range(0,1)) = 1
        _EmissionBlendMask (""Emission Blend Mask"", 2D) = ""white"" {}
        _EmissionMainStrength (""Emission Main Strength"", Range(0,1)) = 0
        _EmissionBlendMode (""Emission Blend Mode"", Float) = 1
        _AsUnlit (""As Unlit"", Range(0,1)) = 0
        _LightMinLimit (""Light Min Limit"", Range(0,1)) = 0
        _LightMaxLimit (""Light Max Limit"", Range(0,10)) = 1
        _MonochromeLighting (""Monochrome Lighting"", Range(0,1)) = 0
        _UseReflection (""UseReflection"", Float) = 0
        _ApplySpecular (""ApplySpecular"", Float) = 1
        _ReflectionColor (""Reflection Color"", Color) = (1,1,1,1)
        _Reflectance (""Reflectance"", Range(0,1)) = 0
        _ApplyReflection (""ApplyReflection"", Float) = 0
        _UseRefraction (""UseRefraction"", Float) = 0
        _UseGlitter (""UseGlitter"", Float) = 0
        _UseEmission2nd (""UseEmission2nd"", Float) = 0
        _UseParallax (""UseParallax"", Float) = 0
        _BackfaceForceShadow (""Backface Force Shadow"", Range(0,1)) = 0
        _ShadowReceive (""Shadow Receive"", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color;
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.uv, _MainTex); return o; }
            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * _Color; }
            ENDCG
        }
    }
}";
    }
}
