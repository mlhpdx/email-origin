// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using Cppl.Utilities.AWS;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Cppl.EmailOrigin;

public static partial class Extensions
{
    public static string[] PromoteToStringArray(this JsonElement element) => element.ValueKind switch {
        JsonValueKind.Null => [],
        JsonValueKind.String => [ element.GetString()! ],
        JsonValueKind.Array => [ ..element.EnumerateArray().Select(e => e.GetString()) ],
        _ => throw new InvalidDataException("Value to promote is not a string or array.")
    };

    // TODO: implement template rendering with simple value replacement.
    public static string RenderTemplate(this string? template, JsonElement data) => template ?? string.Empty;
}

public class Function(Amazon.S3.IAmazonS3 s3)
{
    static Function() { /* start X-Ray here */ }
    
    public Function() : this(new Amazon.S3.AmazonS3Client()) { }

    public async Task<JsonObject> FunctionHandler(JsonObject input, ILambdaContext context)
    {
        var location = input.TryGetPropertyValue("location", out var d) ? d as JsonObject 
            : throw new InvalidDataException("Input is missing location.");
        var request = input.TryGetPropertyValue("request", out var r) ? r as JsonObject 
            : throw new InvalidDataException("Input is missing request.");

        var bucket = location?.TryGetPropertyValue("bucket", out var b) == true ? b!.GetValue<string>() 
            : throw new InvalidDataException("Location is missing bucket.");
                        
        var key = location?.TryGetPropertyValue("key", out var o) == true ? o!.GetValue<string>()
            : throw new InvalidDataException("Location is missing key.");

        if (string.IsNullOrEmpty(bucket)) throw new InvalidDataException("Input location has null or empty bucket name.");
        if (string.IsNullOrEmpty(key)) throw new InvalidDataException("Input location has null or empty object key.");

        await Console.Out.WriteLineAsync($"\nBucket: {bucket}\nKey: {key}");

        var processed_object_key = key.Replace(".queued.json", ".send.eml");
        var processed_object_uri = $"s3://{bucket}/{processed_object_key}";
        var output = new JsonObject() {
            ["bucket"] = bucket,
            ["key"] = processed_object_key,
            ["uri"] = processed_object_uri
        };

        await Console.Out.WriteLineAsync($"Processed object URI: {processed_object_uri}");

        try { 
            using var _ = await s3.GetObjectAsync(bucket, processed_object_key);
            await Console.Out.WriteLineAsync($"Processed object already exists, no need to proceed.");
            return output; // if the processed object already exists (no exception), don't process it again.
        } 
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { 
            await Console.Out.WriteLineAsync($"Processed object doesn't yet exist, continuing."); 
        }
        // ^^--^^ any other exception will propagate up and cause the lambda to fail.

        using var raw_request_stream = new SeekableS3Stream(s3, bucket, key, 1024*1024, 100);
        var json = JsonDocument.Parse(raw_request_stream);

        // TODO: load data object from S3 if data_uri is provided (otherwise use an empty object) for use 
        // in template evaluation.
        var empty = JsonDocument.Parse("{}");
        var data = json.RootElement.TryGetProperty("data", out var data_element) ? data_element.ValueKind switch {
            JsonValueKind.Null => empty,
            JsonValueKind.String when Uri.IsWellFormedUriString(data_element.GetString(), UriKind.Absolute) 
                => await await s3.GetObjectAsync(bucket, data_element.GetString()).ContinueWith(t => JsonDocument.ParseAsync(t.Result.ResponseStream)),
            _ => throw new InvalidDataException("Request `data` value is not a non-empty string formatted absolute S3 URI.")
        } : empty;

        var email = json.RootElement.TryGetProperty("email", out var email_element) ? email_element 
            : throw new InvalidDataException("Request is missing email.");

        var from = email.TryGetProperty("from", out var from_element) ? from_element.GetString() 
            : throw new InvalidDataException("Request is missing from.");

        string[] to = email.TryGetProperty("to", out var to_element) ? to_element.PromoteToStringArray() 
            : throw new InvalidDataException("Request is missing to.");
        string[] cc = email.TryGetProperty("cc", out var cc_element) ? cc_element.PromoteToStringArray() : [];
        string[] bcc = email.TryGetProperty("bcc", out var bcc_element) ? bcc_element.PromoteToStringArray() : [];
        string[] reply_to = email.TryGetProperty("reply_to", out var reply_to_element) ? reply_to_element.PromoteToStringArray() : [];

        var subject = email.TryGetProperty("subject", out var subject_element) ? subject_element.GetString()
            : throw new InvalidDataException("Request is missing subject.");

        var body = email.TryGetProperty("body", out var body_element) ? body_element
            : throw new InvalidDataException("Request is missing body.");

        body.TryGetProperty("text", out var text_element);
        body.TryGetProperty("html", out var html_element);

        // TODO: handle attachments as array of objects (rather than strings) with keys for s3 uri, file name,
        // and content type which should override what's on the object in S3.
        var attachments = email.TryGetProperty("attachments", out var attachments_element) ? attachments_element.EnumerateArray().ToArray() : null;

        // Create the email message using MkimeKit to format it as a raw email message, then save to S3
        // so it can be picked-up by the sender sfn.
        using var message = new MimeKit.MimeMessage();

        if (to.Length > 0) message.To.AddRange(to.Select(t => MimeKit.MailboxAddress.Parse(t)));
        if (cc.Length > 0) message.Cc.AddRange(cc.Select(c => MimeKit.MailboxAddress.Parse(c)));
        if (bcc.Length > 0) message.Bcc.AddRange(bcc.Select(b => MimeKit.MailboxAddress.Parse(b)));
        if (reply_to.Length > 0) message.ReplyTo.AddRange(reply_to.Select(r => MimeKit.MailboxAddress.Parse(r)));

        message.From.Add(MimeKit.MailboxAddress.Parse(from));
        message.Subject = subject;

        using var multipart = new MimeKit.Multipart("mixed");

        // this is a little gross, but since each attachment is going to be added to the email in base64, and there is a practical
        // limit of ~10MB on email size, loading all of the attachments into RAM at once is probably okay. Emails will send faster
        // and eventually I'll need to add handling here for large attachments (swap for per-recipient, pre-signed links).
        var attachment_responses = await Task.WhenAll((attachments ?? []).Select(a => s3.GetObjectAsync(bucket, a.GetString()!)));
        var attachment_tasks = attachment_responses
            .Select(response => (response, ms: new MemoryStream()))
            .Select(async a => { 
                await a.response.ResponseStream.CopyToAsync(a.ms);
                a.ms.Seek(0, SeekOrigin.Begin);
                return (key: a.response.Key, type: a.response.Headers.ContentType, encoding: a.response.Headers.ContentEncoding, a.ms);
            });
        var attachment_contents = await Task.WhenAll(attachment_tasks);
        var attachment_parts = attachment_contents.Select(c => new MimeKit.MimePart(c.type ?? "application/octet-stream")
            {
                FileName = c.key.Split('/').Last(),
                ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment),
                ContentTransferEncoding = MimeKit.ContentEncoding.Base64,
                Content = new MimeKit.MimeContent(c.encoding switch {
                    "gzip" => new System.IO.Compression.GZipStream(c.ms, System.IO.Compression.CompressionMode.Decompress),
                    "deflate" => new System.IO.Compression.DeflateStream(c.ms, System.IO.Compression.CompressionMode.Decompress),
                    "br" => new System.IO.Compression.BrotliStream(c.ms, System.IO.Compression.CompressionMode.Decompress),
                    _ => c.ms 
                })
            });
        var attachment_id_lookup = attachment_parts.ToDictionary(p => p.FileName, p => p.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId());
        foreach (var part in attachment_parts)
        {
            multipart.Add(part);
        }
 
        // TODO: handle inline images in html body (need to know the content ID for each image, which can be
        // found using the filename and `attachment_id_lookup`).
        Console.Out.WriteLine($"Body element types: {text_element.ValueKind}, {html_element.ValueKind}");
        MimeKit.MimeEntity display_part = (text_element.ValueKind, html_element.ValueKind) switch {
            (JsonValueKind.Null or JsonValueKind.Undefined, JsonValueKind.String) => new MimeKit.TextPart("html") { Text = html_element.GetString().RenderTemplate(data.RootElement) },
            (JsonValueKind.String, JsonValueKind.Null or JsonValueKind.Undefined) => new MimeKit.TextPart("plain") { Text = text_element.GetString().RenderTemplate(data.RootElement) },
            (JsonValueKind.String, JsonValueKind.String) => new MimeKit.MultipartAlternative() {
                new MimeKit.TextPart("plain") { Text = text_element.GetString().RenderTemplate(data.RootElement) },
                new MimeKit.TextPart("html") { Text = html_element.GetString().RenderTemplate(data.RootElement) },
            },
            (JsonValueKind.Null or JsonValueKind.Undefined, JsonValueKind.Null or JsonValueKind.Undefined) => throw new InvalidDataException("Request is missing body content (no text, no html)."),
            _ => throw new InvalidDataException("Request has invalid body content (must be `string` if provided).")
        };
        multipart.Add(display_part);

        message.Body = multipart;

        using var stream = new S3UploadStream(s3, processed_object_uri);
        await message.WriteToAsync(stream);

        return output;
    }
}
