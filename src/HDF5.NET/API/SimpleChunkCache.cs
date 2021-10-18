﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HDF5.NET
{
    public partial class SimpleChunkCache : IChunkCache
    {
        #region Constructors

        public SimpleChunkCache(int chunkSlotCount = 521, ulong byteCount = 1024 * 1024/*, double w0 = 0.75*/)
        {
            if (chunkSlotCount < 0)
                throw new Exception("The chunk slot count parameter must be >= 0.");

            //if (!(0 <= w0 && w0 <= 1))
            //    throw new ArgumentException("The parameter w0 must be in the range of 0..1 (inclusive).");

            this.ChunkSlotCount = chunkSlotCount;
            this.ByteCount = byteCount;

            _chunkInfoMap = new ConcurrentDictionary<ulong[], ChunkInfo>(new ArrayEqualityComparer());
        }

        #endregion

        #region Properties

        public int ChunkSlotCount { get; init; }

        public int ConsumedSlots => _chunkInfoMap.Count;

        public ulong ByteCount { get; init; }

        public ulong ConsumedBytes { get; private set; }

        #endregion

        #region Methods

        public async Task<Memory<byte>> GetChunkAsync(ulong[] indices, Func<Task<Memory<byte>>> chunkLoader)
        {
            if (_chunkInfoMap.TryGetValue(indices, out var chunkInfo))
            {
                chunkInfo.LastAccess = DateTime.Now;
            }
            else
            {
                var buffer = await chunkLoader.Invoke();
                chunkInfo = new ChunkInfo(LastAccess: DateTime.Now, buffer);
                var chunk = chunkInfo.Chunk;

                if ((ulong)chunk.Length <= this.ByteCount)
                {
                    while (_chunkInfoMap.Count >= this.ChunkSlotCount || this.ByteCount - this.ConsumedBytes < (ulong)chunk.Length)
                    {
                        this.Preempt();
                    }

                    this.ConsumedBytes += (ulong)chunk.Length;
                    _chunkInfoMap[indices] = chunkInfo;
                }
            }

            return chunkInfo.Chunk;
        }

        #endregion
    }
}
