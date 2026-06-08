namespace Packer;

public sealed class PackerException : Exception
{
    public PackerException(string message)
        : base(message)
    {
    }
}
