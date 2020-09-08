﻿namespace HDF5.NET
{
    public struct FractalHeapEntry
    {
        #region Properties

        public ulong Address { get; set; }
        public ulong FilteredSize { get; set; }
        public uint FilterMask { get; set; }

        #endregion
    }
}
