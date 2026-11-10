using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Voxels;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Создаёт ассет материала воды (HDRP/Lit Transparent) и назначает его VoxelEngine.
    /// Ассет нужен, чтобы HDRP скомпилировал transparent-вариант шейдера заранее:
    /// у материала, созданного только в рантайме, вариант может не подхватиться,
    /// пока его не тронешь в инспекторе.
    /// </summary>
    public static class WaterMaterialTool
    {
        const string Folder = "Assets/Voxels";
        const string Path = Folder + "/Water.mat";

        [MenuItem("Tools/Voxels/Create Water Material")]
        static void CreateWaterMaterial()
        {
            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null)
            {
                Debug.LogError("[Voxels] Шейдер HDRP/Lit не найден.");
                return;
            }

            var material = new Material(shader) { name = "Water" };

            material.SetFloat("_SurfaceType", 1f);
            material.SetFloat("_BlendMode", 0f);
            material.SetFloat("_EnableFogOnTransparent", 0f);
            material.SetFloat("_TransparentSortPriority", 0f);
            material.SetInt("_TransparentCullMode", 0);
            material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0f);
            material.SetFloat("_AlphaCutoffEnable", 0f);
            material.SetFloat("_TransparentZWrite", 0f);
            material.SetInt("_ZTestTransparent", 4);

            material.SetColor("_BaseColor", new Color(0.08f, 0.26f, 0.48f, 0.45f));
            material.SetFloat("_Smoothness", 0.25f);
            material.SetFloat("_Metallic", 0f);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_BLENDMODE_ALPHA");
            material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");

            material.renderQueue = (int)RenderQueue.Transparent;

            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "Voxels");

            AssetDatabase.CreateAsset(material, Path);
            AssetDatabase.SaveAssets();

            var engine = Object.FindAnyObjectByType<VoxelEngine>();
            if (engine != null)
            {
                var serialized = new SerializedObject(engine);
                SerializedProperty property = serialized.FindProperty("_waterMaterial");

                if (property != null && property.objectReferenceValue == null)
                {
                    property.objectReferenceValue = material;
                    serialized.ApplyModifiedProperties();
                }
            }

            Selection.activeObject = material;
            EditorGUIUtility.PingObject(material);
            Debug.Log($"[Voxels] Материал воды создан: {Path}");
        }
    }
}
