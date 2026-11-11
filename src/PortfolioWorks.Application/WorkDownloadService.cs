using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed class WorkDownloadService(IUploadedWorkRepository repository, IWorkFileStorage storage)
    : IWorkDownloadService
{
    public Task<UploadedWork?> FindAsync(Guid workId, string studentEmail, CancellationToken cancellationToken) =>
        // Чужую работу не отличаем от несуществующей, чтобы не раскрывать её наличие.
        repository.GetByIdAsync(workId, StudentEmail.Normalize(studentEmail), cancellationToken);

    public Task ReadAsync(UploadedWork work, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken) =>
        storage.ReadAsync(work.StoredFileName, write, cancellationToken);
}
