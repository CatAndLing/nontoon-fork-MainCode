// Batch 3 diagnostic only. No implementation or source asset writes.
// Exit 0 requires valid arms AND both symptom baselines reproduced; it never means full fidelity.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
namespace LilToonToNonToonConverter {
public static class NTSymptomDiagnostic {
    const string D = "_jp_lilxyzw_nontoon_details_";
    const int Size = 256;
    const double PixelEps = 1e-6, ColorEps = 1e-4;
    static readonly StringBuilder Log = new StringBuilder();
    static int failures;
    static Camera camera;
    static List<Renderer> subjects;
    static bool[] roi;
    static int[] ids;
    static List<int[]> edges;
    static string outDir;
    static string Out => Path.Combine(outDir,"report.txt");
    static void Say(string s) { Log.AppendLine(s); File.WriteAllText(Out,Log.ToString()); }
    static void Check(bool ok,string s) { if(!ok) failures++; Say((ok?"PASS ":"FAIL ")+s); }
    static string F(double v) => v.ToString("G12",CultureInfo.InvariantCulture);
    static string Hash(string p) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(p))).Replace("-",""); }
    static float Number(Material m,string p) {
        if(!m.HasProperty(p)) throw new Exception("Missing property "+p);
        return m.shader.GetPropertyType(m.shader.FindPropertyIndex(p))==ShaderPropertyType.Int ? m.GetInteger(p) : m.GetFloat(p);
    }
    static void Set(Material m,string p,float v) {
        if(!m.HasProperty(p)) throw new Exception("Missing property "+p);
        if(m.shader.GetPropertyType(m.shader.FindPropertyIndex(p))==ShaderPropertyType.Int) m.SetInteger(p,(int)v); else m.SetFloat(p,v);
    }
    static bool Zero(Material m,params string[] props) {
        bool changed=false, correct=true;
        foreach(var p in props) { float before=Number(m,p); Set(m,p,0); float after=Number(m,p);
            changed |= before!=0; correct &= after==0;
            Say("STATE "+m.name+" "+p+" "+F(before)+" -> "+F(after));
        }
        return changed && correct;
    }
    static string PropertyEnding(Material m,string suffix) {
        return Enumerable.Range(0,m.shader.GetPropertyCount()).Select(i=>m.shader.GetPropertyName(i)).Single(p=>p.EndsWith(suffix,StringComparison.Ordinal));
    }
    static bool ZeroColor(Material m,string suffix) {
        string p=PropertyEnding(m,suffix); Color before=m.GetColor(p);
        m.SetColor(p,Color.clear); Color after=m.GetColor(p);
        Say("STATE "+m.name+" "+p+" "+before.ToString("G9")+" -> "+after.ToString("G9"));
        return before.r*before.r+before.g*before.g+before.b*before.b>0 && after==Color.clear;
    }
    static string[] ActiveModule(Material m,string module) {
        return m.shaderKeywords.Where(k=>k.IndexOf("_"+module+"_",StringComparison.OrdinalIgnoreCase)>=0 && k.EndsWith("_ENABLE_1",StringComparison.Ordinal)).ToArray();
    }
    static bool ModuleOff(Material m,string module) {
        var on=ActiveModule(m,module);
        // On keyword comes from the produced material; off comes from the shader's declared keyword space.
        var off=m.shader.keywordSpace.keywords.Select(k=>k.name).Where(k=>k.IndexOf("_"+module+"_",StringComparison.OrdinalIgnoreCase)>=0 && k.EndsWith("_ENABLE_0",StringComparison.Ordinal)).ToArray();
        Say("KEYWORD_BEFORE "+m.name+" "+string.Join(",",m.shaderKeywords)+" declaredOff="+string.Join(",",off));
        if(on.Length!=1 || off.Length!=1) return false;
        m.DisableKeyword(on[0]); m.EnableKeyword(off[0]);
        Say("KEYWORD_AFTER "+m.name+" "+string.Join(",",m.shaderKeywords));
        return !m.IsKeywordEnabled(on[0]) && m.IsKeywordEnabled(off[0]) && ActiveModule(m,module).Length==0;
    }
    public static void Run() {
        outDir=Environment.GetEnvironmentVariable("NT_SYMPTOM_OUT");
        if(string.IsNullOrEmpty(outDir)) throw new Exception("NT_SYMPTOM_OUT required; use isolated runner");
        Directory.CreateDirectory(outDir);
        if(File.Exists(Out)) File.Delete(Out);
        Log.Clear(); failures=0;
        Say("UTC="+DateTime.UtcNow.ToString("O")+" run="+Guid.NewGuid());
        Say("Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceType+" color="+QualitySettings.activeColorSpace);
        Say("PREDECLARED size=256 float32 RGB linear; geometric ROI erosion=2; repeatRMSE<=1e-6; armPixelRMSE>1e-6 required separately on each shader");
        Say("PREDECLARED jacket reproduction: HF_nt > HF_lil + max(1e-5,0.01*HF_lil); HF=RMS RGB adjacent difference on fixed ROI");
        Say("PREDECLARED purple proxy: NT B-G>1e-4 and R-G>1e-4, NT-minus-lil delta(B-G)>1e-4 and delta(R-G)>1e-4; signed B-R also saved; proxy is not a perceptual verdict");
        Say("NO CAUSAL CLAIM if baseline fails. Invalid arms remain failures; measurements from other arms retained. No rerun to erase failures.");
        try {
            Check(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D11 && QualitySettings.activeColorSpace==ColorSpace.Linear && GraphicsSettings.currentRenderPipeline==null,"D3D11 Linear Built-in");
            Check(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat),"ARGBFloat supported");
            if(failures!=0) throw new Exception("Invalid environment");
            ShaderUtil.allowAsyncCompilation=false;
            AssetDatabase.ImportAsset("Packages/com.catandling.nontoon/Shaders/NonToon.scshader",ImportAssetOptions.ForceUpdate);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat; RenderSettings.ambientLight=new Color(.12f,.12f,.12f);
            RenderSettings.ambientIntensity=1; RenderSettings.reflectionIntensity=0; RenderSettings.skybox=null; RenderSettings.fog=false;
            var sh=new SphericalHarmonicsL2(); sh.AddAmbientLight(new Color(.12f,.12f,.12f)); RenderSettings.ambientProbe=sh;
            QualitySettings.pixelLightCount=4; QualitySettings.antiAliasing=0;
            var light=new GameObject("DiagnosticLight").AddComponent<Light>(); light.type=LightType.Directional; light.color=Color.white; light.intensity=1; light.shadows=LightShadows.None;
            light.transform.rotation=Quaternion.Euler(30,-35,0); RenderSettings.sun=light;
            camera=new GameObject("DiagnosticCamera").AddComponent<Camera>(); camera.enabled=false; camera.orthographic=true; camera.aspect=1;
            camera.nearClipPlane=.01f; camera.farClipPlane=100; camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=Color.black; camera.allowHDR=true; camera.allowMSAA=false; camera.renderingPath=RenderingPath.Forward;
            Say("SCENE directional white intensity=1 Euler=(30,-35,0) shadows=None ambient=(0.12,0.12,0.12) flat reflectionIntensity=0 skybox=null");
            foreach(var name in new[]{"clothes_bk","Hair"}) {
                try { Case(name,name=="Hair"?"Assets/SymptomKaguya/material/Hair.mat":"Assets/SymptomKaguya/cloth/material/clothes_bk.mat"); }
                catch(Exception e) { Check(false,"CASE "+name+" exception="+e); CleanupGeometry(); }
            }
            Say("C: UNVERIFIED missing confirmed old aburaage bag/contents renderer pair; no substitute tested.");
        } catch(Exception e) { Check(false,e.ToString()); }
        Say("COMPLETE failures="+failures+"; exit0 requires valid measurements and baseline reproduction, never root cause found");
        EditorApplication.Exit(failures==0?0:1);
    }
    static void Case(string name,string path) {
        Say("CASE "+name+" source="+path);
        var source=AssetDatabase.LoadAssetAtPath<Material>(path); if(source==null) throw new Exception("Missing source "+path);
        string hash=Hash(path); var src=new Material(source) {name=name+"_lil"};
        Material dst=null;
        try {
            Say("sourceSha256="+hash+" shader="+src.shader.name);
            var entry=LilToonMaterialConverter.Convert(source,new ConversionOptions{OutputFolder="Assets/SymptomGenerated",OverwriteExisting=true,CleanPreviousGeneratedOutputs=false});
            if(entry.Severity==ConversionSeverity.Error || string.IsNullOrEmpty(entry.OutputPath)) throw new Exception(string.Join(";",entry.Messages));
            dst=new Material(AssetDatabase.LoadAssetAtPath<Material>(entry.OutputPath)) {name=name+"_nt"};
            Say("target="+entry.OutputPath+" shader="+dst.shader.name+" keywords="+string.Join(",",dst.shaderKeywords));
            Check(ActiveModule(dst,"DETAILS").Length==1,name+" actual Details on keyword");
            BuildGeometry(source);
            BuildROI(name);
            // Warm up exact variants before repeatability measurement; no assertion is retried.
            Render(src); Render(dst);
            var baseL=Render(src); var baseN=Render(dst);
            Save(name+"_baseline_lil",baseL); Save(name+"_baseline_nt",baseN);
            Metrics(name+" baseline",baseL,baseN,baseL,baseN);
            Check(Diff(baseL,Render(src))<=PixelEps && Diff(baseN,Render(dst))<=PixelEps,name+" repeated baseline stable");
            Check(ids.All(i=>baseL[i].r+baseL[i].g+baseL[i].b>1e-8 || baseN[i].r+baseN[i].g+baseN[i].b>1e-8) || ids.Count(i=>baseL[i].r+baseL[i].g+baseL[i].b>1e-8)>100,name+" nonempty ROI");
            if(name=="clothes_bk") Check(HF(baseN)>HF(baseL)+Math.Max(1e-5,HF(baseL)*.01),name+" REPRODUCED_HF_INCREASE");
            else {
                var a=Mean(baseL); var b=Mean(baseN);
                Check(b[2]-b[1]>ColorEps && b[0]-b[1]>ColorEps && (b[2]-b[1])-(a[2]-a[1])>ColorEps && (b[0]-b[1])-(a[0]-a[1])>ColorEps,name+" REPRODUCED_PURPLE_PROXY");
            }
            string[] arms=name=="clothes_bk" ? new[]{"normal1off","normal2off","normalsoff","matcapsoff","specularoff"} : new[]{"matcapsoff","rimoff","shadowoff","normalsoff"};
            foreach(var arm in arms) {
                var a=new Material(src) {name=name+"_"+arm+"_lil"}; var b=new Material(dst) {name=name+"_"+arm+"_nt"};
                try {
                    bool stateA=false,stateB=false;
                    if(arm=="normal1off") { stateA=Zero(a,"_BumpScale"); stateB=Zero(b,D+"Detail0NormalScale"); }
                    if(arm=="normal2off") { stateA=Zero(a,"_Bump2ndScale"); stateB=Zero(b,D+"Detail1NormalScale"); }
                    if(arm=="normalsoff") {
                        stateA=Zero(a,"_BumpScale","_Bump2ndScale");
                        stateB=Zero(b,"_NormalScale",D+"Detail0NormalScale",D+"Detail1NormalScale",D+"Detail2NormalScale",D+"Detail3NormalScale");
                    }
                    if(arm=="matcapsoff") { stateA=Zero(a,"_UseMatCap","_UseMatCap2nd"); stateB=ModuleOff(b,"MATCAPS"); }
                    if(arm=="specularoff") { stateA=Zero(a,"_ApplySpecular","_ApplySpecularFA"); stateB=ZeroColor(b,"_SpecularColor"); }
                    if(arm=="rimoff") { stateA=Zero(a,"_UseRim"); stateB=ZeroColor(b,"_RimLightColor"); }
                    if(arm=="shadowoff") { stateA=Zero(a,"_UseShadow"); stateB=Zero(b,"_ShadowColorEnable"); }
                    Check(stateA,name+" "+arm+" lil state changed/readback");
                    Check(stateB,name+" "+arm+" nt state changed/readback");
                    var pa=Render(a); var pb=Render(b); Save(name+"_"+arm+"_lil",pa); Save(name+"_"+arm+"_nt",pb);
                    double da=Diff(baseL,pa),db=Diff(baseN,pb);
                    bool validA=stateA&&da>PixelEps,validB=stateB&&db>PixelEps;
                    Check(validA,name+" "+arm+" lil arm valid state+pixel RMSE="+F(da));
                    Check(validB,name+" "+arm+" nt arm valid state+pixel RMSE="+F(db));
                    Say("ARM "+name+" "+arm+" lil="+(validA?"VALID":"INVALID")+" nt="+(validB?"VALID":"INVALID")+" pair="+(validA&&validB?"VALID":"INVALID"));
                    Metrics(name+" "+arm,pa,pb,baseL,baseN);
                } catch(Exception e) { Check(false,"INVALID ARM "+name+" "+arm+" "+e); }
                finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
            }
        } finally {
            Check(Hash(path)==hash,name+" source serialized bytes unchanged");
            CleanupGeometry(); Object.DestroyImmediate(src); if(dst!=null) Object.DestroyImmediate(dst);
        }
    }
    static void CleanupGeometry() {
        if(subjects==null) return;
        foreach(var r in subjects) if(r!=null) { Object.DestroyImmediate(r.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(r.gameObject); }
        subjects.Clear();
    }
    static string Hierarchy(Transform t) { return t.parent==null?t.name:Hierarchy(t.parent)+"/"+t.name; }
    static void BuildGeometry(Material source) {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SymptomKaguya/kaguya.prefab");
        if(prefab==null) throw new Exception("Missing kaguya prefab");
        var holder=new GameObject("InactiveExtraction"); holder.SetActive(false);
        var root=Object.Instantiate(prefab,holder.transform); subjects=new List<Renderer>();
        try {
            int missing=0;
            foreach(var behaviour in root.GetComponentsInChildren<Behaviour>(true)) { if(behaviour==null) { missing++; continue; } behaviour.enabled=false; }
            Say("GEOMETRY missing Behaviour entries skipped="+missing);
            foreach(var r in root.GetComponentsInChildren<Renderer>(true)) {
                var indices=Enumerable.Range(0,r.sharedMaterials.Length).Where(i=>r.sharedMaterials[i]==source).ToArray();
                if(indices.Length==0) continue;
                var mesh=new Mesh();
                if(r is SkinnedMeshRenderer sk) sk.BakeMesh(mesh);
                else { var f=r.GetComponent<MeshFilter>(); if(f==null || f.sharedMesh==null) { Object.DestroyImmediate(mesh); continue; } Object.DestroyImmediate(mesh); mesh=Object.Instantiate(f.sharedMesh); }
                var triangles=indices.SelectMany(i=>mesh.GetTriangles(i)).ToArray();
                if(triangles.Length==0) throw new Exception("Empty material triangles "+r.name);
                mesh.subMeshCount=1; mesh.SetTriangles(triangles,0);
                // Mesh.RecalculateBounds includes unreferenced vertices from other material slots; bound selected triangles only.
                var verts=mesh.vertices; var bounds=new Bounds(verts[triangles[0]],Vector3.zero);
                foreach(int i in triangles) bounds.Encapsulate(verts[i]); mesh.bounds=bounds;
                var go=new GameObject("Diagnostic_"+r.name); go.transform.SetPositionAndRotation(r.transform.position,r.transform.rotation); go.transform.localScale=r.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh=mesh; var mr=go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode=ShadowCastingMode.Off; mr.receiveShadows=false; mr.lightProbeUsage=LightProbeUsage.Off; mr.reflectionProbeUsage=ReflectionProbeUsage.Off; subjects.Add(mr);
                Say("GEOMETRY path="+Hierarchy(r.transform)+" slots="+string.Join(",",indices)+" triangles="+triangles.Length/3+" sourceActiveSelf="+r.gameObject.activeSelf+" rendererEnabled="+r.enabled+" bounds="+mr.bounds);
            }
        } finally { Object.DestroyImmediate(holder); }
        Check(subjects.Count>0,"real prefab material slots found: "+source.name+" count="+subjects.Count);
        if(subjects.Count==0) throw new Exception("No geometry for source material");
        var total=subjects[0].bounds; foreach(var r in subjects) total.Encapsulate(r.bounds);
        camera.transform.position=total.center+new Vector3(0,0,-5); camera.transform.rotation=Quaternion.identity;
        camera.orthographicSize=Mathf.Max(total.extents.x,total.extents.y)*1.12f;
        Say("CAMERA position="+camera.transform.position.ToString("G9")+" ortho="+F(camera.orthographicSize)+" rotation=identity bounds="+total);
    }
    static void BuildROI(string name) {
        var s=Shader.Find("Hidden/NTSymptomGeometry"); if(s==null) throw new Exception("Missing geometry shader");
        var m=new Material(s); Color[] mask;
        try { mask=Render(m); } finally { Object.DestroyImmediate(m); }
        roi=new bool[Size*Size];
        for(int y=2;y<Size-2;y++) for(int x=2;x<Size-2;x++) {
            bool good=true;
            for(int dy=-2;dy<=2;dy++) for(int dx=-2;dx<=2;dx++) good &= mask[(y+dy)*Size+x+dx].r>.5f;
            roi[y*Size+x]=good;
        }
        ids=Enumerable.Range(0,roi.Length).Where(i=>roi[i]).ToArray(); edges=new List<int[]>();
        foreach(int i in ids) {
            if(i%Size<Size-1 && roi[i+1]) edges.Add(new[]{i,i+1});
            if(i/Size<Size-1 && roi[i+Size]) edges.Add(new[]{i,i+Size});
        }
        Check(ids.Length>100 && edges.Count>100,name+" fixed geometric ROI samples="+ids.Length+" adjacentPairs="+edges.Count);
        if(ids.Length<=100) throw new Exception("Insufficient ROI");
        File.WriteAllBytes(Path.Combine(outDir,name+"_roi.u8"),roi.Select(v=>(byte)(v?1:0)).ToArray());
        Say("ROI "+name+" sha256="+Hash(Path.Combine(outDir,name+"_roi.u8"))+" shared by source/target/all arms; no RGB threshold selection");
    }
    static Color[] Render(Material m) {
        foreach(var r in subjects) r.sharedMaterial=m;
        var rt=new RenderTexture(Size,Size,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
        var tex=new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true); var old=RenderTexture.active;
        try {
            if(!rt.Create()) throw new Exception("Float RT creation failed");
            camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt; tex.ReadPixels(new Rect(0,0,Size,Size),0,0); tex.Apply();
            var pixels=tex.GetPixels();
            if(!pixels.All(c=>Finite(c.r)&&Finite(c.g)&&Finite(c.b))) throw new Exception("Nonfinite RGB "+m.name);
            if(ShaderUtil.ShaderHasError(m.shader)) throw new Exception("Shader error "+m.shader.name);
            return pixels;
        } finally { camera.targetTexture=null; RenderTexture.active=old; Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt); }
    }
    static bool Finite(float v) => !float.IsNaN(v)&&!float.IsInfinity(v);
    static void Save(string label,Color[] p) {
        using(var w=new BinaryWriter(File.Create(Path.Combine(outDir,label+".rgbf32"))))
            foreach(var c in p) { w.Write(c.r); w.Write(c.g); w.Write(c.b); }
    }
    static double Sq(Color d) => (double)d.r*d.r+(double)d.g*d.g+(double)d.b*d.b;
    static double Diff(Color[] a,Color[] b) => Math.Sqrt(ids.Sum(i=>Sq(a[i]-b[i]))/(ids.Length*3));
    static double HF(Color[] p) => Math.Sqrt(edges.Sum(e=>Sq(p[e[0]]-p[e[1]]))/(edges.Count*3));
    static double HFResidual(Color[] a,Color[] b) => Math.Sqrt(edges.Sum(e=>Sq((b[e[0]]-b[e[1]])-(a[e[0]]-a[e[1]])))/(edges.Count*3));
    static double[] Mean(Color[] p) => new[]{ids.Average(i=>(double)p[i].r),ids.Average(i=>(double)p[i].g),ids.Average(i=>(double)p[i].b)};
    static void Metrics(string label,Color[] a,Color[] b,Color[] ba,Color[] bb) {
        var ma=Mean(a); var mb=Mean(b); var bma=Mean(ba); var bmb=Mean(bb);
        Say("METRIC "+label+" RMSE="+F(Diff(a,b))+" HF_lil="+F(HF(a))+" HF_nt="+F(HF(b))+" HF_residual="+F(HFResidual(a,b)));
        foreach(bool nt in new[]{false,true}) {
            var m=nt?mb:ma; var prev=nt?bmb:bma; var p=nt?b:a;
            Say("COLOR "+label+" "+(nt?"nt":"lil")+" meanRGB="+string.Join(",",m.Select(F))+" B-R="+F(m[2]-m[0])+" B-G="+F(m[2]-m[1])+" deltaBR="+F((m[2]-m[0])-(prev[2]-prev[0]))+" deltaBG="+F((m[2]-m[1])-(prev[2]-prev[1]))+" min="+F(ids.Min(i=>Math.Min(p[i].r,Math.Min(p[i].g,p[i].b))))+" max="+F(ids.Max(i=>Math.Max(p[i].r,Math.Max(p[i].g,p[i].b)))));
        }
        Say("DIRECTION "+label+" ntMinusLil_BR="+F((mb[2]-mb[0])-(ma[2]-ma[0]))+" ntMinusLil_BG="+F((mb[2]-mb[1])-(ma[2]-ma[1])));
    }
}}
