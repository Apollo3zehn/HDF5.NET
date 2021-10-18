using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HDF5.NET
{
    internal record CopyInfo(
        ulong[] SourceDims,
        ulong[] SourceChunkDims,
        ulong[] TargetDims,
        ulong[] TargetChunkDims,
        Selection SourceSelection,
        Selection TargetSelection,
        Func<ulong[], Task<Memory<byte>>>? GetSourceBufferAsync,
        Func<ulong[], Stream>? GetSourceStream,
        Func<ulong[], Task<Memory<byte>>> GetTargetBufferAsync,
        int TypeSize
    );

    internal struct RelativeStep
    {
        public ulong[] Chunk { get; init; }

        public ulong Offset { get; init; }

        public ulong Length { get; init; }
    }

    internal static class SelectionUtils
    {
        public static IEnumerable<RelativeStep> Walk(int rank, ulong[] dims, ulong[] chunkDims, Selection selection)
        {
            /* check if there is anythng to do */
            if (selection.TotalElementCount == 0)
                yield break;

            /* validate rank */
            if (dims.Length != rank || chunkDims.Length != rank)
                throw new RankException($"The length of each array parameter must match the rank parameter.");

            /* prepare some useful arrays */
            var lastDim = rank - 1;
            var chunkLength = chunkDims.Aggregate(1UL, (x, y) => x * y);

            /* prepare last dimension variables */
            var lastChunkDim = chunkDims[lastDim];

            foreach (var step in selection.Walk(limits: dims))
            {
                /* validate rank */
                if (step.Coordinates.Length != rank)
                    throw new RankException($"The length of the step coordinates array must match the rank parameter.");

                var remaining = step.ElementCount;

                /* process slice */
                while (remaining > 0)
                {
#warning Performance issue.
                    var scaledOffsets = new ulong[rank];
                    var chunkOffsets = new ulong[rank];

                    for (int dimension = 0; dimension < rank; dimension++)
                    {
                        scaledOffsets[dimension] = step.Coordinates[dimension] / chunkDims[dimension];
                        chunkOffsets[dimension] = step.Coordinates[dimension] % chunkDims[dimension];
                    }

                    var offset = chunkOffsets.ToLinearIndex(chunkDims);
                    var currentLength = Math.Min(lastChunkDim - chunkOffsets[lastDim], remaining);

                    yield return new RelativeStep()
                    {
                        Chunk = scaledOffsets,
                        Offset = offset,
                        Length = currentLength
                    };

                    remaining -= currentLength;
                    step.Coordinates[lastDim] += currentLength;
                }
            }
        }

        public static async Task Copy(int sourceRank, int targetRank, CopyInfo copyInfo)
        {
            /* validate selections */
            if (copyInfo.SourceSelection.TotalElementCount != copyInfo.TargetSelection.TotalElementCount)
                throw new ArgumentException("The length of the source selection and target selection are not equal.");

            /* validate rank of dims */
            if (copyInfo.SourceDims.Length != sourceRank ||
                copyInfo.SourceChunkDims.Length != sourceRank ||
                copyInfo.TargetDims.Length != targetRank ||
                copyInfo.TargetChunkDims.Length != targetRank)
                throw new RankException($"The length of each array parameter must match the rank parameter.");

            /* walkers */
            var sourceWalker = SelectionUtils
                .Walk(sourceRank, copyInfo.SourceDims, copyInfo.SourceChunkDims, copyInfo.SourceSelection)
                .GetEnumerator();

            var targetWalker = SelectionUtils
               .Walk(targetRank, copyInfo.TargetDims, copyInfo.TargetChunkDims, copyInfo.TargetSelection)
               .GetEnumerator();

            /* select method */
            if (copyInfo.GetSourceBufferAsync is not null)
            {
                var copyTasks = SelectionUtils.CopyMemory(sourceWalker, targetWalker, copyInfo);
                var fifo = new BlockingCollection<CopyTask>(boundedCapacity: 100);

                var producer = Task.Run(() =>
                {
                    foreach (var copyTask in copyTasks)
                    {
                        Console.WriteLine("Add.");
                        fifo.Add(copyTask);
                    }

                    fifo.CompleteAdding();
                });

                var consumer = Task.Run(async () =>
                {
                    try
                    {
                        while (!fifo.IsCompleted)
                        {
                            var copyTask = default(CopyTask);

                            try
                            {
                                Console.WriteLine("Remove.");
                                copyTask = fifo.Take();
                            }
                            catch (InvalidOperationException)
                            {
                                //
                            }

                            var currentSourceBuffer = (await copyTask.SourceBufferTask)
                                .Slice(copyTask.SourceOffset, copyTask.Length);

                            var currentTargetBuffer = (await copyTask.TargetBufferTask)
                                .Slice(copyTask.TargetOffset, copyTask.Length);

                            currentSourceBuffer.CopyTo(currentTargetBuffer);
                        }
                    }
                    catch (Exception ex)
                    {

                        throw;
                    }
                });

                await Task.WhenAll(producer, consumer);
            }

            else if (copyInfo.GetSourceStream is not null)
            {
                SelectionUtils.CopyStream(sourceWalker, targetWalker, copyInfo);
            }

            else
            {
                new Exception($"Either GetSourceBuffer() or GetSourceStream must be non-null.");
            }
        }

        private struct CopyTask
        {
            public Task<Memory<byte>> SourceBufferTask;
            public int SourceOffset;
            public Task<Memory<byte>> TargetBufferTask;
            public int TargetOffset;
            public int Length;
        }

        private static IEnumerable<CopyTask> CopyMemory(IEnumerator<RelativeStep> sourceWalker, IEnumerator<RelativeStep> targetWalker, CopyInfo copyInfo)
        {
            /* initialize source walker */
            var sourceBufferTask = default(Task<Memory<byte>>);
            var lastSourceChunk = default(ulong[]);

            /* initialize target walker */
            var targetBufferTask = default(Task<Memory<byte>>);
            var lastTargetChunk = default(ulong[]);

            /* walk until end */
            while (sourceWalker.MoveNext())
            {
                /* load next source buffer */
                var sourceStep = sourceWalker.Current;

                if (sourceBufferTask is null /* if buffer not assigned yet */ ||
                    !sourceStep.Chunk.SequenceEqual(lastSourceChunk) /* or the chunk has changed */)
                {
                    sourceBufferTask = copyInfo.GetSourceBufferAsync(sourceStep.Chunk);
                    lastSourceChunk = sourceStep.Chunk;
                }

                var currentSourceOffset = (int)sourceStep.Offset * copyInfo.TypeSize;
                var currentSourceLength = (int)sourceStep.Length * copyInfo.TypeSize;

                var currentTargetOffset = default(int);
                var currentTargetLength = default(int);

                while (currentSourceLength > 0)
                {
                    /* load next target buffer */
                    if (currentTargetLength == 0)
                    {
                        var success = targetWalker.MoveNext();
                        var targetStep = targetWalker.Current;

                        if (!success || targetStep.Length == 0)
                            throw new UriFormatException("The target walker stopped early.");

                        if (targetBufferTask is null /* if buffer not assigned yet */ ||
                            !targetStep.Chunk.SequenceEqual(lastTargetChunk) /* or the chunk has changed */)
                        {
                            targetBufferTask = copyInfo.GetTargetBufferAsync(targetStep.Chunk);
                            lastTargetChunk = targetStep.Chunk;
                        }

                        currentTargetOffset = (int)targetStep.Offset * copyInfo.TypeSize;
                        currentTargetLength = (int)targetStep.Length * copyInfo.TypeSize;
                    }

                    /* copy */
                    var length = Math.Min(currentSourceLength, currentTargetLength);

                    yield return new CopyTask() 
                    {
                        SourceBufferTask = sourceBufferTask,
                        SourceOffset = currentSourceOffset,
                        TargetBufferTask = targetBufferTask,
                        TargetOffset = currentTargetOffset,
                        Length = length,
                    };

                    currentSourceOffset += length;
                    currentSourceLength -= length;

                    currentTargetOffset += length;
                    currentTargetLength -= length;
                }
            }
        }

        private static void CopyStream(IEnumerator<RelativeStep> sourceWalker, IEnumerator<RelativeStep> targetWalker, CopyInfo copyInfo)
        {
            /* initialize source walker */
            var sourceStream = default(Stream);
            var lastSourceChunk = default(ulong[]);

            /* initialize target walker */
            var targetBuffer = default(Memory<byte>);
            var lastTargetChunk = default(ulong[]);
            var currentTarget = default(Memory<byte>);

            /* walk until end */
            while (sourceWalker.MoveNext())
            {
                /* load next source stream */
                var sourceStep = sourceWalker.Current;

                if (sourceStream is null /* if stream not assigned yet */ ||
                    !sourceStep.Chunk.SequenceEqual(lastSourceChunk) /* or the chunk has changed */)
                {
                    sourceStream = copyInfo.GetSourceStream(sourceStep.Chunk);
                    lastSourceChunk = sourceStep.Chunk;
                }

                sourceStream?.Seek((int)sourceStep.Offset * copyInfo.TypeSize, SeekOrigin.Begin);        // corresponds to 
                var currentLength = (int)sourceStep.Length * copyInfo.TypeSize;                          // sourceBuffer.Slice()

                while (currentLength > 0)
                {
                    /* load next target buffer */
                    if (currentTarget.Length == 0)
                    {
                        var success = targetWalker.MoveNext();
                        var targetStep = targetWalker.Current;

                        if (!success || targetStep.Length == 0)
                            throw new UriFormatException("The target walker stopped early.");

                        if (targetBuffer.Length == 0 /* if buffer not assigned yet */ ||
                            !targetStep.Chunk.SequenceEqual(lastTargetChunk) /* or the chunk has changed */)
                        {
                            targetBuffer = copyInfo.GetTargetBufferAsync(targetStep.Chunk).Result;
                            lastTargetChunk = targetStep.Chunk;
                        }

                        currentTarget = targetBuffer.Slice(
                            (int)targetStep.Offset * copyInfo.TypeSize,
                            (int)targetStep.Length * copyInfo.TypeSize);
                    }

                    /* copy */
                    var length = Math.Min(currentLength, currentTarget.Length);
                    sourceStream?.Read(currentTarget.Slice(0, length).Span);                             // corresponds to span.CopyTo

                    sourceStream?.Seek((int)sourceStep.Offset * copyInfo.TypeSize, SeekOrigin.Begin);    // corresponds to 
                    currentLength -= (int)sourceStep.Length * copyInfo.TypeSize;                         // sourceBuffer.Slice()

                    currentTarget = currentTarget.Slice(length);
                }
            }
        }
    }
}

