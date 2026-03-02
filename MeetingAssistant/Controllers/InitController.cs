using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Data;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InitController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public InitController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpGet]
        public async Task<IActionResult> GetHello(CancellationToken cancellationToken)
        {
            try
            {
                var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
                var databaseStatus = canConnect ? "connected" : "disconnected";

                return Ok(new
                {
                    Message = "t7yaty ya fandm",
                    DatabaseStatus = databaseStatus
                });
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    Message = "t7yaty ya fandm",
                    DatabaseStatus = "error",
                    Error = ex.Message
                });
            }
        }
    }
}
