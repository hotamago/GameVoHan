using UnityEditor;
using UnityEngine;

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
    }
}


