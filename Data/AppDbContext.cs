using Microsoft.EntityFrameworkCore;
using KuSaFeBackend.Models;

namespace KuSaFeBackend;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameTask> GameTasks => Set<GameTask>();
    public DbSet<AnswerOption> AnswerOptions => Set<AnswerOption>();
    public DbSet<GameAttempt> GameAttempts => Set<GameAttempt>();
    public DbSet<GameTaskAnswer> GameTaskAnswers => Set<GameTaskAnswer>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<GameTask>().HasIndex(x => new { x.GameId, x.Order }).IsUnique();
        modelBuilder.Entity<GameAttempt>().HasIndex(a => new { a.GameId, a.IsPerfect, a.TotalTimeMs });

        modelBuilder.Entity<Game>()
            .HasOne(g => g.OwnerUser).WithMany(u => u.Games)
            .HasForeignKey(g => g.OwnerUserId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AnswerOption>()
            .HasOne(o => o.GameTask).WithMany(q => q.Options)
            .HasForeignKey(o => o.GameTaskId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GameTask>()
            .HasOne(q => q.Game).WithMany(z => z.Tasks)
            .HasForeignKey(q => q.GameId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GameAttempt>()
            .HasOne(a => a.User).WithMany(u => u.Attempts)
            .HasForeignKey(a => a.UserId);

        modelBuilder.Entity<GameAttempt>()
            .HasOne(a => a.Game).WithMany(q => q.Attempts)
            .HasForeignKey(a => a.GameId);

        modelBuilder.Entity<GameTaskAnswer>()
            .HasOne(x => x.Attempt).WithMany(a => a.Answers)
            .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GameTaskAnswer>()
            .HasOne(x => x.GameTask).WithMany(t => t.Answers)
            .HasForeignKey(x => x.GameTaskId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<GameTaskAnswer>()
            .HasOne(x => x.SelectedOption).WithMany()
            .HasForeignKey(x => x.SelectedOptionId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<GameTask>()
            .HasOne<AnswerOption>()
            .WithMany()
            .HasForeignKey(q => q.CorrectOptionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AnswerOption>()
            .Property(o => o.IsActive)
            .HasDefaultValue(true);
    }
}
