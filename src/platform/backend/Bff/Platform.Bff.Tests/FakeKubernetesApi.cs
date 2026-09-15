using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Bff.Tests;

// SC-22, IADR-0456 決定 4 (#1477): Kubernetes API サーバの偽物（ExternalSecret への force-sync 注釈だけを受ける）。
//
// 🔴 **実 API サーバの形を写す**: Bearer は Pod の ServiceAccount トークン（FakeVault と同じ JWT）でなければ 401、
// CRD への部分更新は `application/merge-patch+json` 以外を 415、ExternalSecret の口以外は 404。
// `ForcedStatus` で RBAC の拒否（403）・不在（404）・障害（5xx）を、`Throws` で不達を再現する。
public sealed class FakeKubernetesApi
{
    public const string ApiServer = "https://kubernetes.test";

    public sealed record RecordedRequest(string Method, string Path, string? ContentType, string? Body, string? Authorization);

    private static readonly Regex ExternalSecretPath = new(
        "^/apis/external-secrets\\.io/v1/namespaces/(?<ns>[^/]+)/externalsecrets/(?<name>[^/]+)$", RegexOptions.Compiled);

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public HttpStatusCode? ForcedStatus { get; set; }
    public bool Throws { get; set; }

    public void Reset()
    {
        Requests.Clear();
        ForcedStatus = null;
        Throws = false;
    }

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeKubernetesApi api) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var authorization = request.Headers.Authorization?.ToString();
            api.Requests.Enqueue(new RecordedRequest(
                request.Method.Method, path, request.Content?.Headers.ContentType?.MediaType, body, authorization));

            if (api.Throws)
                throw new HttpRequestException("kubernetes api unreachable (fake)");
            if (authorization != "Bearer " + FakeVault.ServiceAccountJwt)
                return Json(HttpStatusCode.Unauthorized, """{"kind":"Status","reason":"Unauthorized"}""");
            if (api.ForcedStatus is { } forced)
                return Json(forced, """{"kind":"Status","reason":"forced"}""");

            var match = ExternalSecretPath.Match(path);
            if (!match.Success || request.Method != HttpMethod.Patch)
                return Json(HttpStatusCode.NotFound, """{"kind":"Status","reason":"NotFound"}""");
            if (request.Content?.Headers.ContentType?.MediaType != "application/merge-patch+json")
                return Json(HttpStatusCode.UnsupportedMediaType, """{"kind":"Status","reason":"UnsupportedMediaType"}""");

            return Json(HttpStatusCode.OK,
                "{\"apiVersion\":\"external-secrets.io/v1\",\"kind\":\"ExternalSecret\",\"metadata\":{\"name\":\""
                + match.Groups["name"].Value + "\",\"namespace\":\"" + match.Groups["ns"].Value + "\"}}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
