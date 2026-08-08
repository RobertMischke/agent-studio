using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteArtifactTransferTests
{
    [Fact]
    public void Complete_acknowledgement_accepts_every_uploaded_result_path()
    {
        var uploads = new List<RunnerArtifactUpload>
        {
            new("results/deliverables.md", "ZGVsaXZlcmFibGVz"),
            new("results/nested/proof.txt", "cHJvb2Y="),
        };
        var response = new ArtifactIngestResponse(
            "AGT-1",
            2,
            ["results/nested/proof.txt", "results/deliverables.md"],
            ResultDocumentGenerated: true,
            ResultDocumentStatus: "generated");

        RemoteTaskRunner.ValidateArtifactAcknowledgement("AGT-1", uploads, response);
    }

    [Fact]
    public void Partial_acknowledgement_fails_before_worktree_teardown()
    {
        var uploads = new List<RunnerArtifactUpload>
        {
            new("results/deliverables.md", "ZGVsaXZlcmFibGVz"),
            new("results/nested/proof.txt", "cHJvb2Y="),
        };
        var response = new ArtifactIngestResponse(
            "AGT-1",
            1,
            ["results/deliverables.md"]);

        var error = Assert.Throws<InvalidDataException>(() =>
            RemoteTaskRunner.ValidateArtifactAcknowledgement("AGT-1", uploads, response));

        Assert.Contains("1/2 artifact(s)", error.Message);
    }
}
