using Microsoft.EntityFrameworkCore;

namespace Planner.Data;

public class PlannerDb(DbContextOptions<PlannerDb> options) : DbContext(options)
{
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<StatusChange> StatusChanges => Set<StatusChange>();
    public DbSet<BusinessPriority> BusinessPriorities => Set<BusinessPriority>();
    public DbSet<BusinessLink> BusinessLinks => Set<BusinessLink>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BusinessLink>().HasIndex(x => x.IssueKey);
        b.Entity<StatusChange>().HasIndex(x => x.Key);
    }
}
