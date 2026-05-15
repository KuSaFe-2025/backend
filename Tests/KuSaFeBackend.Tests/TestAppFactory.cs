using System.Linq;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KuSaFeBackend.Tests;

public sealed class TestAppFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Action<IServiceCollection>? _configureTestServices;

    public TestAppFactory(Action<IServiceCollection>? configureTestServices = null)
    {
        _configureTestServices = configureTestServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "TEST_TEST_TEST_TEST_TEST_TEST_TEST_TEST_32+", // >= 32 bytes желательно
                ["Jwt:Issuer"] = "kusafe-tests",
                ["Jwt:Audience"] = "kusafe-tests",
                ["Jwt:AccessMinutes"] = "15",
                ["Jwt:RefreshDays"] = "30",
                ["Moderation:OllamaBaseUrl"] = "http://localhost:11434",
                ["Moderation:Model"] = "llama3.1:8b",
                ["Moderation:Votes"] = "5"
            });
        });


        builder.ConfigureServices(services =>
        {
            // убираем реальную БД, подменяем на SQLite in-memory
            var dbOpt = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (dbOpt != null) services.Remove(dbOpt);

            services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(_connection));
            _configureTestServices?.Invoke(services);

            _connection.Open();

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    public async Task SeedAsync(Func<AppDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        Dispose();
    }
}
