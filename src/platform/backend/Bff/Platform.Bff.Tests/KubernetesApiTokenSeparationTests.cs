using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Bff.Foundation.Secrets;
using Xunit;

namespace Platform.Bff.Tests;

// SC-22, NFR-18, IADR-0585 決定 2, IADR-0456 決定 4, IADR-0460 決定 1 (#2193):
// BFF が Kubernetes API（ExternalSecret の `get` / force-sync の `patch`）へ名乗るトークンと、Vault（k8s auth）へ名乗るトークンは
// **読み先が別**である。#2082 で Vault 用を projected token（audience: vault）へ移したあと、Kubernetes API への要求も同じ口
// （`IServiceAccountTokenReader`）を通っていたため Vault 用のトークンを送り、kube-apiserver に 401 で拒まれていた。
//
// 🔴 本物の登録（`AddSecretItemInjection`）と本物のファイル読み取りで測る —— 偽物の口を差し替える BffTestFactory の試験だけでは、
// 「本番の登録がどちらのファイルを読むか」が見えない。
public sealed class KubernetesApiTokenSeparationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bff-2193-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Default_paths_point_at_the_kubernetes_api_token_and_the_vault_projected_token_respectively()
    {
        ExternalSecretSyncOptions.DefaultServiceAccountTokenPath.Should().Be("/var/run/secrets/kubernetes.io/serviceaccount/token");
        new ExternalSecretSyncOptions().ServiceAccountTokenPath.Should().Be(ExternalSecretSyncOptions.DefaultServiceAccountTokenPath);
        ExternalSecretSyncOptions.DefaultServiceAccountTokenPath.Should().NotBe(VaultOptions.DefaultServiceAccountTokenPath);
    }

    [Fact]
    public async Task Registered_readers_read_their_own_files_even_when_both_are_configured()
    {
        await using var provider = BuildProvider(out _, out _);

        var kubernetes = await provider.GetRequiredService<IKubernetesApiTokenReader>().ReadAsync(TestContext.Current.CancellationToken);
        var vault = await provider.GetRequiredService<IServiceAccountTokenReader>().ReadAsync(TestContext.Current.CancellationToken);

        kubernetes.Should().Be(FakeKubernetesApi.ServiceAccountJwt);
        vault.Should().Be(FakeVault.ServiceAccountJwt);
    }

    // 受け入れ基準: Vault 用のトークンを Kubernetes API へ送らない。偽の API サーバは Kubernetes API 用の値以外を 401 で拒む。
    [Fact]
    public async Task Sync_request_and_presence_read_send_the_kubernetes_api_token_and_never_the_vault_token()
    {
        await using var provider = BuildProvider(out var api, out _);
        var target = new ExternalSecretReference("platform-infra", "keycloak-smtp");
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetRequiredService<IExternalSecretSyncRequester>().RequestSyncAsync(target, ct))
            .Should().Be(ExternalSecretSyncOutcome.Requested);
        (await provider.GetRequiredService<IExternalSecretPresenceReader>().ReadAsync(target, ct))
            .Should().Be(ExternalSecretPresence.Present);

        api.Requests.Should().HaveCount(2)
            .And.OnlyContain(r => r.Authorization == "Bearer " + FakeKubernetesApi.ServiceAccountJwt);
    }

    // 陰性の対: Kubernetes API 用のファイルが無ければ（＝既定のトークンがマウントされていない）、Vault 用へ倒れずに失敗する。
    [Fact]
    public async Task Missing_kubernetes_api_token_fails_without_falling_back_to_the_vault_token()
    {
        await using var provider = BuildProvider(out var api, out var kubernetesTokenPath);
        File.Delete(kubernetesTokenPath);
        var target = new ExternalSecretReference("platform-infra", "keycloak-smtp");
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetRequiredService<IExternalSecretSyncRequester>().RequestSyncAsync(target, ct))
            .Should().Be(ExternalSecretSyncOutcome.Failed);
        (await provider.GetRequiredService<IExternalSecretPresenceReader>().ReadAsync(target, ct))
            .Should().Be(ExternalSecretPresence.Unknown);
        api.Requests.Should().BeEmpty("トークンを読めなければ送らない（Vault 用で代用しない）");
    }

    private ServiceProvider BuildProvider(out FakeKubernetesApi api, out string kubernetesTokenPath)
    {
        kubernetesTokenPath = Path.Combine(_dir, "kubernetes-token");
        var vaultTokenPath = Path.Combine(_dir, "vault-token");
        File.WriteAllText(kubernetesTokenPath, FakeKubernetesApi.ServiceAccountJwt + "\n");
        File.WriteAllText(vaultTokenPath, FakeVault.ServiceAccountJwt + "\n");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:Address"] = FakeVault.Address,
            ["Vault:ServiceAccountTokenPath"] = vaultTokenPath,
            ["ExternalSecretSync:Enabled"] = "true",
            ["ExternalSecretSync:ApiServer"] = FakeKubernetesApi.ApiServer,
            ["ExternalSecretSync:ServiceAccountTokenPath"] = kubernetesTokenPath,
        }).Build();

        var fake = new FakeKubernetesApi();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSecretItemInjection();
        services.AddHttpClient(ExternalSecretSyncRequester.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => fake.CreateHandler());
        api = fake;
        return services.BuildServiceProvider();
    }
}
