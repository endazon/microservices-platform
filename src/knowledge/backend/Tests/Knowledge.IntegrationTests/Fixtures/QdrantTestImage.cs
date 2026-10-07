namespace Knowledge.IntegrationTests.Fixtures;

// FR-02, FR-03, ADR-0009, [[IADR-0315]] (#1790):
// 統合試験が起こす Qdrant のイメージの**単一の置き場**。
//
// 🔴 **配備と同じ版に固定する**（`deploy/docker-compose.yml`・`deploy/local/infra/qdrant.yaml`）。
// Testcontainers 4.12.0 の引数なしの `QdrantBuilder()` は廃止予定（CS0618）で、しかも v1.13.4 を起こす。
// 配備と違う版で緑になっても配備の保証にならない（サーバ版はクライアント版へ揃える。IADR-0315）。
// 配備側との一致は `QdrantTestImageDefinitionTests`（コンテナを起こさない。PR の ci.yml で走る）が止める。
public static class QdrantTestImage
{
    /// <summary>配備（compose・k8s マニフェスト）と同じ参照。</summary>
    public const string Reference = "qdrant/qdrant:v1.18.1";
}
