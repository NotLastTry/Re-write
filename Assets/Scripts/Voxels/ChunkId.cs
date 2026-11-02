using System;

namespace Voxels
{
    public readonly struct ChunkId : IEquatable<ChunkId>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public ChunkId(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Перевод мировых координат в координаты чанка (работает и для отрицательных).</summary>
        public static ChunkId FromWorldPos(int x, int y, int z)
        {
            return new ChunkId(
                x >> VoxelConstants.ChunkShift,
                y >> VoxelConstants.ChunkShift,
                z >> VoxelConstants.ChunkShift);
        }

        public bool Equals(ChunkId other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is ChunkId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = X;
                hashCode = (hashCode * 397) ^ Y;
                hashCode = (hashCode * 397) ^ Z;
                return hashCode;
            }
        }

        public static bool operator ==(ChunkId left, ChunkId right) => left.Equals(right);
        public static bool operator !=(ChunkId left, ChunkId right) => !left.Equals(right);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}