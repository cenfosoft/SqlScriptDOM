namespace AdversarialReview;

public sealed record ReviewLimits(
    int? MaxInputTokens, int? MaxOutputTokens, int? TimeoutSeconds, int? MaxRetries,
    decimal? PerAuthorizationUsd, decimal? MonthlyUsd);

public sealed record ModelEntry(string Provider, string Model, bool Enabled);

public sealed record ReviewConfiguration(
    string SchemaVersion, bool Enabled, string AuthorizationLabel, string[] AuthorizedActors,
    string Environment, int? Rounds, ReviewLimits Limits, ModelEntry[] Models)
{
    public const int MaxRounds = 3;

    public static ReviewConfiguration Parse(string json)
    {
        ReviewConfiguration config = Contracts.Read<ReviewConfiguration>(json, "configuration");
        if (config.Models.Select(m => (m.Provider, m.Model)).Distinct().Count() != config.Models.Length)
            throw new ContractException("Duplicate provider/model entries are not permitted.");
        if (config.AuthorizedActors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.AuthorizedActors.Length)
            throw new ContractException("Duplicate authorized actors are not permitted.");
        return config;
    }

    public string[] ExecutionBlockers()
    {
        List<string> errors = [];
        if (!Enabled) errors.Add("Paid execution is disabled.");
        if (Rounds is not > 0 or > MaxRounds) errors.Add($"Review round limit must be between 1 and {MaxRounds}.");
        if (Limits.MaxInputTokens is not > 0 || Limits.MaxOutputTokens is not > 0)
            errors.Add("Token limits are unset.");
        if (Limits.TimeoutSeconds is not > 0 || Limits.MaxRetries is null or < 0)
            errors.Add("Timeout or retry limits are unset.");
        if (Limits.PerAuthorizationUsd is not > 0 || Limits.MonthlyUsd is not > 0)
            errors.Add("Spend limits are unset.");
        else if (Limits.PerAuthorizationUsd > Limits.MonthlyUsd)
            errors.Add("Per-authorization spend limit exceeds the monthly limit.");
        if (!Models.Any(m => m.Enabled)) errors.Add("No model is enabled.");
        return errors.ToArray();
    }
}

public sealed record ModelSelection(ModelEntry Entry, string Reason);

public static class ModelRotation
{
    public static ModelSelection Select(
        ReviewConfiguration config, string headSha, string historyHeadSha,
        IReadOnlyList<ReviewRound> history, Func<ModelEntry, bool> isAvailable)
    {
        if (headSha != historyHeadSha)
            throw new ContractException("Model history does not belong to the current head SHA.");
        if (history.Select(r => r.Number).Where((number, index) => number != index + 1).Any())
            throw new ContractException("Model history must contain contiguous ordered rounds.");

        ModelEntry[] eligible = config.Models.Where(m => m.Enabled && isAvailable(m)).ToArray();
        if (eligible.Length == 0)
            throw new ContractException("No configured model is eligible.");
        if (history.Count == 0)
            return new(eligible[0], "First eligible configured model.");

        ReviewRound last = history[^1];
        int index = Array.FindIndex(config.Models, m => m.Provider == last.Provider && m.Model == last.Model);
        if (index < 0)
            throw new ContractException("History references a model absent from the trusted configuration.");
        for (int offset = 1; offset <= config.Models.Length; offset++)
        {
            ModelEntry next = config.Models[(index + offset) % config.Models.Length];
            if (eligible.Contains(next))
                return new(next, eligible.Length == 1 ? "Only eligible model." :
                    index + offset >= config.Models.Length ? "Restarted ordered model pool." : "Next eligible configured model.");
        }
        throw new InvalidOperationException("Eligible model selection failed.");
    }
}
