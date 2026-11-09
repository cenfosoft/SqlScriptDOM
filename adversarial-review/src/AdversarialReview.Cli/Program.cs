using System.Text.Json;
using AdversarialReview;
using AdversarialReview.Core.Metrics;
using AdversarialReview.Copilot;

return await ReviewCommand.RunAsync(args);

internal static class ReviewCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length >= 7 && args[0] == "baseline")
            {
                if (!int.TryParse(args[1], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int months)
                    || months is < 1 or > 1200)
                    throw new ContractException("Window months must be an integer from 1 through 1200.");
                if (!int.TryParse(args[2], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int maxPages)
                    || maxPages is < 1 or > 1000)
                    throw new ContractException("Maximum pages must be an integer from 1 through 1000.");

                if (args[3] != "phase1-human-nondraft-merged-only")
                    throw new ContractException("Use the approved filter profile 'phase1-human-nondraft-merged-only'.");
                string jsonPath = Path.GetFullPath(args[4]);
                string markdownPath = Path.GetFullPath(args[5]);
                if (string.Equals(jsonPath, markdownPath, StringComparison.OrdinalIgnoreCase))
                    throw new ContractException("JSON and Markdown outputs must use different paths.");
                string[] repositories = args[6..].Select(ParseRepository).ToArray();
                if (repositories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != repositories.Length)
                    throw new ContractException("Repository sources must not contain duplicates.");
                if (!repositories.Contains("cenfosoft/SqlScriptDOM", StringComparer.OrdinalIgnoreCase)
                    || !repositories.Contains("microsoft/SqlScriptDOM", StringComparer.OrdinalIgnoreCase))
                    throw new ContractException("Include both cenfosoft/SqlScriptDOM and microsoft/SqlScriptDOM as read-only sources.");

                string? token = Environment.GetEnvironmentVariable("GH_TOKEN");
                if (string.IsNullOrWhiteSpace(token))
                    throw new ContractException("Baseline collection requires GH_TOKEN; never pass tokens as CLI arguments.");
                using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
                using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
                var client = new GitHubBaselineClient(http, token, maxPages: maxPages);
                var collector = new BaselineCollector(client);
                var reports = new List<BaselineReport>(repositories.Length);
                foreach (string repository in repositories)
                {
                    string[] parts = repository.Split('/');
                    reports.Add(await collector.CollectAsync(parts[0], parts[1], months, cancellation.Token));
                }

                var report = new BaselineCollectionReport(
                    "1.0",
                    DateTimeOffset.UtcNow,
                    months,
                    maxPages,
                    "phase1-human-nondraft-merged-only",
                    "First ready-for-review event; PR creation time only when the complete timeline shows it was initially non-draft.",
                    "GitHub merged_at timestamp. Closed PRs without a merge are excluded, not treated as completed integrations.",
                    "UTC",
                    [
                        "Only human-authored PRs (GitHub author type User and login not ending in [bot]).",
                        "Exclude missing/unknown author identity; malformed author or PR state makes the collection incomplete.",
                        "Exclude PRs that are drafts at extraction time.",
                        "Exclude closed PRs without merged_at.",
                        "Include human-authored dependency and documentation PRs; no topic or label exclusions are applied.",
                        "Use the start event, not PR creation date, to select the inclusive trailing-calendar-month window.",
                        "Require a complete, valid timeline; incomplete, malformed, or ambiguous readiness history is excluded and diagnosed.",
                        "Report open non-draft human PRs as a separate right-censored cohort through each repository's extraction timestamp.",
                        "Deduplicate by PR number; duplicates and inconsistent states make the collection incomplete.",
                        "Current-state API snapshot; it does not reconstruct historical draft/closed/reopened states."
                    ],
                    reports);
                string json = JsonSerializer.Serialize(report, Contracts.JsonOptions);
                string markdown = BaselineMarkdownRenderer.Render(report);
                await WriteBaselineOutputsAsync(jsonPath, json, markdownPath, markdown);
                bool complete = reports.All(item => item.IsComplete);
                Console.WriteLine($"Baseline JSON and Markdown written for {reports.Count} sources; all collections complete: {complete}. Inspect exclusions and diagnostics before use.");
                return complete ? 0 : 3;
            }
            switch (args)
            {
                case ["prepare-review", string configPath, string outputDirectory]:
                    ReviewConfiguration preparationConfig = ReviewConfiguration.Parse(ReadDocument(configPath));
                    string Required(string key) => Environment.GetEnvironmentVariable(key)
                        ?? throw new ContractException($"Required trusted Actions metadata is missing: {key}.");
                    LabelSnapshot snapshot = LabelSnapshot.Parse(ReadDocument(Required("GITHUB_EVENT_PATH")),
                        Required("GITHUB_EVENT_NAME"), Required("GITHUB_REPOSITORY"), Required("GITHUB_ACTOR"), preparationConfig);
                    string configurationSha = Required("GITHUB_SHA");
                    if (!Contracts.IsSha(configurationSha))
                        throw new ContractException("Trusted checkout revision is not a valid SHA.");
                    Directory.CreateDirectory(outputDirectory);
                    using (var apiHandler = new HttpClientHandler { AllowAutoRedirect = false })
                    using (var artifactHandler = new HttpClientHandler { AllowAutoRedirect = false })
                    using (var apiHttp = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(30) })
                    using (var artifactHttp = new HttpClient(artifactHandler) { Timeout = TimeSpan.FromSeconds(30) })
                    using (var preparationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
                    {
                        string credential = Required("GH_TOKEN");
                        var api = new GitHubReviewApi(apiHttp, credential, snapshot.Repository);
                        AuditReport prepared = await new ReviewPreparation(api, new(api, artifactHttp)).PrepareAsync(
                            preparationConfig, snapshot, configurationSha,
                            long.Parse(Required("GITHUB_RUN_ID"), System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(Required("GITHUB_RUN_ATTEMPT"), System.Globalization.CultureInfo.InvariantCulture), preparationTimeout.Token);
                        string json = Contracts.Write(prepared, "audit-report");
                        ReportRenderer.RejectCredentialData(json, [credential]);
                        await WriteNewFileAsync(Path.Combine(outputDirectory, "audit.json"), json);
                        await WriteNewFileAsync(Path.Combine(outputDirectory, "report.md"), ReportRenderer.Render(prepared, [credential]));
                        Console.WriteLine("Preparation report written. No inference, authorization consumption, or PR publication performed.");
                    }
                    return 0;
                case ["list-models"]:
                    string? copilotToken = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
                    if (string.IsNullOrWhiteSpace(copilotToken))
                        throw new ContractException("Set COPILOT_GITHUB_TOKEN to a supported Copilot credential for metadata-only discovery.");
                    if (copilotToken.StartsWith("ghp_", StringComparison.Ordinal))
                        throw new ContractException("Classic PATs are unsupported. Use Copilot OAuth or a fine-grained PAT with Copilot Requests permission.");
                    string privateDirectory = Path.Combine(Path.GetTempPath(), $"scriptdom-copilot-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(privateDirectory);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    try
                    {
                        using CancellationTokenSource discoveryTimeout = new(TimeSpan.FromSeconds(60));
                        await using var runtime = new CopilotRuntime(privateDirectory, privateDirectory, copilotToken);
                        CopilotCatalog catalog = await runtime.DiscoverAsync(discoveryTimeout.Token);
                        Console.WriteLine(JsonSerializer.Serialize(catalog, Contracts.JsonOptions));
                    }
                    finally
                    {
                        Directory.Delete(privateDirectory, recursive: true);
                    }
                    return 0;
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
                case ["manifest", string directory, string check, string platform, string conclusion]:
                    EvidenceManifest manifest = EvidenceManifest.Create(
                        directory, ReadGitHubIdentity(), check, platform, conclusion);
                    await WriteNewFileAsync(Path.Combine(directory, EvidenceManifest.FileName),
                        Contracts.Write(manifest, "evidence-manifest"));
                    Console.WriteLine("SHA-bound evidence manifest written. Authenticity still requires trusted GitHub metadata verification.");
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: prepare-review <config.json> <output-directory> | list-models | validate-config <path> | validate <schema-name> <path> | render <audit.json> <output.md> | baseline <months> <max-pages> phase1-human-nondraft-merged-only <output.json> <output.md> <owner/repo> <owner/repo> [additional owner/repo ...] | manifest <directory> <check> <platform> <conclusion>");
                    return 2;
            }
        }
        catch (Exception exception) when (exception is ContractException or ArgumentException)
        {
            await RecordPreparationFailureAsync(args);
            Console.Error.WriteLine($"Validation failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            await RecordPreparationFailureAsync(args);
            Console.Error.WriteLine($"Input/output failed ({exception.GetType().Name}); no document content is logged.");
            return 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or FormatException or OverflowException)
        {
            await RecordPreparationFailureAsync(args);
            Console.Error.WriteLine($"Operation failed ({exception.GetType().Name}); no provider payload is logged and partial data is not represented as complete.");
            return 1;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            await RecordPreparationFailureAsync(args);
            Console.Error.WriteLine($"Metadata failed ({exception.GetType().Name}); no input or provider payload is logged.");
            return 1;
        }
    }

    private static async Task RecordPreparationFailureAsync(string[] args)
    {
        if (args is not ["prepare-review", _, string directory]) return;
        Directory.CreateDirectory(directory);
        await WriteNewFileAsync(Path.Combine(directory, "failure.json"), Contracts.Write(new
        {
            schemaVersion = "1.0",
            status = "failed",
            stage = "preparation",
            message = "Trusted preparation failed. Consult sanitized job diagnostics; no inference or authorization consumption was performed."
        }, "preparation-failure"));
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

    private static string ParseRepository(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ContractException($"Invalid repository source '{value}'; use owner/repository.");
        return $"{parts[0]}/{parts[1]}";
    }

    private static async Task WriteBaselineOutputsAsync(
        string jsonPath, string json, string markdownPath, string markdown)
    {
        if (File.Exists(jsonPath) || File.Exists(markdownPath))
            throw new IOException("A baseline output file already exists; choose new output paths.");

        string? createdJson = null;
        try
        {
            await WriteNewFileAsync(jsonPath, json);
            createdJson = jsonPath;
            await WriteNewFileAsync(markdownPath, markdown);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (createdJson is not null)
                File.Delete(createdJson);
            throw;
        }
    }

    private static EvidenceIdentity ReadGitHubIdentity()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new ContractException($"Required GitHub metadata is missing: {name}.");
        using JsonDocument eventData = JsonDocument.Parse(ReadDocument(Required("GITHUB_EVENT_PATH")));
        JsonElement root = eventData.RootElement;
        if (!root.TryGetProperty("number", out JsonElement number) || !number.TryGetInt32(out int pr)
            || !root.TryGetProperty("pull_request", out JsonElement pullRequest)
            || !pullRequest.TryGetProperty("head", out JsonElement head)
            || !head.TryGetProperty("sha", out JsonElement sha))
            throw new ContractException("Expected a GitHub pull_request event with a head SHA.");
        return new(Required("GITHUB_REPOSITORY"), pr,
            sha.GetString() ?? throw new ContractException("PR head SHA is missing."),
            long.Parse(Required("GITHUB_RUN_ID"), System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Required("GITHUB_RUN_ATTEMPT"), System.Globalization.CultureInfo.InvariantCulture));
    }
}
