﻿using System.Collections.Generic;

namespace HDF5.NET
{
    public struct BTree2Record10 : IBTree2Record
    {
        #region Constructors

        public BTree2Record10(H5BinaryReader reader, Superblock superblock, ushort recordSize)
        {
            // address
            this.Address = superblock.ReadOffset(reader);

            // scaled offsets
            var dimensionality = (recordSize - superblock.OffsetsSize) / 8;
            this.ScaledOffsets = new ulong[dimensionality];

            for (int i = 0; i < dimensionality; i++)
            {
                this.ScaledOffsets[i] = reader.ReadUInt64();
            }
        }

        #endregion

        #region Properties

        public ulong Address { get; set; }
        public ulong[] ScaledOffsets { get; set; }

        #endregion
    }
}
