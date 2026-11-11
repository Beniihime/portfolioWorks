using Microsoft.EntityFrameworkCore;
using PortfolioWorks.Application;
using PortfolioWorks.Domain;

namespace PortfolioWorks.Infrastructure;

public sealed class UploadedWorkRepository(PortfolioDbContext dbContext) : IUploadedWorkRepository
{
    public async Task AddAsync(UploadedWork work, CancellationToken cancellationToken)
    {
        dbContext.UploadedWorks.Add(work);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<UploadedWork?> GetByIdAsync(Guid id, string studentEmail, CancellationToken cancellationToken) =>
        dbContext.UploadedWorks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.StudentEmail == StudentEmail.Normalize(studentEmail),
                cancellationToken);

    public async Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail,
        CancellationToken cancellationToken) =>
        await dbContext.UploadedWorks.AsNoTracking()
            .Where(x => x.StudentEmail == StudentEmail.Normalize(studentEmail))
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailsAsync(
        IReadOnlyCollection<string> studentEmails, CancellationToken cancellationToken)
    {
        var normalized = studentEmails.Select(StudentEmail.Normalize).Distinct().ToArray();
        return await dbContext.UploadedWorks.AsNoTracking()
            .Where(x => normalized.Contains(x.StudentEmail))
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.UploadedWorks.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
}
