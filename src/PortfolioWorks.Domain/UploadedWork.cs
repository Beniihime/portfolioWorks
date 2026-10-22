namespace PortfolioWorks.Domain;

public sealed class UploadedWork
{
    private UploadedWork()
    {
    }

    public UploadedWork(Guid id, string studentEmail, int disciplineId, string originalFileName,
        string storedFileName, string contentType, long sizeBytes, DateTimeOffset uploadedAt)
    {
        Id = id;
        StudentEmail = studentEmail;
        DisciplineId = disciplineId;
        OriginalFileName = originalFileName;
        StoredFileName = storedFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; }
    public string StudentEmail { get; private set; } = null!;
    public int DisciplineId { get; private set; }
    public string OriginalFileName { get; private set; } = null!;
    public string StoredFileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
}
