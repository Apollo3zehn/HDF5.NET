﻿using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HDF5.NET
{
    internal abstract class H5D_Chunk : H5D_Base
    {
        #region Types

        protected record ChunkInfo(ulong Address, ulong Size, uint FilterMask)
        {
            public static ChunkInfo None { get; } = new ChunkInfo(Superblock.UndefinedAddress, 0, 0);
        }

        #endregion

        #region Fields

        private IChunkCache _chunkCache;
        private bool _indexAddressIsUndefined;

        #endregion

        #region Constructors

        public H5D_Chunk(H5Dataset dataset, H5DatasetAccess datasetAccess) :
           base(dataset, supportsBuffer: true, supportsStream: false, datasetAccess)
        {
            // H5Dchunk.c (H5D__chunk_set_info_real)

            var chunkCacheFactory = datasetAccess.ChunkCacheFactory;

            if (chunkCacheFactory is null)
                chunkCacheFactory = this.Dataset.File.ChunkCacheFactory;

            if (chunkCacheFactory is null)
                chunkCacheFactory = H5File.DefaultChunkCacheFactory;

            _chunkCache = chunkCacheFactory();

            _indexAddressIsUndefined = dataset.Context.Superblock.IsUndefinedAddress(dataset.InternalDataLayout.Address);
        }

        #endregion

        #region Properties

        public ulong[] RawChunkDims { get; private set; }

        public ulong[] ChunkDims { get; private set; }

        public byte ChunkRank { get; private set; }

        public ulong ChunkByteSize { get; private set; }

        public ulong[] Dims { get; private set; }

        public ulong[] MaxDims { get; private set; }

        public ulong[] ScaledDims { get; private set; }

        public ulong[] ScaledMaxDims { get; private set; }

        public ulong[] DownChunkCounts { get; private set; }

        public ulong[] DownMaxChunkCounts { get; private set; }

        public ulong TotalChunkCount { get; private set; }

        public ulong TotalMaxChunkCount { get; private set; }

        #endregion

        #region Methods

        public static H5D_Chunk Create(H5Dataset dataset, H5DatasetAccess datasetAccess)
        {
            return dataset.InternalDataLayout switch
            {
                DataLayoutMessage12 layout12        => new H5D_Chunk123_BTree1(dataset, layout12, datasetAccess),

                DataLayoutMessage4 layout4          => ((ChunkedStoragePropertyDescription4)layout4.Properties).ChunkIndexingType switch
                {
                    // the current, maximum, and chunk dimension sizes are all the same
                    ChunkIndexingType.SingleChunk       => new H5Dataset_Chunk_Single_Chunk4(dataset, layout4, datasetAccess),

                    // fixed maximum dimension sizes
                    // no filter applied to the dataset
                    // the timing for the space allocation of the dataset chunks is H5P_ALLOC_TIME_EARLY
                    ChunkIndexingType.Implicit          => new H5D_Chunk4_Implicit(dataset, layout4, datasetAccess),

                    // fixed maximum dimension sizes
                    ChunkIndexingType.FixedArray        => new H5D_Chunk4_FixedArray(dataset, layout4, datasetAccess),

                    // only one dimension of unlimited extent
                    ChunkIndexingType.ExtensibleArray   => new H5D_Chunk4_ExtensibleArray(dataset, layout4, datasetAccess),

                    // more than one dimension of unlimited extent
                    ChunkIndexingType.BTree2            => new H5D_Chunk4_BTree2(dataset, layout4, datasetAccess),
                    _                                   => throw new Exception("Unknown chunk indexing type.")
                },

                DataLayoutMessage3 layout3          => new H5D_Chunk123_BTree1(dataset, layout3, datasetAccess),

                _                                   => throw new Exception($"Data layout message type '{dataset.InternalDataLayout.GetType().Name}' is not supported.")
            };
        }

        public override void Initialize()
        {
            // H5Dchunk.c (H5D__chunk_set_info_real)

            this.RawChunkDims = this.GetRawChunkDims();
            this.ChunkDims = this.RawChunkDims[..^1].ToArray();
            this.ChunkRank = (byte)this.ChunkDims.Length;
            this.ChunkByteSize = H5Utils.CalculateSize(this.ChunkDims) * this.Dataset.InternalDataType.Size;
            this.Dims = this.Dataset.InternalDataspace.DimensionSizes;
            this.MaxDims = this.Dataset.InternalDataspace.DimensionMaxSizes;
            this.TotalChunkCount = 1;
            this.TotalMaxChunkCount = 1;

            this.ScaledDims = new ulong[this.ChunkRank];
            this.ScaledMaxDims = new ulong[this.ChunkRank];

            for (int i = 0; i < this.ChunkRank; i++)
            {
                this.ScaledDims[i] = H5Utils.CeilDiv(this.Dims[i], this.ChunkDims[i]);

                if (this.MaxDims[i] == H5Constants.Unlimited)
                    this.ScaledMaxDims[i] = H5Constants.Unlimited;

                else
                    this.ScaledMaxDims[i] = H5Utils.CeilDiv(this.MaxDims[i], this.ChunkDims[i]);

                this.TotalChunkCount *= this.ScaledDims[i];

                if (this.ScaledMaxDims[i] == H5Constants.Unlimited || this.TotalMaxChunkCount == H5Constants.Unlimited)
                    this.TotalMaxChunkCount = H5Constants.Unlimited;

                else
                    this.TotalMaxChunkCount *= this.ScaledMaxDims[i];
            }

            /* Get the "down" sizes for each dimension */
            this.DownChunkCounts = this.ScaledDims.AccumulateReverse();
            this.DownMaxChunkCounts = this.ScaledMaxDims.AccumulateReverse();
        }

        public override ulong[] GetChunkDims()
        {
            return this.ChunkDims;
        }

        public override Task<Memory<byte>> GetBufferAsync(ulong[] chunkIndices)
        {
            return _chunkCache.GetChunkAsync(chunkIndices, () => this.ReadChunkAsync(chunkIndices));
        }

        public override Stream? GetStream(ulong[] chunkIndices)
        {
            throw new NotImplementedException();
        }

        protected abstract ulong[] GetRawChunkDims();

        protected abstract ChunkInfo GetChunkInfo(ulong[] chunkIndices);

        private async Task<Memory<byte>> ReadChunkAsync(ulong[] chunkIndices)
        {
            var buffer = new byte[this.ChunkByteSize];

#warning This way, fill values will become part of the cache
            if (_indexAddressIsUndefined)
            {
                if (this.Dataset.InternalFillValue.Value is not null)
                    buffer.AsSpan().Fill(this.Dataset.InternalFillValue.Value);
            }
            else
            {
                var chunkInfo = this.GetChunkInfo(chunkIndices);

                if (this.Dataset.Context.Superblock.IsUndefinedAddress(chunkInfo.Address))
                {
                    if (this.Dataset.InternalFillValue.Value is not null)
                        buffer.AsSpan().Fill(this.Dataset.InternalFillValue.Value);
                }
                else
                {
#warning Better make one file stream per thread instead of per call
                    var fileStream = (FileStream)this.Dataset.Context.Reader.BaseStream;
                    using var fileStreamNew = File.OpenRead(fileStream.Name);
                    fileStreamNew.Seek((long)this.Dataset.Context.Reader.BaseAddress + (long)chunkInfo.Address, SeekOrigin.Begin);

                    await this.ReadChunkAsync(buffer, chunkInfo.Size, chunkInfo.FilterMask, fileStreamNew);
                }
            }

            return buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task ReadChunkAsync(Memory<byte> buffer, ulong rawChunkSize, uint filterMask, Stream stream)
        {
            if (this.Dataset.InternalFilterPipeline is null)
            {
                await stream.ReadAsync(buffer);
            }
            else
            {

                using var filterBufferOwner = MemoryPool<byte>.Shared.Rent((int)rawChunkSize);
                var filterBuffer = filterBufferOwner.Memory[0..(int)rawChunkSize];

                await stream.ReadAsync(filterBuffer);

                await Task.Run(() =>
                    H5Filter.ExecutePipeline(this.Dataset.InternalFilterPipeline.FilterDescriptions, filterMask, H5FilterFlags.Decompress, filterBuffer, buffer));
            }
        }

        #endregion
    }
}
