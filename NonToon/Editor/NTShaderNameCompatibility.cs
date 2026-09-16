using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

namespace jp.lilxyzw.nontoon
{
    // ShaderCore's name parser only accepts [\w\s\/], so the fork's hyphenated
    // names otherwise fail before shader generation. Extend only that known
    // parser in the editor's Mono domain; do not modify the dependency on disk.
    // OnPreprocessAsset also covers asset import worker domains. If upstream
    // already supports these names, leave its parser untouched.
    [InitializeOnLoad]
    internal sealed class NTShaderNameCompatibility : AssetPostprocessor
    {
        private const string OriginalPattern = @"^\s*Shader\s+""([\w\s\/]+)""";
        private const string CompatiblePattern = @"^\s*Shader\s+""([\w\s\/\-]+)""";

        static NTShaderNameCompatibility()
        {
            EnsureSupported();
        }

        private void OnPreprocessAsset()
        {
            if (assetPath.EndsWith(".scshader", StringComparison.OrdinalIgnoreCase))
                EnsureSupported();
        }

        internal static void EnsureSupported()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("jp.lilxyzw.shadercore.ProjectSettings", false))
                .FirstOrDefault(t => t != null);
            var field = type?.GetField("REG_SHADERNAME", BindingFlags.NonPublic | BindingFlags.Static);
            var parser = field?.GetValue(null) as Regex;
            if (parser == null)
                throw new NotSupportedException("nontoon-fork: ShaderCore shader-name parser was not found.");
            if (AcceptsForkNames(parser)) return;
            if (parser.ToString() != OriginalPattern)
                throw new NotSupportedException("nontoon-fork: unrecognized ShaderCore shader-name parser: " + parser);

            field.SetValue(null, new Regex(CompatiblePattern, parser.Options, parser.MatchTimeout));
            if (!AcceptsForkNames((Regex)field.GetValue(null)))
                throw new NotSupportedException("nontoon-fork: ShaderCore still rejects fork shader names.");
        }

        private static bool AcceptsForkNames(Regex parser)
        {
            return new[] { "nontoon-fork", "nontoon-fork-fur", "nontoon-fork-twopass" }.All(name =>
            {
                var match = parser.Match("Shader \"" + name + "\"");
                return match.Success && match.Groups[1].Value == name;
            });
        }
    }
}
