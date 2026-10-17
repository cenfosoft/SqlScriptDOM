using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class FoundationTests
{
    public const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static ReviewConfiguration Config() => new(
        "1.0", true, "adversarial-review:run", ["allancascante"], "adversarial-review", 3,
        new(1000, 500, 30, 0, 1, 10),
        [new("github-models", "first", true), new("github-models", "second", true)]);

    public static CheckResult[] Checks() => CheckPolicy.Required
        .Select(c => new CheckResult("1.0", Sha, c.Name, c.Platform, "passed", $"run/{c.Name}-{c.Platform}"))
        .ToArray();

    public static ReviewRun Run() => new("1.0", "owner/repo", 1, Sha, OtherSha, OtherSha, 1, 1, "label-event-1", "allancascante");

    public static ReviewRound Round(int number, string model) =>
        new(number, "github-models", model, "Configured order.", "1.0", 100, 20, 1000, 0.01m, "completed");

    [TestMethod]
    public void ShippedConfigurationDisablesExecution()
    {
        var config = ReviewConfiguration.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/review.json")));
        Assert.IsFalse(config.Enabled);
        Assert.IsTrue(config.ExecutionBlockers().Length >= 5);
        CollectionAssert.Contains(config.AuthorizedActors, "allancascante");
    }

    [TestMethod]
    public void FindingFixturesValidateAndRoundTrip()
    {
        string valid = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/finding.valid.json"));
        Finding finding = Contracts.Read<Finding>(valid, "finding");
        Assert.AreEqual(finding.Id, Contracts.Read<Finding>(Contracts.Write(finding, "finding"), "finding").Id);
        Assert.ThrowsException<ContractException>(() => Contracts.Validate(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/finding.invalid.json")), "finding"));
    }

    [DataTestMethod]
    [DataRow("schemaVersion", "\"2.0\"")]
    [DataRow("confidence", "1.1")]
    [DataRow("severity", "\"safe\"")]
    [DataRow("headSha", "\"not-a-sha\"")]
    [DataRow("evidence", "[]")]
    [DataRow("futureField", "true")]
    public void InvalidOrFutureFieldsAreRejected(string field, string value)
    {
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/finding.valid.json")))!.AsObject();
        fixture[field] = JsonNode.Parse(value);
        Assert.ThrowsException<ContractException>(() => Contracts.Validate(fixture.ToJsonString(), "finding"));
    }

    [TestMethod]
    public void RejectsDuplicatePropertiesAndOversizeDocuments()
    {
        Assert.ThrowsException<ContractException>(() => Contracts.Validate("{\"id\":1,\"id\":2}", "finding"));
        Assert.ThrowsException<ContractException>(() => Contracts.Validate(new string(' ', Contracts.MaximumDocumentBytes + 1), "finding"));
        Assert.ThrowsException<ContractException>(() => Contracts.Validate("{broken", "finding"));
    }

    [TestMethod]
    public void ConfigurationRejectsDuplicateModelsAndActors()
    {
        ReviewConfiguration config = Config();
        Assert.ThrowsException<ContractException>(() => ReviewConfiguration.Parse(
            JsonSerializer.Serialize(config with { Models = [config.Models[0], config.Models[0]] }, Contracts.JsonOptions)));
        Assert.ThrowsException<ContractException>(() => ReviewConfiguration.Parse(
            JsonSerializer.Serialize(config with { AuthorizedActors = ["allancascante", "AllanCascante"] }, Contracts.JsonOptions)));
    }

    [TestMethod]
    public void RotationWrapsAndSkipsUnavailableModels()
    {
        var config = Config();
        Assert.AreEqual("first", ModelRotation.Select(config, Sha, Sha, [], _ => true).Entry.Model);
        Assert.AreEqual("second", ModelRotation.Select(config, Sha, Sha, [Round(1, "first")], _ => true).Entry.Model);
        Assert.AreEqual("first", ModelRotation.Select(config, Sha, Sha, [Round(1, "first"), Round(2, "second")], _ => true).Entry.Model);
        Assert.AreEqual("first", ModelRotation.Select(config, Sha, Sha, [Round(1, "first")], m => m.Model == "first").Entry.Model);
        Assert.ThrowsException<ContractException>(() => ModelRotation.Select(config, Sha, Sha, [], _ => false));
        Assert.ThrowsException<ContractException>(() => ModelRotation.Select(config, Sha, OtherSha, [], _ => true));
        Assert.ThrowsException<ContractException>(() => ModelRotation.Select(config, Sha, Sha, [Round(2, "first")], _ => true));
        Assert.ThrowsException<ContractException>(() => ModelRotation.Select(config, Sha, Sha, [Round(1, "removed-model")], _ => true));
    }

    [TestMethod]
    public void GateAllowsOnlyCompletePreflight()
    {
        var auth = new AuthorizationContext("allancascante", "adversarial-review:run", Sha, "label-1", true, false);
        var pr = new PullRequestState(Sha, true, false);
        Assert.IsTrue(ReviewGate.Evaluate(Config(), auth, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { Actor = "contributor" }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { Label = "other" }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { HeadSha = OtherSha }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { AuthorizationId = "" }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { AlreadyConsumed = true }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth with { EnvironmentApproved = false }, pr, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth, pr with { IsDraft = true }, Checks()).EligibleForReservation);
        Assert.IsFalse(ReviewGate.Evaluate(Config(), auth, pr with { IsOpen = false }, Checks()).EligibleForReservation);
    }

    [TestMethod]
    public void EveryRequiredCheckMustPassExactlyOnceOnCurrentSha()
    {
        foreach (CheckResult check in Checks())
        {
            CheckResult[] without = Checks().Where(c => c != check).ToArray();
            Assert.IsTrue(CheckPolicy.Blockers(Sha, without).Length > 0);
            Assert.IsTrue(CheckPolicy.Blockers(Sha, [.. without, check with { Conclusion = "failed" }]).Length > 0);
            Assert.IsTrue(CheckPolicy.Blockers(Sha, [.. without, check with { HeadSha = OtherSha }]).Length > 0);
            Assert.IsTrue(CheckPolicy.Blockers(Sha, [.. Checks(), check]).Length > 0);
        }
    }
}
