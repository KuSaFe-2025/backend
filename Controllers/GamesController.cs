using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
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

    [HttpGet("featured")]
    public async Task<IActionResult> Featured()
    {
        var item = await _db.Games
            .AsNoTracking()
            .Where(g => g.Status == GameStatus.Verified)
            .OrderByDescending(g => g.Attempts.Count)
            .ThenByDescending(g => g.UpdatedAtUtc)
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
                false
            ))
            .FirstOrDefaultAsync();

        return item is null ? NoContent() : Ok(item);
    }

    [HttpGet("{gameId:guid}/attempts")]
    public async Task<IActionResult> Attempts(Guid gameId, [FromQuery] int skip = 0, [FromQuery] int take = 10, [FromQuery] string sort = "date_desc")
    {
        var access = await GetPublicGameAccess(gameId);
        if (!access.Exists) return NotFound();
        if (!access.CanView) return NotFound();

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        var query = _db.GameAttempts
            .AsNoTracking()
            .Include(a => a.User)
            .Where(a => a.GameId == gameId);

        query = sort switch
        {
            "date_asc" => query.OrderBy(a => a.FinishedAtUtc),
            "score_desc" => query.OrderByDescending(a => a.Score).ThenBy(a => a.TotalTimeMs),
            "score_asc" => query.OrderBy(a => a.Score).ThenBy(a => a.TotalTimeMs),
            "time_asc" => query.OrderBy(a => a.TotalTimeMs),
            "time_desc" => query.OrderByDescending(a => a.TotalTimeMs),
            _ => query.OrderByDescending(a => a.FinishedAtUtc)
        };

        var total = await query.CountAsync();
        var items = await query
            .Skip(skip)
            .Take(take)
            .Select(a => new GameAttemptListItemDto(a.Id, a.User.DisplayName, a.TotalTimeMs, a.FinishedAtUtc, a.Score, a.MaxScore))
            .ToListAsync();

        return Ok(new PageDto<GameAttemptListItemDto>(items, total, skip, take, skip + items.Count < total));
    }

    [HttpGet("{gameId:guid}/reviews")]
    public async Task<IActionResult> Reviews(Guid gameId, [FromQuery] int skip = 0, [FromQuery] int take = 10, [FromQuery] string sort = "new")
    {
        var access = await GetPublicGameAccess(gameId);
        if (!access.Exists) return NotFound();
        if (!access.CanView) return NotFound();

        var isAdmin = User.IsCurrentUserAdmin();
        return Ok(await BuildReviewsPage(_db.Reviews.AsNoTracking().Where(r => r.GameId == gameId), isAdmin, skip, take, sort));
    }

    [Authorize]
    [HttpPost("{gameId:guid}/reviews")]
    public async Task<IActionResult> CreateReview(Guid gameId, [FromBody] ReviewCreateRequest req)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var game = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();
        if (game.Status != GameStatus.Verified) return BadRequest("Отзывы можно оставлять только к опубликованным играм.");

        var text = (req.Text ?? string.Empty).Trim();
        if (req.Rating is < 1 or > 5) return BadRequest("Рейтинг должен быть от 1 до 5.");
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("Текст отзыва обязателен.");
        if (text.Length > 2000) return BadRequest("Текст отзыва слишком длинный.");

        var review = new Review
        {
            Id = Guid.NewGuid(),
            GameId = gameId,
            UserId = userId.Value,
            Rating = req.Rating,
            Text = text,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Reviews), new { gameId }, new { review.Id });
    }

    internal static async Task<PageDto<ReviewDto>> BuildReviewsPage(IQueryable<Review> query, bool canDelete, int skip, int take, string sort)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        query = sort switch
        {
            "rating_desc" => query.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedAtUtc),
            "rating_asc" => query.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedAtUtc),
            _ => query.OrderByDescending(r => r.CreatedAtUtc)
        };

        var total = await query.CountAsync();
        var items = await query
            .Skip(skip)
            .Take(take)
            .Select(r => new ReviewDto(
                r.Id,
                r.GameId,
                r.Game == null ? null : r.Game.Title,
                r.User.DisplayName,
                r.Rating,
                r.Text,
                r.CreatedAtUtc,
                canDelete
            ))
            .ToListAsync();

        return new PageDto<ReviewDto>(items, total, skip, take, skip + items.Count < total);
    }

    private async Task<(bool Exists, bool CanView)> GetPublicGameAccess(Guid gameId)
    {
        var userId = User.GetCurrentUserId();
        var isAdmin = User.IsCurrentUserAdmin();
        var game = await _db.Games.AsNoTracking().Where(g => g.Id == gameId).Select(g => new { g.Status, g.OwnerUserId }).FirstOrDefaultAsync();
        if (game is null) return (false, false);
        return (true, game.Status == GameStatus.Verified || isAdmin || (userId.HasValue && game.OwnerUserId == userId.Value));
    }
}
