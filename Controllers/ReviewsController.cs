using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/reviews")]
public class ReviewsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReviewsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int skip = 0, [FromQuery] int take = 10, [FromQuery] string sort = "new")
    {
        var isAdmin = User.IsCurrentUserAdmin();
        var query = _db.Reviews
            .AsNoTracking()
            .Where(r => r.GameId == null || (r.Game!.Status == GameStatus.Verified && !r.Game.IsPrivate));

        return Ok(await GamesController.BuildReviewsPage(query, isAdmin, skip, take, sort));
    }

    [Authorize]
    [HttpPost("site")]
    public async Task<IActionResult> CreateSiteReview([FromBody] ReviewCreateRequest req)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var text = (req.Text ?? string.Empty).Trim();
        if (req.Rating is < 1 or > 5) return BadRequest("Рейтинг должен быть от 1 до 5.");
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("Текст отзыва обязателен.");
        if (text.Length > 2000) return BadRequest("Текст отзыва слишком длинный.");

        var review = new Review
        {
            Id = Guid.NewGuid(),
            GameId = null,
            UserId = userId.Value,
            Rating = req.Rating,
            Text = text,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { review.Id });
    }
}

[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("v1/admin/reviews")]
public class AdminReviewsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminReviewsController(AppDbContext db) => _db = db;

    [HttpDelete("{reviewId:guid}")]
    public async Task<IActionResult> Delete(Guid reviewId)
    {
        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId);
        if (review is null) return NotFound();

        _db.Reviews.Remove(review);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
