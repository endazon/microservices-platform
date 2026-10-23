namespace DocumentService.Domain.Ports;

// FR-06 / ADR-0027（E3a）: 文書削除イベントの発行口。
//
// 🔴 **この抽象を挟む理由は設計の好みではない。トポロジ検査の導出単位がファイルだからである。**
// `scripts/check-event-topology.js` の `transportsOfFile` は、ファイル中の `using` から
// そのファイルのトランスポートを導出する。DocumentEndpoints.cs には DocumentUpdated の
// MassTransit 発行（辺 E3b の射程）が残るため、同ファイルで DocumentDeleted を Wolverine 発行
// すると `using` が同居し、発行が `masstransit+wolverine` の union として記録される
// （E1 の IDocumentNormalizedPublisher と同じ理由。IADR-0245 副産物の非対称性）。
//
// よって発行を別ファイル（アダプタ）へ切り出し、1 ファイルに 1 トランスポートだけが現れるようにする。
//
// ⚠️ **イベントの構築は実装側（アダプタ）に置く。** ここで契約型を組み立てて `Publish(ev)` の
// 形にすると、`findPublishers` の regex（`Publish(new <Event>(` にしか一致しない）から
// 発行が見えなくなり、不可視発行元を新たに作ってしまう。引数は素の値で渡す。
//
// FR-06, FR-19, ADR-0027 §決定, ADR-0057 決定 1・3, IADR-0604 (#2042): **発行は DB の確定と同じトランザクションに載る
// （Transactional Outbox）。** 従前は `SaveChangesAsync` の後に素の発行を呼んでおり、コミットの後・送出の前に
// ブローカ不達かプロセス終了が起きると、台帳の行はもう無く**イベントを出し直す起点がどこにも残らなかった**。
//
// 使い方は 2 段である（呼び出し側の約束）:
//   1. DB の変更を DbContext へ積む。あわせて `PublishDeletedAsync` で発行を**積む**（この時点では送らない）。
//   2. `SaveChangesAndFlushAsync` で DB の変更と積んだ発行を**1 つのトランザクションで**確定し、確定の後に送る。
// 🔴 **`DocumentDbContext.SaveChangesAsync` を直接呼んで確定してはならない**（積んだ発行が送られないまま残る）。
// 確定が失敗したときは積んだ発行も巻き戻り、1 通も送られない（例外はそのまま呼び出し側へ出る）。
// 確定の後に送出が失敗した発行は Outbox の表に残り、Wolverine の耐久エージェントが再送する（再起動を跨いでも）。
public interface IDocumentDeletedPublisher
{
    // ⚠️ Wolverine の `IMessageBus.PublishAsync` は CancellationToken を取らない（IADR-0245 /
    // E1 仕様書「受け入れた挙動差」）。ct は契約として受けるが、現行実装では伝播されない。
    Task PublishDeletedAsync(Guid documentId, DateTimeOffset deletedAt, CancellationToken ct = default);

    // DB の変更（スコープの DocumentDbContext に積んだもの）と積んだ発行を 1 トランザクションで確定し、確定の後に送る。
    // 1 スコープで何度呼んでもよい（定期処理は所有者ごと・周期の段ごとに確定する）。
    Task SaveChangesAndFlushAsync(CancellationToken ct = default);
}
