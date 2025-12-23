using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(InfinityRenderChunks))]
public class InfinityRenderChunksEditor : Editor
{
    private bool _snapUp = true;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (InfinityRenderChunks)target;
        if (t == null) return;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("LOD Validation", EditorStyles.boldLabel);

        if (t.IsLodConfigValid(out string error))
        {
            EditorGUILayout.HelpBox("LOD config OK.", MessageType.Info);
            return;
        }

        EditorGUILayout.HelpBox(error, MessageType.Error);

        _snapUp = EditorGUILayout.ToggleLeft(
            new GUIContent("Fix: snap base resolution UP (200→257). If off: snap DOWN (200→129)."),
            _snapUp
        );

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Fix LOD Config", GUILayout.Height(28)))
            {
                Undo.RecordObject(t, "Fix LOD Config");
                t.FixLodConfig(_snapUp);
                EditorUtility.SetDirty(t);
            }

            if (GUILayout.Button("Revalidate", GUILayout.Height(28)))
            {
                // just repaint
                Repaint();
            }
        }
    }
}


