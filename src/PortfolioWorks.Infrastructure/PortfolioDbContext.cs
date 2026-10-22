using Microsoft.EntityFrameworkCore;
using PortfolioWorks.Domain;

namespace PortfolioWorks.Infrastructure;

public sealed class PortfolioDbContext(DbContextOptions<PortfolioDbContext> options) : DbContext(options)
{
    public DbSet<UploadedWork> UploadedWorks => Set<UploadedWork>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var work = modelBuilder.Entity<UploadedWork>();
        work.ToTable("uploaded_works");
        work.HasKey(x => x.Id);
        work.Property(x => x.StudentEmail).HasMaxLength(320).IsRequired();
        work.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        work.Property(x => x.StoredFileName).HasMaxLength(64).IsRequired();
        work.Property(x => x.ContentType).HasMaxLength(255).IsRequired();
        // Индекс в deploy/postgres/init/001-create-uploaded-works.sql объявлен как
        // ("StudentEmail", "UploadedAt" DESC) — держим модель в соответствии с ним.
        work.HasIndex(x => new { x.StudentEmail, x.UploadedAt }).IsDescending(false, true);
    }
}
