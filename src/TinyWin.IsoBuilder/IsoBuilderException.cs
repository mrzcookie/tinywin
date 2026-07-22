namespace TinyWin.IsoBuilder;

/// <summary>Raised when ISO extraction, preflight, writing, or verification fails.</summary>
public sealed class IsoBuilderException : Exception
{
    public IsoBuilderException()
    {
    }

    public IsoBuilderException(string message)
        : base(message)
    {
    }

    public IsoBuilderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
