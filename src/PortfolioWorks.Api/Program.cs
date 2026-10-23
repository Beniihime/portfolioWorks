using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio;
using PortfolioWorks.Api.Services;
using PortfolioWorks.Application;
using PortfolioWorks.Infrastructure;
using Umu;

var builder = WebApplication.CreateBuilder(args);

var uploadOptions = builder.Configuration.GetSection("Storage").Get<UploadOptions>() ?? new UploadOptions();
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Storage"));

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = (int)Math.Min(uploadOptions.MaximumFileSizeBytes + 1024 * 1024, int.MaxValue);
});

var connectionString = builder.Configuration.GetConnectionString("PortfolioWorks");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:PortfolioWorks is not configured. Set ConnectionStrings__PortfolioWorks, " +
        "use user-secrets or appsettings.Local.json.");

var umuAddress = builder.Configuration["Umu:Address"];
var umuAuthorization = builder.Configuration["Umu:Authorization"];
if (!Uri.TryCreate(umuAddress, UriKind.Absolute, out var umuUri))
    throw new InvalidOperationException("Umu:Address is not configured or is not an absolute URI.");
if (string.IsNullOrWhiteSpace(umuAuthorization))
    throw new InvalidOperationException("Umu:Authorization is not configured.");

var minioOptions = builder.Configuration.GetSection("Storage:Minio").Get<MinioOptions>() ?? new MinioOptions();
if (string.IsNullOrWhiteSpace(minioOptions.Endpoint))
    throw new InvalidOperationException("Storage:Minio:Endpoint is not configured.");
if (minioOptions.Endpoint.Contains("://", StringComparison.Ordinal))
    throw new InvalidOperationException(
        "Storage:Minio:Endpoint must be host:port without a scheme, for example minio:9000.");
if (string.IsNullOrWhiteSpace(minioOptions.AccessKey))
    throw new InvalidOperationException("Storage:Minio:AccessKey is not configured.");
if (string.IsNullOrWhiteSpace(minioOptions.SecretKey))
    throw new InvalidOperationException("Storage:Minio:SecretKey is not configured.");
if (string.IsNullOrWhiteSpace(minioOptions.Bucket))
    throw new InvalidOperationException("Storage:Minio:Bucket is not configured.");

builder.Services.Configure<UmuOptions>(builder.Configuration.GetSection("Umu"));
builder.Services.AddGrpcClient<StudentService.StudentServiceClient>(options => options.Address = umuUri);
builder.Services.AddScoped<IStudentCurriculumProvider, UmuCurriculumProvider>();
builder.Services.AddScoped<IStudentIdentityResolver, UmuStudentIdentityResolver>();

builder.Services.AddDbContext<PortfolioDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IUploadedWorkRepository, UploadedWorkRepository>();
builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection("Storage:Minio"));
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(minioOptions.Endpoint)
    .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
    .WithSSL(minioOptions.UseSsl)
    .Build());
builder.Services.AddSingleton<MinioWorkFileStorage>();
builder.Services.AddSingleton<IWorkFileStorage>(services => services.GetRequiredService<MinioWorkFileStorage>());
builder.Services.AddScoped<IWorkDownloadService, WorkDownloadService>();
builder.Services.AddScoped<IWorkSubmissionService>(services => new WorkSubmissionService(
    services.GetRequiredService<IUploadedWorkRepository>(),
    services.GetRequiredService<IWorkFileStorage>(),
    services.GetRequiredService<IStudentCurriculumProvider>(),
    services.GetRequiredService<IOptions<UploadOptions>>().Value));

var app = builder.Build();

await ApplyMigrationsAsync(app.Services, app.Logger);
await EnsureBucketAsync(app.Services, app.Logger);

app.MapGrpcService<PortfolioService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();

static async Task EnsureBucketAsync(IServiceProvider services, ILogger logger)
{
    const int maximumAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await services.GetRequiredService<MinioWorkFileStorage>().EnsureBucketAsync(CancellationToken.None);
            return;
        }
        catch (Exception exception) when (attempt < maximumAttempts)
        {
            logger.LogWarning(exception, "MinIO bucket initialization attempt {Attempt} failed, retrying", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

static async Task ApplyMigrationsAsync(IServiceProvider services, ILogger logger)
{
    const int maximumAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PortfolioDbContext>().Database.MigrateAsync();
            return;
        }
        catch (Exception exception) when (attempt < maximumAttempts)
        {
            logger.LogWarning(exception, "Database migration attempt {Attempt} failed, retrying", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
