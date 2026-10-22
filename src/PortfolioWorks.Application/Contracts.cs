using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed record Discipline(int Id, string Name, IReadOnlyCollection<int> Semesters);

public interface IStudentCurriculumProvider
{
    Task<IReadOnlyCollection<Discipline>> GetCurriculumAsync(string studentEmail, CancellationToken cancellationToken);
}

public interface IUploadedWorkRepository
{
    Task AddAsync(UploadedWork work, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail, CancellationToken cancellationToken);
}

public interface IWorkFileStorage
{
    Task StoreAsync(string storedFileName, Stream content, CancellationToken cancellationToken);
    Task DeleteIfExistsAsync(string storedFileName, CancellationToken cancellationToken);
}

public sealed record UploadWorkRequest(string StudentEmail, int DisciplineId, string FileName, string ContentType,
    Stream Content);

public sealed record UploadWorkResult(bool IsSuccess, string? Error, UploadedWork? Work)
{
    public static UploadWorkResult Failure(string error) => new(false, error, null);
    public static UploadWorkResult Success(UploadedWork work) => new(true, null, work);
}

public interface IWorkSubmissionService
{
    Task<UploadWorkResult> UploadAsync(UploadWorkRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail, CancellationToken cancellationToken);
}

public static class StudentEmail
{
    public const int MaximumLength = 320;

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string? email)
    {
        var normalized = Normalize(email);
        if (normalized.Length is 0 or > MaximumLength) return false;
        var at = normalized.IndexOf('@');
        return at > 0 && at < normalized.Length - 1 && !normalized.Contains(' ');
    }
}
