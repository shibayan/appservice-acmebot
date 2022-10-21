using System.IO;
using System.Text.Json;

using Azure.Core;
using Azure.Core.Pipeline;

namespace AppService.Acmebot.Internal;

public class ArmSdkMitigatePolicy : HttpPipelineSynchronousPolicy
{
    public override void OnReceivedResponse(HttpMessage message)
    {
        if (message.Request.Method != RequestMethod.Put || message.Response.ContentStream == null)
        {
            return;
        }

        var reader = new StreamReader(message.Response.ContentStream);
        var content = reader.ReadToEnd().Replace("\"keyVaultId\":\"\",\"keyVaultSecretName\":\"\",", "");
        var jsonDocument = JsonDocument.Parse(content);
        var stream = new MemoryStream();
        var writer = new Utf8JsonWriter(stream);
        jsonDocument.WriteTo(writer);
        writer.Flush();
        message.Response.ContentStream = stream;
        message.Response.ContentStream.Position = 0;
    }
}
