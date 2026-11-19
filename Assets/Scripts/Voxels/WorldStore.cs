using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Voxels
{
    public struct WorldInfo
    {
        public string Name;
        public int Seed;
        public DateTime LastPlayed;
        public int EditCount;
    }

    /// <summary>
    /// Миры хранятся в Application.persistentDataPath/Worlds/&lt;имя&gt;/world.dat.
    /// В файле сид, имя, время и только изменённые игроком блоки.
    /// </summary>
    public static class WorldStore
    {
        const int Magic = 0x56584C32; // "VXL2"
        const int Version = 2;
        const string FileName = "world.dat";
        const string LastWorldKey = "voxels.lastworld";

        public static string Root => Path.Combine(Application.persistentDataPath, "Worlds");

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "World";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            name = name.Trim();

            if (name.Length > 40)
                name = name.Substring(0, 40);

            return string.IsNullOrWhiteSpace(name) ? "World" : name;
        }

        public static string WorldPath(string name)
            => Path.Combine(Root, SanitizeName(name), FileName);

        public static bool Exists(string name)
            => File.Exists(WorldPath(name));

        public static string UniqueName(string baseName)
        {
            string name = SanitizeName(baseName);

            if (!Exists(name))
                return name;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{name} {i}";
                if (!Exists(candidate))
                    return candidate;
            }

            return $"{name} {DateTime.Now.Ticks}";
        }

        public static string LastWorld
        {
            get => PlayerPrefs.GetString(LastWorldKey, string.Empty);
            set
            {
                PlayerPrefs.SetString(LastWorldKey, value);
                PlayerPrefs.Save();
            }
        }

        public static List<WorldInfo> ListWorlds()
        {
            var worlds = new List<WorldInfo>();

            if (!Directory.Exists(Root))
                return worlds;

            foreach (string directory in Directory.GetDirectories(Root))
            {
                string file = Path.Combine(directory, FileName);

                if (File.Exists(file) && TryReadInfo(directory, out WorldInfo info))
                    worlds.Add(info);
            }

            worlds.Sort((a, b) => b.LastPlayed.CompareTo(a.LastPlayed));
            return worlds;
        }

        public static bool TryReadInfo(string directory, out WorldInfo info)
        {
            info = default;

            try
            {
                string file = Path.Combine(directory, FileName);
                if (!File.Exists(file))
                    return false;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    return false;

                info.Seed = reader.ReadInt32();
                info.Name = reader.ReadString();
                info.LastPlayed = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                info.EditCount = reader.ReadInt32();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Voxels] Не удалось прочитать мир: {exception.Message}");
                return false;
            }
        }

        public static bool TryReadSeed(string name, out int seed)
        {
            seed = 0;

            try
            {
                string file = WorldPath(name);
                if (!File.Exists(file))
                    return false;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    return false;

                seed = reader.ReadInt32();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Voxels] Не удалось прочитать сид: {exception.Message}");
                return false;
            }
        }

        public static void Save(World world, string name)
        {
            if (world == null || string.IsNullOrEmpty(name))
                return;

            try
            {
                string file = WorldPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(file));

                using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(stream);

                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(world.Generator.Seed);
                writer.Write(SanitizeName(name));
                writer.Write(DateTime.UtcNow.Ticks);
                writer.Write(world.EditCount);

                world.WriteEdits(writer);

                LastWorld = SanitizeName(name);
                Debug.Log($"[Voxels] Мир '{name}' сохранён: {world.EditCount} правок.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Voxels] Ошибка сохранения мира '{name}': {exception.Message}");
            }
        }

        public static void Load(World world, string name)
        {
            if (world == null)
                return;

            try
            {
                string file = WorldPath(name);
                if (!File.Exists(file))
                    return;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                {
                    Debug.LogWarning("[Voxels] Неизвестный формат мира.");
                    return;
                }

                int seed = reader.ReadInt32();
                reader.ReadString();
                reader.ReadInt64();
                reader.ReadInt32();

                if (seed != world.Generator.Seed)
                {
                    Debug.LogWarning($"[Voxels] Сид мира {seed} не совпадает с текущим {world.Generator.Seed}.");
                    return;
                }

                world.ReadEdits(reader);
                Debug.Log($"[Voxels] Мир '{name}' загружен: {world.EditCount} правок.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Voxels] Ошибка загрузки мира '{name}': {exception.Message}");
            }
        }

        public static void Delete(string name)
        {
            try
            {
                string directory = Path.Combine(Root, SanitizeName(name));

                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);

                if (LastWorld == SanitizeName(name))
                    LastWorld = string.Empty;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Voxels] Не удалось удалить мир '{name}': {exception.Message}");
            }
        }
    }
}
