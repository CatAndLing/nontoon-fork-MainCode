using System.Collections.Generic;
using System.Linq;
using jp.lilxyzw.shadercore;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

#if !UNITY_6000_1_OR_NEWER
using MaterialProperty = jp.lilxyzw.shadercore.MaterialProperty;
#endif

namespace jp.lilxyzw.nontoon
{
    internal class NTRenderingModeElement : PopupField<int>, IMaterialPropertyElement
    {
        public MaterialProperty Property { get; set; }
        public string ModuleID { get; set; }
        public string LocalizedLabel { get; set; }
        private readonly List<int> values = new(){0,1,2};
        private readonly List<string> names = new(){"Opaque", "Cutout", "Transparent"};
        private List<string> localizedNames;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            AttributeActions.AddDrawer("NTRenderingMode", NTRenderingMode);
        }

        private static void NTRenderingMode(SCMaterialEditor editor, MaterialProperty prop, string args, VisualElement container)
        {
            container.Add(new NTRenderingModeElement(prop));
        }

        public NTRenderingModeElement(MaterialProperty property)
        {
            localizedNames = names.Select(n => SCL10n.L(n)).ToList();

            string GetLabel(int v)
            {
                var index = values.IndexOf(v);
                if (index >= 0) return localizedNames[index];
                return "";
            }
            choices = values;
            formatListItemCallback = GetLabel;
            formatSelectedValueCallback = GetLabel;

            ((IMaterialPropertyElement)this).InitializeVisualElement(this, UpdateUI, property);
            SCStyles.ApplyPopupStyle(this);
            style.flexGrow = 0;

            RegisterCallback<SCLocalizeEvent>(e =>
            {
                SCL10n.Load(ModuleID);
                localizedNames = names.Select(n => SCL10n.L(n)).ToList();
                textElement.text = formatSelectedValueCallback(rawValue);
            });
        }

        public override void SetValueWithoutNotify(int newValue)
        {
            if (Property != null && new System.Diagnostics.StackFrame(3, false).GetMethod().ToString() == "Void ChangeValueFromMenu(Int32)")
            {
                Property.intValue = newValue;
                if (newValue == 0)
                {
                    SetValue("_SrcBlend", (int)BlendMode.One);
                    SetValue("_DstBlend", (int)BlendMode.Zero);
                    SetValue("_AlphaToMask", 0);
                }
                else if (newValue == 1)
                {
                    SetValue("_SrcBlend", (int)BlendMode.One);
                    SetValue("_DstBlend", (int)BlendMode.Zero);
                    var _NTDitherTex = MaterialEditor.GetMaterialProperty(Property.targets, "_NTDitherTex");
                    if (!_NTDitherTex.hasMixedValue) SetValue("_AlphaToMask", _NTDitherTex.textureValue != null ? 0 : 1);
                }
                else if (newValue == 2)
                {
                    SetValue("_SrcBlend", (int)BlendMode.SrcAlpha);
                    SetValue("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    SetValue("_AlphaToMask", 0);
                }
                SyncRenderQueue(newValue);
                SCUpdateEvent.Invoke();
            }
            base.SetValueWithoutNotify(newValue);
        }

        /// <summary>[NT-FIX 31] 把材质的 renderQueue 对齐到渲染模式（**幂等**：一致就不写，
        ///   避免仅仅"打开检视面板"就把资产弄脏）。
        ///
        /// 为什么需要它：两个 `.scshader` 的 SubShader **都没有 `Queue` 标签**
        /// （`NonToon.scshader:57-62` / `:224-231` 的 Tags 里没有 Queue）⇒ 队列只能靠
        /// **材质覆盖**（`m_CustomRenderQueue`）来定。而写它的地方过去只有
        /// ① **用户手动拨本下拉框**（下面那个栈帧门控）与 ② 转换器 ——
        /// ⇒ 脚本 / Undo / 复制粘贴 / 第三方工具改过 `_RenderingMode` 之后，队列会**陈旧**。
        /// 实测（2026-09-17）：
        ///   · Opaque 被脚本改成 Transparent ⇒ renderQueue 仍是 2000(Geometry)
        ///     ⇒ 透明件被当**不透明**排序；
        ///   · Transparent 被脚本改回 Opaque ⇒ renderQueue 仍是 3000
        ///     ⇒ 不透明件**留在透明队列**（更糟：最后画还写深度）。
        /// 本方法在 `UpdateUI()` 里也调一次 ⇒ 打开材质面板即**自愈**。
        /// 另有兜底命令 `Tools ▸ NonToon ▸ ⑦ 修复渲染队列（透明排序）`。
        ///
        /// ⚠️ 映射必须与 `nontoon-converter/Editor/NTRenderQueueFix.cs` 的 `QueueFor()` 一致
        ///    （本包不能引用转换器包，所以只能各自维护一份）：
        ///      0 不透明 → -1（用 shader 默认 = Geometry 2000）／1 裁剪 → 2450／2 透明 → 3000
        /// ⚠️ 与 lilToon 是有意分歧：lilToon 在 BiRP/URP 的透明用 **2460**
        ///    （`lilMaterialUtils.cs:112`），2460 落在 AlphaTest 区间。**别改回去。**
        /// ⚠️ `_ZWrite` **故意不在这里动**：lilToon 除 Gem 外一律 `_ZWrite = 1`
        ///    （`lilMaterialUtils.cs:266-276`），"透明就该 ZWrite Off"是错的直觉。</summary>
        private void SyncRenderQueue(int mode)
        {
            if (Property == null || Property.targets == null || Property.targets.Length == 0) return;
            // ⚠️ 本文件**没有** `using UnityEngine;`（只引了 UnityEngine.Rendering / UIElements），
            //    所以这里必须全限定，否则 CS0246 'Material' could not be found。
            if (!(Property.targets[0] is UnityEngine.Material)) return;
            var want = mode == 1 ? 2450 : mode == 2 ? 3000 : -1;
            using var so = new SerializedObject(Property.targets);
            using var q = so.FindProperty("m_CustomRenderQueue");
            if (q == null || q.intValue == want) return;
            // [NT-FIX 32] **自定义队列不许碰。**
            //   转换器自 `[NT-FIX 32]` 起改为**照搬源材质的生效队列**，于是产物上会出现
            //   2460（lilToon 透明）、2900（Gem）、2450、3005（作者手动"最后画"）这类值。
            //   而本方法在 `UpdateUI()` 里会被调用 ⇒ 若不加这道门，**光是打开一次材质面板**
            //   就会把它们统统改回模式默认值，把保真悄悄撤销（自愈比旧 bug 更隐蔽）。
            //   判据：只改写"看起来像我们自己写的模式默认值"的队列；任何别的值都视为
            //   **作者/转换器有意为之**，原样保留。
            if (!IsModeDefaultQueue(q.intValue)) return;
            q.intValue = want;
            so.ApplyModifiedProperties();
        }

        /// <summary>[NT-FIX 32] 这个队列值看起来是不是"由渲染模式推出来的默认值"？
        ///   是 ⇒ 可能是陈旧（脚本/Undo/粘贴改过模式），允许自愈；
        ///   不是（如 2460 / 2900 / 3005）⇒ 一律当成有意设置，别动。</summary>
        private static bool IsModeDefaultQueue(int queue)
        {
            return queue == -1 || queue == 2000 || queue == 2450 || queue == 3000;
        }

        public void UpdateUI()
        {
            if (!Property.hasMixedValue)
            {
                rawValue = Property.intValue;
                if (formatSelectedValueCallback != null) textElement.text = formatSelectedValueCallback(rawValue);
                // [NT-FIX 31] 自愈：显示时就检查队列是否与模式一致（被脚本/Undo/粘贴改过的话就地纠正）
                SyncRenderQueue(rawValue);
            }
        }

        private void SetValue(string name, int value)
        {
            MaterialEditor.GetMaterialProperty(Property.targets, name).floatValue = value;
        }
    }
}
