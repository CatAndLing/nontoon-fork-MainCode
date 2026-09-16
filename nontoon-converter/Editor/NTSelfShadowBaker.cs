using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NonToonTools
{
    // [NT-TOOL] 自阴影贴图烘焙 + 写入材质。
    //
    // 为什么是纯 CPU 光线投射：
    //   · 不依赖 GPU / 相机 / RenderTexture（批处理与无显卡环境都能跑，也便于自动化验证）
    //   · 不用给场景加任何真实光源 —— VRChat 性能等级对 Lights 是硬要求（PC 上 0 才算 Good 以上）
    // 做法：在光源的正交视锥里，逐像素朝光源方向打一条射线，记录最近命中距离（归一化到 0..1）。
    // 网格用 HideAndDontSave 的临时 MeshCollider 承载（蒙皮的先 BakeMesh 取当前姿态），烘焙完全部销毁。
    internal static class NTSelfShadowBaker
    {
        internal sealed class Result
        {
            public Texture2D map;
            public string path;
            public Vector3 origin, right, up, forward;
            public float halfX, halfY, near, far;
            public int texels, hits, meshCount;
            public int resolution;
            public float[] depth;   // 原始深度缓冲（未归一化的 0..1），供测试/诊断用
        }

        private const float Padding = 1.05f;

        internal static Result Bake(Light light, IEnumerable<Renderer> renderers, int resolution, string outputFolder)
        {
            if (light == null) throw new InvalidOperationException("没有指定光源（Self Light 组件的 Light Source 为空）。");
            // 注意：这里**只读它的方向**（transform 的三轴）。颜色 / 强度取自组件字段，与这盏灯无关 ——
            // 所以「自带光照与阴影」组件可以完全不摆 Light，直接把自己的虚拟光方向传进来。
            return Bake(light.transform.forward, light.transform.right, light.transform.up,
                        light.name, renderers, resolution, outputFolder);
        }

        // [NT-FEAT 21] 虚拟光入口：方向由调用方给（组件字段），不需要场景里有 Light 组件。
        // forward = 光线传播方向（从光源射向场景）；right/up 必须与之正交（调用方负责正交化）。
        internal static Result Bake(Vector3 forward, Vector3 right, Vector3 up, string nameForFile,
                                    IEnumerable<Renderer> renderers, int resolution, string outputFolder)
        {
            resolution = Mathf.Clamp(resolution, 64, 2048);   // 性能不再是约束：允许 2048²（4M 条射线）

            // 关键：这里用的是**光线传播方向**（forward，从光源射向场景），
            // 不是"指向光源"的方向。它同时决定三件事，必须自洽：
            //   1) 近平面 = 离光源最近的那一面 → origin 放在这一侧
            //   2) 逐像素射线沿这个方向往外打 → 找到"离光源最近的表面"（也就是遮挡物）
            //   3) 着色器里 selfDepth = dot(worldPos - origin, _SelfLightForward)，
            //      即"离近平面的距离"，与这里记录的 hit.distance / depth 完全同一套约定
            // （第一版写成 -light.forward，射线朝光源方向打，结果一个都没命中 → 贴图全白。）
            var tempColliders = new List<GameObject>();
            var tempMeshes = new List<Mesh>();
            var vertices = new List<Vector3>();
            var meshCount = 0;
            try
            {
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled) continue;
                    Mesh mesh;
                    var created = false;
                    if (renderer is SkinnedMeshRenderer skinned)
                    {
                        mesh = new Mesh();
                        skinned.BakeMesh(mesh, true);
                        created = true;
                    }
                    else
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null) continue;
                        mesh = filter.sharedMesh;
                    }
                    if (mesh == null || mesh.vertexCount == 0) continue;

                    var holder = new GameObject("NTSelfShadowCollider") { hideFlags = HideFlags.HideAndDontSave };
                    holder.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                    holder.transform.localScale = renderer.transform.lossyScale;
                    holder.AddComponent<MeshCollider>().sharedMesh = mesh;
                    tempColliders.Add(holder);
                    if (created) tempMeshes.Add(mesh);
                    meshCount++;

                    var toWorld = renderer.localToWorldMatrix;
                    var local = mesh.vertices;
                    for (var i = 0; i < local.Length; i++) vertices.Add(toWorld.MultiplyPoint3x4(local[i]));
                }

                if (vertices.Count == 0 || meshCount == 0)
                    throw new InvalidOperationException("组件所在层级里没有可烘焙的网格（需要 MeshRenderer / SkinnedMeshRenderer）。");

                // 光源空间包围盒
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var v in vertices)
                {
                    var x = Vector3.Dot(v, right); var y = Vector3.Dot(v, up); var z = Vector3.Dot(v, forward);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }

                var halfX = Mathf.Max(1e-4f, (maxX - minX) * 0.5f * Padding);
                var halfY = Mathf.Max(1e-4f, (maxY - minY) * 0.5f * Padding);
                var depth = Mathf.Max(1e-4f, (maxZ - minZ) + 0.05f);
                var center = right * ((minX + maxX) * 0.5f) + up * ((minY + maxY) * 0.5f) + forward * ((minZ + maxZ) * 0.5f);
                var origin = center - forward * (depth * 0.5f);   // 近平面中心：depth 从 0 起算

                // 逐像素射线
                // 注意：刚 AddComponent 的 MeshCollider 必须 SyncTransforms 之后才会参与物理查询，
                // 否则所有射线都打空（表现为"烘焙出来的贴图全白"）。
                Physics.SyncTransforms();
                var buffer = new float[resolution * resolution];
                var hits = 0;
                for (var y = 0; y < resolution; y++)
                {
                    for (var x = 0; x < resolution; x++)
                    {
                        var u = (x + 0.5f) / resolution;
                        var v = (y + 0.5f) / resolution;
                        var rayOrigin = origin + right * ((u * 2f - 1f) * halfX) + up * ((v * 2f - 1f) * halfY);
                        var d01 = 1f;
                        // 在表面上的像素距离为 0：给一个极小起点偏移，避免"自己打到自己"以外的浮点抖动
                        if (Physics.Raycast(rayOrigin + forward * 0.001f, forward, out var hit, depth, ~0, QueryTriggerInteraction.Ignore))
                        {
                            d01 = Mathf.Clamp01((hit.distance + 0.001f) / depth);
                            hits++;
                        }
                        buffer[y * resolution + x] = d01;
                    }
                }

                // 写成 PNG（灰度即深度；关闭 sRGB / 压缩 / mipmap，否则深度会被改）
                var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true);
                var pixels = new Color[buffer.Length];
                for (var i = 0; i < buffer.Length; i++) pixels[i] = new Color(buffer[i], buffer[i], buffer[i], 1f);
                tex.SetPixels(pixels);
                tex.Apply();

                var folder = string.IsNullOrEmpty(outputFolder) ? "Assets/NonToonSelfLight" : outputFolder;
                EnsureFolder(folder);
                var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/SelfShadow_" + nameForFile + "_" + resolution + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    importer.sRGBTexture = false;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.SaveAndReimport();
                }

                return new Result
                {
                    map = AssetDatabase.LoadAssetAtPath<Texture2D>(path),
                    path = path,
                    origin = origin,
                    right = right,
                    up = up,
                    forward = forward,
                    halfX = halfX,
                    halfY = halfY,
                    near = 0f,
                    far = depth,
                    texels = buffer.Length,
                    resolution = resolution,
                    hits = hits,
                    meshCount = meshCount,
                    depth = buffer
                };
            }
            finally
            {
                foreach (var go in tempColliders) Object.DestroyImmediate(go);
                foreach (var mesh in tempMeshes) Object.DestroyImmediate(mesh);
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            var leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
