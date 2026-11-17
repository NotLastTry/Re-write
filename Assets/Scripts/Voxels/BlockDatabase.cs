using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// База блоков. Индекс в массиве совпадает с Id, поэтому чанки по-прежнему
    /// хранят только ushort и ничего не знают о ScriptableObject.
    /// </summary>
    [CreateAssetMenu(menuName = "Voxels/Block Database", fileName = "BlockDatabase")]
    public sealed class BlockDatabase : ScriptableObject
    {
        [SerializeField] BlockDefinition[] _blocks;

        public static BlockDatabase Current { get; private set; }

        public BlockDefinition[] Blocks => _blocks;

        public static void SetCurrent(BlockDatabase database)
        {
            Current = database;
        }

        public static BlockDatabase Ensure()
        {
            if (Current == null)
                Current = Load();

            return Current;
        }

        /// <summary>
        /// Загружает все BlockDefinition из Assets/Resources/Blocks.
        /// Если папка пуста — собирает встроенную базу.
        /// </summary>
        public static BlockDatabase Load()
        {
            BlockDefinition[] definitions = Resources.LoadAll<BlockDefinition>("Blocks");

            if (definitions == null || definitions.Length == 0)
                return CreateDefault();

            int maxId = 0;

            foreach (BlockDefinition definition in definitions)
            {
                if (definition != null)
                    maxId = Mathf.Max(maxId, definition.Id);
            }

            var blocks = new BlockDefinition[Mathf.Max(maxId + 1, BlockType.Count)];

            foreach (BlockDefinition definition in definitions)
            {
                if (definition == null)
                    continue;

                if (definition.Id < blocks.Length)
                    blocks[definition.Id] = definition;
                else
                    Debug.LogWarning($"[Voxels] BlockDefinition '{definition.name}' имеет Id {definition.Id} вне диапазона.");
            }

            var database = CreateInstance<BlockDatabase>();
            database.hideFlags = HideFlags.DontSave;
            database._blocks = blocks;
            return database;
        }

        public BlockDefinition Get(ushort id)
        {
            if (_blocks == null)
                return null;

            if (id < _blocks.Length && _blocks[id] != null && _blocks[id].Id == id)
                return _blocks[id];

            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_blocks[i] != null && _blocks[i].Id == id)
                    return _blocks[i];
            }

            return null;
        }

        public void SetBlocks(BlockDefinition[] blocks)
        {
            _blocks = blocks;
        }

        /// <summary>База по умолчанию, если ассет ещё не создан.</summary>
        public static BlockDatabase CreateDefault()
        {
            var database = CreateInstance<BlockDatabase>();
            var blocks = new BlockDefinition[BlockType.Count];

            for (int i = 0; i < blocks.Length; i++)
            {
                var block = (ushort)i;
                blocks[i] = CreateDefaultDefinition(block);
            }

            database._blocks = blocks;
            return database;
        }

        public static BlockDefinition CreateDefaultDefinition(ushort block)
        {
            var definition = CreateInstance<BlockDefinition>();
            definition.hideFlags = HideFlags.DontSave;
            definition.Id = block;
            definition.name = "Block_" + block;
            definition.DisplayName = BlockType.GetName(block);
            definition.Color = BlockType.GetColor(block);
            definition.Smoothness = BlockType.GetSmoothness(block);

            switch (block)
            {
                case BlockType.Air:
                    definition.IsSolid = false;
                    definition.IsPlaceable = false;
                    definition.IsTransparent = true;
                    break;
                case BlockType.Water:
                    definition.IsTransparent = true;
                    definition.IsWater = true;
                    definition.IsPlaceable = true;
                    definition.Smoothness = 0.25f;
                    break;
                case BlockType.Debug:
                    definition.IsPlaceable = false;
                    break;
            }

            return definition;
        }
    }
}
