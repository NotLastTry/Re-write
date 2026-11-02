using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// Временная точка входа: создаёт мир и вешает камеру, если их нет в сцене.
    /// Когда появится нормальная сцена — выключить (AutoStart = false).
    /// </summary>
    public static class VoxelBootstrap
    {
        const bool AutoStart = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            if (!AutoStart)
                return;

            if (Object.FindAnyObjectByType<VoxelEngine>() == null)
            {
                var root = new GameObject("Voxel World");
                root.AddComponent<VoxelEngine>();
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Voxels] Main Camera не найдена — мир без обзора.");
                return;
            }

            if (camera.GetComponent<FlyCamera>() == null)
            {
                camera.gameObject.AddComponent<FlyCamera>();
                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 100f, -70f),
                    Quaternion.Euler(15f, 0f, 0f));
            }
        }
    }
}