using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed record DisciplineGrade(int Value, string Text, string ShortText);

public sealed record Discipline(int Id, string Name, IReadOnlyCollection<int> Semesters,
    int ObjectTypeId, string ObjectTypeName, int PracticeTypeId, string PracticeTypeName,
    bool HasCourseWork, bool HasCourseProject)
{
    public const int DisciplineObjectTypeId = 2;
    public const int PracticeObjectTypeId = 3;

    // Заполняется только в GetDisciplines: работы и оценка студента по дисциплине.
    public IReadOnlyCollection<UploadedWork> Works { get; init; } = [];
    public DisciplineGrade? Grade { get; init; }

    public bool IsPractice => ObjectTypeId == PracticeObjectTypeId || PracticeTypeId > 0;

    public bool IsCourseWorkOrProject =>
        (ObjectTypeId is 0 or DisciplineObjectTypeId) && (HasCourseWork || HasCourseProject);

    public bool IsCourseWorkOrPractice => IsPractice || IsCourseWorkOrProject;
}

public sealed record StudyGroup(int StudentId, int GroupId, string GroupName, string SpecialtyName,
    string LevelName, IReadOnlyCollection<Discipline> Disciplines);

public interface IStudentCurriculumProvider
{
    // Возвращает учебные планы по записям студента: у человека может быть
    // несколько записей (например, бакалавриат и магистратура), каждой
    // соответствует своя учебная группа. includeGrades запрашивает оценки
    // в УМУ; для проверки загрузки и списка работ они не нужны.
    Task<IReadOnlyCollection<StudyGroup>> GetCurriculumAsync(string? studentEmail, int? studentId,
        bool includeGrades, CancellationToken cancellationToken);
}

public interface IStudentIdentityResolver
{
    Task<string?> FindEmailAsync(int studentId, CancellationToken cancellationToken);
}

public interface IUploadedWorkRepository
{
    Task AddAsync(UploadedWork work, CancellationToken cancellationToken);
    Task<UploadedWork?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail, CancellationToken cancellationToken);
}

public interface IWorkFileStorage
{
    Task StoreAsync(string storedFileName, Stream content, CancellationToken cancellationToken);
    Task DeleteIfExistsAsync(string storedFileName, CancellationToken cancellationToken);
    // Передаёт поток файла в read. Владение и освобождение потока остаётся
    // за реализацией хранилища.
    Task ReadAsync(string storedFileName, Func<Stream, CancellationToken, Task> read,
        CancellationToken cancellationToken);
}

public sealed class WorkFileNotFoundException(string storedFileName)
    : Exception($"Stored file '{storedFileName}' was not found.");

public interface IWorkDownloadService
{
    Task<UploadedWork?> FindAsync(Guid workId, string studentEmail, CancellationToken cancellationToken);
    Task ReadAsync(UploadedWork work, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken);
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
