using Unity.Mathematics;

namespace Voxels
{
    /// <summary>fBm и ridged-шум на классическом Перлине из Unity.Mathematics.</summary>
    public static class Noise
    {
        /// <summary>Фрактальный броуновский шум. Возвращает примерно -1..1.</summary>
        public static float Fbm(float2 p, int octaves, float lacunarity = 2f, float gain = 0.5f, float offset = 0f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float normalization = 0f;

            for (int i = 0; i < octaves; i++)
            {
                var q = new float3(p.x * frequency, p.y * frequency, offset + i * 17.31f);
                sum += amplitude * noise.cnoise(q);
                normalization += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }

            return normalization > 0f ? sum / normalization : 0f;
        }

        /// <summary>Хребтовый шум (1 - |noise|)^2. Возвращает 0..1, даёт горные гребни.</summary>
        public static float Ridged(float2 p, int octaves, float offset = 0f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float normalization = 0f;

            for (int i = 0; i < octaves; i++)
            {
                var q = new float3(p.x * frequency, p.y * frequency, offset + i * 9.73f);
                float n = 1f - math.abs(noise.cnoise(q));
                n *= n;
                sum += amplitude * n;
                normalization += amplitude;
                amplitude *= 0.5f;
                frequency *= 2f;
            }

            return normalization > 0f ? sum / normalization : 0f;
        }
    }
}