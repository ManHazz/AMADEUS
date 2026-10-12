using System.Text;
using UnityEditor;
using UnityEngine;
using UniVRM10;

public static class VrmTools
{
    public static void Reimport()
    {
        AssetDatabase.ImportAsset("Assets/Characters/amadeus.vrm", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath("Assets/Characters/amadeus.vrm"))
            UnityEngine.Debug.Log("VRMASSET " + o.GetType().Name + " " + o.name);
    }

    /// <summary>Logs every VRM expression asset: name, morph bindings (mesh blendshape name + weight), overrides.</summary>
    public static void DumpExpressions()
    {
        var all = AssetDatabase.LoadAllAssetsAtPath("Assets/Characters/amadeus.vrm");
        var meshes = new System.Collections.Generic.Dictionary<string, Mesh>();
        foreach (var o in all) if (o is Mesh m) meshes[m.name] = m;
        var sb = new StringBuilder("VRMEXPR\n");
        foreach (var o in all)
        {
            if (!(o is VRM10Expression e)) continue;
            sb.AppendLine("EXPR " + e.name + " binary=" + e.IsBinary + " ovBlink=" + e.OverrideBlink + " ovMouth=" + e.OverrideMouth + " ovLook=" + e.OverrideLookAt + " colorBindings=" + e.MaterialColorBindings.Length);
            foreach (var b in e.MorphTargetBindings)
            {
                string nm = "?";
                var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/amadeus.vrm");
                Transform t = go != null ? go.transform.Find(b.RelativePath) : null;
                var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr != null && smr.sharedMesh != null && b.Index < smr.sharedMesh.blendShapeCount) nm = smr.sharedMesh.GetBlendShapeName(b.Index);
                sb.AppendLine("   bind " + b.RelativePath + " idx=" + b.Index + " (" + nm + ") w=" + b.Weight);
            }
        }
        Debug.Log(sb.ToString());
    }

    public static void DumpBlendShapes()
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/amadeus.vrm");
        var sb = new StringBuilder("VRMBS");
        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
            sb.AppendLine("MESH " + smr.name + " " + smr.sharedMesh.blendShapeCount);
            for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) sb.AppendLine("  " + i + " " + smr.sharedMesh.GetBlendShapeName(i));
        }
        Debug.Log(sb.ToString());
    }
}
