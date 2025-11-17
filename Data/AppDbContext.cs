using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    // Пока схемы нет – делаем одну тестовую сущность,
    // дальше её можно выкинуть или развить.
    public DbSet<User> Users { get; set; } = null!;
}

public class User
{
    public int Id { get; set; }                // PK
    public string Username { get; set; } = ""; // логин/ник
    public DateTime CreatedAt { get; set; }    // когда зарегистрирован
}