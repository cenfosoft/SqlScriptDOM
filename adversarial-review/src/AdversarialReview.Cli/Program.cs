using System.Text.Json;
using AdversarialReview;
using AdversarialReview.Core.Metrics;

return await ReviewCommand.RunAsync(args);

internal static class ReviewCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length is >= 4 and <= 6 && args[0] == "baseline")
            {
                int months = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 6;
                int maxPages = args.Length > 5 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 100;
                string? token = Environment.GetEnvironmentVariable("GH_TOKEN");
                if (string.IsNullOrWhiteSpace(token))
                    throw new ContractException("Baseline collection requires GH_TOKEN; never pass tokens as CLI arguments.");
                using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
                using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
                var client = new GitHubBaselineClient(http, token, maxPages: maxPages);
                var report = await new BaselineCollector(client).CollectAsync(args[1], args[2], months, cancellation.Token);
                await WriteNewFileAsync(args[3], JsonSerializer.Serialize(report, Contracts.JsonOptions));
                Console.WriteLine($"Baseline written; completeness: {report.IsComplete}. Inspect diagnostics before using the dataset.");
                return report.IsComplete ? 0 : 3;
            }
            switch (args)
            {
                case ["validate-config", string path]:
                    ReviewConfiguration config = ReviewConfiguration.Parse(ReadDocument(path));
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        config.SchemaVersion,
                        config.Enabled,
                        executionBlockers = config.ExecutionBlockers()
                    }, Contracts.JsonOptions));
                    return 0;
                case ["validate", string schema, string path]:
                    Contracts.Validate(ReadDocument(path), schema);
                    Console.WriteLine($"Valid {schema} document (schema {Contracts.Version}).");
                    return 0;
                case ["render", string input, string output]:
                    AuditReport audit = Contracts.Read<AuditReport>(ReadDocument(input), "audit-report");
                    await WriteNewFileAsync(output, ReportRenderer.Render(audit));
                    Console.WriteLine("Advisory Markdown written; no PR comment was published.");
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: validate-config <path> | validate <schema-name> <path> | render <audit.json> <output.md> | baseline <owner> <repo> <output.json> [months] [max-pages]");
                    return 2;
            }
        }
        catch (Exception exception) when (exception is ContractException or ArgumentException)
        {
            Console.Error.WriteLine($"Validation failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Input/output failed ({exception.GetType().Name}); no document content is logged.");
            return 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"Collection failed ({exception.GetType().Name}); partial data is not represented as a complete baseline.");
            return 1;
        }
    }

    private static string ReadDocument(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > Contracts.MaximumDocumentBytes)
            throw new ContractException("Input file exceeds the document size limit.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static async Task WriteNewFileAsync(string path, string content)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(content);
    }
}
