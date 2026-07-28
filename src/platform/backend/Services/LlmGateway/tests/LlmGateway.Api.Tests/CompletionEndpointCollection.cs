namespace LlmGateway.Api.Tests;

// IADR-0110 (#395): 補完エンドポイント（/complete・/complete/stream）を叩くテストを直列化するための
// コレクション。`MeterListener` は **Meter 名でプロセス全体の測定を購読**するため、これらのクラスが
// 並行実行されると `CompletionMetricsTests` が他クラスの発行した測定まで拾ってしまう。
// xUnit は既定でクラス（＝コレクション）単位に並行実行するため、同一コレクションへ入れて直列化する。
[CollectionDefinition(Name)]
public sealed class CompletionEndpointCollection
{
    public const string Name = "llm-completion-endpoints";
}
