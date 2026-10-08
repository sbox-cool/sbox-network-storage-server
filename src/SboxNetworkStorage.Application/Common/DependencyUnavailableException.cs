namespace SboxNetworkStorage.Application.Common;

public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(string dependencyName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DependencyName = dependencyName;
    }

    public string DependencyName { get; }
}
