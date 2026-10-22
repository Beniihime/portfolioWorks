using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

builder.Services.Configure<UmuOptions>(builder.Configuration.GetSection("Umu"));
builder.Services.AddGrpcClient<StudentService.StudentServiceClient>(options => options.Address = umuUri);
builder.Services.AddScoped<IStudentCurriculumProvider, UmuCurriculumProvider>();

builder.Services.AddDbContext<PortfolioDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IUploadedWorkRepository, UploadedWorkRepository>();
builder.Services.AddSingleton<IWorkFileStorage>(_ => new LocalWorkFileStorage(
    builder.Configuration["Storage:RootPath"] ?? "data/uploads"));
builder.Services.AddScoped<IWorkSubmissionService>(services => new WorkSubmissionService(
    services.GetRequiredService<IUploadedWorkRepository>(),
    services.GetRequiredService<IWorkFileStorage>(),
    services.GetRequiredService<IStudentCurriculumProvider>(),
    services.GetRequiredService<IOptions<UploadOptions>>().Value));

var app = builder.Build();

await ApplyMigrationsAsync(app.Services, app.Logger);

app.MapGrpcService<PortfolioService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();

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
