using System.Net.Http.Headers;
using System.Text.Json;

namespace AdversarialReview;

// Use a finite-timeout HttpClient with automatic redirects disabled.
public sealed class GitHubReviewApi
{
    private readonly HttpClient http;
    private readonly string token;
    private readonly int maximumPages;
    public string Repository { get; }
    public string Route => $"repos/{Repository}";

    public GitHubReviewApi(HttpClient http, string token, string repository, int maximumPages = 20)
    {
        if (http.Timeout == Timeout.InfiniteTimeSpan || string.IsNullOrWhiteSpace(token)
            || maximumPages is < 1 or > 100 || repository.Split('/').Length != 2
            || repository.Split('/').Any(s => s is "" or "." or ".."
                || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
            throw new ArgumentException("A finite HTTP timeout, credential, safe repository, and bounded pagination are required.");
        this.http = http;
        this.token = token;
        this.maximumPages = maximumPages;
        Repository = repository;
    }

    public async Task<JsonDocument> GetAsync(string relativeRoute, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(relativeRoute, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] content = await ReadBoundedAsync(response.Content, Contracts.MaximumDocumentBytes, cancellationToken);
        try { return JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new ContractException("GitHub returned malformed review metadata; payload is not logged."); }
    }

    public async Task<JsonElement[]> ListAsync(string relativeRoute, string? collection, CancellationToken cancellationToken)
    {
        List<JsonElement> entries = [];
        for (int page = 1; page <= maximumPages; page++)
        {
            using JsonDocument response = await GetAsync(
                $"{relativeRoute}{(relativeRoute.Contains('?') ? '&' : '?')}per_page=100&page={page}", cancellationToken);
            JsonElement items = collection is null ? response.RootElement : response.RootElement.GetProperty(collection);
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100)
                throw new ContractException("GitHub returned an invalid review metadata page.");
            entries.AddRange(items.EnumerateArray().Select(e => e.Clone()));
            if (items.GetArrayLength() < 100) return entries.ToArray();
        }
        throw new ContractException("GitHub review pagination ceiling reached; refusing incomplete metadata.");
    }

    public async Task<HttpResponseMessage> SendAsync(string relativeRoute, CancellationToken cancellationToken)
    {
        if (!relativeRoute.StartsWith(Route + "/", StringComparison.Ordinal)
            || relativeRoute.Contains("..", StringComparison.Ordinal) || relativeRoute.Contains('#')
            || relativeRoute.Contains('\\') || relativeRoute.Any(char.IsControl))
            throw new ContractException("Review API route is outside the configured repository.");
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/{relativeRoute}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SqlScriptDom-AdversarialReview/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) throw new ContractException("GitHub response exceeds its byte ceiling.");
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) throw new ContractException("GitHub response exceeds its byte ceiling.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
