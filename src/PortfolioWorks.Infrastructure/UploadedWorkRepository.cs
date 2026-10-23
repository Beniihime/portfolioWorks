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

    public Task<UploadedWork?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.UploadedWorks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<UploadedWork>> GetByStudentEmailAsync(string studentEmail,
        CancellationToken cancellationToken) =>
        await dbContext.UploadedWorks.AsNoTracking()
            .Where(x => x.StudentEmail == StudentEmail.Normalize(studentEmail))
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);
}
