using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InitController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetHello()
        {
            return Ok("t7yaty ya fandm");
        }
    }
}
