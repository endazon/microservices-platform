namespace Knowledge.IntegrationTests.Fixtures;

// NFR, ADR-0027, [[IADR-0130]], [[IADR-0231]] 決定 3, [[IADR-0414]] (#1336):
// **統合テストが「自分の依存を得られるか」を判定する唯一の点。**
//
// 🔴 **従前の門は「Docker が入っているか」を訊いていた**（`DockerRequired.SkipUnlessAvailable`）。
// それは**問いが違う**。テストが要るのは PostgreSQL・ブローカ・Qdrant・オブジェクトストレージ
// といった**サービス**であって、Docker という特定の入手経路ではない。
//
// この取り違えの代償は実測されている ——
// **Docker Engine API を持たないランタイム（Rancher Desktop の containerd バックエンド等）では、
// 依存を外から与えても 1 件も走らなかった。** `RabbitMqFixture` と `PostgresFixture` は
// #455 W3 / #1073 で外部供給の口を持ったのに、**門がそれを見ていなかった**ためである
// （`BrokerRequired` だけが正しく「外部 または Docker」を訊いていた ——
// 本クラスはその判定を全依存へ広げたものである）。
//
// 🔴 **依存ごとに訊く。** 「Docker がある」を 1 つの真偽値にまとめると、
// **ブローカだけ外から与えた状態**で Postgres を要るテストまで走ってしまい、
// skip ではなく失敗になる（`BrokerRequired` が判定を分けた当時の理由。ここでは
// 依存を明示させることで、同じ精度を全依存へ保ったまま統合した）。
public static class RequiredServices
{
    /// <summary>PostgreSQL（`PostgresFixture` と、自前で立てる試験の両方が使う）。</summary>
    public static ExternallySuppliable Postgres { get; } = new(
        "PostgreSQL",
        PostgresFixture.ExternalEndpointVariable,
        () => PostgresFixture.ExternalEndpoint,
        "Host=localhost;Port=5432;Database=integration_test;Username=kp;Password=kp");

    /// <summary>メッセージブローカ（RabbitMQ）。</summary>
    public static ExternallySuppliable Broker { get; } = new(
        "RabbitMQ",
        RabbitMqFixture.ExternalEndpointVariable,
        () => RabbitMqFixture.ExternalEndpoint,
        "amqp://guest:guest@localhost:5672");

    /// <summary>ベクトル検索（Qdrant）。値は gRPC の `host:port`。</summary>
    public static ExternallySuppliable Qdrant { get; } = new(
        "Qdrant",
        QdrantEndpoint.ExternalEndpointVariable,
        () => QdrantEndpoint.External,
        "localhost:6334");

    /// <summary>オブジェクトストレージ（MinIO / S3 互換）。値は S3 の端点 URL。</summary>
    public static ExternallySuppliable ObjectStorage { get; } = new(
        "MinIO",
        MinioEndpoint.ExternalEndpointVariable,
        () => MinioEndpoint.External,
        "http://localhost:9000");

    /// <summary>
    /// 要る依存を**すべて**得られるのでなければ、テストを**真の Skipped** にする。
    /// </summary>
    /// <remarks>
    /// 🔴 `if (...) return;` のソフトスキップにしないこと（[[IADR-0231]] 決定 3 が撲滅した
    /// 「走っていないのに Passed」へ退化する）。
    /// 🔴 **skip の理由に「どうすれば走るか」を書く。** 「Docker を起動せよ」しか言わない門は、
    /// Docker を使えない利用者に**打つ手が無いように見せる**（それが #1336 の出発点だった）。
    /// </remarks>
    public static void SkipUnlessObtainable(params ExternallySuppliable[] needs)
    {
        var missing = needs.Where(n => !n.Obtainable).ToList();
        Assert.SkipWhen(
            missing.Count > 0,
            "この試験が要るサービスを得られない: "
            + string.Join(" / ", missing.Select(m => m.Name))
            + "。コンテナランタイム（Docker Engine API）を起動するか、"
            + "次の環境変数へ外部の端点を与えること —— "
            + string.Join(" / ", missing.Select(m => $"{m.Variable}={m.Example}")));
    }
}

// 依存 1 つ分の記述。**外部から与えられるか、コンテナで起こせるかのどちらか**で得られる。
public sealed class ExternallySuppliable(
    string name, string variable, Func<string?> external, string example)
{
    public string Name { get; } = name;
    public string Variable { get; } = variable;
    public string Example { get; } = example;

    /// <summary>外部から与えられた端点（未設定なら <c>null</c>）。</summary>
    public string? External => external();

    /// <summary>
    /// 🔴 **外部供給があれば、コンテナランタイムは要らない。**
    /// これが本クラスの要点である —— 従前の門はここを Docker の有無だけで決めていた。
    /// </summary>
    public bool Obtainable => External is not null || DockerRequired.IsAvailable();
}
