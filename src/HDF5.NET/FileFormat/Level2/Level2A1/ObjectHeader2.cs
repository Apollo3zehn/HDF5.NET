﻿using System.Text;

namespace HDF5.NET
{
    internal class ObjectHeader2 : ObjectHeader
    {
        #region Fields

        private byte _version;

        #endregion

        #region Constructors

        internal ObjectHeader2(H5Context context, byte version) : base(context)
        {
            var reader = context.Reader;

            // version
            Version = version;

            // flags
            Flags = (ObjectHeaderFlags)reader.ReadByte();

            // access time, modification time, change time and birth time
            if (Flags.HasFlag(ObjectHeaderFlags.StoreFileAccessTimes))
            {
                AccessTime = reader.ReadUInt32();
                ModificationTime = reader.ReadUInt32();
                ChangeTime = reader.ReadUInt32();
                BirthTime = reader.ReadUInt32();
            }

            // maximum compact attributes count and minimum dense attributes count
            if (Flags.HasFlag(ObjectHeaderFlags.StoreNonDefaultAttributePhaseChangeValues))
            {
                MaximumCompactAttributesCount = reader.ReadUInt16();
                MinimumDenseAttributesCount = reader.ReadUInt16();
            }

            // size of chunk 0
            var chunkFieldSize = (byte)(1 << ((byte)Flags & 0x03));
            SizeOfChunk0 = H5Utils.ReadUlong(reader, chunkFieldSize);

            // header messages
            var withCreationOrder = Flags.HasFlag(ObjectHeaderFlags.TrackAttributeCreationOrder);
            var messages = ReadHeaderMessages(context, SizeOfChunk0, version: 2, withCreationOrder);
            HeaderMessages.AddRange(messages);

            // TODO: H5OCache.c (L. 1595)  /* Gaps should only occur in chunks with no null messages */
            // TODO: read gap and checksum
        }

        #endregion

        #region Properties

        public static byte[] Signature { get; } = Encoding.ASCII.GetBytes("OHDR");

        public byte Version
        {
            get
            {
                return _version;
            }
            set
            {
                if (value != 2)
                    throw new FormatException($"Only version 2 instances of type {nameof(ObjectHeader2)} are supported.");

                _version = value;
            }
        }

        public ObjectHeaderFlags Flags { get; set; }
        public uint AccessTime { get; set; }
        public uint ModificationTime { get; set; }
        public uint ChangeTime { get; set; }
        public uint BirthTime { get; set; }
        public ushort MaximumCompactAttributesCount { get; set; }
        public ushort MinimumDenseAttributesCount { get; set; }
        public ulong SizeOfChunk0 { get; set; }
        public uint Checksum { get; set; }

        #endregion
    }
}
