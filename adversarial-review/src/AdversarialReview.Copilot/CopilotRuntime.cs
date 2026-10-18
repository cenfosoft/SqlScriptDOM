using System.Diagnostics;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AdversarialReview.Copilot;

public interface ICopilotRuntime
{
    Task<CopilotCatalog> DiscoverAsync(CancellationToken cancellationToken);
    Task<CopilotReply> ReviewAsync(SessionConfig session, string context, CancellationToken cancellationToken);
}

public sealed record CopilotReply(string Content, AssistantUsageData[] Usage, long LatencyMilliseconds);

public sealed class CopilotRuntime : ICopilotRuntime, IAsyncDisposable
{
    private readonly CopilotClient client;
    private readonly string workingDirectory;

    public CopilotRuntime(string stateDirectory, string workingDirectory, string? token = null)
    {
        if (!Path.IsPathFullyQualified(stateDirectory) || !Path.IsPathFullyQualified(workingDirectory)
            || !Directory.Exists(stateDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("Existing absolute private state and working directories are required.");
        this.workingDirectory = workingDirectory;
        client = new(CreateClientOptions(stateDirectory, workingDirectory, token));
    }

    public static CopilotClientOptions CreateClientOptions(string stateDirectory, string workingDirectory, string? token) => new()
    {
        Mode = CopilotClientMode.Empty,
        BaseDirectory = stateDirectory,
        WorkingDirectory = workingDirectory,
        GitHubToken = token,
        UseLoggedInUser = false,
        EnableRemoteSessions = false,
        LogLevel = CopilotLogLevel.None,
        Environment = new Dictionary<string, string>
        {
            ["COPILOT_GITHUB_TELEMETRY_DISABLED"] = "true"
        }
    };

    public async Task<CopilotCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.StartAsync(cancellationToken);
            return CopilotModelCatalog.Create(await client.ListModelsAsync(cancellationToken), DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is RemoteRpcException or IOException or InvalidOperationException or TimeoutException)
        {
            throw new HttpRequestException("Copilot model discovery failed; verify runtime, credentials, and account entitlement. Provider payload is not logged.");
        }
    }

    public async Task<CopilotReply> ReviewAsync(SessionConfig config, string context, CancellationToken cancellationToken)
    {
        try
        {
            return await ReviewCoreAsync(config, context, cancellationToken);
        }
        catch (Exception error) when (error is RemoteRpcException or IOException or InvalidOperationException or TimeoutException)
        {
            throw new HttpRequestException("Copilot review transport failed; billing remains unconfirmed. Provider payload is not logged.");
        }
    }

    private async Task<CopilotReply> ReviewCoreAsync(SessionConfig config, string context, CancellationToken cancellationToken)
    {
        await client.StartAsync(cancellationToken);
        config.WorkingDirectory = workingDirectory;
        CopilotSession session = await client.CreateSessionAsync(config, cancellationToken);
        List<AssistantUsageData> usage = [];
        bool failed = false;
        using IDisposable subscription = session.On<AssistantUsageEvent>(evt =>
        {
            lock (usage)
            {
                usage.Add(evt.Data);
            }
        });
        using IDisposable errors = session.On<SessionErrorEvent>(_ =>
        {
            lock (usage) failed = true;
        });
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            AssistantMessageEvent? response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = context }, cancellationToken: cancellationToken);
            lock (usage)
            {
                if (failed || response is null)
                    throw new ContractException("Copilot review failed or returned no final message; payload is not logged.");
                return new(response.Data.Content, usage.ToArray(), elapsed.ElapsedMilliseconds);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            using CancellationTokenSource abort = new(TimeSpan.FromSeconds(10));
            await session.AbortAsync(abort.Token);
            throw;
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            await session.DisposeAsync();
            await client.DeleteSessionAsync(session.SessionId, cleanup.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception error) when (error is IOException or AggregateException or InvalidOperationException)
        {
            throw new HttpRequestException("Copilot runtime cleanup failed; inspect private state and billing before retrying. Provider payload is not logged.");
        }
    }
}
