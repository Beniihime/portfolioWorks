using PortfolioWorks.Domain;

namespace PortfolioWorks.Application;

public sealed class WorkDeletionService(IUploadedWorkRepository repository, IWorkFileStorage storage)
    : IWorkDeletionService
{
    public async Task<bool> DeleteAsync(Guid workId, string studentEmail, CancellationToken cancellationToken)
    {
        var work = await repository.GetByIdAsync(workId, StudentEmail.Normalize(studentEmail), cancellationToken);
        if (work is null) return false;

        // Сначала объект в хранилище, затем запись в БД: если удаление записи
        // не удастся, повторный вызов безопасно завершит операцию, а файл
        // не останется доступным по устаревшим метаданным.
        await storage.DeleteIfExistsAsync(work.StoredFileName, cancellationToken);
        await repository.DeleteAsync(work.Id, cancellationToken);
        return true;
    }
}
