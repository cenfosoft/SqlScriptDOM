using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AdversarialReview;

// Write operations for the trusted review workflow; every PR mutation rechecks the evaluated head SHA.
public sealed class GitHubPullRequestActions
{
    private readonly HttpClient http;
    private readonly string token;
    private readonly string route;

    public GitHubPullRequestActions(HttpClient http, string token, string repository)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Timeout == Timeout.InfiniteTimeSpan || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A finite HTTP timeout and credential are required.");
        string[] parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(p => p is "" or "." or ".."
                || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
            throw new ArgumentException("A safe owner/repository identifier is required.", nameof(repository));
        this.http = http;
        this.token = token;
        route = $"repos/{parts[0]}/{parts[1]}";
    }

    // A deployment is approved only when GitHub recorded an approval for the named environment in this run.
    public async Task<bool> IsEnvironmentApprovedAsync(long runId, string environment, CancellationToken cancellationToken)
    {
        if (runId <= 0 || string.IsNullOrWhiteSpace(environment)) return false;
        using JsonDocument approvals = await RequestAsync(HttpMethod.Get, $"{route}/actions/runs/{runId}/approvals", null, cancellationToken);
        if (approvals.RootElement.ValueKind != JsonValueKind.Array) throw new ContractException("GitHub returned invalid approvals.");
        return approvals.RootElement.EnumerateArray().Any(a =>
            a.GetProperty("state").GetString() == "approved"
            && a.GetProperty("environments").EnumerateArray().Any(e => e.GetProperty("name").GetString() == environment));
    }

    public async Task<string> ApplyRiskLabelAsync(int pullRequest, string headSha, string label, CancellationToken cancellationToken)
    {
        if (label is not (RiskStage.LowRiskLabel or RiskStage.HumanReviewLabel))
            throw new ContractException("Only the configured risk labels may be applied.");
        await RequireCurrentAsync(pullRequest, headSha, cancellationToken);
        string other = label == RiskStage.LowRiskLabel ? RiskStage.HumanReviewLabel : RiskStage.LowRiskLabel;
        // GitHub creates a missing label when it is first added to an issue.
        using (await RequestAsync(HttpMethod.Post, $"{route}/issues/{pullRequest}/labels",
            JsonSerializer.Serialize(new { labels = new[] { label } }), cancellationToken)) { }
        try
        {
            using (await RequestAsync(HttpMethod.Delete,
                $"{route}/issues/{pullRequest}/labels/{Uri.EscapeDataString(other)}", null, cancellationToken)) { }
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        return "applied";
    }

    // Enables GitHub auto-merge bound to the evaluated head; branch protection (including required reviews) still applies.
    public async Task<string> EnableAutoMergeAsync(int pullRequest, string headSha, CancellationToken cancellationToken)
    {
        string nodeId = await RequireCurrentAsync(pullRequest, headSha, cancellationToken);
        string body = JsonSerializer.Serialize(new
        {
            query = "mutation($id: ID!, $sha: GitObjectID!) { enablePullRequestAutoMerge(input: { pullRequestId: $id, mergeMethod: MERGE, expectedHeadOid: $sha }) { pullRequest { number } } }",
            variables = new { id = nodeId, sha = headSha }
        });
        using JsonDocument result = await RequestAsync(HttpMethod.Post, "graphql", body, cancellationToken);
        if (result.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.GetArrayLength() > 0)
            return "failed";
        return result.RootElement.TryGetProperty("data", out JsonElement data)
            && data.TryGetProperty("enablePullRequestAutoMerge", out JsonElement enabled)
            && enabled.ValueKind == JsonValueKind.Object ? "enabled" : "failed";
    }

    private async Task<string> RequireCurrentAsync(int pullRequest, string headSha, CancellationToken cancellationToken)
    {
        if (pullRequest <= 0 || !Contracts.IsSha(headSha)) throw new ContractException("A valid PR identity is required.");
        using JsonDocument pr = await RequestAsync(HttpMethod.Get, $"{route}/pulls/{pullRequest}", null, cancellationToken);
        JsonElement root = pr.RootElement;
        if (root.GetProperty("state").GetString() != "open" || root.GetProperty("draft").GetBoolean()
            || root.GetProperty("head").GetProperty("sha").GetString() != headSha)
            throw new ContractException("PR is closed, draft, or has a different head SHA; no label or merge change was made.");
        return root.GetProperty("node_id").GetString() ?? throw new ContractException("GitHub PR node identity is missing.");
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, $"https://api.github.com/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SqlScriptDom-AdversarialReview/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub request failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        byte[] content = await GitHubReviewApi.ReadBoundedAsync(response.Content, Contracts.MaximumDocumentBytes, cancellationToken);
        try { return JsonDocument.Parse(content.Length == 0 ? "{}"u8.ToArray() : content, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new ContractException("GitHub returned malformed JSON; payload is not logged."); }
    }
}
