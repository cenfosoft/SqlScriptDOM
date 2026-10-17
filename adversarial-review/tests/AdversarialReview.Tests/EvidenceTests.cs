using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdversarialReview.Tests;

[TestClass]
public sealed class EvidenceTests
{
    private string directory = "";
    private readonly EvidenceIdentity identity = new("owner/repo", 1, FoundationTests.Sha, 1, 1);

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), $"scriptdom-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "results.trx"), "synthetic results");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void ManifestRoundTripsAndVerifiesExpectedIdentity()
    {
        var manifest = EvidenceManifest.Create(directory, identity, "test", "ubuntu", "passed");
        string json = Contracts.Write(manifest, "evidence-manifest");
        File.WriteAllText(Path.Combine(directory, EvidenceManifest.FileName), json);
        Contracts.Read<EvidenceManifest>(json, "evidence-manifest").Verify(directory, identity);
        Assert.ThrowsException<ContractException>(() => manifest.Verify(directory, identity with { HeadSha = FoundationTests.OtherSha }));
        Assert.ThrowsException<ContractException>(() => manifest.Verify(directory, identity with { RunAttempt = 2 }));
    }

    [TestMethod]
    public void TamperingUnexpectedFilesAndDuplicatePathsAreRejected()
    {
        var manifest = EvidenceManifest.Create(directory, identity, "test", "ubuntu", "passed");
        File.WriteAllText(Path.Combine(directory, "results.trx"), "tampered");
        Assert.ThrowsException<ContractException>(() => manifest.Verify(directory, identity));
        var current = EvidenceManifest.Create(directory, identity, "test", "ubuntu", "passed");
        File.WriteAllText(Path.Combine(directory, "unexpected.log"), "new");
        Assert.ThrowsException<ContractException>(() => current.Verify(directory, identity));
        Assert.ThrowsException<ContractException>(() => (current with { Files = [current.Files[0], current.Files[0]] }).Verify(directory, identity));
    }

    [TestMethod]
    public void EmptyAndOversizeEvidenceIsRejected()
    {
        File.Delete(Path.Combine(directory, "results.trx"));
        Assert.ThrowsException<ContractException>(() => EvidenceManifest.Create(directory, identity, "test", "ubuntu", "passed"));
        using (FileStream stream = File.Create(Path.Combine(directory, "oversize.log")))
            stream.SetLength(EvidenceManifest.MaximumFileBytes + 1);
        Assert.ThrowsException<ContractException>(() => EvidenceManifest.Create(directory, identity, "test", "ubuntu", "passed"));
    }
}
