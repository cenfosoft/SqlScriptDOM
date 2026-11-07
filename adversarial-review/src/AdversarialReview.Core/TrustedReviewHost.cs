using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdversarialReview;

public sealed class ReviewHostStorage
{
    private readonly string root;

    public ReviewHostStorage(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ContractException("Host storage requires an existing absolute private non-link directory.");
        this.root = root;
    }

    public FileStream Acquire(string repository, int pullRequest)
    {
        if (pullRequest <= 0) throw new ContractException("A positive PR identity is required.");
        string file = Path.Combine(root, Key(repository.ToLowerInvariant()) + "-" + pullRequest.ToString(CultureInfo.InvariantCulture) + ".lock");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new ContractException("Host lock must not be a link.");
        // Fail closed instead of waiting indefinitely. File handle release survives host process exit.
        return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task SaveAsync(AuditReport report, IEnumerable<string> knownSecrets, CancellationToken cancellationToken)
    {
        string json = Contracts.Write(report, "audit-report");
        ReportRenderer.RejectCredentialData(json, knownSecrets);
        string target = PathFor(report.Run);
        if (File.Exists(target))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 || new FileInfo(target).Length > Contracts.MaximumDocumentBytes)
                throw new ContractException("Stored audit is a link or exceeds its byte ceiling.");
            if (await File.ReadAllTextAsync(target, cancellationToken) != json)
                throw new ContractException("A different audit already exists for this reporting attempt.");
            return;
        }
        string temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<AuditReport> ReadAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        Contracts.Write(run, "review-run");
        string path = PathFor(run);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > Contracts.MaximumDocumentBytes)
            throw new ContractException("Stored audit is a link or exceeds its byte ceiling.");
        AuditReport report = Contracts.Read<AuditReport>(await File.ReadAllTextAsync(path, cancellationToken), "audit-report");
        if (report.Run != run) throw new ContractException("Stored audit identity differs from requested publication identity.");
        return report;
    }

    private string PathFor(ReviewRun run) => Path.Combine(root, Key(
        $"{run.Repository.ToLowerInvariant()}:{run.PullRequest}:{run.AuthorizationId}:{run.WorkflowRunId}:{run.RunAttempt}") + ".audit.json");

    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class TrustedReviewHost(
    TrustedAdmission admissionService, GitHubReviewEvidence evidenceService,
    SqliteReviewSpendLedger ledger, IReviewProvider provider, ReviewHostStorage storage)
{
    public async Task<AuditReport> RunAsync(
        ReviewConfiguration configuration, AdmissionRequest request, long reportingRunId, int reportingAttempt,
        bool environmentApproved, Func<AdmittedReview, CancellationToken, Task<ReviewContext>> buildContext,
        Func<CancellationToken, Task<PullRequestState>> readCurrentState,
        IEnumerable<string> knownSecrets, CancellationToken cancellationToken)
    {
        AdmittedReview admission = await admissionService.VerifyAsync(configuration, request, cancellationToken);
        using FileStream prLock = storage.Acquire(admission.Repository, admission.PullRequest);
        IngestedEvidence evidence = await evidenceService.CollectAsync(admission, cancellationToken);
        ReviewRun run = CreateRun(admission, reportingRunId, reportingAttempt);
        string[] secrets = knownSecrets.ToArray();
        string[] blockers = [.. configuration.ExecutionBlockers(), .. evidence.Blockers];
        if (!environmentApproved) blockers = [.. blockers, "Protected environment approval is missing."];
        if (blockers.Length != 0)
        {
            AuditReport blocked = AuditEngine.Create(run, admission.HeadSha, evidence.Checks, [], [], configuration.Rounds ?? 1);
            blocked = blocked with { Blockers = [.. blocked.Blockers, .. blockers] };
            await storage.SaveAsync(blocked, secrets, cancellationToken);
            return blocked;
        }
        // Repeat authenticated admission after downloads/lock acquisition, before reservation.
        AdmittedReview rechecked = await admissionService.VerifyAsync(configuration, request, cancellationToken);
        if (rechecked != admission) throw new ContractException("Admission changed during evidence collection.");
        ReviewContext context = await buildContext(admission, cancellationToken);
        if (!evidence.Checks.Select(c => c.Name + "/" + c.Platform).Order(StringComparer.Ordinal)
                .SequenceEqual(context.Checks.Order(StringComparer.Ordinal))
            || !evidence.Checks.Select(c => c.Artifact).Order(StringComparer.Ordinal).SequenceEqual(context.Artifacts.Order(StringComparer.Ordinal)))
            throw new ContractException("Context evidence inventory does not match admitted checks/artifacts.");
        ReviewRound[] history = await ledger.ReadHistoryAsync(admission.Repository, admission.PullRequest, admission.HeadSha, cancellationToken);
        var authorization = new AuthorizationContext(admission.Actor, admission.Label, admission.HeadSha,
            run.AuthorizationId, environmentApproved, false);
        AuditReport report;
        try
        {
            async Task<PullRequestState> FreshState(CancellationToken token)
            {
                PullRequestState state = await readCurrentState(token);
                if (state.HeadSha == admission.HeadSha && state.IsOpen && !state.IsDraft
                    && await admissionService.VerifyAsync(configuration, request, token) != admission)
                    throw new ContractException("Authorization changed during execution.");
                return state;
            }
            CycleResult result = await new ReviewCycle(provider, ledger).RunAsync(configuration, run, authorization, context,
                evidence.Checks, history, FreshState, Contracts.MaximumDocumentBytes, secrets, cancellationToken);
            report = result.Report;
        }
        catch (Exception error) when (error is ContractException or HttpRequestException or OperationCanceledException)
        {
            using CancellationTokenSource recovery = new(TimeSpan.FromSeconds(30));
            ReviewRound[] recorded = await ledger.ReadHistoryAsync(admission.Repository, admission.PullRequest, admission.HeadSha,
                recovery.Token, run.AuthorizationId);
            report = AuditEngine.Create(run, admission.HeadSha, evidence.Checks, [], recorded, configuration.Rounds!.Value)
                with { Status = "incomplete", Blockers = ["Execution stopped; inspect trusted metadata and retained reservations. No automatic inference retry."],
                    Limitations = ["Recovery report retains accounting but not unpersisted findings. Missing findings must not be interpreted as a clean review."] };
        }
        using CancellationTokenSource persistence = new(TimeSpan.FromSeconds(30));
        await storage.SaveAsync(report, secrets, persistence.Token);
        return report;
    }

    // Publication retries read an immutable report; they never invoke admission/inference.
    public async Task<PublishedComment> PublishStoredAsync(
        ReviewRun run, GitHubAuditPublisher publisher, IEnumerable<string> knownSecrets, CancellationToken cancellationToken)
    {
        using FileStream prLock = storage.Acquire(run.Repository, run.PullRequest);
        AuditReport report = await storage.ReadAsync(run, cancellationToken);
        return await publisher.PublishAsync(report, knownSecrets, cancellationToken);
    }

    public static ReviewRun CreateRun(AdmittedReview admission, long reportingRunId, int reportingAttempt) =>
        new("1.0", admission.Repository, admission.PullRequest, admission.HeadSha, admission.BaseSha, admission.ConfigurationSha,
            reportingRunId, reportingAttempt, TrustedAdmission.AuthorizationId(admission), admission.Actor);
}
