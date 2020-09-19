using Azure.WebJobs.Extensions.HttpApi;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class StaticPageFunctions : HttpFunctionBase
    {
        public StaticPageFunctions(IHttpContextAccessor httpContextAccessor)
            : base(httpContextAccessor)
        {
        }

        [FunctionName(nameof(AddCertificatePage))]
        public IActionResult AddCertificatePage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "static-page/add-certificate")] HttpRequest req,
            ILogger log)
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Forbid();
            }

            return File("static/add-certificate.html");
        }
    }
}
