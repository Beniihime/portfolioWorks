using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed class WorkDownloadService(IUploadedWorkRepository repository, IWorkFileStorage storage)
    : IWorkDownloadService
{
    public async Task<UploadedWork?> FindAsync(Guid workId, string studentEmail, CancellationToken cancellationToken)
    {
        var work = await repository.GetByIdAsync(workId, cancellationToken);
        // Чужую работу не отличаем от несуществующей, чтобы не раскрывать её наличие.
        return work is not null &&
               string.Equals(work.StudentEmail, StudentEmail.Normalize(studentEmail), StringComparison.Ordinal)
            ? work
            : null;
    }

    public Task ReadAsync(UploadedWork work, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken) =>
        storage.ReadAsync(work.StoredFileName, write, cancellationToken);
}
