using Microsoft.EntityFrameworkCore;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Data;

public sealed class TranscoderDbContext(DbContextOptions<TranscoderDbContext> options) : DbContext(options)
{
    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<MediaItemEntity> MediaItems => Set<MediaItemEntity>();
    public DbSet<WorkerEntity> Workers => Set<WorkerEntity>();
    public DbSet<WorkerPathCheckEntity> WorkerPathChecks => Set<WorkerPathCheckEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<ReviewItemEntity> ReviewItems => Set<ReviewItemEntity>();
    public DbSet<SystemSettingEntity> SystemSettings => Set<SystemSettingEntity>();
    public DbSet<ProfileEntity> Profiles => Set<ProfileEntity>();
    public DbSet<IntegrationEntity> Integrations => Set<IntegrationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LibraryEntity>().HasIndex(x => x.RootPath).IsUnique();
        modelBuilder.Entity<MediaItemEntity>().HasIndex(x => new { x.LibraryId, x.RelativePath }).IsUnique();
        modelBuilder.Entity<WorkerEntity>().HasIndex(x => x.WorkerId).IsUnique();
        modelBuilder.Entity<WorkerPathCheckEntity>().HasIndex(x => new { x.WorkerId, x.CheckId, x.MappingConfigHash });
        modelBuilder.Entity<JobEntity>().HasIndex(x => new { x.Status, x.JobType, x.CreatedUtc });
        modelBuilder.Entity<JobEntity>().HasIndex(x => new { x.MediaItemId, x.JobType, x.Status });
        modelBuilder.Entity<SystemSettingEntity>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<ProfileEntity>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<IntegrationEntity>().HasIndex(x => new { x.IntegrationType, x.Name }).IsUnique();
    }
}
