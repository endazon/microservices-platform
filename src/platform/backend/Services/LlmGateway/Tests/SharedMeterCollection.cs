using LlmGateway.Tests.Grpc;

namespace LlmGateway.Tests;

// IADR-0110 (#395) / IADR-0394 (#1275): **共有 Meter へ測定を発行するテストクラス**を直列化する
// コレクション。`MeterListener` は Meter 名でプロセス全体の測定を購読するため、これらのクラスが
// 並行実行されると、Meter 名で絞る probe が他クラスの発行した測定まで拾う。
// xUnit は既定でクラス（＝コレクション）単位に並行実行するため、同一コレクションへ入れて直列化する。
//
// 🔴 **加入規則は「補完エンドポイント（/complete・/complete/stream）を叩くクラス」ではない。**
// #1275 でその規則が危険の範囲より狭いことが実測された —— `LlmUsageMetricsTests` は
// エンドポイントを叩かないが `LlmUsageMetrics.RecordUsage` で同じ Meter へ発行しており、
// 加入していなかったために `LlmSyntheticUsageExclusionTests` の不在の表明が破れた
// （並列度を上げると 5/5 で再現した）。**発行するなら加入する。**
//
// 🔴 **これは多層防御であって主たる防護ではない。** 主は probe 側の絞り込み（Meter の
// **インスタンス**で購読する。IADR-0394 決定 1）である —— 直列化は同一アセンブリ内でしか効かず、
// 加入し忘れは静かに起きる（本 issue がその実例である）。
//
// IADR-0398 (#1255): 🔴 **gRPC の実 Kestrel 器（GrpcKestrelFactory）もこのコレクションが持つ。**
// 理由は 2 つあり、どちらも「1 つに保つ」という同じ形である。
//   1. `GrpcTestConfiguration` は h2c ポートを**プロセスで 1 つだけ**選ぶ。器をクラスごとに作ると
//      2 つ目の Kestrel が同じポートへ bind できず起動に失敗する（埋め込みだけのときは
//      gRPC のテストクラスが 1 つしか無かったので露見しなかった）。
//   2. gRPC の補完テストは **`/complete` と同じ Meter へ発行する**。上の加入規則
//      「発行するなら加入する」により、そもそもこのコレクションへ入らなければならない ——
//      入れずに走らせたところ、`CompletionMetricsTests` の probe が本テストの発行を拾って
//      落ちた（実測）。クラスは 1 つのコレクションにしか属せないので、**器の共有と
//      直列化を同じコレクションで満たす**。
[CollectionDefinition(Name)]
public sealed class SharedMeterCollection : ICollectionFixture<GrpcKestrelFactory>
{
    public const string Name = "llm-shared-meter";
}
