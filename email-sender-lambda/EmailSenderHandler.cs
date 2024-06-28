// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Cppl.EmailOrigin;

public static partial class Extensions
{
}

public class Function(Amazon.S3.IAmazonS3 s3, Amazon.SimpleEmail.IAmazonSimpleEmailService ses)
{
    static Function() { /* start X-Ray here */ }
    
    public Function() : this(new Amazon.S3.AmazonS3Client(), new Amazon.SimpleEmail.AmazonSimpleEmailServiceClient()) { }

    public async Task<JsonObject> FunctionHandler(JsonObject input, ILambdaContext context)
    {
        var cts = new CancellationTokenSource(context.RemainingTime.Add(TimeSpan.FromMilliseconds(-100)));
        var bucket = input?.TryGetPropertyValue("bucket", out var u) == true ? u!.GetValue<string>() 
            : throw new InvalidDataException("Request is missing bucket.");
        var key = input?.TryGetPropertyValue("key", out var k) == true ? k!.GetValue<string>() 
            : throw new InvalidDataException("Request is missing key.");
                        
        await Console.Out.WriteLineAsync($"Raw Email Bucket is {bucket} and Key is {key}");

        using var ms = new MemoryStream();
        using (var response = await s3.GetObjectAsync(bucket, key, cts.Token)) {
            await response.ResponseStream.CopyToAsync(ms);
        }

        var sent = await ses.SendRawEmailAsync(new() { RawMessage = new() { Data = ms } }, cts.Token);
        return new() {
            ["status"] = sent.HttpStatusCode.ToString(),
            ["message_id"] = sent.MessageId
        };
    }
}
