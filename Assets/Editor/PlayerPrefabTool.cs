using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Voxels;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Создаёт Assets/Voxels/Player.prefab: корень с CharacterController и PlayerController,
    /// внутри — камера с HDRP-данными, UnderwaterEffect и BlockInteraction.
    /// </summary>
    public static class PlayerPrefabTool
    {
        const string Path = "Assets/Voxels/Player.prefab";

        [MenuItem("Tools/Voxels/Create Player Prefab")]
        static void CreatePlayerPrefab()
        {
            var root = new GameObject("Player");

            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);

            var player = root.AddComponent<PlayerController>();

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            cameraObject.transform.localRotation = Quaternion.identity;

            var camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.5f;
            camera.farClipPlane = 1000f;

            if (cameraObject.GetComponent<AudioListener>() == null)
                cameraObject.AddComponent<AudioListener>();

            if (cameraObject.GetComponent<HDAdditionalCameraData>() == null)
                cameraObject.AddComponent<HDAdditionalCameraData>();

            if (cameraObject.GetComponent<UnderwaterEffect>() == null)
                cameraObject.AddComponent<UnderwaterEffect>();

            if (cameraObject.GetComponent<BlockInteraction>() == null)
                cameraObject.AddComponent<BlockInteraction>();

            player.CameraTransform = cameraObject.transform;

            PrefabUtility.SaveAsPrefabAsset(root, Path);
            Object.DestroyImmediate(root);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Path);
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);

            Debug.Log($"[Voxels] Префаб игрока создан: {Path}. Удали старую Main Camera из сцены и перетащи Player в сцену.");
        }
    }
}
