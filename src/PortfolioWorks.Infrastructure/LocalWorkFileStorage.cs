using PortfolioWorks.Application;

namespace PortfolioWorks.Infrastructure;

public sealed class LocalWorkFileStorage(string rootPath) : IWorkFileStorage
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task StoreAsync(string storedFileName, Stream content, CancellationToken cancellationToken)
    {
        var path = GetPath(storedFileName);
        Directory.CreateDirectory(_rootPath);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task DeleteIfExistsAsync(string storedFileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(storedFileName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(string storedFileName)
    {
        if (Path.GetFileName(storedFileName) != storedFileName) throw new ArgumentException("Invalid stored file name.");
        return Path.Combine(_rootPath, storedFileName);
    }
}
