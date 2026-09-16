using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace jp.lilxyzw.nontoon
{
    // [NT-L10N 2] 给 ShaderCore 补一份简体中文语言文件。
    //
    // 为什么必须由我们来做：
    //   ShaderCore 的 L10n.L() 对 `__` 开头的内置键是这样取值的 ——
    //       coreTransration ??= SCModule.LoadLocalizationDirect(language, "Packages/jp.lilxyzw.shadercore/lang/");
    //       if (key.StartsWith("__") && coreTransration.TryGetValue(key, out var v)) return v;
    //   而 LoadLocalizationDirect 在找不到 <语言>.po 时会**回落到 en-US.po**。
    //   于是：ShaderCore 0.1.12 起 lang/ 里只有 en-US.po / ja-JP.po（没有 zh-Hans.po），
    //   选中「简体中文」时这些 `__` 键会在 core 表里命中**英文**并直接返回 ——
    //   我们在自己包里写多少 po 都没用（那个分支轮不到）。
    //   结果就是材质面板里 Main / Select Modules / Textrue / Shared Mask / Shared Gradients /
    //   Normal Map / Roughness / Cutoff / Create Texture 这些**永远是英文**。
    //
    // 所以这里做一件事：如果 ShaderCore 的 lang/ 下没有 zh-Hans.po，就替它写一份。
    //   · 只在**缺失**时写，绝不覆盖（上游哪天自己带上了，我们立刻让位）
    //   · 找不到 ShaderCore 的包目录（比如它是放在 Assets 里的老装法）就安静跳过
    //   · ShaderCore 的 AssetPostprocessor 会监听 .po 变化并自动刷新界面，所以不用手动重载
    //
    // 文案来源：与 ShaderCore 0.1.9 曾附带的社区简体中文保持一致（`__Main` 去掉了其中的人名后缀）。
    internal static class NTShaderCoreLocalization
    {
        private const string ShaderCorePackage = "Packages/jp.lilxyzw.shadercore";
        private const string LangFile = ShaderCorePackage + "/lang/zh-Hans.po";
        private const string LogTag = "[NonToon] ";

        private const string ZhHans = @"msgid """"
msgstr """"
""MIME-Version: 1.0\n""
""Content-Type: text/plain; charset=UTF-8\n""
""Content-Transfer-Encoding: 8bit\n""
""Language: zh-Hans\n""

# [NT-L10N] ShaderCore 内置键（__*）的简体中文。
# 这些键只会在 Packages/jp.lilxyzw.shadercore/lang/ 里查表，所以必须放在这里；
# 由 NonToon (Fork) 在缺失时补齐，上游自带时不会被覆盖。

msgid ""__Main""
msgstr ""主要""

msgid ""__RenderingMode""
msgstr ""渲染模式""

msgid ""__Position""
msgstr ""坐标""

msgid ""__Color""
msgstr ""颜色""

msgid ""__Alpha""
msgstr ""透明度""

msgid ""__Strength""
msgstr ""强度""

msgid ""__Range""
msgstr ""范围""

msgid ""__UV""
msgstr ""UV""

msgid ""__Tiling""
msgstr ""平铺""

msgid ""__Offset""
msgstr ""偏移""

msgid ""__Texture""
msgstr ""贴图""

msgid ""__Mask""
msgstr ""遮罩""

msgid ""__NormalMap""
msgstr ""法线贴图""

msgid ""__NormalMapWithRoughness""
msgstr ""使用法线贴图中的粗糙度""

msgid ""__Roughness""
msgstr ""粗糙度""

msgid ""__Cutoff""
msgstr ""裁剪阈值""

msgid ""__SharedMask""
msgstr ""共享遮罩""

msgid ""__SharedGradients""
msgstr ""共享渐变""

msgid ""__GradientIndex""
msgstr ""渐变编号""

msgid ""__MultiplyAlbedo""
msgstr ""正片叠底主颜色""

msgid ""__MaskChannel""
msgstr ""遮罩通道""

msgid ""__Stencil""
msgstr ""模板测试（Stencil）""

msgid ""__Rendering""
msgstr ""渲染""

msgid ""__Disable""
msgstr ""禁用""

msgid ""__Enable""
msgstr ""启用""

msgid ""__RenderQueue""
msgstr ""渲染队列""

msgid ""__EnableGPUInstancing""
msgstr ""启用 GPU 实例化""

msgid ""__DoubleSidedGlobalIllumination""
msgstr ""双面全局光照""

msgid ""__SelectModules""
msgstr ""选择模块""

msgid ""__CreateTexture""
msgstr ""创建贴图""

msgid ""__EditTexture""
msgstr ""编辑贴图""
";

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            // 延迟一拍：包导入过程中写文件容易和 ShaderCore 自己的导入撞上
            EditorApplication.delayCall += () => Ensure(false);
        }

        // 返回 true 表示"当前语言的中文已就位"（原本就有，或本次补上了）
        internal static bool Ensure(bool verbose)
        {
            try
            {
                var coreOk = EnsureShaderCoreCore(verbose);
                var ownOk = EnsureOwnLanguageFiles(verbose);
                return coreOk && ownOk;
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogTag + "中文语言文件补全失败：" + e.Message);
                return false;
            }
        }

        // [NT-L10N 3] 我们**自己**包里的 lang 目录也要补当前语言的文件。
        //
        // 为什么：ShaderCore 取翻译表的文件名是**精确的语言码**——
        //     SCModule.LoadLocalization(language) → <模块目录>/lang/<language>.po
        // 中文 Windows 上 Settings.language 的默认值是 `zh-CN`，而我们只带了 `zh-Hans.po`。
        // 结果：材质面板里 ShaderCore 的核心键是中文（那部分由下面的 EnsureShaderCoreCore 兜住），
        // 但**我们自己写的 100 多条标签全是英文**。实测（干净工程，2022.3.22f1）：
        //     语言 = zh-Hans → 165 条属性里 147 条显示中文
        //     语言 = zh-CN   → 只有 35 条显示中文（= 用户开箱即见的默认状态，也就是"还有很多没汉化"）
        // 所以这里把 zh-Hans.po 复制成 <当前语言>.po（只在缺失时写，绝不覆盖）。
        private static bool EnsureOwnLanguageFiles(bool verbose)
        {
            const string pkg = "Packages/com.catandling.nontoon";
            var current = CurrentLanguage();
            if (string.IsNullOrEmpty(current)) return true;                       // 拿不到语言就什么都不做
            if (!current.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return true;  // 只处理中文
            if (current == "zh-Hans") return true;                                 // 本来就是首选文件名

            var dirs = new List<string> { pkg + "/Shaders/lang" };
            var modules = pkg + "/Shaders/Modules";
            try
            {
                if (Directory.Exists(modules))
                    foreach (var d in Directory.GetDirectories(modules))
                    {
                        var lang = Path.Combine(d, "lang");
                        if (Directory.Exists(lang)) dirs.Add(ToAssetPath(lang));
                    }
            }
            catch (Exception e) { if (verbose) Debug.LogWarning(LogTag + "枚举模块 lang 目录失败：" + e.Message); }

            var allPresent = true;
            var written = 0;
            foreach (var dir in dirs)
            {
                var src = Path.Combine(dir, "zh-Hans.po");
                var dst = Path.Combine(dir, current + ".po");
                if (!File.Exists(src)) continue;
                if (File.Exists(dst)) continue;              // 不覆盖
                allPresent = false;
                try
                {
                    File.WriteAllText(dst, File.ReadAllText(src), new UTF8Encoding(false));
                    AssetDatabase.ImportAsset(ToAssetPath(dst), ImportAssetOptions.ForceUpdate);
                    written++;
                }
                catch (Exception e)
                {
                    // 包目录只读（例如被放在 Library/PackageCache 里）时安静降级：
                    // 面板会退回成"只有核心键是中文"，不会影响其它功能。
                    if (verbose) Debug.LogWarning(LogTag + "写不了 " + dst + "（包目录只读？）：" + e.Message);
                }
            }
            if (written > 0)
                Debug.Log(LogTag + "当前语言是 " + current + "，但本包只带 zh-Hans.po；已为 " + written
                    + " 个 lang 目录补齐 " + current + ".po —— 否则材质面板里本包自己的标签会全是英文。");
            else if (allPresent && verbose) Debug.Log(LogTag + "本包各 lang 目录已有 " + current + ".po，无需处理。");
            return true;
        }

        // 给 ShaderCore 自己的核心键补一份（它住在别的包里，我们没法"发货"带过去）
        private static bool EnsureShaderCoreCore(bool verbose)
        {
            try
            {
                var packageDir = ShaderCorePackage;
                if (!Directory.Exists(packageDir))
                {
                    if (verbose) Debug.Log(LogTag + "找不到 " + ShaderCorePackage + "，跳过 ShaderCore 中文补全。");
                    return false;
                }
                var langDir = Path.Combine(packageDir, "lang");
                if (!Directory.Exists(langDir))
                {
                    // ShaderCore 的 lang 目录本身没有的话，说明装法很特殊，别乱建
                    if (verbose) Debug.Log(LogTag + "ShaderCore 没有 lang 目录，跳过中文补全。");
                    return false;
                }

                // zh-Hans 是首选；另外，中文 Windows 上 ShaderCore 的默认语言是 zh-CN，
                // 所以当前语言只要是 zh 系就一并补上（内容相同，po 的表头不影响查表）。
                var wanted = new List<string> { "zh-Hans" };
                var current = CurrentLanguage();
                if (!string.IsNullOrEmpty(current) && current.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !wanted.Contains(current))
                    wanted.Add(current);

                var allPresent = true;
                foreach (var code in wanted)
                {
                    var target = Path.Combine(langDir, code + ".po");
                    if (File.Exists(target)) continue;   // 不覆盖：上游自带时立刻让位
                    allPresent = false;
                    File.WriteAllText(target, ZhHans, new UTF8Encoding(false));
                    AssetDatabase.ImportAsset(ToAssetPath(target), ImportAssetOptions.ForceUpdate);
                    Debug.Log(LogTag + "ShaderCore 缺少简体中文语言文件（0.1.12 起上游只带 en-US/ja-JP），已补齐："
                        + code + ".po。材质面板里的 Main / 贴图 / 共享遮罩 / 粗糙度 / 裁剪阈值 / 选择模块 等内置项现在会显示中文。");
                }
                if (allPresent && verbose) Debug.Log(LogTag + "ShaderCore 已有中文语言文件，无需处理。");
                return File.Exists(Path.Combine(langDir, "zh-Hans.po"));
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogTag + "补全 ShaderCore 中文语言文件失败：" + e.Message);
                return false;
            }
        }

        // 当前 ShaderCore 选中的语言（Settings 是 internal，只能反射；instance 在泛型基类上，要 FlattenHierarchy）
        private static string CurrentLanguage()
        {
            try
            {
                var type = Type.GetType("jp.lilxyzw.shadercore.Settings, jp.lilxyzw.shadercore");
                if (type == null) return null;
                var instance = type.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)?.GetValue(null);
                if (instance == null) return null;
                return type.GetField("language", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(instance) as string;
            }
            catch { return null; }
        }

        private static string ToAssetPath(string absoluteOrRelative)
        {
            var normalized = absoluteOrRelative.Replace('\\', '/');
            var index = normalized.IndexOf("Packages/", StringComparison.Ordinal);
            return index >= 0 ? normalized.Substring(index) : normalized;
        }

        // 供验证探针使用：检查我们的内嵌文本是否覆盖了某个键
        internal static bool ContainsKey(string key)
        {
            return ZhHans.Contains("msgid \"" + key + "\"");
        }
    }
}
