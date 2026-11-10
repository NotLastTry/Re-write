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

            if (camera.GetComponent<UnderwaterEffect>() == null)
                camera.gameObject.AddComponent<UnderwaterEffect>();

            PlayerController player = camera.GetComponentInParent<PlayerController>();

            if (player == null)
            {
                // Старый режим без префаба: летающая камера и телепорт в точку обзора.
                if (camera.GetComponent<FlyCamera>() == null)
                {
                    camera.gameObject.AddComponent<FlyCamera>();
                    camera.transform.SetPositionAndRotation(
                        new Vector3(0f, 100f, -70f),
                        Quaternion.Euler(15f, 0f, 0f));
                }

                var playerObject = new GameObject("Player");
                playerObject.transform.position = camera.transform.position;

                var controller = playerObject.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = 0.35f;
                controller.center = new Vector3(0f, 0.9f, 0f);

                player = playerObject.AddComponent<PlayerController>();
                player.CameraTransform = camera.transform;

                camera.transform.SetParent(playerObject.transform, false);
                camera.transform.localPosition = new Vector3(0f, 1.6f, 0f);
                camera.transform.localRotation = Quaternion.identity;
            }
            else
            {
                // Игрок уже из префаба: FlyCamera не нужна.
                var fly = camera.GetComponent<FlyCamera>();
                if (fly != null)
                    fly.enabled = false;
            }

            if (camera.GetComponent<BlockInteraction>() == null)
                camera.gameObject.AddComponent<BlockInteraction>();
        }
    }
}