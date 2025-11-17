using Microsoft.AspNetCore.Mvc;

namespace KuSaFeBackend.Controllers
{
    [ApiController]
    [Route("v1/health")]
    public class HealthController : ControllerBase
    {
        private readonly AppLifetimeInfo _lifetime;

        public HealthController(AppLifetimeInfo lifetime)
        {
            _lifetime = lifetime;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var now = DateTimeOffset.UtcNow;
            var uptime = now - _lifetime.StartedAtUtc;

            return Ok(new
            {
                status = "OK",
                currentTimeUtc = now,                 // текущее время (UTC)
                startedAtUtc = _lifetime.StartedAtUtc,
                uptimeSeconds = (long)uptime.TotalSeconds
            });
        }
    }
}
