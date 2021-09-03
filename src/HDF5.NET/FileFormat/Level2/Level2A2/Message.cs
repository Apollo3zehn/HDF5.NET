namespace HDF5.NET
{
    public abstract class Message : FileBlock
    {
        #region Constructors

        internal Message(H5BinaryReader reader) : base(reader)
        {
            //
        }

        #endregion
    }
}
