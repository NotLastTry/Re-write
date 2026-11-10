using UnityEditor;
using UnityEngine;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Ищет рендереры, в чьи границы попадает камера. Помогает найти сферу/скай-дом,
    /// которую near-плоскость камеры режет кругом.
    /// </summary>
    public static class FindRenderersAroundCamera
    {
        [MenuItem("Tools/Voxels/Find Renderers Around Camera")]
        static void Find()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogError("[Voxels] Main Camera не найдена.");
                return;
            }

            Vector3 position = camera.transform.position;
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

            int found = 0;

            foreach (MeshRenderer renderer in renderers)
            {
                Bounds bounds = renderer.bounds;
                if (!bounds.Contains(position))
                    continue;

                found++;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                string mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "?";

                Debug.Log(
                    $"[Voxels] Вокруг камеры: '{renderer.name}', mesh: '{mesh}', размер: {bounds.size}, " +
                    $"шейдер: {(renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "нет")}",
                    renderer);
            }

            if (found == 0)
                Debug.Log("[Voxels] Рендереров, содержащих камеру, не найдено.");
            else
                Debug.Log($"[Voxels] Найдено рендереров вокруг камеры: {found}. Смотри объекты с именем Sky/Sphere/Dome/Lens.");
        }
    }
}
