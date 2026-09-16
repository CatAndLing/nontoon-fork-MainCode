using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LilToonToNonToonConverter
{
    public sealed class LilToonToNonToonConverterWindow : EditorWindow
    {
        internal const string BoothUrl = "https://vrc-levanilla.booth.pm/";
        internal const string GigaFileUrl = "https://gigafile.nu/";

        private enum AvatarUploadIdentity { KeepBlueprintId, DetachBlueprintIdForNewAvatar }

        private DefaultAsset outputFolder;
        private bool overwriteExisting;
        private bool bakeTextures = true;
        private bool cleanPreviousGeneratedOutputs = true;
        private bool replacePrefabReferences = true;
        private bool duplicateHierarchyObjects = true;
        private AvatarUploadIdentity avatarUploadIdentity = AvatarUploadIdentity.KeepBlueprintId;
        private Vector2 scroll;
        private ConversionReport report;

        // [NT-VENDOR] 拖放区：原版窗口只能吃「当前选中」，很多人（包括我们）第一反应是把模型往里拖，
        // 而 IMGUI 窗口默认不接收拖放 → 看起来像"转换器坏了"。这里显式接收拖放。
        private Object[] droppedTargets = new Object[0];
        private bool draggingOver;
        private GUIStyle dropAreaStyle;

        [MenuItem(ConverterConstants.MenuPath)]
        public static void Open() { GetWindow<LilToonToNonToonConverterWindow>("lilToon → NonToon " + ConverterConstants.Version); }

        [MenuItem("Assets/将 lilToon 转换为 NonToon (Convert lilToon to NonToon)", true)]
        private static bool ValidateConvertSelection() { return CollectMaterials(Selection.objects).Any(); }

        [MenuItem("Assets/将 lilToon 转换为 NonToon (Convert lilToon to NonToon)")]
        private static void ConvertSelection() { Open(); GetWindow<LilToonToNonToonConverterWindow>("lilToon → NonToon " + ConverterConstants.Version).Convert(Selection.objects); }

        [MenuItem("GameObject/将 lilToon 转换为 NonToon (Convert lilToon to NonToon)", true, 49)]
        private static bool ValidateConvertHierarchySelection() { return Selection.gameObjects.Any(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any()); }

        [MenuItem("GameObject/将 lilToon 转换为 NonToon (Convert lilToon to NonToon)", false, 49)]
        private static void ConvertHierarchySelection() { Open(); }

        [MenuItem("Tools/lilToon → NonToon 转换器 (Converter)/打开最新调试日志 (Open Latest Debug Log)")]
        internal static void OpenLatestDebugLog()
        {
            var log = FindLatestDebugLog();
            if (string.IsNullOrEmpty(log)) { Debug.LogWarning(NTL10n.L("No lilToon to NonToon debug log was found.")); return; }
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(log);
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        [MenuItem("Tools/lilToon → NonToon 转换器 (Converter)/生成反馈用调试日志 ZIP (Create Debug Log ZIP)")]
        internal static void CreateDebugLogZipForSupport()
        {
            CreateDebugLogZip(true);
        }

        [MenuItem("Tools/lilToon → NonToon 转换器 (Converter)/疑难排查 (Troubleshooting)")]
        internal static void OpenTroubleshooting()
        {
            GetWindow<LilToonToNonToonTroubleshootingWindow>("NonToon 转换 · 疑难排查");
        }

        private void OnGUI()
        {
            HandleDragAndDrop();
            EditorGUILayout.LabelField("lilToon → NonToon 转换", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("适用：Unity 2022.3 / URP。当前 " + NonToonCompatibility.VersionStatus + "。不会修改原 lilToon 材质。", MessageType.Info);
            DrawCompatibility();
            DrawDropArea();
            outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("输出文件夹（可选）", outputFolder, typeof(DefaultAsset), false);
            overwriteExisting = EditorGUILayout.Toggle("覆盖已有的转换材质", overwriteExisting);
            bakeTextures = EditorGUILayout.Toggle("烘焙颜色与共享遮罩", bakeTextures);
            cleanPreviousGeneratedOutputs = EditorGUILayout.Toggle("清理旧的自动编号输出", cleanPreviousGeneratedOutputs);
            replacePrefabReferences = EditorGUILayout.Toggle("替换所选 Prefab 的引用", replacePrefabReferences);
            duplicateHierarchyObjects = EditorGUILayout.Toggle("复制 Hierarchy 对象并禁用原对象", duplicateHierarchyObjects);
            if (duplicateHierarchyObjects)
            {
                var choices = new[] { "更新同一个 avatar（保留 Blueprint ID）", "作为新 avatar 上传（解除 Blueprint ID）" };
                avatarUploadIdentity = (AvatarUploadIdentity)EditorGUILayout.Popup("复制出的 avatar 上传到", (int)avatarUploadIdentity, choices);
            }
            var targets = EffectiveTargets();
            var materials = CollectMaterials(targets).ToList();
            EditorGUILayout.LabelField("检测到的 lilToon 材质", materials.Count.ToString());

            // [NT-VENDOR] 自诊断：转换器以 lilToon 材质为源（读的是 lilToon 的属性）。
            // 工程里没装 lilToon 时，对象上的材质会处于"着色器缺失"状态（Hidden/InternalErrorShader），
            // 于是检出 0、按钮变灰 —— 用户容易误以为"工具坏了/没有转换按钮"。直接说清楚。
            if (!LilToonCompatibility.AnyLilToon)
                EditorGUILayout.HelpBox(
                    "⚠ 没有检测到 lilToon（jp.lilxyzw.liltoon）。\n" +
                    "本转换器以 **lilToon 材质作为源** —— 它读的是 lilToon 的属性，所以必须先把 lilToon 装进这个工程（VPM/ALCOM 里有），否则对象上的材质会显示为着色器缺失，检出数永远是 0。",
                    MessageType.Error);

            using (new EditorGUI.DisabledScope(materials.Count == 0 || !NonToonCompatibility.IsInstalled))
                if (GUILayout.Button(droppedTargets.Length > 0 ? "转换拖入的对象" : "转换所选对象", GUILayout.Height(26))) Convert(targets);
            if (materials.Count == 0)
            {
                if (targets.Length == 0)
                    EditorGUILayout.HelpBox("① 把模型 / 材质 / 文件夹拖到上面的框里；或 ② 在 Project（材质、文件夹、fbx、prefab）或 Hierarchy（模型）里选中，再点下面的按钮。", MessageType.None);
                else
                    EditorGUILayout.HelpBox(
                        "没有检测到 lilToon 材质，所以上面的转换按钮是灰色的（禁用状态），不是没有按钮。\n" +
                        "转换器只处理「着色器名里含 lilToon」的材质。\n" +
                        "· 拖的是 Project 里的 fbx？本窗口会去已打开的场景里找它的实例来取材质；如果场景里没有这个模型的实例，就取不到 —— 请在 Hierarchy 里直接选中场景中的模型再转换。\n" +
                        "· 也可以在 Project 里直接选中材质，或选中材质所在的文件夹。\n" +
                        "实际看到的材质：" + DescribeFoundShaders(targets),
                        MessageType.Warning);
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("支持与问题反馈", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("疑难排查")) OpenTroubleshooting();
                if (GUILayout.Button("发布与咨询页面")) Application.OpenURL(BoothUrl);
            }
            if (GUILayout.Button("把最新日志打包成 ZIP 并打开 GigaFile")) CreateDebugLogZip(true);
            EditorGUILayout.HelpBox("ZIP 不会被自动上传。请把它拖进打开的 GigaFile，再把生成的 URL 在咨询时一并提供。", MessageType.None);
            DrawReport();
        }

        // [NT-VENDOR] 拖放支持：接受模型（Hierarchy 对象，或 Project 里的 fbx / prefab）、材质、文件夹。
        private static bool IsSupportedDropTarget(Object o)
        {
            return o is Material || o is GameObject || o is DefaultAsset;
        }

        // 拖进来的优先；没拖就用当前选中（原行为，完全不变）。
        private Object[] EffectiveTargets()
        {
            return droppedTargets != null && droppedTargets.Length > 0 ? droppedTargets : Selection.objects;
        }

        private void HandleDragAndDrop()
        {
            var e = Event.current;
            if (e.type == EventType.DragUpdated)
            {
                draggingOver = DragAndDrop.objectReferences.Any(IsSupportedDropTarget);
                DragAndDrop.visualMode = draggingOver ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                e.Use();
            }
            else if (e.type == EventType.DragPerform)
            {
                var picked = DragAndDrop.objectReferences.Where(IsSupportedDropTarget).ToArray();
                if (picked.Length > 0)
                {
                    droppedTargets = picked;
                    DragAndDrop.AcceptDrag();
                }
                draggingOver = false;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.DragExited)
            {
                draggingOver = false;
                Repaint();
            }
        }

        private void DrawDropArea()
        {
            if (dropAreaStyle == null)
                dropAreaStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 12,
                    padding = new RectOffset(10, 10, 12, 12)
                };

            string text;
            if (draggingOver) text = "松手即可放入";
            else if (droppedTargets.Length == 0) text = "把模型 / 材质 / 文件夹拖到这里\n（也可以照旧：在 Project 或 Hierarchy 里选中，再点下面的按钮）";
            else text = "已放入：" + string.Join("、", droppedTargets.Take(4).Select(o => o == null ? "(空)" : o.name)) +
                        (droppedTargets.Length > 4 ? " 等 " + droppedTargets.Length + " 个" : "");

            GUILayout.Box(text, dropAreaStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(48));

            if (droppedTargets.Length > 0 && GUILayout.Button("清空，改回用当前选中"))
            {
                droppedTargets = new Object[0];
                GUI.FocusControl(null);
                Repaint();
            }
        }

        internal static string FindLatestDebugLog()
        {
            const string folder = "Assets/NonToonConversionLogs";
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            var guids = AssetDatabase.FindAssets("t:TextAsset", new[] { folder });
            return guids.Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => File.GetLastWriteTime(ToAbsoluteProjectPath(path)))
                .FirstOrDefault();
        }

        internal static string CreateDebugLogZip(bool openUploadPage)
        {
            var logAssetPath = FindLatestDebugLog();
            if (string.IsNullOrEmpty(logAssetPath))
            {
                EditorUtility.DisplayDialog("没有调试日志", "请先执行一次材质转换。转换后即可把最新日志打包成 ZIP。", "OK");
                return null;
            }

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outputFolder = Path.Combine(projectRoot, "LilToonToNonToonDebugPackages");
            Directory.CreateDirectory(outputFolder);
            var zipPath = Path.Combine(outputFolder, "LilToonToNonToon_Debug_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
            using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddFileToZip(archive, ToAbsoluteProjectPath(logAssetPath), Path.GetFileName(logAssetPath));
                AddTextToZip(archive, "Environment.txt", BuildEnvironmentSummary());
                AddTextToZip(archive, "发送说明.txt",
                    "1. 把这个 ZIP 拖进 GigaFile。\r\n" +
                    "2. 复制上传后显示的下载 URL。\r\n" +
                    "3. 在反馈时提供：现象、原材质名、转换后材质名、以及该 URL。\r\n\r\n" +
                    "注意：日志包含 Assets 下的文件名与设置值。发送前请自行确认内容。\r\n");
            }

            EditorGUIUtility.systemCopyBuffer = zipPath;
            EditorUtility.RevealInFinder(zipPath);
            if (openUploadPage) Application.OpenURL(GigaFileUrl);
            Debug.Log("Created lilToon to NonToon support ZIP: " + zipPath);
            EditorUtility.DisplayDialog("调试 ZIP 已生成", "已打开 ZIP 所在位置，并把路径复制到剪贴板。\n\n请把 ZIP 拖进 GigaFile。文件不会被自动外传。", "OK");
            return zipPath;
        }

        internal static void RevealDebugLogFolder()
        {
            var path = ToAbsoluteProjectPath("Assets/NonToonConversionLogs");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static string ToAbsoluteProjectPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void AddFileToZip(ZipArchive archive, string sourcePath, string entryName)
        {
            var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = entry.Open()) input.CopyTo(output);
        }

        private static void AddTextToZip(ZipArchive archive, string entryName, string text)
        {
            var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(text);
        }

        private static string BuildEnvironmentSummary()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            return "lilToon to NonToon Converter support information\r\n" +
                   "Converter: " + ConverterConstants.Version + "\r\n" +
                   "Unity: " + Application.unityVersion + "\r\n" +
                   "Operating system: " + SystemInfo.operatingSystem + "\r\n" +
                   "Color space: " + QualitySettings.activeColorSpace + "\r\n" +
                   "Render pipeline: " + (pipeline == null ? "Built-in" : pipeline.name) + "\r\n" +
                   "NonToon: " + NonToonCompatibility.NonToonVersion + "\r\n" +
                   "Shader Core: " + NonToonCompatibility.ShaderCoreVersion + "\r\n" +
                   "Generated: " + DateTime.Now.ToString("O") + "\r\n";
        }

        private static void DrawCompatibility()
        {
            if (!NonToonCompatibility.IsInstalled)
            {
                EditorGUILayout.HelpBox(NTL10n.F("nontoon-fork and/or nontoon-fork-fur shader was not found. Install NonToon {0} and Shader Core {1} through VPM.", ConverterConstants.ExpectedNonToonVersion, ConverterConstants.ExpectedShaderCoreVersion), MessageType.Error);
                return;
            }
            var missing = NonToonCompatibility.MissingModules();
            if (missing.Count == 0 && NonToonCompatibility.UsesSupportedVersions) EditorGUILayout.HelpBox(NTL10n.L("NonToon core and detected modules are available. ") + NonToonCompatibility.VersionStatus, MessageType.Info);
            else if (missing.Count == 0) EditorGUILayout.HelpBox(NTL10n.L("The shaders are available, but the installed versions are older than the tested target. ") + NonToonCompatibility.VersionStatus, MessageType.Warning);
            else EditorGUILayout.HelpBox(NTL10n.L("Missing NonToon modules: ") + string.Join(", ", missing) + NTL10n.L(". Related features will be reported and skipped."), MessageType.Warning);
        }

        private void Convert(Object[] selection)
        {
            var materials = CollectMaterials(selection).Distinct().ToList();
            report = new ConversionReport();
            if (materials.Count == 0) return;
            var customOutput = outputFolder == null ? null : AssetDatabase.GetAssetPath(outputFolder);
            var hierarchyOutputFolders = GetHierarchyOutputFolders(selection);
            var replacements = new Dictionary<Material, Material>();
            // Every generated texture is explicitly imported by the converter. Suppressing
            // automatic refresh prevents Unity 2022.3 from re-entering the Importer Inspector
            // while a large hierarchy conversion is still writing assets.
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                for (var i = 0; i < materials.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(NTL10n.L("Converting lilToon materials"), materials[i].name, (float)i / materials.Count);
                    var options = new ConversionOptions
                    {
                        OutputFolder = customOutput ?? (hierarchyOutputFolders.TryGetValue(materials[i], out var objectFolder) ? objectFolder : null),
                        OverwriteExisting = overwriteExisting,
                        BakeTextures = bakeTextures,
                        CleanPreviousGeneratedOutputs = cleanPreviousGeneratedOutputs,
                        ReplacePrefabReferences = replacePrefabReferences
                    };
                    var entry = LilToonMaterialConverter.Convert(materials[i], options);
                    report.Entries.Add(entry);
                    if (!string.IsNullOrEmpty(entry.OutputPath)) replacements[materials[i]] = AssetDatabase.LoadAssetAtPath<Material>(entry.OutputPath);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.AllowAutoRefresh();
            }
            AssetDatabase.SaveAssets();
            if (replacePrefabReferences)
            {
                ReplacePrefabMaterials(selection, replacements, report);
                ReplaceHierarchyMaterials(selection, replacements, report, duplicateHierarchyObjects, avatarUploadIdentity);
            }
            WriteReport(new ConversionOptions { OutputFolder = customOutput }, report);
            Repaint();
        }

        // [NT-VENDOR] 重写说明（原版是一个很长的 if / else-if 链，实测在「Project 里的模型资源」
        // 这条路径上会**一个分支都不进**，于是收集到 0 个材质 → 转换按钮被禁用 →
        // 用户看到的现象是"拖进模型之后找不到转换按钮"）。
        // 现在改成：每个对象独立判定 + continue，且**无论是场景对象还是资源**都先从它自己的
        // Renderer 收材质，资源再补「依赖」与「场景实例」两路。判定条件不再互相耦合。
        private static IEnumerable<Material> CollectMaterials(IEnumerable<Object> selection)
        {
            return CollectMaterials(selection, true);
        }

        // [NT-VENDOR] 「检出 0 个材质」时把实际看到的着色器列出来 —— 一眼区分
        // 「工程没装 lilToon」（材质显示为着色器缺失/Standard）与「对象上确实没材质」。
        private static string DescribeFoundShaders(IEnumerable<Object> selection)
        {
            var all = CollectMaterials(selection, false);
            if (all.Count == 0) return "（这些对象上一个材质都没有）";
            var byShader = new Dictionary<string, int>();
            foreach (var material in all)
            {
                var name = material == null || material.shader == null ? "<null>" : material.shader.name;
                if (!byShader.ContainsKey(name)) byShader[name] = 0;
                byShader[name]++;
            }
            return string.Join("、", byShader.Select(p => p.Key + " ×" + p.Value));
        }

        private static List<Material> CollectMaterials(IEnumerable<Object> selection, bool onlyLilToon)
        {
            var materials = new HashSet<Material>();
            foreach (var item in selection.Where(o => o != null))
            {
                if (item is Material material) { materials.Add(material); continue; }

                var path = AssetDatabase.GetAssetPath(item);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { path }))
                        materials.Add(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid)));
                    continue;
                }

                if (item is GameObject gameObject)
                {
                    // ① 对象自己身上（场景对象 / Prefab 实例都适用）
                    foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                        foreach (var rendererMaterial in renderer.sharedMaterials)
                            if (rendererMaterial != null) materials.Add(rendererMaterial);

                    if (!string.IsNullOrEmpty(path))
                    {
                        // ② 资源依赖：Prefab 自带的材质会在这里
                        foreach (var dependency in AssetDatabase.GetDependencies(path, true))
                        {
                            if (!dependency.EndsWith(".mat")) continue;
                            var dependencyMaterial = AssetDatabase.LoadAssetAtPath<Material>(dependency);
                            if (dependencyMaterial != null) materials.Add(dependencyMaterial);
                        }
                        // ③ 场景里这个模型/Prefab 的实例：fbx 往往不带材质，材质挂在实例上
                        CollectMaterialsFromSceneInstances(gameObject, materials);
                    }
                }
            }
            // 立即求值（原来是延迟查询，容易被后续状态变化影响）
            return onlyLilToon ? materials.Where(LilToonMaterialConverter.IsLilToon).ToList() : materials.ToList();
        }

        // 在已加载的场景里找该模型 / Prefab 资源的实例，收它们的 sharedMaterials（含材质覆盖）。
        private static void CollectMaterialsFromSceneInstances(GameObject assetRoot, HashSet<Material> materials)
        {
            var assetPath = AssetDatabase.GetAssetPath(assetRoot);
            if (string.IsNullOrEmpty(assetPath)) return;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>(true))
            {
                if (renderer == null) continue;
                var root = renderer.transform.root.gameObject;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
                if (source == null || AssetDatabase.GetAssetPath(source) != assetPath) continue;
                foreach (var rendererMaterial in renderer.sharedMaterials) if (rendererMaterial != null) materials.Add(rendererMaterial);
            }
        }

        private static Dictionary<Material, string> GetHierarchyOutputFolders(IEnumerable<Object> selection)
        {
            var folders = new Dictionary<Material, string>();
            foreach (var root in selection.OfType<GameObject>().Where(go => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go))))
            {
                var safeName = string.Join("_", root.name.Split(Path.GetInvalidFileNameChars()));
                var folder = "Assets/NonToonConverted/" + (string.IsNullOrWhiteSpace(safeName) ? "HierarchyObject" : safeName);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (LilToonMaterialConverter.IsLilToon(material) && !folders.ContainsKey(material)) folders.Add(material, folder);
            }
            return folders;
        }

        private static void ReplaceHierarchyMaterials(IEnumerable<Object> selection, IDictionary<Material, Material> replacements, ConversionReport report, bool duplicateAndDisableOriginal, AvatarUploadIdentity uploadIdentity)
        {
            var selectedRoots = selection.OfType<GameObject>()
                .Where(go => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go)))
                .Where(go => !selection.OfType<GameObject>().Any(other => other != go && go.transform.IsChildOf(other.transform)))
                .ToList();
            foreach (var root in selectedRoots)
            {
                var targetRoot = root;
                if (duplicateAndDisableOriginal)
                {
                    targetRoot = Object.Instantiate(root, root.transform.parent);
                    targetRoot.name = root.name + "_NonToon";
                    targetRoot.transform.SetSiblingIndex(root.transform.GetSiblingIndex() + 1);
                    targetRoot.SetActive(true);
                    Undo.RegisterCreatedObjectUndo(targetRoot, "Duplicate object for NonToon conversion");
                }
                var changed = false;
                foreach (var renderer in targetRoot.GetComponentsInChildren<Renderer>(true))
                {
                    var rendererChanged = false;
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++) if (materials[i] != null && replacements.TryGetValue(materials[i], out var converted)) { materials[i] = converted; changed = rendererChanged = true; }
                    if (rendererChanged) { Undo.RecordObject(renderer, "Replace lilToon material with NonToon material"); renderer.sharedMaterials = materials; EditorUtility.SetDirty(renderer); }
                }
                if (changed)
                {
                    if (duplicateAndDisableOriginal)
                    {
                        Undo.RecordObject(root, "Disable original lilToon object");
                        root.SetActive(false);
                        if (uploadIdentity == AvatarUploadIdentity.DetachBlueprintIdForNewAvatar)
                            TryDetachBlueprintId(targetRoot);
                    }
                    EditorSceneManager.MarkSceneDirty(root.scene);
                    var uploadMessage = uploadIdentity == AvatarUploadIdentity.DetachBlueprintIdForNewAvatar ? " The copy's Blueprint ID was detached for a new avatar upload." : " The copy retains its Blueprint ID for updating the same avatar.";
                    report.Entries.Add(new ConversionEntry { SourcePath = root.scene.path + ":" + root.name, Severity = ConversionSeverity.Success, Messages = { duplicateAndDisableOriginal ? "Duplicated the selected Hierarchy object, assigned NonToon materials to the copy, and disabled the original." + uploadMessage : "Replaced child Renderer material references in the selected Hierarchy object." } });
                }
                else if (duplicateAndDisableOriginal) Undo.DestroyObjectImmediate(targetRoot);
            }
        }

        private static void ReplacePrefabMaterials(IEnumerable<Object> selection, IDictionary<Material, Material> replacements, ConversionReport report)
        {
            foreach (var prefab in selection.OfType<GameObject>())
            {
                var path = AssetDatabase.GetAssetPath(prefab);
                if (string.IsNullOrEmpty(path)) continue;
                var root = PrefabUtility.LoadPrefabContents(path);
                var changed = false;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var rendererChanged = false;
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++) if (materials[i] != null && replacements.TryGetValue(materials[i], out var converted)) { materials[i] = converted; changed = rendererChanged = true; }
                    if (rendererChanged)
                    {
                        Undo.RecordObject(renderer, "Replace lilToon material with NonToon material");
                        renderer.sharedMaterials = materials;
                    }
                }
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    report.Entries.Add(new ConversionEntry { SourcePath = path, OutputPath = path, Severity = ConversionSeverity.Success, Messages = { "Replaced Renderer material references with converted NonToon materials." } });
                }
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
        }

        private static void WriteReport(ConversionOptions options, ConversionReport report)
        {
            var folder = string.IsNullOrEmpty(options.OutputFolder) ? "Assets" : options.OutputFolder;
            LilToonMaterialConverter.EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/LilToonToNonToonReport.txt");
            File.WriteAllText(path, report.ToText());
            AssetDatabase.ImportAsset(path);
            const string debugFolder = "Assets/NonToonConversionLogs";
            LilToonMaterialConverter.EnsureFolder(debugFolder);
            var debugPath = AssetDatabase.GenerateUniqueAssetPath(debugFolder + "/LilToonToNonToon_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(debugPath, report.ToText());
            AssetDatabase.ImportAsset(debugPath);
            Debug.Log("lilToon to NonToon conversion finished. Report: " + path + " | Debug log: " + debugPath);
        }

        private void DrawReport()
        {
            if (report == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(NTL10n.L("Last conversion"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(NTL10n.F("Success: {0}, Warnings: {1}, Unsupported: {2}, Errors: {3}", report.SuccessCount, report.WarningCount, report.UnsupportedCount, report.ErrorCount));
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(130));
            foreach (var entry in report.Entries)
                EditorGUILayout.HelpBox("[" + NTL10n.L(entry.Severity.ToString()) + "] " + entry.SourcePath + "\n" + string.Join("\n", entry.Messages), entry.Severity == ConversionSeverity.Error ? MessageType.Error : entry.Severity == ConversionSeverity.Warning ? MessageType.Warning : MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        // [NT-VENDOR] VRChat SDK 的 PipelineManager 通过反射访问，
        // 避免 NonToon 依赖 VRChat SDK（asmdef 的 references 无法条件化）。
        // 非 VRChat 工程里找不到该类型，直接跳过。
        private static void TryDetachBlueprintId(GameObject root)
        {
            var type = FindTypeByName("VRC.Core.PipelineManager");
            if (type == null) return;
            var component = root.GetComponent(type);
            if (component == null) return;
            var field = type.GetField("blueprintId", BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(string)) return;
            Undo.RecordObject(component, "Detach duplicated avatar Blueprint ID");
            field.SetValue(component, string.Empty);
            EditorUtility.SetDirty(component);
        }

        private static Type FindTypeByName(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = assembly.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }

    public sealed class LilToonToNonToonTroubleshootingWindow : EditorWindow
    {
        private Vector2 scroll;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("转换疑难排查", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("请先用最新版本重新转换同一对象，并目视确认转换后的材质。若问题仍在，可提供调试 ZIP。", MessageType.Info);
            EditorGUILayout.LabelField("安装状态", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(NonToonCompatibility.IsInstalled ? NonToonCompatibility.VersionStatus : "找不到 nontoon-fork 或 nontoon-fork-fur。请通过 VPM 安装 NonToon 与 Shader Core。", NonToonCompatibility.IsInstalled ? MessageType.Info : MessageType.Error);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawIssue("脸或部分材质偏亮", "请在日志里确认 Reflection / Apply Specular / Emission 的启用状态。转换器 1.1.3 起会禁用「父级 Reflection 关闭时的 Specular」和「实际强度为 0 的 Emission」。");
            DrawIssue("半透明部分过透或偏亮", "1.1.3 已修正「Main 2nd/3rd 普通混合不该改动 Alpha」的问题。转换后仍请检查 Alpha Mask、ZWrite 以及透明面的叠加。");
            DrawIssue("MatCap 质感或亮度不同", "lilToon 与 NonToon 的 UV、混合与强度算法不同。转换后请调整 MatCap 颜色与 Shared Mask。");
            DrawIssue("描边过粗或消不掉", "请在日志里确认原 Use Outline、宽度、颜色、顶点色设置。Width Mask 与 Outline Texture 无法完整移植到标准 NonToon。");
            DrawIssue("花纹、贴花或颜色不同", "请检查 Main 2nd/3rd 的启用状态、Blend Mask、Alpha Mode、UV Mode。贴花的左右指定、特殊 UV、Dissolve 属近似或未支持。");
            DrawIssue("材质缺失或串到别的材质", "为避免同名材质冲突，1.1.2 起会把原 GUID 附加到输出名上。请先清理旧输出再重新转换。");
            DrawIssue("距离淡变发黑", "1.0.7 及更早版本的输出可能起止相反。请用最新版覆盖重转，并确认 Distance Fade 的起点/终点。");
            DrawIssue("变粉或贴图损坏", "请确认 NonToon / Shader Core 的安装、生成的 Base Texture、以及原图的 Importer 设置。Crunch 压缩图在最新版会临时解压后处理。");
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            if (GUILayout.Button("打开最新调试日志")) LilToonToNonToonConverterWindow.OpenLatestDebugLog();
            if (GUILayout.Button("显示日志文件夹")) LilToonToNonToonConverterWindow.RevealDebugLogFolder();
            if (GUILayout.Button("把最新日志打包成 ZIP 并打开 GigaFile")) LilToonToNonToonConverterWindow.CreateDebugLogZip(true);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("发布与咨询页面")) Application.OpenURL(LilToonToNonToonConverterWindow.BoothUrl);
                if (GUILayout.Button("GigaFile")) Application.OpenURL(LilToonToNonToonConverterWindow.GigaFileUrl);
            }
        }

        private static void DrawIssue(string title, string body)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(body, MessageType.None);
        }
    }

    // [NT-VENDOR] lilToon 是否在位。
    // 转换器以 **lilToon 材质作为源**（读的是 lilToon 的属性），所以工程里没装 lilToon 时：
    // 对象上的材质会处于"着色器缺失"状态（Hidden/InternalErrorShader）→ IsLilToon 判 false
    // → 检出 0 个 → 转换按钮变灰。用户很容易判断成"工具坏了"，所以窗口要主动说明。
    internal static class LilToonCompatibility
    {
        internal static bool AnyLilToon
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/jp.lilxyzw.liltoon/package.json");
                if (package != null && !string.IsNullOrEmpty(package.version)) return true;
                // 有些工程把 lilToon 直接放在 Assets 里（不是 VPM 安装）
                return Shader.Find("lilToon") != null || Shader.Find("lilToonCutout") != null || Shader.Find("lilToonTransparent") != null;
            }
        }
    }
}
