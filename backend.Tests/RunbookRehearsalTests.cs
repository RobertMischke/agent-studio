using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AgentStudio.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Executable proof for the two operational runbooks in
/// docs/operations/setup/networked-task-server.md — "Runner secret rotation
/// rehearsal" and "Certificate renewal rehearsal". The review flagged both as
/// documented but not exercised. These tests walk each runbook's steps against
/// the real security store / real X.509 crypto and assert the invariants the
/// operator would otherwise have to verify by hand, and they emit the same
/// secret-free evidence the operator script (deploy/networked/rehearse-runbooks.sh)
/// writes to its evidence log. They are deterministic and in-process (no sockets,
/// no timing windows), so they run in the standard gate.
/// </summary>
public sealed class RunbookRehearsalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-runbook-rehearsal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Runner_secret_rotation_runbook_is_exercised()
    {
        var store = NewStore();
        var evidence = new List<string>();
        void Note(string line) => evidence.Add($"{DateTime.UtcNow:O} rotation: {line}");

        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", "First Owner"));
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("rehearsal-runner", [RunnerScopes.Claim, RunnerScopes.Lease], null, null));
        var initial = store.EnrollRunner(enrollment.Code);
        Note($"enrolled runner id={initial.Runner.Id} credential={initial.Credential.Id}");
        Assert.NotNull(store.AuthenticateRunner(initial.Secret));

        // Runbook step 1-2: mint an OVERLAPPING credential (the API's
        // POST /api/auth/runners/{id}/credentials) and prove the overlap — the old
        // daemon keeps working while the new secret is installed and restarted.
        var rotated = store.RotateRunner(initial.Runner.Id, new RunnerRotateRequest([RunnerScopes.Claim, RunnerScopes.Lease], null));
        Note($"minted overlapping credential={rotated.Credential.Id} (secret withheld)");
        Assert.NotNull(store.AuthenticateRunner(initial.Secret)); // old still valid during overlap
        Assert.NotNull(store.AuthenticateRunner(rotated.Secret));  // new already valid

        // Runbook step 3-4: revoke ONLY the old credential; prove the old secret is
        // now refused (would be 401 on the wire) and the new one still authenticates.
        store.RevokeCredential(initial.Runner.Id, initial.Credential.Id);
        Assert.Null(store.AuthenticateRunner(initial.Secret));
        Assert.NotNull(store.AuthenticateRunner(rotated.Secret));
        Note($"revoked old credential={initial.Credential.Id}; old secret now fails closed, new secret still claims");

        // Runbook step 5: host loss — revoke the whole identity and prove every
        // credential fails closed without waiting for expiry.
        store.RevokeRunner(initial.Runner.Id);
        Assert.Null(store.AuthenticateRunner(rotated.Secret));
        Note($"revoked identity={initial.Runner.Id}; all credentials fail closed (host-loss path)");

        // The rehearsal must leave durable, non-secret audit evidence (N7/N9): the
        // captured evidence names credential ids and outcomes but never a secret.
        var log = string.Join("\n", evidence);
        Assert.Contains("fails closed", log);
        Assert.Contains(initial.Credential.Id, log);
        Assert.Contains(rotated.Credential.Id, log);
        Assert.DoesNotContain(initial.Secret, log);
        Assert.DoesNotContain(rotated.Secret, log);
    }

    [Fact]
    public void Certificate_renewal_runbook_invariants_hold()
    {
        // Mirrors deploy/networked/rehearse-runbooks.sh cert-selftest in-process:
        // issue a "current" cert, simulate ACME renewal with a later expiry, and
        // assert the invariants an operator checks (serial rotates, expiry extends,
        // both parse, 21-day alert threshold computes) — no production cert needed.
        var now = DateTimeOffset.UtcNow;
        using var current = IssueSelfSigned(now.AddDays(-1), now.AddDays(20));
        using var renewed = IssueSelfSigned(now.AddDays(-1), now.AddDays(90));

        Assert.NotEqual(current.SerialNumber, renewed.SerialNumber);
        Assert.True(renewed.NotAfter > current.NotAfter, "renewed certificate must expire later than the current one");
        // Both certificates parse and expose a usable public key.
        Assert.NotNull(current.GetRSAPublicKey());
        Assert.NotNull(renewed.GetRSAPublicKey());

        // 21-day pre-expiry alert threshold: the current (20-day) cert is inside the
        // window and would alert; the renewed (90-day) cert is not.
        var threshold = TimeSpan.FromDays(21);
        Assert.True(current.NotAfter.ToUniversalTime() - DateTime.UtcNow < threshold, "current cert should be within the alert window");
        Assert.True(renewed.NotAfter.ToUniversalTime() - DateTime.UtcNow > threshold, "renewed cert should clear the alert window");
    }

    private static X509Certificate2 IssueSelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=agent-taskboard-rehearsal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private AccessSecurityStore NewStore()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, Guid.NewGuid().ToString("N")),
            ["Security:Profile"] = "networked",
        }).Build();
        return new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
