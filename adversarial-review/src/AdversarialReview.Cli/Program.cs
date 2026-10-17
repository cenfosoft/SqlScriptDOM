using System.Text.Json;
using AdversarialReview;

return await ReviewCommand.RunAsync(args);

internal static class ReviewCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
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
                    return Task.FromResult(0);
                case ["validate", string schema, string path]:
                    Contracts.Validate(ReadDocument(path), schema);
                    Console.WriteLine($"Valid {schema} document (schema {Contracts.Version}).");
                    return Task.FromResult(0);
                default:
                    Console.Error.WriteLine("Usage: validate-config <path> | validate <schema-name> <path>");
                    return Task.FromResult(2);
            }
        }
        catch (Exception exception) when (exception is ContractException or ArgumentException)
        {
            Console.Error.WriteLine($"Validation failed: {exception.Message}");
            return Task.FromResult(1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Input/output failed ({exception.GetType().Name}); no document content is logged.");
            return Task.FromResult(1);
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
}
