// lilToon to NonToon Converter
// Target: Unity 2022.3+, URP, NonToon 0.1.5, Shader Core 0.1.9
//
// [NT-VENDOR] 本文件由 lilxyzw/NonToon 项目内置维护（原为独立工具 LilToonToNonToonConverter 1.1.4）。
// 与上游的差异：
//   1) 阴影改走 NonToon 原生 ShadowColor 模块（保真 lilToon 的 border/blur/strength/2nd/3rd/contrast），
//      不再把阴影色烧进 256px Ramp。
//   2) 版本期望值随 NonToon 内置版本更新。
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LilToonToNonToonConverter
{
    internal static class ConverterConstants
    {
        // Release version: change only this value, then run Release.ps1 to regenerate
        // the .unitypackage and BOOTH description with the same version number.
        internal const string Version = "1.2.7";
        internal const string PackageName = "LilToonToNonToonConverter";
        internal const string MenuPath = "Tools/lilToon → NonToon 转换器 (Converter)";
        internal const string OutputFolderName = "NonToonConverted";
        internal const string ConvertedSuffix = "_NonToon";
        internal const string NonToonShaderName = "NonToon";
        internal const string NonToonFurShaderName = "NonToonFur";
        // [NT-VENDOR] 期望版本随本分支更新（我们要求 NonToon ≥ 0.1.11、Shader Core ≥ 0.1.9）。
        internal const string ExpectedNonToonVersion = "0.3.11";
        internal const string ExpectedShaderCoreVersion = "0.1.9";
    }

    internal enum ConversionSeverity { Success, Warning, Unsupported, Error }

    [Serializable]
    internal sealed class ConversionEntry
    {
        internal string SourcePath;
        internal string OutputPath;
        internal ConversionSeverity Severity;
        internal readonly List<string> Messages = new List<string>();
        internal readonly List<string> Diagnostics = new List<string>();
        // [NT-L10N] 消息本地化后不能再靠英文前缀判断严重度，改为显式标记。
        internal bool HasWarning;

        // 记一条警告：内容自己已经是翻译后的文本
        internal void Warn(string message)
        {
            Messages.Add(message);
            HasWarning = true;
        }
    }

    internal sealed class ConversionReport
    {
        internal readonly List<ConversionEntry> Entries = new List<ConversionEntry>();
        internal int SuccessCount { get { return Entries.Count(e => e.Severity == ConversionSeverity.Success); } }
        internal int WarningCount { get { return Entries.Count(e => e.Severity == ConversionSeverity.Warning); } }
        internal int UnsupportedCount { get { return Entries.Count(e => e.Severity == ConversionSeverity.Unsupported); } }
        internal int ErrorCount { get { return Entries.Count(e => e.Severity == ConversionSeverity.Error); } }

        internal string ToText()
        {
            var lines = new List<string>
            {
                NTL10n.L("lilToon to NonToon Converter report"),
                NTL10n.L("Converter version: ") + ConverterConstants.Version,
                NTL10n.L("Generated: ") + DateTime.Now.ToString("O"),
                NTL10n.L("Target NonToon: ") + ConverterConstants.ExpectedNonToonVersion,
                NTL10n.L("Target Shader Core: ") + ConverterConstants.ExpectedShaderCoreVersion,
                NTL10n.L("Installed packages: NonToon ") + NonToonCompatibility.NonToonVersion + NTL10n.L(", Shader Core ") + NonToonCompatibility.ShaderCoreVersion,
                NTL10n.F("Success: {0}, Warnings: {1}, Unsupported: {2}, Errors: {3}", SuccessCount, WarningCount, UnsupportedCount, ErrorCount),
                string.Empty
            };
            foreach (var entry in Entries)
            {
                lines.Add(string.Format("[{0}] {1}", NTL10n.L(entry.Severity.ToString()), entry.SourcePath));
                if (!string.IsNullOrEmpty(entry.OutputPath)) lines.Add(NTL10n.L("  Output: ") + entry.OutputPath);
                foreach (var message in entry.Messages) lines.Add("  - " + message);
                // 诊断块里的纯标签行也翻译；带数值的行命中不了 key 就保持英文原文
                foreach (var diagnostic in entry.Diagnostics) lines.Add("  > " + NTL10n.L(diagnostic));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    internal sealed class ConversionOptions
    {
        internal string OutputFolder;
        internal bool OverwriteExisting;
        internal bool BakeTextures = true;
        internal bool CleanPreviousGeneratedOutputs = true;
        internal bool ReplacePrefabReferences = true;
    }

    internal static class NonToonCompatibility
    {
        // Module presence is detected by a material property, which keeps this independent of the package layout.
        internal static readonly Dictionary<string, string> RequiredModules = new Dictionary<string, string>
        {
            { "Shade", "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex" },
            { "Specular", "_jp_lilxyzw_nontoon_specular_SpecularColor" },
            { "MatCaps", "_jp_lilxyzw_nontoon_matcaps_MatCapMultiply" },
            { "RimLight", "_jp_lilxyzw_nontoon_rimlight_RimLightColor" },
            { "RimShade", "_jp_lilxyzw_nontoon_rimshade_RimShadeGradientIndex" },
            { "Details", "_jp_lilxyzw_nontoon_details_DetailMask" },
            { "DistanceFade", "_jp_lilxyzw_nontoon_distancefade_DistanceFade" },
            { "Lighten", "_jp_lilxyzw_nontoon_lighten_LightBoost" },
            { "HairSpecular", "_jp_lilxyzw_nontoon_hairspecular_HairSpecularGradientIndex" },
            // [NT-VENDOR] 我们分支新增的两个模块。这两个模块设了 keepPropertyNames，
            // 属性名不带 _jp_lilxyzw_nontoon_* 前缀。
            { "ShadowColor", "_ShadowColorEnable" },
            { "Emission", "_UseEmission" },
            { "SelfLight", "_UseSelfLight" }
        };

        internal static Shader MainShader { get { return Shader.Find(ConverterConstants.NonToonShaderName); } }
        internal static Shader FurShader { get { return Shader.Find(ConverterConstants.NonToonFurShaderName); } }
        internal static bool IsInstalled { get { return MainShader != null && FurShader != null; } }
        internal static string NonToonVersion { get { return InstalledVersion("com.catandling.nontoon"); } }
        internal static string ShaderCoreVersion { get { return InstalledVersion("jp.lilxyzw.shadercore"); } }
        internal static bool UsesSupportedVersions { get { return IsAtLeast(NonToonVersion, ConverterConstants.ExpectedNonToonVersion) && IsAtLeast(ShaderCoreVersion, ConverterConstants.ExpectedShaderCoreVersion); } }

        internal static string VersionStatus
        {
            get
            {
                return NTL10n.F("NonToon {0} (target {1}), Shader Core {2} (target {3})", NonToonVersion, ConverterConstants.ExpectedNonToonVersion, ShaderCoreVersion, ConverterConstants.ExpectedShaderCoreVersion);
            }
        }

        private static string InstalledVersion(string packageName)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName + "/package.json");
            return package == null || string.IsNullOrEmpty(package.version) ? NTL10n.L("not found") : package.version;
        }

        private static bool IsAtLeast(string installed, string expected)
        {
            Version installedVersion;
            Version expectedVersion;
            return Version.TryParse(installed, out installedVersion) && Version.TryParse(expected, out expectedVersion) && installedVersion.CompareTo(expectedVersion) >= 0;
        }

        internal static List<string> MissingModules()
        {
            var missing = new List<string>();
            if (MainShader == null) return RequiredModules.Keys.ToList();
            var probe = new Material(MainShader);
            foreach (var pair in RequiredModules)
                if (!probe.HasProperty(pair.Value)) missing.Add(pair.Key);
            UnityEngine.Object.DestroyImmediate(probe);
            return missing;
        }
    }

    internal static class LilToonMaterialConverter
    {
        // [NT-FIX 35 / 2026-09-18] 措辞修正：这条通用警告**只在** `_UseGlitter`/`_UseEmission2nd`/`_UseParallax`
        //   任一为真时触发，而真实素材实测（辉夜 + 虎尾，49 个 lilToon 材质）这三个开关**全为 0/47**
        //   ⇒ 本工程里它一次都不会触发。反过来，**频率最高的几项缺失**
        //   （环境反射 24/49、描边贴图 22/49、受光描边 15/49、Gem/Refraction 2/49）
        //   以前**一条提示都没有**，现在各自有专门且可定位的警告。
        //   ⇒ 这条文案只负责"确实开了这些开关"的那批材质，不再笼统地宣称覆盖一切。
        private static readonly string[] UnsupportedFeatures =
        {
            "Glitter, parallax/POM, decals, dissolve, audio link and animated UVs are not reproduced.",
            "NonToon does not provide a one-to-one equivalent for every lilToon shadow, emission and fur parameter. Review converted materials visually."
        };

        internal static bool IsLilToon(Material material)
        {
            return material != null && material.shader != null &&
                material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static ConversionEntry Convert(Material source, ConversionOptions options)
        {
            var entry = new ConversionEntry { SourcePath = AssetDatabase.GetAssetPath(source), Severity = ConversionSeverity.Success };
            try
            {
                if (!IsLilToon(source))
                {
                    entry.Severity = ConversionSeverity.Unsupported;
                    entry.Messages.Add(NTL10n.L("Skipped: material shader is not lilToon."));
                    return entry;
                }
                if (!NonToonCompatibility.IsInstalled)
                {
                    entry.Severity = ConversionSeverity.Error;
                    entry.Messages.Add(NTL10n.F("NonToon or NonToonFur was not found. Install NonToon {0} and Shader Core {1} through VPM first.", ConverterConstants.ExpectedNonToonVersion, ConverterConstants.ExpectedShaderCoreVersion));
                    return entry;
                }
                if (!NonToonCompatibility.UsesSupportedVersions)
                    entry.Warn(NTL10n.F("WARNING: Installed package versions are older than the tested target. {0}. Update through VPM, then convert again.", NonToonCompatibility.VersionStatus));

                var isFur = IsFur(source);
                var shader = isFur ? NonToonCompatibility.FurShader : NonToonCompatibility.MainShader;
                var outputFolder = string.IsNullOrEmpty(options.OutputFolder) ? DefaultOutputFolder(source) : options.OutputFolder;
                var targetName = OutputMaterialName(source, outputFolder);
                var target = new Material(shader) { name = targetName };
                EnsureFolder(outputFolder);
                if (targetName != source.name + ConverterConstants.ConvertedSuffix)
                    entry.Messages.Add(NTL10n.L("Used a source-GUID suffix in the output material name to prevent same-name materials from overwriting each other in this shared output folder."));
                if (options.CleanPreviousGeneratedOutputs) CleanLegacyGeneratedOutputs(source, outputFolder);
                var outputPath = AssetDatabase.GenerateUniqueAssetPath(outputFolder + "/" + target.name + ".mat");
                if (options.OverwriteExisting)
                {
                    var requested = outputFolder + "/" + target.name + ".mat";
                    if (AssetDatabase.LoadAssetAtPath<Material>(requested) != null)
                    {
                        AssetDatabase.DeleteAsset(requested);
                        outputPath = requested;
                    }
                }

                CopyCore(source, target, options, outputFolder, entry);
                CopyModules(source, target, options, outputFolder, entry);
                if (isFur) CopyFur(source, target, entry);
                // Creating/importing baked textures and .scgradients can refresh a material's
                // integer keyword-like values. Apply module switches after those imports.
                ReapplyModuleEnables(source, target, entry);
                CopyRenderState(source, target, isFur, entry);
                ReportUnsupportedFeatures(source, entry);

                Undo.RegisterCreatedObjectUndo(target, "Create NonToon material");
                AssetDatabase.CreateAsset(target, outputPath);
                entry.OutputPath = outputPath;
                // CreateAsset may initialize integer-backed Shader Core module properties.
                // Apply the enable keywords once more after the material is an asset, then
                // capture diagnostics from the actual generated material state.
                ReapplyModuleEnables(source, target, entry);
                EditorUtility.SetDirty(target);
                ConversionDiagnostics.Capture(source, target, entry);
                // [NT-L10N] 以前这里是嗅探英文前缀 "WARNING"/"MISSING MODULE"。
                // 消息本地化之后前缀变中文，所以改成在产生消息时直接置位。
                if (entry.HasWarning) entry.Severity = ConversionSeverity.Warning;
            }
            catch (Exception exception)
            {
                entry.Severity = ConversionSeverity.Error;
                entry.Messages.Add(exception.GetType().Name + ": " + exception.Message);
                entry.Diagnostics.Add(exception.StackTrace ?? NTL10n.L("No stack trace available."));
            }
            return entry;
        }

        private static void CopyCore(Material source, Material target, ConversionOptions options, string outputFolder, ConversionEntry entry)
        {
            var baseTexture = GetTexture(source, "_MainTex");
            var baseColor = GetColor(source, "_Color", Color.white);
            var hasMain2nd = HasMainLayer(source, "_Main2nd");
            var hasMain3rd = HasMainLayer(source, "_Main3rd");
            var hasMainLayers = hasMain2nd || hasMain3rd;
            var hasAlphaMask = GetTexture(source, "_AlphaMask") != null && Mathf.RoundToInt(GetFloat(source, "_AlphaMaskMode", 0f)) != 0;
            if (baseTexture != null && (!options.BakeTextures || (IsWhite(baseColor) && !hasMainLayers && !hasAlphaMask)))
            {
                target.SetTexture("_BaseTexture", baseTexture);
                if (options.BakeTextures) entry.Messages.Add(NTL10n.L("Referenced lilToon main texture directly because _Color is white; no base texture was baked."));
            }
            else if (baseTexture != null && options.BakeTextures)
            {
                var baked = hasMainLayers || hasAlphaMask
                    ? TextureBaker.BakeMainLayers(source, baseTexture, baseColor, outputFolder, source.name + "_Base", entry)
                    : TextureBaker.BakeColorTexture(baseTexture, baseColor, outputFolder, source.name + "_Base", entry);
                if (baked != null)
                {
                    target.SetTexture("_BaseTexture", baked);
                    if (!IsWhite(baseColor)) entry.Messages.Add(NTL10n.L("Baked lilToon Main 1st color multiplier into the generated NonToon Base Texture."));
                    if (hasMain2nd) entry.Messages.Add(NTL10n.L("Detected active lilToon Main 2nd layer and baked it into the generated NonToon Base Texture."));
                    if (hasMain3rd) entry.Messages.Add(NTL10n.L("Detected active lilToon Main 3rd layer and baked it into the generated NonToon Base Texture."));
                }
                else
                {
                    target.SetTexture("_BaseTexture", baseTexture);
                    entry.Warn(NTL10n.L("WARNING: Main color could not be baked; assigned _MainTex without the _Color multiplier."));
                }
            }
            else if (baseTexture == null && baseColor != Color.white)
            {
                // NonToon has no independent base-color multiplier. A small solid texture
                // preserves lilToon's color (and alpha) for every mesh UV without requiring
                // a source main texture.
                var solid = TextureBaker.BakeSolidColor(baseColor, outputFolder, source.name + "_BaseColor");
                if (solid != null)
                {
                    target.SetTexture("_BaseTexture", solid);
                    entry.Messages.Add(NTL10n.L("Baked lilToon _Color into a solid NonToon base texture because _MainTex is empty."));
                }
            }
            else
            {
                target.SetTexture("_BaseTexture", baseTexture);
                if (baseColor != Color.white) entry.Warn(NTL10n.L("WARNING: Main color multiplier was not baked because texture baking is disabled."));
            }
            if (hasAlphaMask && baseTexture == null)
                entry.Warn(NTL10n.L("WARNING: lilToon Alpha Mask could not be baked because Main Texture is empty."));
            CopyTextureScaleOffset(source, "_MainTex", target, "_BaseTexture");

            // NonToon's Details A is the closest equivalent to lilToon's normal-map layer.
            var detailNormalMap = ModuleProperty(target, "details", "Detail0NormalMap");
            var sourceNormalMap = GetTexture(source, "_BumpMap");
            if (detailNormalMap != null && sourceNormalMap != null)
            {
                CopyTexture(source, "_BumpMap", target, detailNormalMap);
                CopyFloat(source, "_BumpScale", target, ModuleProperty(target, "details", "Detail0NormalScale"));
                CopyTextureScaleOffset(source, "_BumpMap", target, ModuleProperty(target, "details", "Detail0Texture"));
                SetInteger(target, ModuleProperty(target, "details", "Detail0UV"), Mathf.RoundToInt(GetFloat(source, "_BumpMap_UVMode", 0f)));
                SetModuleEnabled(target, "details", true);
                entry.Messages.Add(NTL10n.L("Assigned lilToon normal map to NonToon Details A (Detail0 Normal Map)."));
            }
            else if (detailNormalMap == null && sourceNormalMap != null)
            {
                CopyTexture(source, "_BumpMap", target, "_NormalMap");
                CopyFloat(source, "_BumpScale", target, "_NormalScale");
                entry.Warn(NTL10n.L("MISSING MODULE Details: assigned normal map to NonToon base Normal Map instead of Details A."));
            }
            CopySecondNormal(source, target, entry);
            CopyDetailMask(source, target, baseTexture, outputFolder, entry);
            if (source.HasProperty("_Cutoff") && target.HasProperty("_Cutoff"))
            {
                SetNumber(target, "_Cutoff", Mathf.Clamp(source.GetFloat("_Cutoff"), -.001f, 1.001f));
                entry.Messages.Add(NTL10n.L("Copied lilToon Cutoff to NonToon Cutoff."));
            }
            if (target.HasProperty("_Roughness"))
            {
                var reflectionEnabled = IsEnabled(source, "_UseReflection");
                var smoothness = Mathf.Clamp01(GetFloat(source, "_Smoothness", .5f));
                var roughness = reflectionEnabled ? (1f - smoothness) * (1f - smoothness) : .5f;
                SetNumber(target, "_Roughness", Mathf.Clamp(roughness, .05f, 1f));
                entry.Messages.Add(reflectionEnabled
                    ? "Mapped lilToon Smoothness to NonToon physical Roughness using (1 - Smoothness)^2."
                    : "Set NonToon Roughness to 0.5 because lilToon reflection is disabled.");
                entry.Messages.Add(NTL10n.L("NOTICE: Visually readjust NonToon Roughness and Specular after conversion; lilToon and NonToon use different reflection models."));
                if (GetTexture(source, "_SmoothnessTex") != null)
                    entry.Warn(NTL10n.L("WARNING: lilToon Smoothness Texture is per-pixel and cannot be represented by NonToon's scalar Roughness."));
                if (GetTexture(source, "_MetallicGlossMap") != null)
                    entry.Warn(NTL10n.L("WARNING: lilToon Metallic Gloss Map was not used for Roughness; Metallic is not a NonToon Roughness equivalent."));
            }

            string matCapAddSource;
            string matCapMultiplySource;
            GetMatCapAssignments(source, out matCapAddSource, out matCapMultiplySource);
            // [NT-VENDOR] 我们分支自带 Emission 模块，且属性名与 lilToon **逐字符相同**、贴图是 1:1 拷贝。
            // 那种情况下不需要再把 Emission Blend Mask 烧进 SharedMask 去抢一条通道
            // （共有遮罩只有 4 条通道，这是 0.1.8 起支持反向通道想缓解的痛点）。
            var hasNativeEmission = target.HasProperty("_UseEmission") && target.HasProperty("_EmissionBlendMask");
            var emissionBlendMask = !hasNativeEmission && HasAssetTexture(source, "_EmissionBlendMask") && IsEnabled(source, "_UseEmission") ? GetTexture(source, "_EmissionBlendMask") : null;
            var emissionMaskChannel = emissionBlendMask == null ? -1 : GetEmissionMaskChannel(source);
            var masks = new[]
            {
                emissionMaskChannel == 0 ? emissionBlendMask : matCapAddSource == null ? null : GetTexture(source, matCapAddSource + "BlendMask"),
                emissionMaskChannel == 1 ? emissionBlendMask : matCapMultiplySource == null ? null : GetTexture(source, matCapMultiplySource + "BlendMask"),
                emissionMaskChannel == 2 ? emissionBlendMask : GetTexture(source, "_RimShadeMask"),
                emissionMaskChannel == 3 ? emissionBlendMask : null as Texture
            };
            if (hasAlphaMask && options.BakeTextures)
                entry.Messages.Add(NTL10n.L("Baked lilToon Alpha Mask into the generated NonToon Base Texture alpha channel."));
            else if (hasAlphaMask)
                entry.Warn(NTL10n.L("WARNING: lilToon Alpha Mask requires Base Texture baking and was not copied because baking is disabled."));
            // [NT-FEAT 11] lilToon 的逐像素阴影遮罩现在能在 NonToon 的 ShadowColor 模块里复现
            //（同名属性、同通道语义），不再只是警告跳过。
            CopyShadowMasks(source, target, entry);
            if (options.BakeTextures && masks.Any(t => t != null))
            {
                var shared = TextureBaker.CombineMasks(masks, outputFolder, source.name + "_SharedMask", entry);
                if (shared != null)
                {
                    target.SetTexture("_SharedMask", shared);
                    entry.Messages.Add(NTL10n.L("Shared mask channels: R=MatCap Add Blend Mask, G=MatCap Multiply Blend Mask, B=RimShade Mask, A=shared white mask for Specular/RimLight/Lighten/HairSpecular."));
                    if (emissionMaskChannel >= 0)
                    {
                        entry.Messages.Add(NTL10n.F("Mapped lilToon Emission Blend Mask to Shared Mask {0} and assigned it to NonToon Lighten.", SharedMaskChannelName(emissionMaskChannel)));
                        if (emissionMaskChannel == 3)
                            entry.Warn(NTL10n.L("WARNING: Emission Blend Mask uses Shared Mask A because R/G/B are occupied; Specular, RimLight and HairSpecular may also be limited by this mask."));
                    }
                }
            }

            // [NT-VENDOR] 我们分支的光照调整属性与 lilToon **同名同义** → 1:1 拷贝。
            // （原版转换器只把它们写进诊断列表，Mapping.md 里没有对应行，因为当年的 NonToon 没这些属性。）
            CopyLightingAdjustments(source, target, entry);
        }

        // [NT-VENDOR] lilToon 的光照调整 / 受光方向 → 本分支的同名属性。
        private static void CopyLightingAdjustments(Material source, Material target, ConversionEntry entry)
        {
            var copied = new List<string>();
            foreach (var p in new[] { "_AsUnlit", "_LightMinLimit", "_LightMaxLimit", "_MonochromeLighting" })
            {
                if (!source.HasProperty(p) || !target.HasProperty(p)) continue;
                SetNumber(target, p, source.GetFloat(p));
                copied.Add(p);
            }
            if (copied.Count > 0)
                entry.Messages.Add(NTL10n.F("Copied lilToon lighting adjustments 1:1 (identical property names): {0}.", string.Join(", ", copied)));
            else
                entry.Messages.Add(NTL10n.L("NOTICE: the source had none of _AsUnlit / _LightMinLimit / _LightMaxLimit / _MonochromeLighting, so NonToon's defaults were kept."));

            // 本分支的 Shade 方向默认带 1.5 的「视线权重」（NonToon 原本的风格：受光面跟着相机走）。
            // lilToon 的受光是**跟随光照方向**的，所以转换时置 0，转换结果才与 lilToon 一致。
            // 想要 NonToon 原味就把材质里的 Shade Direction Bias 调回 1.5。
            if (target.HasProperty("_ShadeDirectionBias"))
            {
                SetNumber(target, "_ShadeDirectionBias", 0f);
                entry.Messages.Add(NTL10n.L("Set Shade Direction Bias to 0 so the toon shade follows the real light direction like lilToon (raise it back toward 1.5 for NonToon's original view-biased look)."));
            }
        }

        private static void CopyModules(Material source, Material target, ConversionOptions options, string outputFolder, ConversionEntry entry)
        {
            CopyOutline(source, target, entry);
            CopySpecular(source, target, entry);
            CopyMatCap(source, target, entry);
            CopyRim(source, target, entry);
            CopyBacklight(source, target, entry);
            CopyDistanceFade(source, target, entry);
            CopyStencil(source, target, entry);
            CopyShade(source, target, entry);
            CopyColorRamps(source, target, outputFolder, entry);
            // [NT-VENDOR] 优先走我们分支的原生 Emission 模块（1:1）；
            // 只有没装该模块的旧 NonToon 才回退到 LightBoost 近似。
            if (target.HasProperty("_UseEmission") && target.HasProperty("_EmissionColor"))
                CopyEmissionNative(source, target, entry);
            else CopyEmissionAsLightBoost(source, target, entry);

            if (IsEnabled(source, "_UseAnisotropy") && ModuleProperty(target, "hairspecular", "HairSpecularGradientIndex") == null)
                entry.Warn(NTL10n.L("MISSING MODULE HairSpecular: anisotropy was not converted."));
        }

        // NonToonFur has a deliberately small, compatible set of fur controls.  Keep this
        // mapping isolated from the normal material conversion: the source's advanced fur
        // controls (vector texture, gravity, randomization, etc.) have no target equivalent.
        private static void CopyFur(Material source, Material target, ConversionEntry entry)
        {
            if (!target.HasProperty("_FurNoiseMask"))
            {
                entry.Warn(NTL10n.L("WARNING: The installed NonToonFur shader has no Fur Noise properties; fur-specific values were not converted."));
                return;
            }

            var noise = GetTexture(source, "_FurNoiseMask");
            if (noise != null)
            {
                target.SetTexture("_FurNoiseMask", noise);
                var scale = source.GetTextureScale("_FurNoiseMask");
                // lilToon permits independent U/V scale while NonToonFur uses one scalar.
                // The geometric mean preserves the average repetition density without
                // arbitrarily preferring either axis.
                var tiling = Mathf.Sqrt(Mathf.Abs(scale.x * scale.y));
                if (target.HasProperty("_FurNoiseTiling"))
                {
                    SetNumber(target, "_FurNoiseTiling", Mathf.Max(.0001f, tiling));
                    entry.Messages.Add(NTL10n.F("Mapped lilToon Fur Noise Mask and texture tiling to NonToonFur Fur Noise / Noise Tiling ({0}).", tiling.ToString("F3")));
                }
                else
                    entry.Warn(NTL10n.L("WARNING: The installed NonToonFur shader has no Noise Tiling property; only the Fur Noise Mask was copied."));
                if (Mathf.Abs(scale.x - scale.y) > .0001f || source.GetTextureOffset("_FurNoiseMask").sqrMagnitude > .0000001f)
                    entry.Warn(NTL10n.L("WARNING: NonToonFur Fur Noise has one tiling value only; lilToon non-uniform Noise scale and offset were approximated."));
            }

            if (source.HasProperty("_FurLayerNum"))
            {
                var subdivision = Mathf.Clamp(Mathf.RoundToInt(source.GetFloat("_FurLayerNum")), 1, 3);
                SetInteger(target, "_FurSubdivision", subdivision);
                entry.Messages.Add(NTL10n.F("Mapped lilToon Fur Layer Num to NonToonFur Subdivision ({0}).", subdivision));
            }

            if (source.HasProperty("_FurVector") && target.HasProperty("_FurVector"))
            {
                var sourceVector = source.GetVector("_FurVector");
                var direction = new Vector3(sourceVector.x, sourceVector.y, sourceVector.z);
                // lilToon normalizes xyz and uses w as the displacement length. NonToonFur
                // stores the final tangent-space displacement directly in xyz.
                var displacement = direction.sqrMagnitude > .0000001f
                    ? direction.normalized * sourceVector.w
                    : Vector3.zero;
                target.SetVector("_FurVector", new Vector4(displacement.x, displacement.y, displacement.z, 0f));
                entry.Messages.Add(NTL10n.L("Mapped lilToon Fur Vector direction and length to NonToonFur Fur Vector."));
            }

            if (HasAssetTexture(source, "_FurVectorTex") || IsEnabled(source, "_VertexColor2FurVector"))
                entry.Warn(NTL10n.L("WARNING: NonToonFur has no Fur Vector Texture or vertex-color direction input; detailed lilToon fur direction was approximated with Fur Vector."));
            if (Mathf.Abs(GetFloat(source, "_FurGravity", 0f)) > .0001f || Mathf.Abs(GetFloat(source, "_FurRandomize", 0f)) > .0001f)
                entry.Warn(NTL10n.L("WARNING: NonToonFur has no lilToon-equivalent Fur Gravity or Randomize control; these fur direction modifiers were not converted."));
        }

        private static void CopySecondNormal(Material source, Material target, ConversionEntry entry)
        {
            var secondNormal = GetTexture(source, "_Bump2ndMap");
            if (!IsEnabled(source, "_UseBump2ndMap") || secondNormal == null) return;
            var detail1Normal = ModuleProperty(target, "details", "Detail1NormalMap");
            if (detail1Normal == null)
            {
                entry.Warn(NTL10n.L("MISSING MODULE Details: second normal map was not converted."));
                return;
            }
            target.SetTexture(detail1Normal, secondNormal);
            SetNumber(target, ModuleProperty(target, "details", "Detail1NormalScale"), GetFloat(source, "_Bump2ndScale", 1f));
            CopyTextureScaleOffset(source, "_Bump2ndMap", target, ModuleProperty(target, "details", "Detail1Texture"));
            SetNumber(target, ModuleProperty(target, "details", "Detail1UV"), GetFloat(source, "_Bump2ndMap_UVMode", 0f));
            SetModuleEnabled(target, "details", true);
            entry.Messages.Add(NTL10n.L("Assigned lilToon second normal map to NonToon Details B (Detail1 Normal Map)."));
        }

        private static void CopyDetailMask(Material source, Material target, Texture baseTexture, string outputFolder, ConversionEntry entry)
        {
            var detailMask = ModuleProperty(target, "details", "DetailMask");
            var hasFirst = GetTexture(source, "_BumpMap") != null;
            var hasSecond = IsEnabled(source, "_UseBump2ndMap") && GetTexture(source, "_Bump2ndMap") != null;
            if (detailMask == null || (!hasFirst && !hasSecond)) return;
            var mask = TextureBaker.BakeDetailMask(source, baseTexture, hasFirst, hasSecond, outputFolder, source.name + "_DetailMask", entry);
            if (mask == null) return;
            target.SetTexture(detailMask, mask);
            SetModuleEnabled(target, "details", true);
            entry.Messages.Add(NTL10n.L("Generated NonToon Details RGBA mask: R=1st Normal, G=2nd Normal/Scale Mask, B/A=unused."));
        }

        private static void CopyOutline(Material source, Material target, ConversionEntry entry)
        {
            // NonToon's default outline width is non-zero; disable it explicitly for lilToon materials without outlines.
            var usesOutlineShader = source.shader != null && source.shader.name.IndexOf("outline", StringComparison.OrdinalIgnoreCase) >= 0;
            var outlineEnabled = IsEnabled(source, "_UseOutline") || usesOutlineShader;
            if (!outlineEnabled)
            {
                SetNumber(target, "_OutlineWidth", 0f);
                target.SetColor("_OutlineColor", Color.clear);
                SetNumber(target, "_OutlineZOffset", 0f);
                SetInteger(target, "_OutlineFromVertexColor", 0);
                return;
            }
            if (!target.HasProperty("_OutlineColor")) { entry.Warn(NTL10n.L("WARNING: Outline is unavailable in the installed NonToon shader.")); return; }
            target.SetColor("_OutlineColor", GetColor(source, "_OutlineColor", Color.black));
            SetNumber(target, "_OutlineWidth", GetFloat(source, "_OutlineWidth", 0.1f));
            // _OutlineFixWidth controls lilToon's width correction, not its position offset.
            // NonToon's Z Offset has no direct lilToon equivalent, so leave it at zero.
            SetNumber(target, "_OutlineZOffset", 0f);
            var vertexColorMode = Mathf.RoundToInt(GetFloat(source, "_OutlineVertexR2Width", GetFloat(source, "_OutlineVertexColorUsages", 0f)));
            SetInteger(target, "_OutlineFromVertexColor", vertexColorMode > 0 ? 1 : 0);
            entry.Messages.Add(NTL10n.F("Mapped lilToon Outline color and width to NonToon; vertex-color outline={0}.", vertexColorMode > 0));
            if (vertexColorMode > 0)
                entry.Messages.Add(NTL10n.L("NOTICE: NonToon uses vertex RGB as the outline direction and vertex A for Z Offset; lilToon vertex-color outline modes are approximated."));
            var outlineTexture = GetTexture(source, "_OutlineTex");
            var outlineWidthMask = GetTexture(source, "_OutlineWidthMask");
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(outlineTexture)) || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(outlineWidthMask)) || Mathf.Abs(GetFloat(source, "_OutlineZBias", 0f)) > .0001f)
                entry.Warn(NTL10n.L("WARNING: NonToon standard Outline has no equivalent for lilToon Outline Texture, Width Mask, or Z Bias; those settings were not copied."));
        }

        private static void CopySpecular(Material source, Material target, ConversionEntry entry)
        {
            // ⛔ [NT-FIX 35b / 2026-09-18] 环境反射的报警必须放在**所有** early-return 之前。
            //    第一版我把它放在"镜面未启用"那个 return 之前，却漏掉了**更早**的这个
            //    `specularColor == null` ⇒ 目标没有 Specular 模块时，警告依然不会执行
            //    （审计第 7 轮 a 抓到；我先前的注释还声称"必须在 early-return 之前"，实为空话）。
            var reflectionEnabled = IsEnabled(source, "_UseReflection");
            WarnEnvironmentReflection(source, reflectionEnabled, entry);

            var specularColor = ModuleProperty(target, "specular", "SpecularColor");
            if (specularColor == null) { entry.Warn(NTL10n.L("MISSING MODULE Specular: reflection/specular was not converted.")); return; }
            var applySpecular = !source.HasProperty("_ApplySpecular") || IsEnabled(source, "_ApplySpecular");
            if (!ShouldCopySpecular(reflectionEnabled, applySpecular))
            {
                // lilToon evaluates specular only inside its _UseReflection block. Some
                // materials retain _ApplySpecular=1 while the parent toggle is disabled.
                // Explicitly clear NonToon's always-present Specular module so the retained
                // child value cannot create bright highlights after conversion.
                target.SetColor(specularColor, Color.black);
                entry.Messages.Add(NTL10n.L("Disabled NonToon Specular because lilToon Reflection and Apply Specular were not both enabled."));
                return;
            }
            target.SetColor(specularColor, GetColor(source, "_ReflectionColor", Color.white));
            // lilToon's reflection color is independent of the base albedo by default.
            SetNumber(target, ModuleProperty(target, "specular", "SpecularMultiplyAlbedo"), 0f);
            SetInteger(target, ModuleProperty(target, "specular", "SpecularMaskChannel"), 3);
            // [NT-FIX 35 / 2026-09-18] 措辞修正：只映了**直接光镜面高光**，不要说成"反射已转换"。
            entry.Messages.Add(NTL10n.L("Mapped lilToon reflection colour to NonToon Specular (direct-light specular only); Smoothness is mapped to Roughness."));
        }

        // [NT-FIX 35 / 2026-09-18] 环境反射：**NonToon 完全没有对应物，而以前这里一声不吭。**
        //
        // 实测依据：对 NonToon 全部着色器 `grep "Reflect|Cubemap|_EnvRim|Reflection"` = **零命中**
        // ⇒ 它没有任何 cubemap / probe 反射代码。而 lilToon 的 `_UseReflection` 覆盖两件不同的事：
        //   ① 直接光镜面高光（映到 Specular）
        //   ② **环境反射**（`_Reflectance` 强度 + `_ApplyReflection` 是否把 cubemap 结果混进最终色）
        // 真实素材实测（辉夜 + 虎尾，49 个 lilToon 材质）：
        //   `_UseReflection` 开着 = **24 个（51%）**、`_Reflectance` 非默认 = **17 个**
        //   ⇒ 这是**频率第二高**的能力缺失，而且以前**没有任何提示**（与 [NT-FIX 30] 同类故障）。
        // 判据故意保守：只有"确实启用了环境反射"（强度 > 0 或 `_ApplyReflection` 开）才报警，
        // 免得给 `_Reflectance=0` 的材质白报一条。
        private static void WarnEnvironmentReflection(Material source, bool reflectionEnabled, ConversionEntry entry)
        {
            if (!reflectionEnabled) return;
            var envReflectance = GetFloat(source, "_Reflectance", 0f);
            var envApplied = source.HasProperty("_ApplyReflection") && IsEnabled(source, "_ApplyReflection");
            if (envReflectance > 0.001f || envApplied)
                // ⚠️ [NT-FIX 35b] 措辞按审计第 7 轮 a 收紧：**不得**再声明"只有直接光镜面被映了" ——
                //    `_ApplySpecular=0` 或目标缺 Specular 时根本没有映。这里只陈述**确定的**那一件事
                //    （环境反射没有对应物、源设置未被转换），镜面的成败由后续分支各自报告。
                entry.Warn(NTL10n.F(
                    "WARNING: NonToon has no cubemap/probe environment reflection; the source environment reflection settings were not converted. Affected source inputs: Reflectance={0}, Apply Reflection={1}.",
                    envReflectance.ToString("F3"), envApplied ? "on" : "off"));
        }

        internal static bool ShouldCopySpecular(bool reflectionEnabled, bool applySpecular)
        {
            return reflectionEnabled && applySpecular;
        }

        private static void CopyMatCap(Material source, Material target, ConversionEntry entry)
        {
            if (!IsEnabled(source, "_UseMatCap") && !IsEnabled(source, "_UseMatCap2nd")) return;
            if (ModuleProperty(target, "matcaps", "MatCapMultiply") == null) { entry.Warn(NTL10n.L("MISSING MODULE MatCaps: matcap was not converted.")); return; }
            SetModuleEnabled(target, "matcaps", true);
            entry.Messages.Add(NTL10n.L("NOTICE: Visually readjust converted MatCaps; lilToon and NonToon use different matcap UV, lighting, mask, and blend behavior, so the material appearance can change significantly."));
            string addSource;
            string multiplySource;
            var exactPair = GetMatCapAssignments(source, out addSource, out multiplySource);
            if (addSource != null) CopyMatCapLayer(source, target, addSource, "MatCapAdd", 0, entry);
            if (multiplySource != null) CopyMatCapLayer(source, target, multiplySource, "MatCapMultiply", 1, entry);
            if (exactPair)
                entry.Messages.Add(NTL10n.L("Mapped one lilToon Add MatCap and one Multiply MatCap to their matching NonToon slots."));
            else if (IsEnabled(source, "_UseMatCap") && IsEnabled(source, "_UseMatCap2nd"))
                entry.Warn(NTL10n.L("WARNING: Both lilToon MatCaps are not one Add and one Multiply pair; converted 1st MatCap only, using its closest NonToon blend slot."));
        }

        // NonToon has precisely one Add and one Multiply MatCap slot. Retain both layers
        // only when lilToon provides that exact pair. In every other two-layer combination,
        // 1st MatCap is the explicit priority requested by the converter workflow.
        private static bool GetMatCapAssignments(Material source, out string addSource, out string multiplySource)
        {
            addSource = null;
            multiplySource = null;
            var firstEnabled = IsEnabled(source, "_UseMatCap");
            var secondEnabled = IsEnabled(source, "_UseMatCap2nd");
            var firstMode = Mathf.RoundToInt(GetFloat(source, "_MatCapBlendMode", 1f));
            var secondMode = Mathf.RoundToInt(GetFloat(source, "_MatCap2ndBlendMode", 1f));
            if (firstEnabled && secondEnabled && ((firstMode == 1 && secondMode == 3) || (firstMode == 3 && secondMode == 1)))
            {
                addSource = firstMode == 1 ? "_MatCap" : "_MatCap2nd";
                multiplySource = firstMode == 3 ? "_MatCap" : "_MatCap2nd";
                return true;
            }
            var prioritySource = firstEnabled ? "_MatCap" : secondEnabled ? "_MatCap2nd" : null;
            if (prioritySource == null) return false;
            var priorityMode = prioritySource == "_MatCap" ? firstMode : secondMode;
            if (priorityMode == 3) multiplySource = prioritySource;
            else addSource = prioritySource; // Normal / Add / Screen are approximated as Add.
            return false;
        }

        private static void CopyMatCapLayer(Material source, Material target, string sourcePrefix, string targetPrefix, int maskChannel, ConversionEntry entry)
        {
            var mode = Mathf.RoundToInt(GetFloat(source, sourcePrefix + "BlendMode", 1f));
            var color = GetColor(source, sourcePrefix + "Color", Color.white);
            var opacity = Mathf.Clamp01(color.a * GetFloat(source, sourcePrefix + "Blend", 1f));
            // NonToon has no lilToon-equivalent MatCap opacity control. Preserve the RGB
            // tint without darkening it; baking opacity into RGB caused large appearance
            // changes and made the converted color misleading to edit.
            var nonToonColor = new Color(color.r, color.g, color.b, 1f);
            target.SetTexture(ModuleProperty(target, "matcaps", targetPrefix), GetTexture(source, sourcePrefix + "Tex"));
            target.SetColor(ModuleProperty(target, "matcaps", targetPrefix + "Color"), nonToonColor);
            SetNumber(target, ModuleProperty(target, "matcaps", targetPrefix + "Detail"), GetFloat(source, sourcePrefix + "NormalStrength", 1f));
            SetInteger(target, ModuleProperty(target, "matcaps", targetPrefix + "MaskChannel"), maskChannel);
            entry.Messages.Add(NTL10n.F("{0} mapped to NonToon {1} with Shared Mask {2}; copied RGB tint without converting lilToon opacity into color intensity.", sourcePrefix, targetPrefix, maskChannel == 0 ? "R" : "G"));
            if (opacity < .999f)
                entry.Messages.Add(NTL10n.F("NOTICE: {0} lilToon opacity {1} has no direct NonToon MatCap control and was not converted; visually adjust the MatCap color or mask after conversion.", sourcePrefix, opacity.ToString("F3")));
            var expectedMode = targetPrefix == "MatCapAdd" ? 1 : 3;
            if (mode != expectedMode)
                entry.Warn(NTL10n.F("WARNING: {0} blend mode {1} was approximated as NonToon {2}.", sourcePrefix, mode, targetPrefix));
        }

        private static void CopyRim(Material source, Material target, ConversionEntry entry)
        {
            if (IsEnabled(source, "_UseRim"))
            {
                var rimLightColor = ModuleProperty(target, "rimlight", "RimLightColor");
                if (rimLightColor == null) entry.Warn(NTL10n.L("MISSING MODULE RimLight: rim light was not converted."));
                else
                {
                    target.SetColor(rimLightColor, GetColor(source, "_RimColor", Color.white));
                    SetNumber(target, ModuleProperty(target, "rimlight", "RimLightMultiplyAlbedo"), GetFloat(source, "_RimMainStrength", 0f));
                    var border = GetFloat(source, "_RimBorder", .5f);
                    var blur = GetFloat(source, "_RimBlur", .1f);
                    var fresnelPower = Mathf.Max(.01f, GetFloat(source, "_RimFresnelPower", 1f));
                    // lilToon thresholds pow(1 - NdotV, FresnelPower); NonToon thresholds
                    // the unpowered 1 - NdotV term. Convert both ends of Border ± Blur / 2.
                    var range = ToNonToonRimRange(border, blur, fresnelPower);
                    target.SetVector(ModuleProperty(target, "rimlight", "RimLightRange"), new Vector4(range.x, range.y, 0, 0));
                    SetInteger(target, ModuleProperty(target, "rimlight", "RimLightMaskChannel"), 3);
                    entry.Messages.Add(NTL10n.F("Mapped rim light Border/Blur/Fresnel Power to NonToon RimLight Range ({0}–{1}).", range.x.ToString("F3"), range.y.ToString("F3")));
                }
            }
            if (IsEnabled(source, "_UseRimShade") && ModuleProperty(target, "rimshade", "RimShadeGradientIndex") == null)
                entry.Warn(NTL10n.L("MISSING MODULE RimShade: rim shade was not converted."));
        }

        private static void CopyBacklight(Material source, Material target, ConversionEntry entry)
        {
            if (!IsEnabled(source, "_UseBacklight")) return;
            if (!target.HasProperty("_BacklightRange"))
            {
                entry.Warn(NTL10n.L("WARNING: Installed NonToon shader has no Backlight properties; lilToon Backlight was not converted."));
                return;
            }
            var range = Mathf.Clamp01(GetFloat(source, "_BacklightBorder", .75f));
            var blur = Mathf.Max(.01f, GetFloat(source, "_BacklightBlur", .1f));
            // NonToon's Sharpness acts as toon strength: a smaller lilToon blur is sharper.
            var toonStrength = Mathf.Clamp(1f / blur, .01f, 32f);
            SetNumber(target, "_BacklightRange", range);
            SetNumber(target, "_BacklightSharpness", toonStrength);
            SetInteger(target, "_BacklightMaskChannel", 3);
            entry.Messages.Add(NTL10n.L("Mapped lilToon Backlight Border directly to NonToon Backlight Range and inverse Blur to Backlight Sharpness (toon strength)."));
            if (GetTexture(source, "_BacklightColorTex") != null || GetColor(source, "_BacklightColor", Color.white) != Color.white)
                entry.Warn(NTL10n.L("WARNING: NonToon Backlight controls light shaping only; lilToon Backlight color and color texture were not reproduced."));
        }

        private static void CopyDistanceFade(Material source, Material target, ConversionEntry entry)
        {
            if (!source.HasProperty("_DistanceFade")) return;
            if (ModuleProperty(target, "distancefade", "DistanceFade") == null) { entry.Warn(NTL10n.L("MISSING MODULE DistanceFade: distance fade was not converted.")); return; }
            var fade = source.GetVector("_DistanceFade");
            if (fade.z <= 0f) return;
            // Both shaders calculate a 0–1 range from X/Y, but use its direction
            // oppositely: lilToon fades *toward* its fade color as the range rises,
            // whereas NonToon fades from black as its range rises. Swap endpoints so
            // NonToon's (1 - range) black factor reproduces lilToon's range factor.
            target.SetVector(ModuleProperty(target, "distancefade", "DistanceFade"), new Vector4(fade.y, fade.x, 0, 0));
            SetNumber(target, ModuleProperty(target, "distancefade", "DistanceFadeStrength"), Mathf.Clamp01(fade.z));
            entry.Messages.Add(NTL10n.L("Mapped lilToon Distance Fade start/end in reverse order to preserve its fade direction in NonToon; copied strength."));
            var fadeColor = GetColor(source, "_DistanceFadeColor", Color.black);
            if (fadeColor.r != 0f || fadeColor.g != 0f || fadeColor.b != 0f || GetFloat(source, "_DistanceFadeMode", 0f) != 0f || GetColor(source, "_DistanceFadeRimColor", Color.clear).a > 0f)
                entry.Warn(NTL10n.L("WARNING: NonToon Distance Fade fades to black camera depth only; lilToon fade color, object-depth mode, and rim fade are not equivalent."));
        }

        // [NT-FIX 33 / 2026-09-18] **阴影默认走本 fork 的 ShadowColor 模块**（= lilToon 阴影的逐行忠实移植）。
        // 只有目标没有 ShadowColor 模块时才回退到上游的 Shade 梯度 ramp。
        //
        // ⛔ 这里在 [NT-FIX 28]（2026-09-17）被翻成"优先上游 ramp"，本轮**翻回来**。依据
        //    （`_notes/素材扫描-范围锁定.md` §6.4，全部为直读源码 + 真实素材实测）：
        //      · `ShadowColor/phase_shade.hlsl` 是 lilToon 阴影的**逐行忠实移植**，承载
        //        `_ShadowBorderColor`(本库 44/49 用到) / `_ShadowBorderRange`(22) /
        //        `_ShadowMainStrength`(13) / 逐像素遮罩 / `fwidth` 抗锯齿 / `min(indirect,albedo)`；
        //      · 该文件 `:86-88` 自己写明：ramp 关闭是「**the normal configuration when using
        //        shadow colours instead of a ramp**」⇒ fork 原本的设计配置就是这个；
        //      · 上游 Shade **ramp** 是**一维颜色查找**，结构上：① 承载不了 `fwidth` 抗锯齿
        //        （依赖屏幕导数）；② 烘焙器只建模了 lns.x/.y/.z 三层色带，**没有把
        //        `lnB × _ShadowBorderColor` 渐变项烘进去**（`lnB` 是唯一用 `_ShadowBorderRange` 的项）
        //        ⇒ 那 4 项在 ramp 路径下**全部 inert**；
        //      · `[NT-FIX 28]` 当初的理由（消除泛紫泛红）**已由别的补丁达成，而那些补丁本身是错的**
        //        —— 见 `CopyShadowColourNative` 里 [NT-FIX 33] 的说明。
        private static void CopyShade(Material source, Material target, ConversionEntry entry)
        {
            if (!IsEnabled(source, "_UseShadow")) return;

            // 首选：本 fork 的 ShadowColor 模块（属性名与 lilToon 逐字符相同，1:1 承载）
            if (target.HasProperty("_ShadowColorEnable"))
            {
                CopyShadowColourNative(source, target, entry);
                return;
            }

            // 回退：上游的 Shade 梯度 ramp（会丢失上面列出的 4 项，如实报警）
            var sdfType = ModuleProperty(target, "shade", "SDFType");
            if (sdfType != null)
            {
                // ramp 本身由 `CopyColorRamps` 烘焙并写入 `_ShadeGradientIndex`，
                // 这里只负责把 SDF 与取值范围设成"无 SDF、0..1 全范围"。
                SetInteger(target, sdfType, 0);
                var shadeRange = ModuleProperty(target, "shade", "ShadeGradientRange");
                if (shadeRange != null) target.SetVector(shadeRange, new Vector4(0f, 1f, 0f, 0f));
                entry.Warn(NTL10n.L("WARNING: the installed NonToon shader has no ShadowColor module, so the shadow fell back to the Shade gradient ramp. A 1D ramp cannot carry lilToon Border Range, Contrast (Shadow Main Strength), Border Color, per-pixel shadow masks or screen-derivative antialiasing; those were not converted."));
                return;
            }

            entry.Warn(NTL10n.L("MISSING MODULE Shade: lilToon shadow settings were not converted."));
        }

        // [NT-VENDOR] 把 lilToon 的阴影设置按名搬到 NonToon 的 ShadowColor 模块。
        // 属性名逐字符相同（该模块设了 keepPropertyNames: true），所以是直接拷贝而非近似。
        private static void CopyShadowColourNative(Material source, Material target, ConversionEntry entry)
        {
            // [NT-VENDOR] 必须用字面属性名：ShadowColor 模块设了 keepPropertyNames，
            // 属性就叫 _ShadowColorEnable，ModuleProperty 拼出来的 _jp_lilxyzw_nontoon_shadowcolor_Enable
            // 并不存在（它会回退成 _Enable，也不存在 → 直接 return，阴影颜色模块永远不会被打开）。
            SetNumber(target, "_ShadowColorEnable", 1f);

            // [NT-FIX 26] 阴影**本体颜色**必须去色偏，不能照搬。
            //
            // ⚠️ [NT-FIX 33] 复核后**保留本补丁**（曾一度想撤，已收回）。原因：它与 MA 菜单耦合 ——
            //    `NT_ShadowStrength` 的**默认值是 1**（三点/两点 BlendTree：t=0→0.00 / t=1→1.00），
            //    ⇒ **VRChat 里 `_ShadowStrength` 一律被驱动到 1.0**，而 lilToon 作者设的是 **0.10**。
            //    所以"粉色被放大"的真正来源是**菜单默认值**，不是本补丁。
            //    在菜单仍强制 1.0 的前提下撤掉中性化 ⇒ 会得到**强粉阴影**（正是用户抱怨过的观感）。
            //    ⇒ 先解决菜单默认值（那是另一项决定），再谈是否恢复作者的色彩。
            //
            // 为什么：`ShadowColor/phase_shade.hlsl:104` 是
            //     ntSIndirect = lerp(albedo, shadowColorTex.rgb, tex.a) * **_ShadowColor.rgb**
            // 也就是**整个阴影区都被乘上 `_ShadowColor`**。lilToon 的作者常把阴影色设成
            // **暖粉**（这只 avatar 的 Body 是 `(0.953, 0.733, 0.747)`，R 远高于 G/B），
            // 在上游 NonToon 里阴影走的是梯度 ramp，所以看不出这个色偏；
            // 而我们的 fork 用 `_ShadowColor` 承载阴影 ⇒ **阴影越重，脸越红**
            // （用户 2026-09-17 实机反馈原话："阴影强度越高越红"，已实测确认）。
            //
            // 做法：**保持亮度、去掉色偏**（朝中性灰靠，但保留 20% 原色偏，避免洗成死灰）。
            // 接近中性的阴影色（饱和度 < 0.15）原样保留 —— 不动作者有意调的灰紫。
            foreach (var p in new[]
            {
                "_ShadowColor", "_Shadow2ndColor", "_Shadow3rdColor",
            })
            {
                if (!source.HasProperty(p) || !target.HasProperty(p)) continue;
                var original = source.GetColor(p);
                var fixedUp = NeutralizeShadowTint(original);
                target.SetColor(p, fixedUp);
                if (fixedUp != original)
                    entry.Messages.Add(NTL10n.F(
                        "Desaturated {0} {1} -> {2} so a heavy shadow does not tint the model (luminance kept, hue cast removed).",
                        p, original.ToString("F3"), fixedUp.ToString("F3")));
            }

            // [NT-FIX 22] `_ShadowBorderColor`（影的**边界色**）单独处理，不能照搬。
            //
            // ⚠️ [NT-FIX 33] 复核后**保留本补丁**（同 [NT-FIX 26]：与菜单 `NT_ShadowStrength` 默认 1
            //    ⇒ VRChat 里 `_ShadowStrength` = 1.0 耦合；在 1.0 下 lilToon 的默认 `(1,0.1,0)`
            //    会画出明显的红边界）。撤掉它必须先解决菜单默认值。
            //
            // 为什么：`ShadowColor/phase_shade.hlsl:121` 是**逐通道**权重
            //     ntSIndirect = lerp(ntSIndirect, ntSAlbedo, ntSLitW * _ShadowBorderColor.rgb);
            // R 通道权重远大于 G/B ⇒ 边界处**偏红**。着色器默认是 `(1, 0.1, 0)`（很淡），
            // 但实机这只 avatar 的 lilToon 材质给的是 **`(1, 0, 0)` 饱和纯红**。
            // lilToon 时代看不出来，是因为它的 `_ShadowStrength` 只有 0.1，边界几乎不被显示；
            // 而我们的菜单「阴影强度」默认给到 1.0（为了让阴影可见）⇒ **红边界被一起放大 ⇒ 脸红**
            // （2026-09-17 用户实机反馈）。
            //
            // 判据故意收紧：**只有 G、B 都接近 0、R 明显高**（= 纯红/饱和红）才中性化成白
            // （= 不做边界染色）。用户有意调的暖边界色（例如 (1, 0.6, 0.4)）原样保留。
            var borderColor = GetColor(source, "_ShadowBorderColor", new Color(1f, .1f, 0f, 1f));
            if (target.HasProperty("_ShadowBorderColor"))
            {
                // ⚠️ 判据**故意放宽**到把 lilToon 的**默认值 `(1, 0.1, 0)`** 也算进来
                //    （2026-09-17 实机：一开始要求 G<0.05，结果默认的 (1,0.1,0) 被放过了，
                //     而 1:0.1 的逐通道权重依然是**强烈的红边界** ⇒ 脸照样红）。
                var redBorder = borderColor.r > .5f && borderColor.g < .35f && borderColor.b < .35f;
                target.SetColor("_ShadowBorderColor", redBorder ? Color.white : borderColor);
                if (redBorder)
                    entry.Warn(NTL10n.F(
                        "NOTICE: lilToon Border Color was red-dominant {0}; NonToon applies the border colour per channel, so with a strong shadow strength it paints a red rim (very visible on the face). It was neutralised to white (no border tint). Set _ShadowBorderColor back manually if you want the warm terminator.",
                        borderColor.ToString("F3")));
                else
                    entry.Messages.Add(NTL10n.L("Copied Border Color to NonToon's per-channel border gradation."));
            }

            foreach (var p in new[]
            {
                "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
            }) CopyTexture(source, p, target, p);

            // _AAStrength 在 lilToon 与 NonToon 两边同名；不存在时保持 NonToon 默认。
            foreach (var p in new[]
            {
                "_ShadowBorder", "_ShadowBlur",
                "_Shadow2ndBorder", "_Shadow2ndBlur",
                "_Shadow3rdBorder", "_Shadow3rdBlur",
                "_ShadowStrength", "_ShadowBorderRange", "_ShadowMainStrength", "_AAStrength",
            }) CopyFloat(source, p, target, p);

            // [NT-FIX 27] `_ShadowStrength` 必须给一个**可见下限**。
            //
            // 为什么不能照搬：本 fork **关掉了 Shade 模块的梯度 ramp**（`ShadeGradientIndex = -1`，
            // 见本方法末尾），把"toon 阴影"整条交给了这个 ShadowColor 模块 ——
            // 而 `_ShadowStrength` 正是它的**总强度**（`phase_shade.hlsl:93-94`
            // `ntSLit1 = lerp(1.0, ntSLit1, _ShadowStrength * …)`）。
            // lilToon 里作者可以把它设得很低（这只 avatar 是 **0.10 / 0.29**），因为 lilToon 的
            // 阴影还叠着 ramp；但在我们这里 = **完全没有明暗层次**（实机反馈："完全没有层次感"）。
            // ⇒ 低于 0.85 就抬到 0.85，并写进日志（用户想更淡可以自己在材质里往下调）。
            if (target.HasProperty("_ShadowStrength"))
            {
                var sourceStrength = source.HasProperty("_ShadowStrength") ? source.GetFloat("_ShadowStrength") : 1f;
                if (sourceStrength < .85f)
                {
                    SetNumber(target, "_ShadowStrength", .85f);
                    entry.Warn(NTL10n.F(
                        "NOTICE: lilToon Shadow Strength {0} would leave NonToon with almost no toon shading, because this fork drives the shadow from ShadowColor instead of the Shade ramp. It was raised to 0.85 - lower it in the material if you want a flatter look.",
                        sourceStrength.ToString("F3")));
                }
            }

            // ⚠️ **只有走本回退路径**（用 ShadowColor 模块承载阴影）时才关掉 Shade 的 Ramp，
            //    避免两者都乘 albedo 而叠加。
            var shadeGradientIndex = ModuleProperty(target, "shade", "ShadeGradientIndex");
            if (shadeGradientIndex != null) SetInteger(target, shadeGradientIndex, -1);

            // [NT-FIX 36 / 2026-09-18] 两个**确定无法转换**的 lilToon 阴影参数，如实报警而不是静默丢弃。
            //   （审计第 7 轮 b 指出，我逐条核对了真实用法后才写进来。）
            //   · `_BackfaceForceShadow`：lilToon 用 `fd.facing < 0` 判背面并强制阴影
            //     （`lil_common_frag.hlsl:1038 / :1156`：`lns.x/.y/.w/.z *= bfshadow`）。
            //     ⛔ **NonToon 的 `sd` 里没有 facing 等价字段**（全仓 grep "facing" 只命中一处注释）
            //     ⇒ 按审计的忠告**不猜**，如实报"未转换"。
            //   · `_ShadowReceive`：控制是否接收主光阴影，模块未实现。
            // 实测本库：`_BackfaceForceShadow` 非默认 **6/49**、`_ShadowReceive` **1/49**。
            // ⚪ 它另外点名的 `_ShadowEnvStrength` / `_ShadowAOShift` / `_ShadowMaskType`
            //     在本库**全部是默认值**：默认下 `saturate(indLightColor * 0) = 0` ⇒ lerp 权重 0 = no-op；
            //     AO/MaskType 分支都要求**未被指定**的遮罩贴图 ⇒ 整支关闭。**不报警，避免噪音。**
            var backfaceForce = GetFloat(source, "_BackfaceForceShadow", 0f);
            if (backfaceForce > 0.001f)
                entry.Warn(NTL10n.F("WARNING: lilToon Backface Force Shadow {0} was not converted - NonToon's shading data has no back-face flag equivalent, so back-facing polygons are not forced into shadow.",
                    backfaceForce.ToString("F3")));
            var shadowReceive = GetFloat(source, "_ShadowReceive", 1f);
            if (shadowReceive < 0.999f)
                entry.Warn(NTL10n.F("WARNING: lilToon Shadow Receive {0} was not converted - NonToon always receives the main light shadow.",
                    shadowReceive.ToString("F3")));

            entry.Messages.Add(NTL10n.L("Copied lilToon shadow settings to NonToon's native ShadowColor module (border/blur/strength/2nd/3rd/border colour preserved 1:1) and disabled the Shade ramp to avoid stacking."));
        }

        // [NT-FEAT 11] lilToon 的三张逐像素阴影遮罩 → NonToon ShadowColor 模块的同名属性。
        // 通道语义与 lilToon 完全一致：_ShadowStrengthMask 用 .r，_ShadowBlurMask / _ShadowBorderMask
        // 用 .rgb 分别对应第 1/2/3 层。三张贴图默认 white，所以打开开关也不会把阴影弄没。
        private static void CopyShadowMasks(Material source, Material target, ConversionEntry entry)
        {
            var strength = GetTexture(source, "_ShadowStrengthMask");
            var border = GetTexture(source, "_ShadowBorderMask");
            var blur = GetTexture(source, "_ShadowBlurMask");
            if (strength == null && border == null && blur == null) return;

            if (!target.HasProperty("_ShadowStrengthMask") || !target.HasProperty("_ShadowMaskEnable"))
            {
                entry.Warn(NTL10n.L("WARNING: the installed NonToon shader has no per-pixel shadow mask inputs; lilToon Shadow Strength/Border/Blur Masks were not copied."));
                return;
            }

            if (strength != null) { CopyTexture(source, "_ShadowStrengthMask", target, "_ShadowStrengthMask"); CopyTextureScaleOffset(source, "_ShadowStrengthMask", target, "_ShadowStrengthMask"); }
            if (border != null) { CopyTexture(source, "_ShadowBorderMask", target, "_ShadowBorderMask"); CopyTextureScaleOffset(source, "_ShadowBorderMask", target, "_ShadowBorderMask"); }
            if (blur != null) { CopyTexture(source, "_ShadowBlurMask", target, "_ShadowBlurMask"); CopyTextureScaleOffset(source, "_ShadowBlurMask", target, "_ShadowBlurMask"); }
            SetInteger(target, "_ShadowMaskEnable", 1);
            entry.Messages.Add(NTL10n.L("Copied lilToon per-pixel shadow masks (Strength / Border / Blur) into NonToon's ShadowColor module and enabled Use Shadow Masks."));
        }

        // [NT-FIX 27] ramp 用的阴影强度也要给可见下限（同一个理由，见调用点注释）。
        private static float RampStrength(Material source, ConversionEntry entry)
        {
            var v = GetFloat(source, "_ShadowStrength", 1f);
            if (v >= .85f) return v;
            entry.Warn(NTL10n.F(
                "NOTICE: lilToon Shadow Strength {0} would make the NonToon shade ramp almost entirely light (no visible toon shading). It was raised to 0.85 when baking the ramp - edit the .scgradients asset if you want it flatter.",
                v.ToString("F3")));
            return .85f;
        }

        // [NT-FIX 26] 阴影颜色的"完全中性化"。
        //
        // ⚠️ 曾经写成"保留 20% 原色偏"，**那是错的**（2026-09-17 实机用 A/B 渲染diff 量出来）：
        //    保留后 `Body._ShadowColor = (0.815, 0.771, 0.774)` —— R、B 都 > G，
        //    而 `phase_shade.hlsl:104` 是**逐通道相乘** ⇒ 阴影乘上去后 G 被压得最多
        //    ⇒ 阴影**偏品红**。实测：`_ShadowStrength` 0→1 时平均
        //    ΔR=-19.3 / ΔG=-23.1 / ΔB=-26.5（R 降得最少 = 往红走），
        //    阴影区平均色 (69.1, 65.6, 79.0) = R>G 且 B>G = 品红/紫。
        //    ⇒ 必须把三通道**压成完全相等的灰**（只保留亮度），否则色偏一定会被放大。
        //
        // 阈值也放宽到 0.04：Hair/Clothes 的 `(0.745, 0.686, 0.745)` 饱和度只有 0.079，
        // 按 0.15 的旧阈值会被放过，但它同样是 R=B>G 的品红 ⇒ 一样会泛紫红。
        private static Color NeutralizeShadowTint(Color c)
        {
            var maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            var minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            var saturation = maxc > 1e-4f ? (maxc - minc) / maxc : 0f;
            if (saturation < 0.04f) return c;
            var luma = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return new Color(luma, luma, luma, c.a);   // 完全中性：三通道相等
        }

        private static void CopyColorIfPresent(Material source, Material target, string property)
        {
            if (source.HasProperty(property) && target.HasProperty(property))
                target.SetColor(property, source.GetColor(property));
        }

        private static void CopyColorRamps(Material source, Material target, string outputFolder, ConversionEntry entry)
        {
            var shadeGradientIndex = ModuleProperty(target, "shade", "ShadeGradientIndex");
            var rimShadeGradientIndex = ModuleProperty(target, "rimshade", "RimShadeGradientIndex");
            var hairSpecularGradientIndex = ModuleProperty(target, "hairspecular", "HairSpecularGradientIndex");
            // [NT-FIX 33 / 2026-09-18] 有 ShadowColor 模块时**不烘 shade ramp** —— 否则 `CopyColorRamps`
            // （本函数在 `CopyShade` **之后**调用，:417 → :418）会把 `CopyShadowColourNative` 刚写下的
            // `_ShadeGradientIndex = -1` **覆盖**成一条烘焙出来的 ramp，于是 Shade 与 ShadowColor
            // **两条路径同时活着**：Shade 先算，ShadowColor 再整段赋值覆盖（`phase_shade.hlsl:124`）
            // ⇒ ramp 白烘、且 `_SharedGradients` 多出一张无用的图。
            // 这条 `&& !HasProperty(...)` 在 [NT-FIX 28] 里被删掉过，本轮恢复（这次理由与上次相反，见 `CopyShade`）。
            var hasShadow = IsEnabled(source, "_UseShadow") && shadeGradientIndex != null
                && !target.HasProperty("_ShadowColorEnable");
            var hasRimShade = IsEnabled(source, "_UseRimShade") && rimShadeGradientIndex != null;
            var hasHairSpecular = IsEnabled(source, "_UseAnisotropy") && hairSpecularGradientIndex != null;
            if (!hasShadow && !hasRimShade && !hasHairSpecular) return;
            if (!target.HasProperty("_SharedGradients"))
            {
                entry.Warn(NTL10n.L("MISSING MODULE Shade/RimShade: color ramps could not be assigned."));
                return;
            }

            var ramps = new List<Color[]>();
            var shadeIndex = -1;
            var rimShadeIndex = -1;
            var hairSpecularIndex = -1;
            if (hasShadow)
            {
                ramps.Add(GradientAssetBaker.CreateLilToonShadowRamp(
                    // ⚠️ 烘进 ramp 之前也要**中性化**（[NT-FIX 26]）：ramp 是"阴影区颜色"，
                    //    如果直接拿 lilToon 的粉色 `_ShadowColor` 去烘，结果一样会泛红
                    //    —— 换路径不解决色偏，色偏来自源颜色本身。
                    NeutralizeShadowTint(GetColor(source, "_ShadowColor", Color.gray)),
                    NeutralizeShadowTint(GetColor(source, "_Shadow2ndColor", Color.gray)),
                    NeutralizeShadowTint(GetColor(source, "_Shadow3rdColor", new Color(0, 0, 0, 0))),
                    GetFloat(source, "_ShadowBorder", .5f), GetFloat(source, "_ShadowBlur", .1f),
                    GetFloat(source, "_Shadow2ndBorder", .15f), GetFloat(source, "_Shadow2ndBlur", .1f),
                    GetFloat(source, "_Shadow3rdBorder", .25f), GetFloat(source, "_Shadow3rdBlur", .1f),
                    // ⚠️ `_ShadowStrength` 也要给**可见下限**（与 [NT-FIX 27] 同一个理由）：
                    //    它是被**烘进 ramp** 的强度。lilToon 里作者能设到 0.10（因为还叠着别的），
                    //    但在 NonToon 的 ramp 里 0.10 ⇒ 渐变几乎是纯亮端 ⇒ **完全没有层次感**
                    //    （用户 2026-09-17 反复反馈）。低于 0.85 就抬到 0.85 并写进日志。
                    RampStrength(source, entry)));
                shadeIndex = ramps.Count - 1;
                entry.Messages.Add(NTL10n.L("Converted lilToon shadow colors and Border/Blur thresholds to a NonToon Shade ramp."));
            }
            if (hasRimShade)
            {
                ramps.Add(GradientAssetBaker.CreateLilToonRimShadeRamp(
                    GetColor(source, "_RimShadeColor", Color.gray),
                    GetFloat(source, "_RimShadeBorder", .5f), GetFloat(source, "_RimShadeBlur", 1f),
                    Mathf.Max(.01f, GetFloat(source, "_RimShadeFresnelPower", 1f))));
                rimShadeIndex = ramps.Count - 1;
                entry.Messages.Add(NTL10n.L("Converted lilToon rim-shade color and Border/Blur/Fresnel Power to a NonToon RimShade ramp."));
            }
            if (hasHairSpecular)
            {
                ramps.Add(GradientAssetBaker.CreateHairSpecularRamp(
                    GetFloat(source, "_AnisotropyTangentWidth", 1f),
                    GetFloat(source, "_AnisotropyShift", 0f),
                    GetFloat(source, "_AnisotropySpecularStrength", 1f)));
                hairSpecularIndex = ramps.Count - 1;
                entry.Messages.Add(NTL10n.L("Mapped lilToon anisotropy strength, shift and tangent width to an approximate NonToon Hair Specular ramp."));
            }
            var gradients = GradientAssetBaker.Write(ramps, outputFolder, source.name + "_Gradients");
            if (gradients == null) return;
            target.SetTexture("_SharedGradients", gradients);
            // Assign indices after importing the .scgradients asset so the module sees
            // the final Texture2DArray and the values cannot be reset by the import.
            if (shadeIndex >= 0) SetInteger(target, shadeGradientIndex, shadeIndex);
            if (rimShadeIndex >= 0)
            {
                SetInteger(target, rimShadeGradientIndex, rimShadeIndex);
                SetInteger(target, ModuleProperty(target, "rimshade", "RimShadeMaskChannel"), 2);
            }
            if (hairSpecularIndex >= 0)
            {
                SetInteger(target, hairSpecularGradientIndex, hairSpecularIndex);
                SetNumber(target, ModuleProperty(target, "hairspecular", "HairSpecularMultiplyAlbedo"), 0f);
                SetInteger(target, ModuleProperty(target, "hairspecular", "HairSpecularMaskChannel"), 3);
            }
        }

        private static void ReapplyModuleEnables(Material source, Material target, ConversionEntry entry)
        {
            var detailsEnable = ModuleProperty(target, "details", "Enable");
            if (detailsEnable != null && (GetTexture(source, "_BumpMap") != null ||
                                          (IsEnabled(source, "_UseBump2ndMap") && GetTexture(source, "_Bump2ndMap") != null)))
                SetModuleEnabled(target, "details", true);

            var matCapsEnable = ModuleProperty(target, "matcaps", "Enable");
            if (matCapsEnable != null && (IsEnabled(source, "_UseMatCap") || IsEnabled(source, "_UseMatCap2nd")))
                SetModuleEnabled(target, "matcaps", true);

            // [NT-VENDOR] ShadowColor 模块同样要在这里重放：本函数在 AssetDatabase.CreateAsset
            // **之前与之后各调用一次**，而 CreateAsset 会把整数型属性初始化回默认值。
            // 又因为该模块 keepPropertyNames，属性名就是 _ShadowColorEnable（不能用 ModuleProperty 拼）。
            //
            // [NT-FIX 33 / 2026-09-18] 判据**反转**，与 `CopyShade`(:717) 保持一致：
            //    有 ShadowColor 模块 ⇒ **开它**，并把 Shade 的 `_ShadeGradientIndex` 钉成 **-1**
            //    （两条路径都会整段赋值 `sd.col.rgb`，同时活着则 ShadowColor 覆盖 Shade ⇒ ramp 白烘）。
            //    没有 ShadowColor 模块 ⇒ 关它，阴影交给上游 Shade ramp（`CopyColorRamps` 烘）。
            if (target.HasProperty("_ShadowColorEnable"))
            {
                var wantShadowColor = IsEnabled(source, "_UseShadow");
                SetNumber(target, "_ShadowColorEnable", wantShadowColor ? 1f : 0f);
                // [NT-FIX 35b / 审计第 7 轮 a] ⚠️ **源没开阴影时也要钉 -1**。
                // 原来只在 wantShadowColor 为真时钉 ⇒ 若目标模板 / 复用资产（OverwriteExisting）
                // 里残留一个合法的 `_ShadeGradientIndex`，`_UseShadow = 0` 的材质**照样会走
                // Shade ramp 着色** —— 与"源关闭了阴影"矛盾。这不是"双开"，而是**漏接管**：
                // 转换器既然全权接管阴影状态，就应该把两条路径都关干净。
                // ⚠️ 必须在 CreateAsset **之后**也重放：整数型属性会被 CreateAsset 复位。
                var shadeIdx = ModuleProperty(target, "shade", "ShadeGradientIndex");
                if (shadeIdx != null)
                {
                    SetInteger(target, shadeIdx, -1);
                }
                else if (ModuleProperty(target, "shade", "SDFType") != null)
                {
                    // [NT-FIX 35b] Shade 模块**存在**却解析不出索引 ⇒ 互斥无法保证。
                    // 审计要求把这种"静默跳过"变成可见诊断 —— 否则又是一个"够不到的判据"。
                    entry.Warn(NTL10n.L("WARNING: the Shade module is present but its Shade Gradient Index property could not be resolved, so the Shade ramp and the ShadowColor module cannot be guaranteed to be mutually exclusive. Check the converted material for doubled shading."));
                }
            }

            // [NT-VENDOR] Emission 模块同理：本函数在 CreateAsset 前后各调用一次，
            // 只在 CopyEmissionNative 里设一次的话，最终资产里模块可能是关着的（阴影那个 bug 的同类）。
            if (target.HasProperty("_UseEmission") && IsEnabled(source, "_UseEmission"))
                SetNumber(target, "_UseEmission", 1f);
        }

        // [NT-VENDOR] lilToon 的发光 → 我们分支的**原生 Emission 模块**。
        // 该模块设了 keepPropertyNames，属性名与 lilToon 逐字符相同，所以是直接拷贝而非近似：
        //   _UseEmission / _EmissionColor / _EmissionMap / _EmissionBlend /
        //   _EmissionBlendMask / _EmissionMainStrength / _EmissionBlendMode
        // 而且模块算法逐字对应 lilToon 的 lilBlendColor（multiply mask、用 alpha 做混合、4 种混合模式），
        // 所以发光**不再被阴影衰减**、贴图与遮罩都保真。
        // 原版转换器只能近似成 LightBoost 的强度，Mapping.md 里明确写着「Emission Map は未対応」。
        private static void CopyEmissionNative(Material source, Material target, ConversionEntry entry)
        {
            if (!target.HasProperty("_UseEmission")) return;
            if (!IsEnabled(source, "_UseEmission"))
            {
                // 源没开发光：显式关掉，避免上一次转换或手改残留
                SetNumber(target, "_UseEmission", 0f);
                return;
            }

            SetNumber(target, "_UseEmission", 1f);
            CopyColorIfPresent(source, target, "_EmissionColor");
            CopyTexture(source, "_EmissionMap", target, "_EmissionMap");
            CopyTexture(source, "_EmissionBlendMask", target, "_EmissionBlendMask");
            foreach (var p in new[] { "_EmissionBlend", "_EmissionMainStrength" }) CopyFloat(source, p, target, p);
            if (source.HasProperty("_EmissionBlendMode")) SetNumber(target, "_EmissionBlendMode", source.GetFloat("_EmissionBlendMode"));

            var absent = new List<string>();
            foreach (var p in new[] { "_EmissionColor", "_EmissionMap", "_EmissionBlend", "_EmissionBlendMask", "_EmissionMainStrength", "_EmissionBlendMode" })
                if (!source.HasProperty(p)) absent.Add(p);
            entry.Messages.Add(
                "Copied lilToon emission 1:1 to NonToon's native Emission module (colour / map / blend / blend mask / main strength / blend mode)."
                + (absent.Count > 0 ? " Source material had no " + string.Join(", ", absent) + "; NonToon defaults were kept for those." : ""));

            if (IsEnabled(source, "_UseEmission2nd"))
                entry.Warn(NTL10n.L("WARNING: lilToon 2nd emission (_UseEmission2nd) has no NonToon equivalent and was not converted."));
            foreach (var p in new[] { "_EmissionBlink", "_EmissionFluorescence", "_EmissionParallaxDepth" })
                if (IsEnabled(source, p))
                    entry.Warn(NTL10n.F("WARNING: lilToon {0} has no NonToon equivalent and was not converted.", p));
        }

        private static void CopyEmissionAsLightBoost(Material source, Material target, ConversionEntry entry)
        {
            if (!IsEnabled(source, "_UseEmission")) return;
            if (ModuleProperty(target, "lighten", "LightBoost") == null) { entry.Warn(NTL10n.L("MISSING MODULE Lighten: emission was not converted.")); return; }
            var emission = GetColor(source, "_EmissionColor", Color.white);
            var emissionBlend = Mathf.Max(0f, GetFloat(source, "_EmissionBlend", 1f));
            var effectiveStrength = CalculateEmissionStrength(emission, emissionBlend);
            if (effectiveStrength <= .0001f)
            {
                SetNumber(target, ModuleProperty(target, "lighten", "LightBoost"), 1f);
                SetInteger(target, ModuleProperty(target, "lighten", "LightBoostAsEmission"), 0);
                entry.Messages.Add(NTL10n.L("Skipped NonToon Lighten because lilToon Emission has zero effective strength after Color Alpha and Blend were applied."));
                return;
            }
            SetNumber(target, ModuleProperty(target, "lighten", "LightBoost"), Mathf.Clamp(effectiveStrength, 0f, 10f));
            SetInteger(target, ModuleProperty(target, "lighten", "LightBoostAsEmission"), 1);
            var hasEmissionBlendMask = HasAssetTexture(source, "_EmissionBlendMask");
            var maskChannel = hasEmissionBlendMask ? GetEmissionMaskChannel(source) : 3;
            SetInteger(target, ModuleProperty(target, "lighten", "LightBoostMaskChannel"), maskChannel);
            if (hasEmissionBlendMask)
            {
                entry.Messages.Add(NTL10n.F("Mapped lilToon Emission Blend Mask to NonToon Lighten Shared Mask {0}.", SharedMaskChannelName(maskChannel)));
                if (target.GetTexture("_SharedMask") == null)
                    entry.Warn(NTL10n.L("WARNING: Emission Blend Mask requires texture baking; the Shared Mask was not generated, so Lighten uses its default mask."));
            }
            if (GetTexture(source, "_EmissionMap") != null || Mathf.Abs(emission.r - emission.g) > .01f || Mathf.Abs(emission.g - emission.b) > .01f)
                entry.Warn(NTL10n.L("WARNING: NonToon Lighten has no colored emission texture; only lilToon emission intensity was mapped."));
            else entry.Messages.Add(NTL10n.F("Mapped lilToon grayscale emission effective strength (RGB × Alpha × Blend = {0}) to NonToon Lighten.", effectiveStrength.ToString("F3")));
        }

        internal static float CalculateEmissionStrength(Color emission, float blend)
        {
            return Mathf.Max(0f, emission.maxColorComponent) * Mathf.Clamp01(emission.a) * Mathf.Max(0f, blend);
        }

        private static int GetEmissionMaskChannel(Material source)
        {
            if (!IsEnabled(source, "_UseMatCap")) return 0;
            if (!IsEnabled(source, "_UseMatCap2nd")) return 1;
            if (!IsEnabled(source, "_UseRimShade")) return 2;
            return 3;
        }

        private static string SharedMaskChannelName(int channel)
        {
            return channel == 0 ? "R" : channel == 1 ? "G" : channel == 2 ? "B" : "A";
        }

        private static bool HasAssetTexture(Material material, string property)
        {
            var texture = GetTexture(material, property);
            return texture != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture));
        }

        private static void CopyRenderState(Material source, Material target, bool isFur, ConversionEntry entry)
        {
            if (isFur) return;
            var transparentMode = Mathf.RoundToInt(GetFloat(source, "_TransparentMode", 0));
            var shaderName = source.shader == null ? string.Empty : source.shader.name.ToLowerInvariant();
            // Multi shaders store this in _TransparentMode. The dedicated lilToon variants
            // encode it in their shader name instead (e.g. Hidden/lilToonCutout).
            var isCutout = transparentMode == 1 || shaderName.Contains("cutout");
            var isTransparent = transparentMode >= 2 || shaderName.Contains("trans") || shaderName.Contains("refraction") || shaderName.Contains("gem");
            // lilToon: opaque/cutout/transparent; NonToon uses the same three values.
            var targetMode = isCutout ? 1 : isTransparent ? 2 : 0;
            SetInteger(target, "_RenderingMode", targetMode);
            ApplyNonToonRenderState(source, target, targetMode);
            if (source.HasProperty("_Cull")) SetNumber(target, "_Cull", source.GetFloat("_Cull"));
            CopyFloat(source, "_ZWrite", target, "_ZWrite");
            entry.Messages.Add(NTL10n.F("Mapped lilToon rendering mode to NonToon {0}.", NTL10n.L(targetMode == 0 ? "Opaque" : targetMode == 1 ? "Cutout" : "Transparent")));
            // [NT-FIX 34 / 2026-09-18] 判据修正：**只看 `transparentMode >= 3` 永远不触发**。
            //
            // 为什么：专用变体材质（`Hidden/lilToonGem` / `Hidden/lilToonRefraction*`）把模式**编在着色器名里**，
            // 它们的 `_TransparentMode` **属性本来就是 0**（从未被更新）⇒ 判据恒为假。
            // 实测（辉夜）：`tail_Crystal.mat` 是 `Hidden/lilToonGem`、属性 0 ⇒ 转换 severity = **Success**，
            // **一个警告都没有**，而它其实被近似成了普通透明 —— Gem 的折射/色散全部丢失。
            // 与 `[NT-FIX 30]` 同类：**能解析出正确结论，却写了一个够不到的判据。**
            var isGemOrRefraction = transparentMode >= 3
                || shaderName.Contains("gem")
                || shaderName.Contains("refraction")
                || IsEnabled(source, "_UseRefraction");
            if (isGemOrRefraction)
                entry.Warn(NTL10n.L("WARNING: lilToon gem/refraction rendering is approximated as NonToon transparent; gem distortion, fresnel and chromatic aberration are not reproduced."));
        }

        private static void ApplyNonToonRenderState(Material target, int mode)
        {
            ApplyNonToonRenderState(null, target, mode);
        }

        private static void ApplyNonToonRenderState(Material source, Material target, int mode)
        {
            if (target.HasProperty("_SrcBlend")) SetNumber(target, "_SrcBlend", mode == 2 ? 5f : 1f); // SrcAlpha / One
            if (target.HasProperty("_DstBlend")) SetNumber(target, "_DstBlend", mode == 2 ? 10f : 0f); // OneMinusSrcAlpha / Zero
            if (target.HasProperty("_AlphaToMask")) SetNumber(target, "_AlphaToMask", mode == 1 ? 1f : 0f);
            target.renderQueue = ResolveRenderQueue(source, mode);
        }

        /// <summary>[NT-FIX 32] 渲染队列：**照搬源材质的"生效队列"**，不再按渲染模式规范化。
        ///
        /// 为什么改（2026-09-17，实测 + 两个独立来源）：
        ///   旧策略是 `mode == 0 ? -1 : mode == 1 ? 2450 : 3000`，**完全无视源**。
        ///   实测辉夜模型的源材质（`AssetDatabase` 直读）：
        ///     · `age` / `face_transparent` 生效队列 **2460**（由源 shader 声明，不是材质覆盖）
        ///     · `HeartGun` **2900**（Gem，由 shader 声明）
        ///     · **`stockings` 生效 2450 而 shader 声明 2460** ⇒ `m_CustomRenderQueue = 2450`，
        ///       作者**手动**把它放进 AlphaTest 带；
        ///     · **`tail_Crystal` 生效 3005 而 shader 声明 2900** ⇒ 作者手动设 3005，
        ///       典型的"保证它最后画"手法。
        ///   ⇒ 旧策略把 2450 与 3005 **双双碾成 3000**，`tail_Crystal` 的排序意图被彻底破坏。
        ///
        ///   机理（为什么"照搬"是对的）：BiRP 默认把 **≤2500 按不透明排序、>2500 才按透明排序**
        ///   ⇒ 改队列会**同时**改变「相对其它材质的位置」与「默认距离排序策略」。
        ///   配合 `_ZWrite = 1`（lilToon 透明除 Gem 外一律 1），画序直接决定谁先写深度、
        ///   谁被后来的片元剔除 —— 这正是"前后遮挡乱了"的来源。
        ///
        /// ⚠️ 判据必须用 **`source.renderQueue`（生效队列）**，不能用 `m_CustomRenderQueue`：
        ///    上面 5 个材质里 4 个的覆盖值是 **-1**，队列全部来自源 shader 声明。
        /// ⚠️ 只有"源也拿不到"时才退回模式默认值；源是 2000(Geometry) 时写 **-1**，
        ///    语义与 2000 等价，但保持"该材质没有覆盖"的干净状态。
        /// ⚠️ 与 `[NT-FIX 31]`（`NTRenderQueueFix.cs` 的 ⑦、以及 `RenderingModeElement` 的自愈）的交互：
        ///    那两处**只允许改写"看起来像模式默认值"的队列**（-1/2000/2450/3000），
        ///    自定义值（如 2460 / 3005）一律不动 —— 否则开一次检视面板就会把本次保真悄悄撤销。
        /// </summary>
        private static int ResolveRenderQueue(Material source, int mode)
        {
            var fallback = mode == 0 ? -1 : mode == 1 ? 2450 : 3000;
            if (source == null) return fallback;
            var queue = source.renderQueue;          // 材质显式覆盖优先，否则源 shader 声明的队列
            if (queue < 0) return fallback;
            if (queue == 2000) return -1;            // Geometry = 目标 shader 的默认 ⇒ 保持"无覆盖"
            return queue;
        }

        private static void CopyStencil(Material source, Material target, ConversionEntry entry)
        {
            CopyFloat(source, "_StencilRef", target, "_StencilRef");
            CopyFloat(source, "_StencilComp", target, "_StencilComp");
            CopyFloat(source, "_StencilPass", target, "_StencilPass");
            CopyFloat(source, "_OutlineStencilRef", target, "_OutlineStencilRef");
            CopyFloat(source, "_OutlineStencilComp", target, "_OutlineStencilComp");
            CopyFloat(source, "_OutlineStencilPass", target, "_OutlineStencilPass");
            if (source.HasProperty("_StencilRef") || source.HasProperty("_OutlineStencilRef"))
                entry.Messages.Add(NTL10n.L("Mapped lilToon main/outline Stencil Ref, Comp and Pass to NonToon."));
            var unsupported = new[] { "_StencilReadMask", "_StencilWriteMask", "_StencilFail", "_StencilZFail", "_OutlineStencilReadMask", "_OutlineStencilWriteMask", "_OutlineStencilFail", "_OutlineStencilZFail" }
                .Any(property => source.HasProperty(property) && source.GetFloat(property) != (property.Contains("Mask") ? 255f : 0f));
            if (unsupported) entry.Warn(NTL10n.L("WARNING: NonToon has no matching Stencil read/write mask or Fail/ZFail setting; those lilToon values were not converted."));
        }

        private static void ReportUnsupportedFeatures(Material source, ConversionEntry entry)
        {
            var enabled = new[] { "_UseGlitter", "_UseEmission2nd", "_UseParallax" }
                .Any(property => IsEnabled(source, property));
            if (enabled) foreach (var message in UnsupportedFeatures) entry.Warn(NTL10n.F("WARNING: {0}", NTL10n.L(message)));
        }

        private static bool IsFur(Material material)
        {
            var shaderName = material.shader.name;
            var transparentMode = Mathf.RoundToInt(GetFloat(material, "_TransparentMode", 0));
            return shaderName.IndexOf("fur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                transparentMode == 5 || transparentMode == 6;
        }

        private static string DefaultOutputFolder(Material material)
        {
            var sourcePath = AssetDatabase.GetAssetPath(material);
            return Path.GetDirectoryName(sourcePath).Replace('\\', '/') + "/" + ConverterConstants.OutputFolderName;
        }

        private static string OutputMaterialName(Material material, string outputFolder)
        {
            var baseName = material.name + ConverterConstants.ConvertedSuffix;
            // Hierarchy and explicit output folders can contain materials with identical
            // filenames from different source folders. A stable GUID suffix ensures each
            // source retains its own output and is overwritten only by its own reconvert.
            if (string.Equals(outputFolder.TrimEnd('/'), DefaultOutputFolder(material).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return baseName;
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material));
            return string.IsNullOrEmpty(guid) ? baseName : material.name + "_" + guid.Substring(0, 8) + ConverterConstants.ConvertedSuffix;
        }

        private static bool IsWhite(Color color)
        {
            return Mathf.Abs(color.r - 1f) < .0001f && Mathf.Abs(color.g - 1f) < .0001f &&
                   Mathf.Abs(color.b - 1f) < .0001f && Mathf.Abs(color.a - 1f) < .0001f;
        }

        // lilToon keeps all layer fields after a user turns a layer off. In particular,
        // _Color2nd can remain tinted while _UseMain2ndTex is zero. The toggle is the
        // authoritative visibility state; disabled layers must never be baked.
        private static bool HasMainLayer(Material material, string prefix)
        {
            return IsEnabled(material, "_Use" + prefix.Substring(1) + "Tex");
        }

        // Old converter versions used GenerateUniqueAssetPath, creating " 1", " 2", ... on
        // every run. Remove only those legacy names; hash-named cache assets remain reusable.
        private static void CleanLegacyGeneratedOutputs(Material source, string outputFolder)
        {
            var legacyStems = new[]
            {
                source.name + ConverterConstants.ConvertedSuffix,
                source.name + "_Base", source.name + "_BaseColor", source.name + "_SharedMask", source.name + "_Gradients"
            };
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { outputFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var extension = Path.GetExtension(path);
                if (extension != ".mat" && extension != ".png" && extension != ".scgradients") continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (!legacyStems.Any(stem => name == stem || (name.StartsWith(stem + " ", StringComparison.Ordinal) && int.TryParse(name.Substring(stem.Length + 1), out _)))) continue;
                AssetDatabase.DeleteAsset(path);
            }
        }

        internal static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static Vector2 ToNonToonRimRange(float border, float blur, float fresnelPower)
        {
            var minimum = Mathf.Clamp01(border - blur * .5f);
            var maximum = Mathf.Clamp01(border + blur * .5f);
            var inversePower = 1f / fresnelPower;
            return new Vector2(Mathf.Pow(minimum, inversePower), Mathf.Pow(maximum, inversePower));
        }

        // Shader Core prefixes each module property with its module uniqueID.
        // Example: jp.lilxyzw.nontoon.details + Detail0NormalMap becomes
        // _jp_lilxyzw_nontoon_details_Detail0NormalMap.
        private static string ModuleProperty(Material material, string moduleName, string propertyName)
        {
            var prefixed = "_jp_lilxyzw_nontoon_" + moduleName + "_" + propertyName.TrimStart('_');
            if (material.HasProperty(prefixed)) return prefixed;
            var legacy = "_" + propertyName.TrimStart('_');
            return material.HasProperty(legacy) ? legacy : null;
        }

        // Shader Core declares SC_uint / SC_int as true Integer properties. Material.SetInt
        // appears to update their temporary value, but it is not serialized into the .mat file.
        // Use SetInteger for those properties and retain a float fallback for old shader variants.
        // [NT-VENDOR] 与 SetInteger 同理，但保留浮点精度。
        // 背景：NonToon 里 SC_uint / ShaderLab Int 声明的属性（_SrcBlend / _DstBlend / _AlphaToMask /
        // _Cull / _ZWrite / _Stencil* / 模块的 Enable …）用 Material.SetFloat 写是**静默无效**的，
        // 转换出来的材质会悄悄退回默认值 —— 例如透明材质拿到不透明混合、阴影颜色模块没被打开。
        // 实测依据：_ShadowColorEnable 用 SetFloat 写 → 转换后仍为 0；ShadeGradientIndex 用 SetInteger 写 → 正常保留。
        private static void SetNumber(Material material, string property, float value)
        {
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
            var index = material.shader.FindPropertyIndex(property);
            if (index >= 0 && material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Int)
                material.SetInteger(property, Mathf.RoundToInt(value));
            else
                material.SetFloat(property, value);
        }

        private static void SetInteger(Material material, string property, int value)
        {
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
            var index = material.shader.FindPropertyIndex(property);
            if (index >= 0 && material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Int)
                material.SetInteger(property, value);
            else
                material.SetFloat(property, value);
        }

        // SCConstValue turns a module's header toggle into a shader keyword. Both the integer
        // and the generated _0/_1 keyword are required for the module to be visible and active.
        private static void SetModuleEnabled(Material material, string moduleName, bool enabled)
        {
            var property = ModuleProperty(material, moduleName, "Enable");
            if (property == null) return;
            SetInteger(material, property, enabled ? 1 : 0);
            var keyword = property.ToUpperInvariant();
            if (enabled)
            {
                material.EnableKeyword(keyword + "_1");
                material.DisableKeyword(keyword + "_0");
            }
            else
            {
                material.EnableKeyword(keyword + "_0");
                material.DisableKeyword(keyword + "_1");
            }
        }

        internal static Texture GetTexture(Material material, string property) { return material.HasProperty(property) ? material.GetTexture(property) : null; }
        internal static float GetFloat(Material material, string property, float fallback) { return material.HasProperty(property) ? material.GetFloat(property) : fallback; }
        internal static Color GetColor(Material material, string property, Color fallback) { return material.HasProperty(property) ? material.GetColor(property) : fallback; }
        internal static bool IsEnabled(Material material, string property) { return GetFloat(material, property, 0f) > .5f; }
        internal static void CopyFloat(Material source, string sourceProperty, Material target, string targetProperty) { if (source.HasProperty(sourceProperty) && target.HasProperty(targetProperty)) SetNumber(target, targetProperty, source.GetFloat(sourceProperty)); }
        internal static void CopyTexture(Material source, string sourceProperty, Material target, string targetProperty) { if (source.HasProperty(sourceProperty) && target.HasProperty(targetProperty)) target.SetTexture(targetProperty, source.GetTexture(sourceProperty)); }
        internal static void CopyTextureScaleOffset(Material source, string sourceProperty, Material target, string targetProperty)
        {
            if (!source.HasProperty(sourceProperty) || !target.HasProperty(targetProperty)) return;
            target.SetTextureScale(targetProperty, source.GetTextureScale(sourceProperty));
            target.SetTextureOffset(targetProperty, source.GetTextureOffset(sourceProperty));
        }
    }

    internal static class ConversionDiagnostics
    {
        private static readonly string[] SourceProperties =
        {
            "_MainTex", "_Color", "_MainTexHSVG", "_UseMain2ndTex", "_Main2ndTex", "_Color2nd", "_Main2ndBlendMask", "_Main2ndTexBlendMode", "_Main2ndTexAlphaMode", "_Main2ndTex_UVMode", "_Main2ndEnableLighting",
            "_UseMain3rdTex", "_Main3rdTex", "_Color3rd", "_Main3rdBlendMask", "_Main3rdTexBlendMode", "_Main3rdTexAlphaMode", "_Main3rdTex_UVMode", "_Main3rdEnableLighting", "_AlphaMask", "_AlphaMaskMode", "_AlphaMaskScale", "_AlphaMaskValue",
            "_BumpMap", "_BumpScale", "_BumpMap_UVMode", "_UseBump2ndMap", "_Bump2ndMap", "_Bump2ndScale", "_Bump2ndMap_UVMode", "_Bump2ndScaleMask",
            "_UseShadow", "_ShadowColor", "_Shadow2ndColor", "_Shadow3rdColor", "_ShadowBorder", "_ShadowBlur", "_Shadow2ndBorder", "_Shadow2ndBlur", "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask",
            "_UseRimShade", "_RimShadeColor", "_RimShadeMask", "_RimShadeBorder", "_RimShadeBlur", "_RimShadeFresnelPower", "_UseRim", "_RimColor", "_RimBorder", "_RimBlur", "_RimFresnelPower",
            "_UseMatCap", "_MatCapTex", "_MatCapColor", "_MatCapBlendMask", "_MatCapBlendMode", "_UseMatCap2nd", "_MatCap2ndTex", "_MatCap2ndColor", "_MatCap2ndBlendMask", "_MatCap2ndBlendMode",
            "_UseReflection", "_ApplySpecular", "_ReflectionColor", "_Smoothness", "_SmoothnessTex", "_MetallicGlossMap", "_UseEmission", "_EmissionColor", "_EmissionMap", "_EmissionBlendMask", "_EmissionBlend", "_EmissionBlendMode", "_EmissionMap_UVMode",
            "_AsUnlit", "_LightMinLimit", "_LightMaxLimit", "_MonochromeLighting", "_VertexLightStrength", "_ShadowEnvStrength", "_BeforeExposureLimit", "_lilDirectionalLightStrength", "_UseBacklight", "_BacklightColor", "_BacklightBorder", "_BacklightBlur",
            "_UseOutline", "_OutlineColor", "_OutlineWidth", "_OutlineVertexR2Width", "_OutlineFixWidth", "_OutlineZBias", "_OutlineTex", "_OutlineWidthMask", "_TransparentMode", "_Cutoff", "_Cull", "_ZWrite", "_SrcBlend", "_DstBlend", "_StencilRef", "_StencilComp", "_StencilPass",
            "_FurNoiseMask", "_FurVector", "_FurVectorTex", "_FurVectorScale", "_VertexColor2FurVector", "_FurLayerNum", "_FurGravity", "_FurRandomize"
        };

        internal static void Capture(Material source, Material target, ConversionEntry entry)
        {
            entry.Diagnostics.Add("=== Input material snapshot ===");
            entry.Diagnostics.Add("Source shader: " + (source.shader == null ? "<missing>" : source.shader.name));
            entry.Diagnostics.Add("Target shader: " + (target.shader == null ? "<missing>" : target.shader.name));
            foreach (var property in SourceProperties) entry.Diagnostics.Add("Source " + Describe(source, property));
            entry.Diagnostics.Add("Source layer decision: Main2nd=" + Enabled(source, "_UseMain2ndTex") + ", Main3rd=" + Enabled(source, "_UseMain3rdTex") + ", AlphaMask=" + (GetTexture(source, "_AlphaMask") != null && GetFloat(source, "_AlphaMaskMode", 0f) != 0f));
            entry.Diagnostics.Add("Source effective reflection/specular: " + LilToonMaterialConverter.ShouldCopySpecular(Enabled(source, "_UseReflection"), !source.HasProperty("_ApplySpecular") || Enabled(source, "_ApplySpecular")));
            entry.Diagnostics.Add("Source effective emission strength: " + LilToonMaterialConverter.CalculateEmissionStrength(source.HasProperty("_EmissionColor") ? source.GetColor("_EmissionColor") : Color.white, GetFloat(source, "_EmissionBlend", 1f)).ToString("F4") + " (RGB maximum × Color Alpha × Blend)");
            entry.Diagnostics.Add("=== Generated assets and target application ===");
            entry.Diagnostics.Add("Target " + Describe(target, "_BaseTexture"));
            entry.Diagnostics.Add("Target " + Describe(target, "_SharedMask"));
            entry.Diagnostics.Add("Target " + Describe(target, "_SharedGradients"));
            foreach (var module in NonToonCompatibility.RequiredModules)
            {
                entry.Diagnostics.Add("Module " + module.Key + ": " + (target.HasProperty(module.Value) ? "present" : "MISSING") + " (" + module.Value + ")");
            }
            var targetProperties = new[]
            {
                "_RenderingMode", "_Cutoff", "_Cull", "_OutlineColor", "_OutlineWidth", "_OutlineZOffset", "_OutlineFromVertexColor", "_Roughness", "_NormalMap", "_NormalScale", "_jp_lilxyzw_nontoon_details_Enable", "_jp_lilxyzw_nontoon_details_DetailMask", "_jp_lilxyzw_nontoon_details_Detail0NormalMap", "_jp_lilxyzw_nontoon_details_Detail0NormalScale", "_jp_lilxyzw_nontoon_details_Detail1NormalMap", "_jp_lilxyzw_nontoon_details_Detail1NormalScale", "_jp_lilxyzw_nontoon_matcaps_Enable",
                "_jp_lilxyzw_nontoon_matcaps_MatCapMultiply", "_jp_lilxyzw_nontoon_matcaps_MatCapAdd", "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex",
                "_jp_lilxyzw_nontoon_rimshade_RimShadeGradientIndex", "_jp_lilxyzw_nontoon_rimshade_RimShadeMaskChannel", "_jp_lilxyzw_nontoon_rimlight_RimLightColor", "_jp_lilxyzw_nontoon_rimlight_RimLightRange",
                "_jp_lilxyzw_nontoon_specular_SpecularColor", "_jp_lilxyzw_nontoon_specular_SpecularMaskChannel", "_jp_lilxyzw_nontoon_lighten_LightBoost", "_jp_lilxyzw_nontoon_lighten_LightBoostMaskChannel", "_jp_lilxyzw_nontoon_lighten_LightBoostAsEmission", "_StencilRef", "_StencilComp", "_StencilPass", "_FurNoiseMask", "_FurNoiseTiling", "_FurSubdivision", "_FurVector"
            };
            foreach (var property in targetProperties) entry.Diagnostics.Add("Target " + Describe(target, property));
            entry.Diagnostics.Add("Target module validation: Details=" + DescribeModuleToggle(target, "_jp_lilxyzw_nontoon_details_Enable") + ", MatCaps=" + DescribeModuleToggle(target, "_jp_lilxyzw_nontoon_matcaps_Enable"));
        }

        private static bool Enabled(Material material, string property) { return material.HasProperty(property) && material.GetFloat(property) > .5f; }
        private static Texture GetTexture(Material material, string property) { return material.HasProperty(property) ? material.GetTexture(property) : null; }
        private static float GetFloat(Material material, string property, float fallback) { return material.HasProperty(property) ? material.GetFloat(property) : fallback; }

        private static string DescribeModuleToggle(Material material, string property)
        {
            if (!material.HasProperty(property)) return "<absent>";
            var enabled = material.GetInteger(property) != 0;
            var keyword = property.ToUpperInvariant() + "_1";
            return (enabled ? "enabled" : "disabled") + ", keyword " + keyword + "=" + material.IsKeywordEnabled(keyword);
        }

        private static string Describe(Material material, string property)
        {
            if (!material.HasProperty(property)) return property + " = <absent>";
            for (var index = 0; index < ShaderUtil.GetPropertyCount(material.shader); index++)
            {
                if (ShaderUtil.GetPropertyName(material.shader, index) != property) continue;
                var type = ShaderUtil.GetPropertyType(material.shader, index);
                if (type == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    var texture = material.GetTexture(property);
                    return texture == null ? property + " = <none>" : property + " = " + DescribeTexture(texture);
                }
                if (type == ShaderUtil.ShaderPropertyType.Color) return property + " = " + material.GetColor(property);
                if (type == ShaderUtil.ShaderPropertyType.Vector) return property + " = " + material.GetVector(property);
                // Shader Core's SC_uint/SC_int values are serialized in m_Ints.
                // GetFloat returns the float-default for these properties, which made the
                // report falsely claim modules were disabled and gradient indices were zero.
                if (type == ShaderUtil.ShaderPropertyType.Int) return property + " = " + material.GetInteger(property).ToString();
                return property + " = " + material.GetFloat(property).ToString("F4");
            }
            return property + " = <unknown shader property type>";
        }

        private static string DescribeTexture(Texture texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var result = "Texture(" + texture.name + ", " + path + ", " + texture.width + "x" + texture.height;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
                result += ", max=" + importer.maxTextureSize + ", compression=" + importer.textureCompression + ", crunch=" + importer.crunchedCompression + ", sRGB=" + importer.sRGBTexture + ", readable=" + importer.isReadable + ", filter=" + importer.filterMode;
            return result + ")";
        }
    }

    internal static class TextureBaker
    {
        private static bool IsEnabled(Material material, string property) { return material.HasProperty(property) && material.GetFloat(property) > .5f; }
        private static Texture GetTexture(Material material, string property) { return material.HasProperty(property) ? material.GetTexture(property) : null; }
        private static float GetFloat(Material material, string property, float fallback) { return material.HasProperty(property) ? material.GetFloat(property) : fallback; }
        private static Color GetColor(Material material, string property, Color fallback) { return material.HasProperty(property) ? material.GetColor(property) : fallback; }

        // This 4x4 asset is intentionally UV-independent: every UV coordinate samples the same lilToon color.
        internal static Texture2D BakeSolidColor(Color color, string outputFolder, string name)
        {
            const int size = 4;
            var pixels = Enumerable.Repeat(color, size * size).ToArray();
            return WritePng(pixels, size, size, outputFolder, CachedName(name, "solid", color.ToString()), true, null);
        }

        internal static Texture2D BakeColorTexture(Texture texture, Color multiplier, string outputFolder, string name, ConversionEntry entry)
        {
            var pixels = ReadPixels(texture, entry);
            if (pixels == null) return null;
            for (var i = 0; i < pixels.Length; i++) pixels[i] *= multiplier;
            return WritePng(pixels, texture.width, texture.height, outputFolder, CachedName(name, "color", TextureKey(texture) + "|" + multiplier), true, texture);
        }

        internal static Texture2D BakeDetailMask(Material source, Texture baseTexture, bool firstNormal, bool secondNormal, string outputFolder, string name, ConversionEntry entry)
        {
            var scaleMask = GetTexture(source, "_Bump2ndScaleMask");
            // A constant Details mask is intentionally tiny. Only the UV-transformed 2nd
            // normal scale mask needs the source mask's resolution.
            var reference = scaleMask;
            var width = reference == null ? 4 : reference.width;
            var height = reference == null ? 4 : reference.height;
            var scalePixels = scaleMask == null ? null : ReadPixels(scaleMask, entry);
            if (scaleMask != null && scalePixels == null)
                entry.Warn(NTL10n.L("WARNING: lilToon Bump 2nd Scale Mask could not be baked; Details G uses white instead."));
            var scale = source.HasProperty("_Bump2ndScaleMask") ? source.GetTextureScale("_Bump2ndScaleMask") : Vector2.one;
            var offset = source.HasProperty("_Bump2ndScaleMask") ? source.GetTextureOffset("_Bump2ndScaleMask") : Vector2.zero;
            var pixels = new Color[width * height];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
                var secondValue = !secondNormal ? 0f : scalePixels == null ? 1f : SampleRepeat(scalePixels, scaleMask.width, scaleMask.height, new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y)).r;
                pixels[y * width + x] = new Color(firstNormal ? 1f : 0f, secondValue, 0f, 0f);
            }
            var key = (firstNormal ? "1" : "0") + "|" + (secondNormal ? "1" : "0") + "|" + TextureKey(scaleMask) + "|" + scale + "|" + offset;
            return WritePng(pixels, width, height, outputFolder, CachedName(name, "details", key), false, reference);
        }

        // NonToon has no live Main 2nd/3rd layer system. Bake their color, blend mask and
        // blend mode into the base texture while leaving every lilToon source asset untouched.
        internal static Texture2D BakeMainLayers(Material source, Texture baseTexture, Color baseColor, string outputFolder, string name, ConversionEntry entry)
        {
            var basePixels = ReadPixels(baseTexture, entry);
            if (basePixels == null) return null;
            var size = ResolveMainBakeSize(source, baseTexture);
            var pixels = Resample(basePixels, baseTexture.width, baseTexture.height, size.x, size.y);
            for (var i = 0; i < pixels.Length; i++) pixels[i] *= baseColor;
            var key = TextureKey(baseTexture) + "|" + baseColor;
            if (HasMainLayer(source, "_Main2nd")) { ApplyMainLayer(source, "_Main2nd", pixels, size.x, size.y, entry); key += "|" + MainLayerKey(source, "_Main2nd"); }
            if (HasMainLayer(source, "_Main3rd")) { ApplyMainLayer(source, "_Main3rd", pixels, size.x, size.y, entry); key += "|" + MainLayerKey(source, "_Main3rd"); }
            ApplyAlphaMask(source, pixels, size.x, size.y, entry, ref key);
            key += "|resolution|" + size.x + "x" + size.y;
            if (size.x != baseTexture.width || size.y != baseTexture.height)
                entry.Messages.Add(NTL10n.F("Baked Main layers at {0}x{1}, matching the largest participating source texture (Main 1st was {2}x{3}).", size.x, size.y, baseTexture.width, baseTexture.height));
            return WritePng(pixels, size.x, size.y, outputFolder, CachedName(name, "mainlayers", key), true, baseTexture);
        }

        private static Vector2Int ResolveMainBakeSize(Material source, Texture baseTexture)
        {
            var textures = new List<Texture>
            {
                baseTexture,
                GetTexture(source, "_AlphaMask"),
                GetTexture(source, "_Main2ndTex"), GetTexture(source, "_Main2ndBlendMask"),
                GetTexture(source, "_Main3rdTex"), GetTexture(source, "_Main3rdBlendMask")
            };
            var width = 1;
            var height = 1;
            foreach (var texture in textures)
            {
                if (texture == null) continue;
                width = Mathf.Max(width, texture.width);
                height = Mathf.Max(height, texture.height);
            }
            return new Vector2Int(width, height);
        }

        private static Color[] Resample(Color[] source, int sourceWidth, int sourceHeight, int width, int height)
        {
            if (sourceWidth == width && sourceHeight == height) return source;
            var output = new Color[width * height];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                output[y * width + x] = SampleRepeatBilinear(source, sourceWidth, sourceHeight, new Vector2((x + .5f) / width, (y + .5f) / height));
            return output;
        }

        // Keep this rule identical to the converter's pre-bake decision. lilToon retains
        // disabled-layer color and texture fields, so only the explicit toggle indicates
        // whether the layer is visible in the source material.
        private static bool HasMainLayer(Material material, string prefix)
        {
            return IsEnabled(material, "_Use" + prefix.Substring(1) + "Tex");
        }

        private static bool IsWhite(Color color)
        {
            return Mathf.Abs(color.r - 1f) < .0001f && Mathf.Abs(color.g - 1f) < .0001f &&
                   Mathf.Abs(color.b - 1f) < .0001f && Mathf.Abs(color.a - 1f) < .0001f;
        }

        private static void ApplyAlphaMask(Material source, Color[] destination, int width, int height, ConversionEntry entry, ref string key)
        {
            var mask = GetTexture(source, "_AlphaMask");
            var mode = Mathf.RoundToInt(GetFloat(source, "_AlphaMaskMode", 0f));
            if (mask == null || mode == 0) return;
            var pixels = ReadPixels(mask, entry);
            if (pixels == null) { entry.Warn(NTL10n.L("WARNING: lilToon Alpha Mask could not be read and was not baked.")); return; }
            var scale = source.GetTextureScale("_AlphaMask");
            var offset = source.GetTextureOffset("_AlphaMask");
            var multiplier = GetFloat(source, "_AlphaMaskScale", 1f);
            var value = GetFloat(source, "_AlphaMaskValue", 0f);
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
                var alpha = Mathf.Clamp01(SampleRepeatBilinear(pixels, mask.width, mask.height, new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y)).r * multiplier + value);
                var index = y * width + x;
                var color = destination[index];
                if (mode == 1) color.a = alpha;
                else if (mode == 2) color.a *= alpha;
                else if (mode == 3) color.a = Mathf.Clamp01(color.a + alpha);
                else if (mode == 4) color.a = Mathf.Clamp01(color.a - alpha);
                else { entry.Warn(NTL10n.F("WARNING: lilToon Alpha Mask mode {0} is unsupported and was not baked.", mode)); break; }
                destination[index] = color;
            }
            key += "|alpha|" + TextureKey(mask) + "|" + mode + "|" + multiplier + "|" + value + "|" + scale + "|" + offset;
        }

        private static void ApplyMainLayer(Material source, string prefix, Color[] destination, int width, int height, ConversionEntry entry)
        {
            var texture = GetTexture(source, prefix + "Tex");
            var texturePixels = texture == null ? null : ReadPixels(texture, entry);
            if (texture != null && texturePixels == null) { entry.Warn(NTL10n.F("WARNING: {0} layer was not baked because its texture could not be read.", prefix)); return; }
            var blendMask = GetTexture(source, prefix + "BlendMask");
            var maskPixels = blendMask == null ? null : ReadPixels(blendMask, entry);
            if (blendMask != null && maskPixels == null) { entry.Warn(NTL10n.F("WARNING: {0} blend mask could not be read; the layer was not baked.", prefix)); return; }
            var uvMode = Mathf.RoundToInt(GetFloat(source, prefix + "Tex_UVMode", 0f));
            if (uvMode != 0) entry.Warn(NTL10n.F("WARNING: {0} uses UV mode {1}; its bake is approximated with UV0.", prefix, uvMode));
            var scale = source.GetTextureScale(prefix + "Tex");
            var offset = source.GetTextureOffset(prefix + "Tex");
            var maskScale = blendMask == null ? Vector2.one : source.GetTextureScale(prefix + "BlendMask");
            var maskOffset = blendMask == null ? Vector2.zero : source.GetTextureOffset(prefix + "BlendMask");
            var color = GetColor(source, prefix == "_Main2nd" ? "_Color2nd" : "_Color3rd", Color.white);
            var blendMode = Mathf.RoundToInt(GetFloat(source, prefix + "TexBlendMode", 0f));
            var alphaMode = Mathf.RoundToInt(GetFloat(source, prefix + "TexAlphaMode", 0f));
            if (alphaMode < 0 || alphaMode > 4) entry.Warn(NTL10n.F("WARNING: {0} Alpha Mode {1} is unsupported and was ignored.", prefix, alphaMode));
            if (IsEnabled(source, prefix + "TexIsDecal") || IsEnabled(source, prefix + "TexIsLeftOnly") || IsEnabled(source, prefix + "TexIsRightOnly"))
                entry.Warn(NTL10n.F("WARNING: {0} decal/mirror options are not reproduced by the Base Texture bake; only its UV0 texture, color, alpha and blend mask are baked.", prefix));
            if (GetTexture(source, prefix + "DissolveMask") != null || GetTexture(source, prefix + "DissolveNoiseMask") != null)
                entry.Warn(NTL10n.F("WARNING: {0} dissolve settings are not reproduced by the Base Texture bake.", prefix));
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
                var layer = texturePixels == null ? Color.white : SampleRepeatBilinear(texturePixels, texture.width, texture.height, new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y));
                layer *= color;
                var alpha = layer.a * (maskPixels == null ? 1f : SampleRepeatBilinear(maskPixels, blendMask.width, blendMask.height, new Vector2(uv.x * maskScale.x + maskOffset.x, uv.y * maskScale.y + maskOffset.y)).r);
                var index = y * width + x;
                var destinationColor = destination[index];
                if (alphaMode == 1) { destinationColor.a = alpha; alpha = 1f; }
                else if (alphaMode == 2) { destinationColor.a *= alpha; alpha = 1f; }
                else if (alphaMode == 3) { destinationColor.a = Mathf.Clamp01(destinationColor.a + alpha); alpha = 1f; }
                else if (alphaMode == 4) { destinationColor.a = Mathf.Clamp01(destinationColor.a - alpha); alpha = 1f; }
                alpha *= Mathf.Clamp01(GetFloat(source, prefix + "EnableLighting", 1f));
                destination[index] = Blend(destinationColor, layer, alpha, blendMode);
            }
            entry.Messages.Add(NTL10n.F("Baked {0} texture, color, blend mask and blend mode into the NonToon base texture.", prefix));
        }

        private static string MainLayerKey(Material source, string prefix)
        {
            var texture = GetTexture(source, prefix + "Tex");
            var mask = GetTexture(source, prefix + "BlendMask");
            var colorProperty = prefix == "_Main2nd" ? "_Color2nd" : "_Color3rd";
            return TextureKey(texture) + "|" + TextureKey(mask) + "|" + GetColor(source, colorProperty, Color.white) + "|" + GetFloat(source, prefix + "EnableLighting", 1f) + "|" + GetFloat(source, prefix + "TexBlendMode", 0f) + "|" + GetFloat(source, prefix + "TexAlphaMode", 0f) + "|" + GetFloat(source, prefix + "Tex_UVMode", 0f) + "|" + TextureKey(GetTexture(source, prefix + "DissolveMask")) + "|" + TextureKey(GetTexture(source, prefix + "DissolveNoiseMask")) + "|" + source.GetTextureScale(prefix + "Tex") + "|" + source.GetTextureOffset(prefix + "Tex") + "|" + source.GetTextureScale(prefix + "BlendMask") + "|" + source.GetTextureOffset(prefix + "BlendMask");
        }

        internal static Color Blend(Color destination, Color source, float alpha, int blendMode)
        {
            var preservedAlpha = destination.a;
            Color result;
            if (blendMode == 1) result = destination + source;
            else if (blendMode == 2) result = new Color(destination.r + source.r - destination.r * source.r, destination.g + source.g - destination.g * source.g, destination.b + source.b - destination.b * source.b, destination.a);
            else if (blendMode == 3) result = destination * source;
            else result = source;
            var blended = Color.Lerp(destination, result, Mathf.Clamp01(alpha));
            // Main 2nd/3rd layer opacity controls RGB blending in lilToon. It must not alter
            // the material alpha unless the layer's explicit Alpha Mode changed it above.
            blended.a = preservedAlpha;
            return blended;
        }

        private static Color SampleRepeat(Color[] pixels, int width, int height, Vector2 uv)
        {
            uv.x -= Mathf.Floor(uv.x);
            uv.y -= Mathf.Floor(uv.y);
            var x = Mathf.Clamp(Mathf.FloorToInt(uv.x * width), 0, width - 1);
            var y = Mathf.Clamp(Mathf.FloorToInt(uv.y * height), 0, height - 1);
            return pixels[y * width + x];
        }

        // Main layers are often baked to a canvas larger than Main 1st. Bilinear sampling
        // avoids turning the lower-resolution inputs into visible block pixels while their
        // UV coverage remains unchanged.
        private static Color SampleRepeatBilinear(Color[] pixels, int width, int height, Vector2 uv)
        {
            uv.x -= Mathf.Floor(uv.x);
            uv.y -= Mathf.Floor(uv.y);
            var x = uv.x * width - .5f;
            var y = uv.y * height - .5f;
            var x0 = Mathf.FloorToInt(x);
            var y0 = Mathf.FloorToInt(y);
            var x1 = x0 + 1;
            var y1 = y0 + 1;
            x0 = (x0 % width + width) % width;
            x1 = (x1 % width + width) % width;
            y0 = (y0 % height + height) % height;
            y1 = (y1 % height + height) % height;
            var tx = x - Mathf.Floor(x);
            var ty = y - Mathf.Floor(y);
            return Color.Lerp(Color.Lerp(pixels[y0 * width + x0], pixels[y0 * width + x1], tx), Color.Lerp(pixels[y1 * width + x0], pixels[y1 * width + x1], tx), ty);
        }

        internal static Texture2D CombineMasks(Texture[] masks, string outputFolder, string name, ConversionEntry entry)
        {
            var reference = masks.FirstOrDefault(t => t != null);
            if (reference == null) return null;
            var key = string.Join("|", masks.Select(texture => texture == null ? "none" : TextureKey(texture)));
            var editableMask = CreateShaderCoreMask(masks, reference, outputFolder, CachedName(name, "mask", key), entry);
            if (editableMask != null)
            {
                entry.Messages.Add(NTL10n.L("Generated editable Shader Core Shared Mask (.scmask). Use the Edit button in the NonToon material Inspector to change its RGBA sources."));
                return editableMask;
            }
            entry.Warn(NTL10n.L("WARNING: Shader Core .scmask generation was unavailable; generated a PNG Shared Mask instead."));
            var channels = masks.Select(t => t == null ? null : ReadPixels(t, entry)).ToArray();
            for (var i = 0; i < channels.Length; i++)
                if (masks[i] != null && channels[i] == null) return null;
            var output = new Color[reference.width * reference.height];
            for (var y = 0; y < reference.height; y++) for (var x = 0; x < reference.width; x++)
            {
                var index = y * reference.width + x;
                output[index] = new Color(Channel(channels[0], masks[0], x, y, reference), Channel(channels[1], masks[1], x, y, reference), Channel(channels[2], masks[2], x, y, reference), Channel(channels[3], masks[3], x, y, reference));
            }
            return WritePng(output, reference.width, reference.height, outputFolder, CachedName(name, "mask", key), false, reference);
        }

        // A .scmask is a Shader Core ScriptedImporter asset. The importer serializes its
        // per-channel source textures in the .meta file, then exposes an Edit button from
        // NonToon's [SCMask] material field. Use SerializedObject intentionally so this
        // package has no compile-time dependency on Shader Core's internal editor classes.
        private static Texture2D CreateShaderCoreMask(Texture[] masks, Texture reference, string outputFolder, string name, ConversionEntry entry)
        {
            if (masks == null || masks.Length != 4 || reference == null) return null;
            var path = outputFolder + "/" + name + ".scmask";
            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (cached != null) return cached;
            File.WriteAllText(path, string.Empty);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path);
            var serialized = importer == null ? null : new SerializedObject(importer);
            if (serialized == null || serialized.FindProperty("R") == null)
            {
                AssetDatabase.DeleteAsset(path);
                return null;
            }
            serialized.FindProperty("width").intValue = reference.width;
            serialized.FindProperty("height").intValue = reference.height;
            var channelNames = new[] { "R", "G", "B", "A" };
            for (var i = 0; i < channelNames.Length; i++)
            {
                var channel = serialized.FindProperty(channelNames[i]);
                var texture = masks[i] as Texture2D;
                if (masks[i] != null && texture == null)
                {
                    entry.Warn(NTL10n.F("WARNING: Shared Mask {0} source is not a Texture2D and cannot be stored in .scmask; it uses white instead.", channelNames[i]));
                    texture = null;
                }
                channel.FindPropertyRelative("tex").objectReferenceValue = texture;
                channel.FindPropertyRelative("mode").enumValueIndex = 0; // R
                channel.FindPropertyRelative("fallbackValue").floatValue = 1f;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Channel(Color[] pixels, Texture texture, int x, int y, Texture reference)
        {
            if (pixels == null || texture == null) return 1f;
            var sourceX = Mathf.Clamp(Mathf.FloorToInt((float)x / reference.width * texture.width), 0, texture.width - 1);
            var sourceY = Mathf.Clamp(Mathf.FloorToInt((float)y / reference.height * texture.height), 0, texture.height - 1);
            return pixels[sourceY * texture.width + sourceX].r;
        }


        private static Color[] ReadPixels(Texture texture, ConversionEntry entry)
        {
            if (!(texture is Texture2D texture2D)) { entry.Warn(NTL10n.F("WARNING: Texture '{0}' is not a Texture2D and cannot be baked.", texture.name)); return null; }
            var path = AssetDatabase.GetAssetPath(texture2D);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            var restoreReadable = importer != null && !importer.isReadable;
            var restoreCrunch = importer != null && importer.crunchedCompression;
            var originalCompression = importer == null ? TextureImporterCompression.Uncompressed : importer.textureCompression;
            var originalCrunchedCompression = importer != null && importer.crunchedCompression;
            try
            {
                if (restoreReadable || restoreCrunch)
                {
                    Undo.RecordObject(importer, "Prepare texture for NonToon conversion");
                    if (restoreReadable) importer.isReadable = true;
                    // Texture2D.GetPixels cannot decode Crunch textures, even when readable.
                    // Temporarily use Unity's ordinary uncompressed import, read the pixels,
                    // and restore every source importer option in finally.
                    if (restoreCrunch)
                    {
                        importer.crunchedCompression = false;
                        importer.textureCompression = TextureImporterCompression.Uncompressed;
                        entry.Messages.Add(NTL10n.F("Temporarily decompressed Crunch texture '{0}' to bake it; its importer settings will be restored.", texture.name));
                    }
                    importer.SaveAndReimport();
                }
                return texture2D.GetPixels();
            }
            catch (Exception exception)
            {
                entry.Warn(NTL10n.F("WARNING: Could not read texture '{0}': {1}", texture.name, exception.Message));
                return null;
            }
            finally
            {
                if ((restoreReadable || restoreCrunch) && importer != null)
                {
                    Undo.RecordObject(importer, "Restore texture importer after NonToon conversion");
                    if (restoreReadable) importer.isReadable = false;
                    if (restoreCrunch)
                    {
                        importer.textureCompression = originalCompression;
                        importer.crunchedCompression = originalCrunchedCompression;
                    }
                    importer.SaveAndReimport();
                }
            }
        }

        private static string TextureKey(Texture texture)
        {
            if (texture == null) return "none";
            var path = AssetDatabase.GetAssetPath(texture);
            return string.IsNullOrEmpty(path) ? texture.GetInstanceID().ToString() : AssetDatabase.AssetPathToGUID(path);
        }

        private static string CachedName(string name, string kind, string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in kind + "|" + value) { hash ^= character; hash *= 16777619; }
                return name + "_" + hash.ToString("X8");
            }
        }

        private static Texture2D WritePng(Color[] pixels, int width, int height, string outputFolder, string name, bool sRgb, Texture sourceTexture)
        {
            var path = outputFolder + "/" + name + ".png";
            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (cached != null) return cached;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, !sRgb);
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            var sourceImporter = sourceTexture == null ? null : AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceTexture)) as TextureImporter;
            if (importer != null)
            {
                if (sourceImporter != null)
                {
                    var settings = new TextureImporterSettings();
                    sourceImporter.ReadTextureSettings(settings);
                    importer.SetTextureSettings(settings);
                    importer.textureCompression = sourceImporter.textureCompression;
                    importer.compressionQuality = sourceImporter.compressionQuality;
                    importer.crunchedCompression = sourceImporter.crunchedCompression;
                    importer.maxTextureSize = sourceImporter.maxTextureSize;
                    importer.SetPlatformTextureSettings(sourceImporter.GetDefaultPlatformTextureSettings());
                }
                importer.sRGBTexture = sRgb;
                // [NT-FEAT 28] 上面把**源贴图的 TextureImporterSettings 整套抄了过来**
                // （`importer.SetTextureSettings(settings)`），而源贴图很可能是
                // `alphaSource = None`（作者主动关掉了 alpha 导入）或来自 JPEG ⇒
                // 我们烘进去的透明遮罩 / 图层 alpha 会**在导入时被静默丢弃**，
                // 表现就是"透明遮罩明明烘了、材质却还是实心的"。
                // 只在 PNG 真的带非不透明 alpha 时才钉成 FromInput，
                // 免得给纯不透明贴图平白加一条无用的 alpha 通道。
                // （对照实现：LilToNonToonSwitcher / NonToonTextureBaker.cs:321-323。）
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a < .999f) { importer.alphaSource = TextureImporterAlphaSource.FromInput; break; }
                }
                importer.isReadable = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }

    internal static class GradientAssetBaker
    {
        private const int Width = 256;

        internal static Color[] CreateLilToonShadowRamp(
            Color shadow, Color secondShadow, Color thirdShadow,
            float shadowBorder, float shadowBlur, float secondBorder, float secondBlur,
            float thirdBorder, float thirdBlur, float strength)
        {
            var ramp = new Color[Width];
            for (var i = 0; i < Width; i++)
            {
                var position = i / (float)(Width - 1);
                // lilToon layers its 2nd/3rd shadow over the 1st shadow. The transition
                // is lilTooningScale(value, Border, Blur), whose bounds are Border ± Blur/2.
                // Shadow 1 is the base shade over the entire unlit-to-lit range, not a
                // flat background. Reproduce its Border/Blur transition from shadow to light.
                var shadow1 = Color.Lerp(Color.white, Opaque(shadow), Mathf.Clamp01(strength));
                var color = Color.Lerp(shadow1, Color.white, Tooning(position, shadowBorder, shadowBlur));
                var secondLayer = Mathf.Clamp01(secondShadow.a) * (1f - Tooning(position, secondBorder, secondBlur));
                var thirdLayer = Mathf.Clamp01(thirdShadow.a) * (1f - Tooning(position, thirdBorder, thirdBlur));
                color = Color.Lerp(color, Opaque(secondShadow), secondLayer);
                color = Color.Lerp(color, Opaque(thirdShadow), thirdLayer);
                ramp[i] = color;
            }
            return ramp;
        }

        internal static Color[] CreateLilToonRimShadeRamp(Color rimShade, float border, float blur, float fresnelPower)
        {
            var ramp = new Color[Width];
            // NonToon samples this ramp by NdotV (low values are the silhouette). lilToon
            // thresholds pow(1-NdotV, FresnelPower), so transform the threshold endpoints.
            var lowRim = Mathf.Clamp01(border + blur * .5f);
            var highRim = Mathf.Clamp01(border - blur * .5f);
            var inversePower = 1f / Mathf.Max(.01f, fresnelPower);
            var start = 1f - Mathf.Pow(lowRim, inversePower);
            var end = 1f - Mathf.Pow(highRim, inversePower);
            var rimColor = Color.Lerp(Color.white, Opaque(rimShade), Mathf.Clamp01(rimShade.a));
            for (var i = 0; i < Width; i++)
            {
                var position = i / (float)(Width - 1);
                ramp[i] = Color.Lerp(rimColor, Color.white, Mathf.InverseLerp(start, end, position));
            }
            return ramp;
        }

        internal static Color[] CreateHairSpecularRamp(float tangentWidth, float shift, float strength)
        {
            var ramp = new Color[Width];
            // Both shaders use a tangent-space scalar. lilToon's distribution has more
            // parameters, so preserve the primary peak's position, width and intensity.
            var center = Mathf.Clamp01(.5f + shift * .05f);
            var halfWidth = Mathf.Lerp(.04f, .5f, Mathf.Clamp01(tangentWidth / 10f));
            var intensity = Mathf.Clamp01(strength / 10f);
            for (var i = 0; i < Width; i++)
            {
                var position = i / (float)(Width - 1);
                var normalized = (position - center) / halfWidth;
                var value = Mathf.Exp(-normalized * normalized * 4f) * intensity;
                ramp[i] = new Color(value, value, value, 1f);
            }
            return ramp;
        }

        private static Color Opaque(Color color) { return new Color(color.r, color.g, color.b, 1f); }

        private static float Tooning(float value, float border, float blur)
        {
            var minimum = Mathf.Clamp01(border - blur * .5f);
            var maximum = Mathf.Clamp01(border + blur * .5f);
            return maximum <= minimum ? (value >= border ? 1f : 0f) : Mathf.Clamp01((value - minimum) / (maximum - minimum));
        }

        internal static Texture2DArray Write(IList<Color[]> ramps, string outputFolder, string name)
        {
            if (ramps == null || ramps.Count == 0) return null;
            // Shader Core exposes a dedicated .scgradients importer. Creating this format rather than a raw
            // Texture2DArray .asset lets the generated ramps remain editable in NonToon's material inspector.
            var path = outputFolder + "/" + name + "_" + RampHash(ramps).ToString("X8") + ".scgradients";
            var cached = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (cached != null) return cached;
            File.WriteAllText(path, string.Empty);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path);
            var serializedImporter = new SerializedObject(importer);
            var gradients = serializedImporter.FindProperty("gradients");
            gradients.arraySize = ramps.Count;
            for (var slice = 0; slice < ramps.Count; slice++) gradients.GetArrayElementAtIndex(slice).gradientValue = ToGradient(ramps[slice]);
            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        }

        private static uint RampHash(IList<Color[]> ramps)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ramp in ramps)
                foreach (var color in ramp)
                {
                    var c = (Color32)color;
                    hash = (hash ^ c.r) * 16777619;
                    hash = (hash ^ c.g) * 16777619;
                    hash = (hash ^ c.b) * 16777619;
                    hash = (hash ^ c.a) * 16777619;
                }
                return hash;
            }
        }

        private static Gradient ToGradient(Color[] ramp)
        {
            // Unity Gradients have an eight-key limit. Pick the points with the greatest
            // interpolation error instead of fixed positions: Border/Blur endpoints become
            // keys, so lilToon's narrow transitions survive in editable .scgradients assets.
            var samples = SelectGradientSamples(ramp, 8);
            var colorKeys = samples.Select(index => new GradientColorKey(ramp[index], index / (float)(ramp.Length - 1))).ToArray();
            var alphaKeys = samples.Select(index => new GradientAlphaKey(ramp[index].a, index / (float)(ramp.Length - 1))).ToArray();
            var gradient = new Gradient();
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        private static List<int> SelectGradientSamples(Color[] ramp, int maximumKeys)
        {
            var samples = new List<int> { 0, ramp.Length - 1 };
            while (samples.Count < maximumKeys)
            {
                samples.Sort();
                var bestIndex = -1;
                var bestError = 0f;
                for (var segment = 0; segment < samples.Count - 1; segment++)
                {
                    var left = samples[segment];
                    var right = samples[segment + 1];
                    if (right - left < 2) continue;
                    for (var index = left + 1; index < right; index++)
                    {
                        var linear = Color.Lerp(ramp[left], ramp[right], (index - left) / (float)(right - left));
                        var delta = ramp[index] - linear;
                        var error = delta.r * delta.r + delta.g * delta.g + delta.b * delta.b + delta.a * delta.a;
                        if (error <= bestError) continue;
                        bestError = error;
                        bestIndex = index;
                    }
                }
                if (bestIndex < 0) break;
                samples.Add(bestIndex);
            }
            samples.Sort();
            return samples;
        }
    }
}
