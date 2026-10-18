using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class IngestionTests
{
    internal static AdmittedReview Admission() => new("owner/repo", 1, 1, FoundationTests.Sha, FoundationTests.OtherSha,
        FoundationTests.OtherSha, "allancascante", "adversarial-review:run", 50, DateTimeOffset.UtcNow, 70, 2, 10, 2);

    internal static byte[] Zip(string check = "build", string platform = "windows", string rawPath = "raw.log",
        string? extraPath = null, bool tamper = false, int attributes = 0, string? head = null)
    {
        byte[] raw = Encoding.UTF8.GetBytes("Raw deterministic evidence; never executed.");
        var manifest = new EvidenceManifest("1.0", new("owner/repo", 1, head ?? FoundationTests.Sha, 70, 2),
            check, platform, "passed", [new(rawPath, raw.Length, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant())]);
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry data = archive.CreateEntry(rawPath);
            data.ExternalAttributes = attributes;
            using (Stream stream = data.Open()) stream.Write(tamper ? Encoding.UTF8.GetBytes("tampered") : raw);
            using (StreamWriter writer = new(archive.CreateEntry("manifest.json").Open()))
                writer.Write(Contracts.Write(manifest, "evidence-manifest"));
            if (extraPath is not null)
                using (StreamWriter writer = new(archive.CreateEntry(extraPath).Open())) writer.Write("extra");
        }
        return output.ToArray();
    }

    [TestMethod]
    public void ValidArchiveNormalizesOnlyAfterIdentityAndChecksumsVerify()
    {
        CheckResult check = ReviewEvidenceArchive.Verify(Zip(), new("owner/repo", 1, FoundationTests.Sha, 70, 2),
            "build", "windows", "actions-artifact:1");
        Assert.AreEqual("passed", check.Conclusion);
        Assert.AreEqual("actions-artifact:1", check.Artifact);
    }

    [DataTestMethod]
    [DataRow("../outside")]
    [DataRow("/absolute")]
    [DataRow("C:/outside")]
    [DataRow("dir\\outside")]
    [DataRow("a/../outside")]
    [DataRow("NUL.txt")]
    [DataRow("folder./raw")]
    [DataRow("folder /raw")]
    [DataRow("raw.log")]
    [DataRow("RAW.LOG")]
    public void UnsafeExtraOrDuplicateArchivePathsAreRejected(string path)
    {
        Assert.ThrowsException<ContractException>(() => ReviewEvidenceArchive.Verify(Zip(extraPath: path),
            new("owner/repo", 1, FoundationTests.Sha, 70, 2), "build", "windows", "artifact"));
    }

    [TestMethod]
    public void CorruptionLinksTamperingWrongIdentityAndUnlistedFilesAreRejected()
    {
        foreach (byte[] zip in new[]
        {
            new byte[] { 1, 2, 3 }, Zip(tamper: true), Zip(attributes: unchecked((int)0xA0000000)),
            Zip(extraPath: "unlisted.log"), Zip(head: FoundationTests.OtherSha)
        })
            Assert.ThrowsException<ContractException>(() => ReviewEvidenceArchive.Verify(zip,
                new("owner/repo", 1, FoundationTests.Sha, 70, 2), "build", "windows", "artifact"));
    }

    [TestMethod]
    public void CompressedExpansionAndEntryCountCeilingsAreEnforcedBeforeReadingPayloads()
    {
        using MemoryStream oversized = new();
        using (ZipArchive zip = new(oversized, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream raw = zip.CreateEntry("raw.log", CompressionLevel.SmallestSize).Open())
                raw.Write(new byte[EvidenceManifest.MaximumFileBytes + 1]);
            using StreamWriter manifest = new(zip.CreateEntry("manifest.json").Open());
            manifest.Write("{}");
        }
        Assert.IsTrue(oversized.Length < EvidenceManifest.MaximumFileBytes);
        Assert.ThrowsException<ContractException>(() => ReviewEvidenceArchive.Verify(oversized.ToArray(),
            new("owner/repo", 1, FoundationTests.Sha, 70, 2), "build", "windows", "artifact"));
        using MemoryStream many = new();
        using (ZipArchive zip = new(many, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < EvidenceManifest.MaximumFiles + 2; i++) zip.CreateEntry($"entry-{i}");
        Assert.ThrowsException<ContractException>(() => ReviewEvidenceArchive.Verify(many.ToArray(),
            new("owner/repo", 1, FoundationTests.Sha, 70, 2), "build", "windows", "artifact"));
    }

    internal sealed class EvidenceHandler : HttpMessageHandler
    {
        public JsonArray Artifacts { get; } = [];
        public JsonArray Jobs { get; } = [];
        public Dictionary<long, byte[]> Archives { get; } = [];
        public Uri? Redirect { get; set; }

        public EvidenceHandler(bool complete = true)
        {
            int id = 1;
            foreach ((string check, string platform) in CheckPolicy.Required)
            {
                if (!complete && platform == "independent") continue;
                string os = platform == "independent" ? platform : platform == "windows" ? "windows-latest" : "ubuntu-latest";
                Artifacts.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = $"{check}-{os}-{FoundationTests.Sha}-2",
                    ["expired"] = false, ["size_in_bytes"] = 1000,
                    ["workflow_run"] = new JsonObject
                    {
                        ["id"] = 70, ["repository_id"] = 1, ["head_repository_id"] = 2, ["head_sha"] = FoundationTests.Sha
                    }
                });
                Jobs.Add(new JsonObject
                {
                    ["name"] = platform == "independent" ? check : $"{check} ({os})",
                    ["run_id"] = 70, ["status"] = "completed", ["conclusion"] = "success",
                    ["steps"] = new JsonArray(new JsonObject
                    {
                        ["name"] = check == "build" ? "Build" : check == "test"
                            ? platform == "windows" ? "Run tests Windows" : "Run tests Linux"
                            : check == "semgrep" ? "Run Semgrep" : "Run Roslyn",
                        ["status"] = "completed", ["conclusion"] = "success"
                    })
                });
                Archives[id++] = Zip(check, platform);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Task.FromResult(Json(new JsonObject { ["artifacts"] = Artifacts.DeepClone() }));
            if (path.EndsWith("/jobs", StringComparison.Ordinal))
                return Task.FromResult(Json(new JsonObject { ["jobs"] = Jobs.DeepClone() }));
            if (path.EndsWith("/zip", StringComparison.Ordinal))
            {
                if (Redirect is not null)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = Redirect;
                    return Task.FromResult(response);
                }
                long id = long.Parse(path.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archives[id]) });
            }
            throw new AssertFailedException("Unexpected artifact route: " + path);
        }

        private static HttpResponseMessage Json(JsonNode value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    [TestMethod]
    public async Task CompleteAndMissingAnalyzerEvidenceAreDistinguished()
    {
        foreach (bool complete in new[] { true, false })
        {
            using var handler = new EvidenceHandler(complete);
            using HttpClient http = new(handler);
            IngestedEvidence result = await new GitHubReviewEvidence(AdmissionTests.Api(http), http)
                .CollectAsync(Admission(), CancellationToken.None);
            Assert.AreEqual(complete ? 6 : 4, result.Checks.Length);
            Assert.AreEqual(complete, result.Blockers.Length == 0);
            if (!complete) StringAssert.Contains(string.Join(" ", result.Blockers), "semgrep");
        }
    }

    [DataTestMethod]
    [DataRow("expired")]
    [DataRow("size")]
    [DataRow("sha")]
    [DataRow("repository")]
    [DataRow("job")]
    [DataRow("duplicate")]
    [DataRow("skipped-step")]
    public async Task UntrustedArtifactMetadataFailsClosed(string mutation)
    {
        using var handler = new EvidenceHandler();
        switch (mutation)
        {
            case "expired": handler.Artifacts[0]!["expired"] = true; break;
            case "size": handler.Artifacts[0]!["size_in_bytes"] = ReviewEvidenceArchive.MaximumArchiveBytes + 1; break;
            case "sha": handler.Artifacts[0]!["workflow_run"]!["head_sha"] = FoundationTests.OtherSha; break;
            case "repository": handler.Artifacts[0]!["workflow_run"]!["repository_id"] = 99; break;
            case "job": handler.Jobs[0]!["conclusion"] = "failure"; break;
            case "duplicate": handler.Jobs.Add(handler.Jobs[0]!.DeepClone()); break;
            case "skipped-step": handler.Jobs[0]!["steps"]![0]!["conclusion"] = "skipped"; break;
        }
        using HttpClient http = new(handler);
        await Assert.ThrowsExceptionAsync<ContractException>(() =>
            new GitHubReviewEvidence(AdmissionTests.Api(http), http).CollectAsync(Admission(), CancellationToken.None));
    }

    private sealed class StorageHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.IsNull(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [TestMethod]
    public async Task SignedStorageRedirectNeverReceivesGitHubCredentialAndUnsafeOriginsAreRejected()
    {
        using var handler = new EvidenceHandler();
        using var storage = new StorageHandler(Zip());
        using HttpClient apiHttp = new(handler);
        using HttpClient storageHttp = new(storage);
        var client = new GitHubReviewEvidence(AdmissionTests.Api(apiHttp), storageHttp);
        foreach (string uri in new[] { "http://test.blob.core.windows.net/file", "https://evil.test/file",
            "https://test.blob.core.windows.net.evil.test/file", "https://user@test.blob.core.windows.net/file" })
        {
            handler.Redirect = new(uri);
            await Assert.ThrowsExceptionAsync<ContractException>(() => client.CollectAsync(Admission(), CancellationToken.None));
        }
        Assert.AreEqual(0, storage.Calls);
        handler.Redirect = new("https://test.blob.core.windows.net/file?signature=test");
        // First download is valid; second intentionally returns a build/windows archive for another check.
        await Assert.ThrowsExceptionAsync<ContractException>(() => client.CollectAsync(Admission(), CancellationToken.None));
        Assert.AreEqual(2, storage.Calls);
    }
}
