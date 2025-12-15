using Microsoft.EntityFrameworkCore;
using KuSaFeBackend.Models;

namespace KuSaFeBackend;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<AnswerOption> AnswerOptions => Set<AnswerOption>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<Question>().HasIndex(x => new { x.QuizId, x.Order }).IsUnique();
        modelBuilder.Entity<QuizAttempt>().HasIndex(a => new { a.QuizId, a.IsPerfect, a.TotalTimeSeconds });

        modelBuilder.Entity<Question>()
            .HasOne(q => q.Quiz).WithMany(z => z.Questions)
            .HasForeignKey(q => q.QuizId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AnswerOption>()
            .HasOne(o => o.Question).WithMany(q => q.Options)
            .HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<QuizAttempt>()
            .HasOne(a => a.User).WithMany(u => u.Attempts)
            .HasForeignKey(a => a.UserId);

        modelBuilder.Entity<QuizAttempt>()
            .HasOne(a => a.Quiz).WithMany(q => q.Attempts)
            .HasForeignKey(a => a.QuizId);

        modelBuilder.Entity<AttemptAnswer>()
            .HasOne(x => x.Attempt).WithMany(a => a.Answers)
            .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AttemptAnswer>()
            .HasOne(x => x.SelectedOption).WithMany()
            .HasForeignKey(x => x.SelectedOptionId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Question>()
            .HasOne<AnswerOption>().WithMany()
            .HasForeignKey(q => q.CorrectOptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
