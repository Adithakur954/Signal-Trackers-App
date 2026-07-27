using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;

namespace SignalTracker.Controllers
{
    // Generates the exact same Excel report as ExcelReportController,
    // but reads its data from an uploaded log zip instead of the database.
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExcelZipReportController : ExcelReportController
    {
        public ExcelZipReportController(ApplicationDbContext db, IHttpClientFactory httpClientFactory)
            : base(db, httpClientFactory)
        {
        }
    }
}
