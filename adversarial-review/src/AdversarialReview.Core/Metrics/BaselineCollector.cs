namespace AdversarialReview.Core.Metrics;

/// <summary>Collects a current snapshot and calculates merged and censored cohorts independently.</summary>
public sealed class BaselineCollector(GitHubBaselineClient client)
{
    public async Task<BaselineReport> CollectAsync(string owner, string repository, int months = 6,
        CancellationToken cancellationToken = default)
    {
        if (months < 1 || months > 1200)
        {
            throw new ArgumentOutOfRangeException(nameof(months));
        }

        RepositorySnapshot snapshot = await client.FetchSnapshotAsync(owner, repository, months, cancellationToken);
        return BaselineCalculator.Calculate(snapshot, months);
    }
}
