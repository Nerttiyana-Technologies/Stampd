using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Storage;

namespace Stampd.Storage.FileSystem;

public static class FileSystemStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FileSystemDocumentStorageProvider"/> rooted at
    /// <paramref name="rootDirectory"/>. Creates the directory if it doesn't exist.
    /// </summary>
    public static IServiceCollection AddFileSystemDocumentStorage(
        this IServiceCollection services,
        string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        services.TryAddSingleton<IDocumentStorageProvider>(
            _ => new FileSystemDocumentStorageProvider(rootDirectory));

        return services;
    }
}
