using System;
using System.Collections.Generic;
using InfinityTerrain.Vegetation;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VegetationScatterSettings))]
public class VegetationScatterSettingsEditor : Editor
{
    private const string DefaultFolder = "Assets/Idyllic Fantasy Nature/Prefabs";

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Idyllic Prefabs Auto Fill", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "This will scan prefabs in 'Assets/Idyllic Fantasy Nature/Prefabs' and populate the list with default categories/weights.\n" +
            "You can edit/remove entries afterwards.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto Fill From Idyllic Prefabs", GUILayout.Height(28)))
            {
                AutoFill((VegetationScatterSettings)target, DefaultFolder);
            }

            if (GUILayout.Button("Clear Prefab List", GUILayout.Height(28)))
            {
                Undo.RecordObject(target, "Clear Vegetation Prefab List");
                var s = (VegetationScatterSettings)target;
                s.prefabs.Clear();
                EditorUtility.SetDirty(s);
            }
        }
    }

    private static void AutoFill(VegetationScatterSettings s, string folder)
    {
        if (s == null) return;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
        if (guids == null || guids.Length == 0)
        {
            EditorUtility.DisplayDialog("Vegetation Auto Fill", $"No prefabs found in:\n{folder}", "OK");
            return;
        }

        Undo.RecordObject(s, "Auto Fill Vegetation Prefabs");
        s.prefabs.Clear();

        int added = 0;
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (string.IsNullOrEmpty(path)) continue;

            // Filter out non-terrain things
            string lowerPath = path.Replace("\\", "/").ToLowerInvariant();
            if (lowerPath.Contains("/code related/")) continue;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            string n = prefab.name;
            if (IsExcludedName(n)) continue;

            VegetationCategory cat = Categorize(n);

            VegetationPrefabEntry e = new VegetationPrefabEntry
            {
                name = n,
                category = cat,
                prefab = prefab,
                weight = 1f,
                minUniformScale = DefaultMinScale(cat),
                maxUniformScale = DefaultMaxScale(cat),
                alignToNormal = cat == VegetationCategory.Rock,
                randomYaw = true,
                yOffset = 0f
            };

            // Trees often should remain upright
            if (cat == VegetationCategory.Tree)
            {
                e.alignToNormal = false;
            }

            s.prefabs.Add(e);
            added++;
        }

        EditorUtility.SetDirty(s);
        AssetDatabase.SaveAssets();

        EditorUtility.DisplayDialog("Vegetation Auto Fill", $"Added {added} prefabs into settings.", "OK");
    }

    private static bool IsExcludedName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        string n = name.ToLowerInvariant();
        return
            n.Contains("controller") ||
            n.Contains("windcontrol") ||
            n.Contains("vegetationbendcontrol") ||
            n.Contains("butterfly") ||
            n.Contains("spawnarea") ||
            n.Contains("particles") ||
            n.Contains("godrays") ||
            n == "water" ||
            n.Contains("waterlily") ||
            n.Contains("lilypads") ||
            n.Contains("floatingleaf");
    }

    private static VegetationCategory Categorize(string name)
    {
        string n = (name ?? "").ToLowerInvariant();

        if (n.Contains("tree") || n.Contains("fir") || n.Contains("willow") || n.Contains("broadleaf") || n.Contains("blossom"))
            return VegetationCategory.Tree;

        if (n.Contains("rock") || n.Contains("stone") || n.Contains("cliff") || n.Contains("stones"))
            return VegetationCategory.Rock;

        if (n.Contains("grass") || n.Contains("flowermeadow"))
            return VegetationCategory.Grass;

        if (n.Contains("flower") || n.Contains("plant") || n.Contains("bush") || n.Contains("branch") || n.Contains("reeds") || n.Contains("cattail"))
            return VegetationCategory.Plant;

        return VegetationCategory.Other;
    }

    private static float DefaultMinScale(VegetationCategory cat)
    {
        switch (cat)
        {
            case VegetationCategory.Tree: return 0.9f;
            case VegetationCategory.Rock: return 0.8f;
            case VegetationCategory.Plant: return 0.75f;
            case VegetationCategory.Grass: return 0.8f;
            default: return 0.9f;
        }
    }

    private static float DefaultMaxScale(VegetationCategory cat)
    {
        switch (cat)
        {
            case VegetationCategory.Tree: return 1.2f;
            case VegetationCategory.Rock: return 1.45f;
            case VegetationCategory.Plant: return 1.25f;
            case VegetationCategory.Grass: return 1.15f;
            default: return 1.1f;
        }
    }
}


