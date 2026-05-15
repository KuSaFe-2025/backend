using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/games")]
public class GamesController : ControllerBase
{
    private readonly AppDbContext _db;

    public GamesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = User.GetCurrentUserId();
        var isAdmin = User.IsCurrentUserAdmin();

        var items = await _db.Games
            .AsNoTracking()
            .Where(g => g.Status == GameStatus.Verified || (userId.HasValue && g.OwnerUserId == userId.Value) || isAdmin)
            .OrderByDescending(g => g.UpdatedAtUtc)
            .Select(g => new GameListItemDto(
                g.Id,
                g.Title,
                g.Description,
                g.DescriptionFormat,
                g.Tasks.Count,
                g.ThemeColor,
                g.Status,
                g.LastModeratedAtUtc,
                g.ModerationDecision,
                g.ModerationYesVotes,
                g.ModerationNoVotes,
                g.OwnerUser.DisplayName,
                isAdmin || (userId.HasValue && g.OwnerUserId == userId.Value)
            ))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("{gameId:guid}")]
    public async Task<IActionResult> GetOne(Guid gameId)
    {
        var userId = User.GetCurrentUserId();
        var isAdmin = User.IsCurrentUserAdmin();

        var game = await _db.Games
            .AsNoTracking()
            .Include(g => g.OwnerUser)
            .Include(g => g.Tasks)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game is null) return NotFound();

        var canEdit = isAdmin || (userId.HasValue && game.OwnerUserId == userId.Value);
        if (game.Status != GameStatus.Verified && !canEdit) return NotFound();

        var dto = new GameDetailsDto(
                game.Id,
                game.Title,
                game.Description,
                game.DescriptionFormat,
                game.CreatedAtUtc,
                game.ThemeColor,
                game.Status,
                game.LastModeratedAtUtc,
                game.ModerationDecision,
                game.ModerationYesVotes,
                game.ModerationNoVotes,
                game.OwnerUser.DisplayName,
                game.Tasks.Count,
                game.Tasks.GroupBy(t => t.Type).Select(x => new TaskTypeCountDto(x.Key, x.Count())).ToList(),
                canEdit
            );

        return Ok(dto);
    }
}
