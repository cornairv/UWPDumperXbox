using Microsoft.AspNetCore.Mvc;

ushort port = 8000;
const string UPLOAD_DIR = "uploads";
const int BUFFER_SIZE = 1024 * 1024 * 8;

if (args.Length > 0 && !ushort.TryParse(args[0], out port))
{
    Console.Error.WriteLine($"Invalid port number: {port}");
    return 1;
}

Directory.CreateDirectory(UPLOAD_DIR);

var builder = WebApplication.CreateBuilder();
builder.Logging.AddSystemdConsole();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
var app = builder.Build();

app.MapPost("/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart/form-data");
    }
    var form = await request.ReadFormAsync();
    foreach (var file in form.Files)
    {
        var filename = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(filename))
        {
            Console.Error.WriteLine($"Bad file name: {file.FileName}");
            continue;
        }
        app.Logger.LogInformation("{}", filename);
        var path = Path.Combine(UPLOAD_DIR, filename);
        await using var outputStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, true);
        await file.CopyToAsync(outputStream);
    }

    return Results.NoContent();

}).WithMetadata(
    new RequestSizeLimitAttribute(long.MaxValue),
    new RequestFormLimitsAttribute
    {
        MultipartBodyLengthLimit = long.MaxValue
    });

app.Run($"http://0.0.0.0:{port}");
return 0;