using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;

namespace PortfolioWorks.Infrastructure;

public sealed class MinioOptions
{
    // host:port без схемы, например minio:9000.
    public string Endpoint { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public bool UseSsl { get; init; }
}

public sealed class MinioWorkFileStorage(IMinioClient client, IOptions<MinioOptions> options) : IWorkFileStorage
{
    private readonly string _bucket = options.Value.Bucket;

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), cancellationToken);
        if (!exists)
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), cancellationToken);
    }

    public async Task StoreAsync(string storedFileName, Stream content, CancellationToken cancellationToken)
    {
        ValidateName(storedFileName);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(storedFileName)
            .WithStreamData(content)
            .WithObjectSize(content.Length - content.Position), cancellationToken);
    }

    public async Task DeleteIfExistsAsync(string storedFileName, CancellationToken cancellationToken)
    {
        ValidateName(storedFileName);
        await client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(storedFileName), cancellationToken);
    }

    public async Task ReadAsync(string storedFileName, Func<Stream, CancellationToken, Task> read,
        CancellationToken cancellationToken)
    {
        ValidateName(storedFileName);
        try
        {
            await client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(storedFileName)
                .WithCallbackStream(read), cancellationToken);
        }
        catch (ObjectNotFoundException)
        {
            throw new WorkFileNotFoundException(storedFileName);
        }
    }

    private static void ValidateName(string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName) ||
            storedFileName.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            Path.GetFileName(storedFileName) != storedFileName)
            throw new ArgumentException("Invalid stored file name.", nameof(storedFileName));
    }
}
