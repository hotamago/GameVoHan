using UnityEngine;
using InfinityTerrain.Settings;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Manages terrain and water materials.
    /// </summary>
    public class MaterialManager
    {
        private Material terrainMaterial;
        private MaterialSettings materialSettings;
        private TerrainSettings terrainSettings;

        public Material TerrainMaterial => terrainMaterial;

        public MaterialManager(MaterialSettings materialSettings, TerrainSettings terrainSettings)
        {
            this.materialSettings = materialSettings;
            this.terrainSettings = terrainSettings;
        }

        /// <summary>
        /// Initialize terrain material with fallback logic.
        /// </summary>
        public void InitializeTerrainMaterial()
        {
            // Terrain Fallback Logic
            if (materialSettings.proceduralTerrainShader == null)
                materialSettings.proceduralTerrainShader = Shader.Find("Custom/ProceduralTerrain");

            // If still null or not supported, fallback to Standard
            bool useFallback = (materialSettings.proceduralTerrainShader == null);
            if (!useFallback && !materialSettings.proceduralTerrainShader.isSupported)
                useFallback = true;

            if (useFallback)
            {
                Debug.LogWarning("Custom Shader missing or not supported. Falling back to Standard.");
                materialSettings.proceduralTerrainShader = Shader.Find("Standard");
            }

            terrainMaterial = new Material(materialSettings.proceduralTerrainShader);
            if (useFallback) terrainMaterial.color = new Color(0.4f, 0.6f, 0.4f); // Green

            UpdateMaterialProperties();
        }

        /// <summary>
        /// Update material properties based on current settings.
        /// </summary>
        public void UpdateMaterialProperties()
        {
            if (terrainMaterial == null) return;

            // Set height multiplier
            terrainMaterial.SetFloat("_HeightMultiplier", terrainSettings.heightMultiplier);

            // Set height thresholds
            terrainMaterial.SetFloat("_WaterLevel", materialSettings.waterLevel);
            terrainMaterial.SetFloat("_BeachLevel", materialSettings.beachLevel);
            terrainMaterial.SetFloat("_GrassLevel", materialSettings.grassLevel);
            terrainMaterial.SetFloat("_RockLevel", materialSettings.rockLevel);
            terrainMaterial.SetFloat("_SnowLevel", materialSettings.snowLevel);
        }
    }
}

