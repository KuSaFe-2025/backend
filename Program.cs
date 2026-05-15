
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;
using KuSaFeBackend.Services;

namespace KuSaFeBackend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddDbContext<AppDbContext>(options =>
            {
                var cs = builder.Configuration.GetConnectionString("DefaultConnection");
                var provider = builder.Configuration["Database:Provider"];
                if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                    options.UseSqlite(cs);
                else
                    options.UseNpgsql(cs);
            });

            // Add services to the container.

            builder.Services.AddControllers();
            if (string.Equals(builder.Configuration["Moderation:Provider"], "Deterministic", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddSingleton<IGameModerationService, DeterministicGameModerationService>();
            }
            else
            {
                builder.Services.AddHttpClient<IGameModerationService, OllamaGameModerationService>(client =>
                {
                    var baseUrl = builder.Configuration["Moderation:OllamaBaseUrl"] ?? "http://localhost:11434";
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(60);
                });
            }

            // Registering AppLifetimeInfo as a singleton service
            builder.Services.AddSingleton<AppLifetimeInfo>();

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "KuSaFeBackend", Version = "v1" });

                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Вставь: Bearer <твой_access_token>"
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            });

            var jwtKey = builder.Configuration["Jwt:Key"]!;
            var jwtIssuer = builder.Configuration["Jwt:Issuer"];
            var jwtAudience = builder.Configuration["Jwt:Audience"];

            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),

                        ValidateIssuer = !string.IsNullOrWhiteSpace(jwtIssuer),
                        ValidIssuer = jwtIssuer,

                        ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
                        ValidAudience = jwtAudience,

                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(2)
                    };
                });

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("AdminOnly", p => p.RequireClaim("isAdmin", "true"));
            });

            const string DevCors = "DevCors";

            builder.Services.AddCors(options =>
            {
                options.AddPolicy(DevCors, policy =>
                {
                    policy
                        .WithOrigins(
                            "http://localhost:5173",
                            "http://127.0.0.1:5173"
                        )
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var resetOnStartup = app.Configuration.GetValue<bool>("Database:ResetOnStartup");

                if (resetOnStartup)
                {
                    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("E2E"))
                        throw new InvalidOperationException("Database:ResetOnStartup is allowed only in Development or E2E.");

                    db.Database.EnsureDeleted();
                    Console.WriteLine("! Database schema reset requested");
                }

                var created = db.Database.EnsureCreated(); // создаст таблицы, если их нет
                Console.WriteLine(created
                    ? "+ Database schema created"
                    : "~ Database schema already exists");
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseCors(DevCors);

            app.UseAuthentication();
            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
