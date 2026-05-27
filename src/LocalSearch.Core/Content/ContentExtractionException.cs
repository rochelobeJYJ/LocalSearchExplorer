namespace LocalSearch.Core.Content;

public sealed class ContentExtractionException : Exception
{
    public ContentExtractionException(string message) : base(message)
    {
    }

    public ContentExtractionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
