using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxels
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class Chunk : MonoBehaviour
    {
        // Порядок граней: -X, +X, -Y, +Y, -Z, +Z.
        // Обход вершин — по часовой стрелке при взгляде снаружи (лицевые треугольники Unity).
        static readonly Vector3[][] FaceVertices =
        {
            new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0) },
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) },
            new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1) },
        };

        static readonly Vector3Int[] FaceDirections =
        {
            new Vector3Int(-1, 0, 0),
            new Vector3Int(1, 0, 0),
            new Vector3Int(0, -1, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, 0, -1),
            new Vector3Int(0, 0, 1),
        };

        // Смещения соседей в плоском массиве (x*size + y)*size + z.
        static readonly int[] FaceOffsets =
        {
            -VoxelConstants.ChunkSize * VoxelConstants.ChunkSize,
            VoxelConstants.ChunkSize * VoxelConstants.ChunkSize,
            -VoxelConstants.ChunkSize,
            VoxelConstants.ChunkSize,
            -1,
            1,
        };

        static readonly Vector2[] QuadUvs =
        {
            new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0),
        };

        static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        readonly ushort[] _voxels = new ushort[VoxelConstants.ChunkVolume];
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly List<Vector2> _uvs = new List<Vector2>();
        readonly List<int>[] _triangles = CreateTriangleLists();
        readonly List<Color32> _colors = new List<Color32>();

        // Смещения соседей для AO: по 3 сэмпла на каждую вершину каждой грани.
        static readonly Vector3Int[] AoOffsets = BuildAoOffsets();
        static readonly float[] AoShade = { 1f, 0.8f, 0.62f, 0.45f };
        readonly bool[] _topMerged = new bool[VoxelConstants.ChunkVolume];
        bool _useVertexAo;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        MeshCollider _meshCollider;
        Mesh _mesh;

        public ChunkId Id { get; private set; }
        public ushort[] Voxels => _voxels;
        public bool IsEmpty { get; private set; } = true;
        /// <summary>Есть ли в чанке твёрдые блоки (заполняется генератором).</summary>
        public bool HasBlocks { get; set; }

        static List<int>[] CreateTriangleLists()
        {
            var lists = new List<int>[BlockType.Count];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<int>();
            return lists;
        }

        /// <summary>
        /// Для каждой грани и каждой её вершины запоминаем трёх соседей,
        /// которые дают затенение в углу: два по сторонам и один по диагонали.
        /// </summary>
        static Vector3Int[] BuildAoOffsets()
        {
            var u = new[]
            {
                new Vector3Int(0, 0, 1), // -X
                new Vector3Int(0, 1, 0), // +X
                new Vector3Int(1, 0, 0), // -Y
                new Vector3Int(0, 0, 1), // +Y
                new Vector3Int(0, 1, 0), // -Z
                new Vector3Int(1, 0, 0), // +Z
            };

            var v = new[]
            {
                new Vector3Int(0, 1, 0), // -X
                new Vector3Int(0, 0, 1), // +X
                new Vector3Int(0, 0, 1), // -Y
                new Vector3Int(1, 0, 0), // +Y
                new Vector3Int(1, 0, 0), // -Z
                new Vector3Int(0, 1, 0), // +Z
            };

            var offsets = new Vector3Int[6 * 4 * 3];

            for (int face = 0; face < 6; face++)
            {
                Vector3Int normal = FaceDirections[face];

                for (int vertex = 0; vertex < 4; vertex++)
                {
                    int su = (vertex == 1 || vertex == 2) ? 1 : -1;
                    int sv = vertex >= 2 ? 1 : -1;
                    int b = (face * 4 + vertex) * 3;

                    offsets[b] = normal + u[face] * su;
                    offsets[b + 1] = normal + v[face] * sv;
                    offsets[b + 2] = normal + u[face] * su + v[face] * sv;
                }
            }

            return offsets;
        }

        void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _meshCollider = GetComponent<MeshCollider>();
            if (_meshCollider == null)
                _meshCollider = gameObject.AddComponent<MeshCollider>();

            _mesh = new Mesh { name = "Chunk Mesh", indexFormat = IndexFormat.UInt32 };
            _meshFilter.sharedMesh = _mesh;

            // Инициализируем материалы заранее: от них зависит, нужен ли AO.
            VoxelMaterials.Get();
        }

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }

        public void Initialize(ChunkId id)
        {
            Id = id;
            name = "Chunk " + id;
            transform.SetPositionAndRotation(
                new Vector3(id.X, id.Y, id.Z) * VoxelConstants.ChunkSize,
                Quaternion.identity);
        }

        /// <summary>Локальные координаты 0..31.</summary>
        public ushort this[int x, int y, int z]
        {
            get => _voxels[(x * VoxelConstants.ChunkSize + y) * VoxelConstants.ChunkSize + z];
            set => _voxels[(x * VoxelConstants.ChunkSize + y) * VoxelConstants.ChunkSize + z] = value;
        }

        /// <summary>Строит меш: только грани на стыке с воздухом.</summary>
        public void RebuildMesh(World world)
        {
            using var meshMarker = VoxelProfiler.Mesh.Auto();
            if (!HasBlocks)
            {
                _mesh.Clear();
                _meshRenderer.enabled = false;
                IsEmpty = true;
                return;
            }

            _vertices.Clear();
            _normals.Clear();
            _uvs.Clear();
            _colors.Clear();
            _useVertexAo = VoxelMaterials.VertexColorSupported;

            for (int i = 0; i < _triangles.Length; i++)
                _triangles[i].Clear();

            MergeTopFaces();

            const int size = VoxelConstants.ChunkSize;
            int originX = Id.X * size;
            int originY = Id.Y * size;
            int originZ = Id.Z * size;
            int solidCount = 0;

            using (VoxelProfiler.MeshFaces.Auto())
            {
                for (int x = 0; x < size; x++)
                {
                    int xBase = x * size * size;

                    for (int y = 0; y < size; y++)
                    {
                        int yBase = xBase + y * size;

                        for (int z = 0; z < size; z++)
                        {
                            int index = yBase + z;
                            ushort block = _voxels[index];

                            if (block == BlockType.Air)
                                continue;

                            solidCount++;

                            for (int face = 0; face < FaceDirections.Length; face++)
                            {
                                if (face == 3 && _topMerged[index])
                                    continue;

                                Vector3Int direction = FaceDirections[face];
                                int nx = x + direction.x;
                                int ny = y + direction.y;
                                int nz = z + direction.z;

                                ushort neighbour;
                                if (nx >= 0 && nx < size && ny >= 0 && ny < size && nz >= 0 && nz < size)
                                    neighbour = _voxels[index + FaceOffsets[face]];
                                else
                                    neighbour = world.GetBlock(originX + nx, originY + ny, originZ + nz);

                                // Вода не скрывает грани камня (она прозрачная),
                                // а собственные грани рисует только к воздуху.
                                if (block == BlockType.Water)
                                {
                                    if (neighbour != BlockType.Air)
                                        continue;
                                }
                                else if (neighbour != BlockType.Air && neighbour != BlockType.Water)
                                {
                                    continue;
                                }

                                AddFace(face, x, y, z, block, world);
                            }
                        }
                    }
                }
            }

            IsEmpty = solidCount == 0;
            UploadMesh();
        }

        void MergeTopFaces()
        {
            using var greedyMarker = VoxelProfiler.MeshGreedy.Auto();

            const int size = VoxelConstants.ChunkSize;
            System.Array.Clear(_topMerged, 0, _topMerged.Length);

            for (int x = 1; x < size - 1; x++)
            {
                for (int z = 1; z < size - 1; z++)
                {
                    for (int y = 1; y < size - 1; y++)
                    {
                        int index = (x * size + y) * size + z;

                        if (_topMerged[index] || !CanMergeTop(x, y, z))
                            continue;

                        ushort block = _voxels[index];
                        int run = 1;

                        while (z + run < size - 1)
                        {
                            int next = (x * size + y) * size + z + run;

                            if (_topMerged[next] || _voxels[next] != block || !CanMergeTop(x, y, z + run))
                                break;

                            run++;
                        }

                        AddTopQuad(x, y, z, run, block);

                        for (int i = 0; i < run; i++)
                            _topMerged[(x * size + y) * size + z + i] = true;
                    }
                }
            }
        }

        bool CanMergeTop(int x, int y, int z)
        {
            const int size = VoxelConstants.ChunkSize;
            int index = (x * size + y) * size + z;
            ushort block = _voxels[index];

            if (block == BlockType.Air || block == BlockType.Water)
                return false;

            // Сверху и по 3x3 вокруг верхней плоскости должно быть пусто:
            // тогда AO у всех четырёх углов одинаковый (максимум).
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sample = ((x + dx) * size + (y + 1)) * size + (z + dz);
                    if (_voxels[sample] != BlockType.Air)
                        return false;
                }
            }

            return true;
        }

        void AddTopQuad(int x, int y, int z, int run, ushort block)
        {
            int baseIndex = _vertices.Count;
            float yTop = y + 1f;

            _vertices.Add(new Vector3(x, yTop, z));
            _vertices.Add(new Vector3(x, yTop, z + run));
            _vertices.Add(new Vector3(x + 1, yTop, z + run));
            _vertices.Add(new Vector3(x + 1, yTop, z));

            for (int i = 0; i < 4; i++)
                _normals.Add(Vector3.up);

            _uvs.Add(new Vector2(0f, 0f));
            _uvs.Add(new Vector2(0f, run));
            _uvs.Add(new Vector2(1f, run));
            _uvs.Add(new Vector2(1f, 0f));

            if (_useVertexAo)
            {
                var white = new Color32(255, 255, 255, 255);
                _colors.Add(white);
                _colors.Add(white);
                _colors.Add(white);
                _colors.Add(white);
            }

            List<int> triangles = _triangles[block];
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }

        void AddFace(int face, int x, int y, int z, ushort block, World world)
        {
            Vector3[] quad = FaceVertices[face];
            Vector3Int direction = FaceDirections[face];
            var normal = new Vector3(direction.x, direction.y, direction.z);
            int baseIndex = _vertices.Count;

            int originX = Id.X * VoxelConstants.ChunkSize;
            int originY = Id.Y * VoxelConstants.ChunkSize;
            int originZ = Id.Z * VoxelConstants.ChunkSize;

            for (int i = 0; i < 4; i++)
            {
                _vertices.Add(new Vector3(x, y, z) + quad[i]);
                _normals.Add(normal);
                _uvs.Add(QuadUvs[i]);
            }

            if (_useVertexAo)
            {
                using (VoxelProfiler.MeshAo.Auto())
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _colors.Add(AoColor(
                            world,
                            originX + x,
                            originY + y,
                            originZ + z,
                            face,
                            i));
                    }
                }
            }

            List<int> triangles = _triangles[block];
            for (int i = 0; i < QuadTriangles.Length; i++)
                triangles.Add(baseIndex + QuadTriangles[i]);
        }

        /// <summary>
        /// Классическое вершинное AO: угол затемняется, если рядом с ним есть блоки.
        /// Значение уходит в цвет вершины; его использует Shader Graph блоков.
        /// </summary>
        Color32 AoColor(World world, int x, int y, int z, int face, int vertex)
        {
            int b = (face * 4 + vertex) * 3;

            bool side1 = IsSolid(world, x + AoOffsets[b].x, y + AoOffsets[b].y, z + AoOffsets[b].z);
            bool side2 = IsSolid(world, x + AoOffsets[b + 1].x, y + AoOffsets[b + 1].y, z + AoOffsets[b + 1].z);
            bool corner = IsSolid(world, x + AoOffsets[b + 2].x, y + AoOffsets[b + 2].y, z + AoOffsets[b + 2].z);

            int level = (side1 && side2) ? 0 : 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0);
            byte shade = (byte)(AoShade[level] * 255f);

            return new Color32(shade, shade, shade, 255);
        }

        bool IsSolid(World world, int worldX, int worldY, int worldZ)
        {
            int originX = Id.X * VoxelConstants.ChunkSize;
            int originY = Id.Y * VoxelConstants.ChunkSize;
            int originZ = Id.Z * VoxelConstants.ChunkSize;

            if (worldX >= originX && worldX < originX + VoxelConstants.ChunkSize &&
                worldY >= originY && worldY < originY + VoxelConstants.ChunkSize &&
                worldZ >= originZ && worldZ < originZ + VoxelConstants.ChunkSize)
            {
                return this[
                    worldX & VoxelConstants.ChunkMask,
                    worldY & VoxelConstants.ChunkMask,
                    worldZ & VoxelConstants.ChunkMask] != BlockType.Air;
            }

            return world.GetBlock(worldX, worldY, worldZ) != BlockType.Air;
        }

        void UploadMesh()
        {
            using var uploadMarker = VoxelProfiler.MeshUpload.Auto();

            _mesh.Clear();
            _mesh.indexFormat = IndexFormat.UInt32;

            if (_vertices.Count == 0)
            {
                _meshRenderer.enabled = false;
                if (_meshCollider != null)
                {
                    _meshCollider.sharedMesh = null;
                    _meshCollider.enabled = false;
                }
                return;
            }

            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
            _mesh.SetUVs(0, _uvs);

            if (_useVertexAo && _colors.Count == _vertices.Count)
                _mesh.SetColors(_colors);

            _mesh.subMeshCount = BlockType.Count;
            for (int i = 0; i < BlockType.Count; i++)
                _mesh.SetTriangles(_triangles[i], i);
            _mesh.RecalculateBounds();

            Material[] materials = VoxelMaterials.Get();
            if (materials != null)
                _meshRenderer.sharedMaterials = materials;

            _meshRenderer.enabled = true;

            if (_meshCollider != null)
            {
                using (VoxelProfiler.Collider.Auto())
                {
                    // Переприсваиваем, чтобы Unity перезапекала коллайдер после правки блоков.
                    _meshCollider.sharedMesh = null;
                    _meshCollider.sharedMesh = _mesh;
                    _meshCollider.enabled = true;
                }
            }
        }
    }
}