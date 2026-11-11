using System.Net.Http.Headers;
using System.Text.Json;

namespace AdversarialReview.Core.Metrics;

/// <summary>
/// Read-only GitHub REST client. The caller owns HttpClient and must configure a finite Timeout.
/// Authentication is supplied externally and is sent only to the configured API origin.
/// </summary>
public sealed class GitHubBaselineClient
{
    private readonly HttpClient httpClient;
    private readonly string? token;
    private readonly int maxPages;
    private readonly TimeProvider timeProvider;

    public GitHubBaselineClient(HttpClient httpClient, string? token = null, int maxPages = 100,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException("A finite HttpClient.Timeout is required.", nameof(httpClient));
        }

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        }

        this.httpClient = httpClient;
        this.token = token;
        this.maxPages = maxPages;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RepositorySnapshot> FetchSnapshotAsync(string owner, string repository,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(owner, nameof(owner));
        ValidateSegment(repository, nameof(repository));
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        var diagnostics = new List<BaselineDiagnostic>();
        var pulls = new List<PullRequestSnapshot>();
        bool complete = true;
        var numbers = new HashSet<int>();

        // Never stop at a created_at cutoff: an old PR can become ready inside the requested window.
        for (int page = 1; page <= maxPages; page++)
        {
            PageResult result = await GetPageAsync(
                $"{path}/pulls?state=all&sort=created&direction=desc&per_page=100&page={page}", cancellationToken);
            if (result.Error is not null)
            {
                diagnostics.Add(new BaselineDiagnostic($"PullRequests{result.Error}"));
                complete = false;
                break;
            }

            foreach (JsonElement item in result.Items)
            {
                if (!TryReadPullRequest(item, out PullRequestSnapshot? pr))
                {
                    diagnostics.Add(new BaselineDiagnostic("MalformedPullRequest"));
                    complete = false;
                    continue;
                }

                if (!numbers.Add(pr.Number))
                {
                    diagnostics.Add(new BaselineDiagnostic("DuplicatePullRequest", pr.Number));
                    complete = false;
                    continue;
                }

                if (!pr.IsDraft && pr.State is "open" or "closed"
                    && (pr.State == "open" || pr.MergedAt is not null)
                    && pr.AuthorType.Equals("User", StringComparison.OrdinalIgnoreCase)
                    && !pr.AuthorLogin.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
                {
                    (TimelineStatus status, IReadOnlyList<ReadinessEvent> events) =
                        await FetchTimelineAsync($"{path}/issues/{pr.Number}/timeline", cancellationToken);
                    pr = pr with { TimelineStatus = status, ReadinessEvents = events };
                    complete &= status == TimelineStatus.Complete;
                }

                pulls.Add(pr);
            }

            if (!result.HasNext)
            {
                break;
            }

            if (page == maxPages)
            {
                diagnostics.Add(new BaselineDiagnostic("PullRequestsPageLimitReached"));
                complete = false;
            }
        }

        return new RepositorySnapshot($"{owner}/{repository}", observedAt, pulls, complete, diagnostics);
    }

    private async Task<(TimelineStatus Status, IReadOnlyList<ReadinessEvent> Events)> FetchTimelineAsync(
        string path, CancellationToken cancellationToken)
    {
        var events = new List<ReadinessEvent>();
        for (int page = 1; page <= maxPages; page++)
        {
            PageResult result = await GetPageAsync($"{path}?per_page=100&page={page}", cancellationToken);
            if (result.Error is not null)
            {
                return (result.Error == "Malformed" ? TimelineStatus.Malformed : TimelineStatus.Unavailable, events);
            }

            foreach (JsonElement item in result.Items)
            {
                if (!TryString(item, "event", out string? kind))
                {
                    return (TimelineStatus.Malformed, events);
                }

                if (kind is "ready_for_review" or "convert_to_draft")
                {
                    if (!TryDate(item, "created_at", out DateTimeOffset date))
                    {
                        return (TimelineStatus.Malformed, events);
                    }

                    events.Add(new ReadinessEvent(kind, date));
                }
            }

            if (!result.HasNext)
            {
                return (TimelineStatus.Complete, events);
            }
        }

        return (TimelineStatus.PageLimitReached, events);
    }

    private async Task<PageResult> GetPageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var baseUri = httpClient.BaseAddress ?? new Uri("https://api.github.com/");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.UserAgent.ParseAdd("AdversarialReview-Baseline/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            // ResponseContentRead keeps the client's Timeout in force while buffering the body.
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new([], false, $"Http{(int)response.StatusCode}");
            }

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new([], false, "Malformed");
            }

            JsonElement[] items = document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
            // Do not follow server-supplied URLs, which could forward the token to another origin.
            bool hasNext = items.Length == 100 || (response.Headers.TryGetValues("Link", out var links)
                && links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)));
            return new(items, hasNext, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], false, "Timeout");
        }
        catch (HttpRequestException)
        {
            return new([], false, "Unavailable");
        }
        catch (JsonException)
        {
            return new([], false, "Malformed");
        }
    }

    private static bool TryReadPullRequest(JsonElement item, out PullRequestSnapshot pr)
    {
        pr = null!;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("number", out JsonElement number) || number.ValueKind != JsonValueKind.Number
            || !number.TryGetInt32(out int n) || n < 1
            || !item.TryGetProperty("user", out JsonElement user)
            || !TryString(user, "login", out string login) || !TryString(user, "type", out string type)
            || !TryDate(item, "created_at", out DateTimeOffset created)
            || !TryDate(item, "updated_at", out DateTimeOffset updated)
            || !TryString(item, "state", out string state)
            || !item.TryGetProperty("draft", out JsonElement draft)
            || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryNullableDate(item, "merged_at", out DateTimeOffset? merged)
            || !TryNullableDate(item, "closed_at", out DateTimeOffset? closed))
        {
            return false;
        }

        pr = new(n, login, type, created, updated, state, draft.GetBoolean(), merged, closed,
            TimelineStatus.NotRequested, []);
        return true;
    }

    private static bool TryString(JsonElement item, string name, out string value)
    {
        value = "";
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryDate(JsonElement item, string name, out DateTimeOffset value)
    {
        value = default;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement date)
            && date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out value);
    }

    private static bool TryNullableDate(JsonElement item, string name, out DateTimeOffset? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out JsonElement date))
        {
            return false;
        }

        if (date.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out DateTimeOffset parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static void ValidateSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')
            || value is "." or "..")
        {
            throw new ArgumentException("Supply a GitHub owner or repository name, not a URL.", parameter);
        }
    }

    private sealed record PageResult(JsonElement[] Items, bool HasNext, string? Error);
}
