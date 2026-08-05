using System;
using System.IO;

using AppService.Acmebot.Internal;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Azure.Functions.Worker;

namespace AppService.Acmebot.Functions;

public class StaticPage
{
    private static readonly string s_wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    private static readonly FileExtensionContentTypeProvider s_contentTypeProvider = new();

    [Function($"{nameof(StaticPage)}_{nameof(Serve)}")]
    public IActionResult Serve(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "{*path}")] HttpRequest req,
        string path)
    {
        // The UI requires App Service Authentication; mirrors the in-process behavior.
        if (!IdentityExtensions.IsEasyAuthEnabled || !req.ParseClientPrincipal().IsAppAuthorized())
        {
            return new ForbidResult();
        }

        // Replaces HttpFunctionBase.LocalStaticApp(): serve files from wwwroot, falling
        // back to the directory's index.html for extensionless (SPA-style) paths.
        var relativePath = string.IsNullOrEmpty(path) ? "index.html" : path.Replace('/', Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(s_wwwroot, relativePath));

        // Prevent path traversal outside wwwroot
        if (!fullPath.StartsWith(s_wwwroot, StringComparison.OrdinalIgnoreCase))
        {
            return new NotFoundResult();
        }

        if (Directory.Exists(fullPath) || !Path.HasExtension(fullPath))
        {
            fullPath = Path.Combine(fullPath, "index.html");
        }

        if (!File.Exists(fullPath))
        {
            return new NotFoundResult();
        }

        if (!s_contentTypeProvider.TryGetContentType(fullPath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return new PhysicalFileResult(fullPath, contentType);
    }
}
