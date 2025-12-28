using UnityEditor;
using UnityEngine;
using InfinityTerrain.Vegetation;

[CustomEditor(typeof(InfinityTerrain.InfinityRenderChunks))]
public class InfinityRenderChunksEditor : Editor
{
    private bool _snapUp = true;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (InfinityTerrain.InfinityRenderChunks)target;
        if (t == null) return;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Terrain LOD Validation", EditorStyles.boldLabel);

        if (t.IsLodConfigValid(out string error))
        {
            EditorGUILayout.HelpBox("Terrain LOD config OK.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(error, MessageType.Error);

            _snapUp = EditorGUILayout.ToggleLeft(
                new GUIContent("Fix: snap base resolution UP (200→257). If off: snap DOWN (200→129)."),
                _snapUp
            );

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fix Terrain LOD Config", GUILayout.Height(28)))
                {
                    Undo.RecordObject(t, "Fix Terrain LOD Config");
                    t.FixLodConfig(_snapUp);
                    EditorUtility.SetDirty(t);
                }

                if (GUILayout.Button("Revalidate", GUILayout.Height(28)))
                {
                    Repaint();
                }
            }
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Water LOD Validation", EditorStyles.boldLabel);

        if (t.IsWaterLodConfigValid(out string waterError))
        {
            EditorGUILayout.HelpBox("Water LOD config OK.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(waterError, MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fix Water LOD Config", GUILayout.Height(28)))
                {
                    Undo.RecordObject(t, "Fix Water LOD Config");
                    t.FixWaterLodConfig();
                    EditorUtility.SetDirty(t);
                }

                if (GUILayout.Button("Revalidate", GUILayout.Height(28)))
                {
                    Repaint();
                }
            }
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Vegetation Scatter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Vegetation scatter spawns Idyllic prefabs on nearby high-detail chunks (mesh terrain). " +
            "Create a VegetationScatterSettings asset and auto-fill it from the Idyllic prefabs folder.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Create Settings Asset", GUILayout.Height(28)))
            {
                CreateSettingsAssetAndAssign(t);
            }

            if (GUILayout.Button("Open Settings (if assigned)", GUILayout.Height(28)))
            {
                OpenSettingsIfAssigned(t);
            }
        }
    }

    private static void CreateSettingsAssetAndAssign(InfinityTerrain.InfinityRenderChunks t)
    {
        if (t == null) return;

        var asset = ScriptableObject.CreateInstance<VegetationScatterSettings>();
        string path = AssetDatabase.GenerateUniqueAssetPath("Assets/VegetationScatterSettings.asset");
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Undo.RecordObject(t, "Assign VegetationScatterSettings");

        // Assign via SerializedObject to avoid reflection/private-field issues
        SerializedObject so = new SerializedObject(t);
        SerializedProperty p = so.FindProperty("vegetationScatterSettings");
        if (p != null)
        {
            p.objectReferenceValue = asset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(t);
        }

        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

    private static void OpenSettingsIfAssigned(InfinityTerrain.InfinityRenderChunks t)
    {
        if (t == null) return;
        SerializedObject so = new SerializedObject(t);
        SerializedProperty p = so.FindProperty("vegetationScatterSettings");
        if (p == null) return;
        if (p.objectReferenceValue == null) return;
        Selection.activeObject = p.objectReferenceValue;
        EditorGUIUtility.PingObject(p.objectReferenceValue);
    }
}


