using Microsoft.AspNetCore.Mvc;

namespace SignalTracker.Controllers
{
    public class BaseController : Controller
    {
        protected bool IsAngularRequest()
        {
            var isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";
        //chiued
            var acceptsJson = Request.Headers["Accept"].ToString().Contains("application/json");
            var customHeader = Request.Headers["X-App-Call"] == "Angular";

            return isAjax || acceptsJson || customHeader;
        }
        
    }
}


