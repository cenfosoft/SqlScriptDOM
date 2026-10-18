using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class PreparationTests
{
    private static string Event() => new JsonObject
    {
        ["action"] = "labeled", ["number"] = 1,
        ["sender"] = new JsonObject { ["login"] = "allancascante", ["type"] = "User" },
        ["repository"] = new JsonObject { ["full_name"] = "owner/repo", ["default_branch"] = "main" },
        ["label"] = new JsonObject { ["name"] = "adversarial-review:run" },
        ["pull_request"] = new JsonObject
        {
            ["number"] = 1, ["state"] = "open", ["draft"] = false, ["updated_at"] = "2026-10-17T00:00:00Z",
            ["head"] = new JsonObject { ["sha"] = FoundationTests.Sha },
            ["base"] = new JsonObject { ["ref"] = "main", ["repo"] = new JsonObject { ["full_name"] = "owner/repo" } }
        }
    }.ToJsonString();

    [DataTestMethod]
    [DataRow("event")]
    [DataRow("actor")]
    [DataRow("repository")]
    [DataRow("action")]
    [DataRow("label")]
    [DataRow("draft")]
    [DataRow("branch")]
    public void UntrustedWebhookMetadataCannotEnterPreparation(string mutation)
    {
        JsonNode root = JsonNode.Parse(Event())!;
        string eventName = "pull_request_target", actor = "allancascante", repo = "owner/repo";
        switch (mutation)
        {
            case "event": eventName = "pull_request"; break;
            case "actor": actor = "attacker"; break;
            case "repository": repo = "attacker/repo"; break;
            case "action": root["action"] = "synchronize"; break;
            case "label": root["label"]!["name"] = "other"; break;
            case "draft": root["pull_request"]!["draft"] = true; break;
            case "branch": root["pull_request"]!["base"]!["ref"] = "contributor-branch"; break;
        }
        Assert.ThrowsException<ContractException>(() =>
            LabelSnapshot.Parse(root.ToJsonString(), eventName, repo, actor, FoundationTests.Config()));
    }

    private static void AddRuns(AdmissionTests.Handler admission)
    {
        admission.Responses[$"/repos/owner/repo/actions/workflows/pr-validation.yml/runs?event=pull_request&head_sha={FoundationTests.Sha}&per_page=100&page=1"]
            = new JsonObject { ["workflow_runs"] = new JsonArray(new JsonObject { ["id"] = 70, ["run_attempt"] = 2, ["head_sha"] = FoundationTests.Sha }) };
    }

    [TestMethod]
    public async Task PreparationCannotRunInferenceEvenWhenConfigurationIsEnabled()
    {
        foreach (bool enabled in new[] { true, false })
        {
            using var admission = new AdmissionTests.Handler();
            AddRuns(admission);
            using var evidence = new IngestionTests.EvidenceHandler(complete: false);
            using HttpClient http = new(new HostTests.Router(admission, evidence));
            var api = AdmissionTests.Api(http);
            var config = FoundationTests.Config() with { Enabled = enabled };
            LabelSnapshot snapshot = LabelSnapshot.Parse(Event(), "pull_request_target", "owner/repo", "allancascante", config);
            AuditReport report = await new ReviewPreparation(api, new(api, http)).PrepareAsync(config, snapshot,
                FoundationTests.OtherSha, 100, 1, CancellationToken.None);
            Assert.AreEqual("incomplete", report.Status);
            Assert.AreEqual(0, report.Rounds.Length);
            Assert.IsNull(report.RiskScore);
            StringAssert.Contains(string.Join(" ", report.Blockers), "no inference");
            StringAssert.Contains(string.Join(" ", report.Blockers), "semgrep");
            Contracts.Write(report, "audit-report");
        }
    }

    [TestMethod]
    public async Task MissingRunAndAmbiguousLabelCorrelationFailInsteadOfInventingAuthorization()
    {
        foreach (string reason in new[] { "run", "label" })
        {
            using var handler = new AdmissionTests.Handler();
            AddRuns(handler);
            if (reason == "run")
                handler.Responses[$"/repos/owner/repo/actions/workflows/pr-validation.yml/runs?event=pull_request&head_sha={FoundationTests.Sha}&per_page=100&page=1"]["workflow_runs"] = new JsonArray();
            else
                handler.Responses["/repos/owner/repo/issues/1/events?per_page=100&page=1"][0]!["created_at"] = "2026-10-17T00:00:01Z";
            using HttpClient http = new(handler);
            var api = AdmissionTests.Api(http);
            await Assert.ThrowsExceptionAsync<ContractException>(() => new ReviewPreparation(api, new(api, http))
                .PrepareAsync(FoundationTests.Config(),
                    LabelSnapshot.Parse(Event(), "pull_request_target", "owner/repo", "allancascante", FoundationTests.Config()),
                    FoundationTests.OtherSha, 100, 1, CancellationToken.None));
        }
    }
}
