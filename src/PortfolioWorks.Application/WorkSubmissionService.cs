using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed class WorkSubmissionService : IWorkSubmissionService
{
    private const int MaximumFileNameLength = 255;
    private const int MaximumContentTypeLength = 255;
    private readonly IUploadedWorkRepository _repository;
    private readonly IWorkFileStorage _storage;
    private readonly IStudentCurriculumProvider _curriculumProvider;
    private readonly UploadOptions _options;

    public WorkSubmissionService(IUploadedWorkRepository repository, IWorkFileStorage storage,
        IStudentCurriculumProvider curriculumProvider, UploadOptions options)
    {
        _repository = repository;
        _storage = storage;
        _curriculumProvider = curriculumProvider;
        _options = options;
    }

    public async Task<UploadWorkResult> UploadAsync(UploadWorkRequest request, CancellationToken cancellationToken)
    {
        if (!StudentEmail.IsValid(request.StudentEmail))
            return UploadWorkResult.Failure("Student email is invalid.");
        if (request.DisciplineId <= 0)
            return UploadWorkResult.Failure("Discipline id must be positive.");

        var studentEmail = StudentEmail.Normalize(request.StudentEmail);
        var curriculum = await _curriculumProvider.GetCurriculumAsync(studentEmail, cancellationToken);
        if (!curriculum.Any(x => x.Id == request.DisciplineId))
            return UploadWorkResult.Failure("The selected discipline is not in the student's curriculum.");

        var fileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaximumFileNameLength || fileName != request.FileName)
            return UploadWorkResult.Failure("File name is invalid.");

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || !_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return UploadWorkResult.Failure("File extension is not allowed.");

        var contentType = (request.ContentType ?? string.Empty).Trim();
        if (contentType.Length > MaximumContentTypeLength)
            return UploadWorkResult.Failure("Content type is invalid.");

        await using var limitedContent = new SizeLimitedStream(request.Content, _options.MaximumFileSizeBytes);
        var id = Guid.NewGuid();
        var storedFileName = id.ToString("N") + extension.ToLowerInvariant();
        try
        {
            await _storage.StoreAsync(storedFileName, limitedContent, cancellationToken);
            if (limitedContent.BytesRead == 0)
            {
                await _storage.DeleteIfExistsAsync(storedFileName, CancellationToken.None);
                return UploadWorkResult.Failure("File is empty.");
            }
            var work = new UploadedWork(id, studentEmail, request.DisciplineId, fileName, storedFileName,
                contentType, limitedContent.BytesRead, DateTimeOffset.UtcNow);
            await _repository.AddAsync(work, cancellationToken);
            return UploadWorkResult.Success(work);
        }
        catch (FileSizeLimitExceededException)
        {
            await _storage.DeleteIfExistsAsync(storedFileName, CancellationToken.None);
            return UploadWorkResult.Failure("File exceeds the configured size limit.");
        }
        catch
        {
            await _storage.DeleteIfExistsAsync(storedFileName, CancellationToken.None);
            throw;
        }
    }

    public Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail, CancellationToken cancellationToken) =>
        _repository.GetByStudentEmailAsync(StudentEmail.Normalize(studentEmail), cancellationToken);
}

public sealed class UploadOptions
{
    public long MaximumFileSizeBytes { get; init; } = 25 * 1024 * 1024;
    public string[] AllowedExtensions { get; init; } = [".pdf", ".doc", ".docx", ".zip"];
}

internal sealed class FileSizeLimitExceededException : Exception
{
}

internal sealed class SizeLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    public long BytesRead { get; private set; }
    public SizeLimitedStream(Stream inner, long maximumBytes) => (_inner, _maximumBytes) = (inner, maximumBytes);
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await _inner.ReadAsync(buffer, ct));
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    private int Count(int read)
    {
        BytesRead += read;
        if (BytesRead > _maximumBytes) throw new FileSizeLimitExceededException();
        return read;
    }
}
