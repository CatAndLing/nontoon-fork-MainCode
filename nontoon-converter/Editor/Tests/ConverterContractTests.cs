#if LILTOON_NONTOON_CONVERTER_TESTS
using NUnit.Framework;
using UnityEngine;

namespace LilToonToNonToonConverter.Tests
{
    public sealed class ConverterContractTests
    {
        [Test]
        public void ConverterTargetsThePinnedNonToonRelease()
        {
            Assert.That(ConverterConstants.ExpectedNonToonVersion, Is.EqualTo("0.1.3"));
            Assert.That(ConverterConstants.ExpectedShaderCoreVersion, Is.EqualTo("0.1.5"));
            Assert.That(ConverterConstants.NonToonShaderName, Is.EqualTo("NonToon"));
            Assert.That(ConverterConstants.NonToonFurShaderName, Is.EqualTo("NonToonFur"));
        }

        [Test]
        public void NonLilToonMaterialIsNotAccepted()
        {
            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) Assert.Ignore("No built-in test shader available.");
            var material = new Material(shader);
            Assert.That(LilToonMaterialConverter.IsLilToon(material), Is.False);
            Object.DestroyImmediate(material);
        }

        [Test]
        public void ReportIncludesEachSeverity()
        {
            var report = new ConversionReport();
            report.Entries.Add(new ConversionEntry { SourcePath = "Assets/Test.mat", Severity = ConversionSeverity.Warning });
            var text = report.ToText();
            // [NT-L10N] 报告文本会随语言变化，断言也跟着本地化后的模板走。
            Assert.That(text, Does.Contain(NTL10n.F("Success: {0}, Warnings: {1}, Unsupported: {2}, Errors: {3}", 0, 1, 0, 0)));
            Assert.That(text, Does.Contain("Assets/Test.mat"));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void SpecularRequiresBothReflectionToggles(bool reflection, bool applySpecular, bool expected)
        {
            Assert.That(LilToonMaterialConverter.ShouldCopySpecular(reflection, applySpecular), Is.EqualTo(expected));
        }

        [Test]
        public void EmissionStrengthIncludesColorAlphaAndBlend()
        {
            Assert.That(LilToonMaterialConverter.CalculateEmissionStrength(new Color(1f, .5f, .25f, 0f), 1f), Is.EqualTo(0f));
            Assert.That(LilToonMaterialConverter.CalculateEmissionStrength(new Color(2f, .5f, .25f, .5f), .25f), Is.EqualTo(.25f).Within(.0001f));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void MainLayerBlendPreservesDestinationAlpha(int blendMode)
        {
            var destination = new Color(.2f, .3f, .4f, .8f);
            var source = new Color(.9f, .7f, .5f, .1f);
            Assert.That(TextureBaker.Blend(destination, source, .6f, blendMode).a, Is.EqualTo(.8f).Within(.0001f));
        }
    }
}
#endif
