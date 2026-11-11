using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AdversarialReview;

public sealed record PublishedComment(long CommentId, bool Created, bool MarkedStale);

public sealed class GitHubAuditPublisher
{
    private const string MetadataPrefix = "<!-- scriptdom-adversarial-review-run:";
    private readonly HttpClient http;
    private readonly string token;
    private readonly int maximumPages;

    public GitHubAuditPublisher(HttpClient http, string token, int maximumPages = 20)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A reporting credential is required.", nameof(token));
        if (maximumPages is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumPages));
        if (http.Timeout == Timeout.InfiniteTimeSpan) throw new ArgumentException("A finite HTTP timeout is required.", nameof(http));
        this.http = http;
        this.token = token;
        this.maximumPages = maximumPages;
    }

    // The trusted host must hold a per-PR publication lock for the entire call.
    public async Task<PublishedComment> PublishAsync(
        AuditReport report, IEnumerable<string> knownSecrets, CancellationToken cancellationToken)
    {
        Contracts.Write(report, "audit-report");
        string[] secrets = [token, .. knownSecrets];
        ReportRenderer.RejectCredentialData(Contracts.Write(report, "audit-report"), secrets);
        string repository = report.Run.Repository;
        string[] parts = repository.Split('/');
        if (parts.Any(p => p is "." or ".."))
            throw new ContractException("Invalid GitHub repository identifier.");
        string route = $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        string prRoute = $"{route}/pulls/{report.Run.PullRequest}";
        await RequireCurrentPrAsync(prRoute, report.Run.HeadSha, cancellationToken);

        (long Id, string Body)? existing = null;
        for (int page = 1; ; page++)
        {
            using JsonDocument comments = await RequestAsync(HttpMethod.Get,
                $"{route}/issues/{report.Run.PullRequest}/comments?per_page=100&page={page}", null, cancellationToken);
            if (comments.RootElement.ValueKind != JsonValueKind.Array)
                throw new ContractException("GitHub returned an invalid comment list.");
            foreach (JsonElement comment in comments.RootElement.EnumerateArray())
            {
                string body = comment.GetProperty("body").GetString() ?? "";
                JsonElement user = comment.GetProperty("user");
                if (user.GetProperty("type").GetString() != "Bot"
                    || user.GetProperty("login").GetString() != "github-actions[bot]"
                    || !body.StartsWith(ReportRenderer.Marker + "\n", StringComparison.Ordinal))
                    continue;
                if (existing is not null) throw new ContractException("Multiple bot audit comments require reconciliation.");
                long id = comment.GetProperty("id").GetInt64();
                if (id <= 0) throw new ContractException("GitHub returned an invalid comment ID.");
                EnsureNotNewer(body, report.Run);
                existing = (id, body);
            }
            if (comments.RootElement.GetArrayLength() < 100) break;
            if (page == maximumPages)
                throw new ContractException("Comment pagination limit reached; refusing to create a possible duplicate.");
        }

        await RequireCurrentPrAsync(prRoute, report.Run.HeadSha, cancellationToken);
        string markdown = RenderWithMetadata(report, secrets);
        long commentId;
        if (existing is null)
        {
            using JsonDocument created = await RequestAsync(HttpMethod.Post,
                $"{route}/issues/{report.Run.PullRequest}/comments", markdown, cancellationToken);
            commentId = created.RootElement.GetProperty("id").GetInt64();
            if (commentId <= 0) throw new ContractException("GitHub returned an invalid created comment ID.");
        }
        else
        {
            commentId = existing.Value.Id;
            if (existing.Value.Body != markdown)
            {
                using JsonDocument updated = await RequestAsync(HttpMethod.Patch,
                    $"{route}/issues/comments/{commentId}", markdown, cancellationToken);
            }
        }

        bool stillCurrent = await IsCurrentPrAsync(prRoute, report.Run.HeadSha, cancellationToken);
        if (!stillCurrent)
        {
            AuditReport stale = report with
            {
                Status = "stale",
                Blockers = [.. report.Blockers, "PR changed or closed during publication; this report is stale."]
            };
            using JsonDocument updated = await RequestAsync(HttpMethod.Patch,
                $"{route}/issues/comments/{commentId}", RenderWithMetadata(stale, secrets), cancellationToken);
        }
        return new(commentId, existing is null, !stillCurrent);
    }

    private static string RenderWithMetadata(AuditReport report, string[] secrets)
    {
        string body = ReportRenderer.Render(report, secrets);
        string metadata = $"{MetadataPrefix}{report.Run.HeadSha}:{report.Run.WorkflowRunId}:{report.Run.RunAttempt} -->\n";
        body = body.Insert(ReportRenderer.Marker.Length + 1, metadata);
        if (body.Length > ReportRenderer.MaximumCommentCharacters)
            throw new ContractException("Comment and publication metadata exceed the safe publication limit.");
        return body;
    }

    private static void EnsureNotNewer(string body, ReviewRun run)
    {
        string? metadata = body.Split('\n').Skip(1).FirstOrDefault();
        if (metadata is null || !metadata.StartsWith(MetadataPrefix, StringComparison.Ordinal)
            || !metadata.EndsWith(" -->", StringComparison.Ordinal))
            throw new ContractException("Existing audit comment has no valid publication metadata.");
        string[] fields = metadata[MetadataPrefix.Length..^4].Split(':');
        if (fields.Length != 3 || !Contracts.IsSha(fields[0])
            || !long.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long runId)
            || !int.TryParse(fields[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int attempt)
            || runId <= 0 || attempt <= 0)
            throw new ContractException("Existing audit publication metadata is malformed.");
        if (runId > run.WorkflowRunId || runId == run.WorkflowRunId && attempt > run.RunAttempt)
            throw new ContractException("A newer audit report is already published.");
    }

    private async Task RequireCurrentPrAsync(string route, string sha, CancellationToken cancellationToken)
    {
        if (!await IsCurrentPrAsync(route, sha, cancellationToken))
            throw new ContractException("PR is draft, closed, or has a different head SHA; no comment was published.");
    }

    private async Task<bool> IsCurrentPrAsync(string route, string sha, CancellationToken cancellationToken)
    {
        using JsonDocument pr = await RequestAsync(HttpMethod.Get, route, null, cancellationToken);
        JsonElement root = pr.RootElement;
        return root.GetProperty("state").GetString() == "open"
            && !root.GetProperty("draft").GetBoolean()
            && root.GetProperty("head").GetProperty("sha").GetString() == sha;
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string route, string? body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, $"https://api.github.com/{route}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SqlScriptDom-AdversarialReview/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(new { body }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub reporting request failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > Contracts.MaximumDocumentBytes)
                throw new ContractException("GitHub reporting response exceeds size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        try
        {
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new ContractException("GitHub reporting response is malformed; response body is not logged.");
        }
    }
}
