namespace DocumentService.Domain.Ports;

// FR-06, UC-03 / ADR-0027（E3b）: 文書更新イベントの発行口。
//
// 🔴 **抽象を挟む理由は IDocumentDeletedPublisher と同じ**（トポロジ検査の導出単位がファイル）。
// 加えて E3b では、**発行 8 箇所のうち 7 箇所が検査器に不可視**（`Publish(ToEvent(...))` の形。
// IADR-0245 決定 8 の実測）だった。ポートへ集約しアダプタ側でイベントを構築することで、
// **可視の発行点が 1 箇所に定まり**、DocumentUpdated の発行トランスポートが表に正しく載る。
//
// ⚠️ 引数は素の値で渡す（契約型を渡すと `findPublishers` から発行が見えなくなる）。
// 識別子 → 表示名の変換（タグ）は呼び出し側の責務のまま（変換点は
// `DocumentEndpoints.PublishUpdatedAsync` の 1 つ。IADR-0153 決定 2）。
//
// FR-06, ADR-0027 §決定, IADR-0608 (#2168): **発行は DB の確定と同じトランザクションに載る（Transactional Outbox）。**
// 従前は `SaveChangesAsync` の後に素の発行を呼んでおり、コミットの後・発行の前にプロセスが止まると更新が下流へ伝わらなかった。
//
// 使い方は 1 つである（呼び出し側の約束）:
//   `CommitAsync(async c => { await db.SaveChangesAsync(c); ...発行を積む... }, ct)`
//   関数の中で DB を保存し（トランザクションの中なので、まだ確定しない）、保存の後の DB を読んでイベントを積む。
//   関数が終わると、DB の変更と積んだ発行を 1 つのトランザクションで確定し、確定の後に送る。
// 🔴 **`PublishUpdatedAsync` は `CommitAsync` の関数の中でだけ呼べる**（外では例外。確定されない発行を作らない）。
// 🔴 **段 1（`IDocumentDeletedPublisher`）の「積む → 確定」の順にしないのは、イベントの中身が保存の後の DB に依るからである**
// （共有先・タグの表示名。保存の前に積むと、共有の付与・取り消し・タグ改名の直後のイベントが古い値を運ぶ。IADR-0608 決定 1）。
// 確定が失敗したときは積んだ発行も巻き戻り、1 通も送られない（例外はそのまま呼び出し側へ出る）。
// 確定の後に送出が失敗した発行は Outbox の表に残り、Wolverine の耐久エージェントが再送する（再起動を跨いでも）。
public interface IDocumentUpdatedPublisher
{
    // DB の変更と、`saveAndPublish` の中で積んだ発行を 1 トランザクションで確定し、確定の後に送る。
    // `saveAndPublish` は DB を保存してから発行を積む（保存しなければ DB の変更は確定しない）。1 スコープで何度呼んでもよい。
    Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default);

    // ⚠️ Wolverine の `IMessageBus.PublishAsync` は CancellationToken を取らない（E1 仕様書
    // 「受け入れた挙動差」）。ct は契約として受けるが、現行実装では伝播されない。
    // contentFingerprint: ADR-0050 決定 1 (#911)。本文の内容のみに依存する不透明な値（null = 不明）。
    Task PublishUpdatedAsync(
        Guid documentId,
        string title,
        string status,
        string? markdownUri,
        Dictionary<string, string> attributes,
        List<string> tags,
        DateTimeOffset updatedAt,
        string? contentFingerprint = null,
        // ADR-0070 決定 3・決定 4 / [[IADR-0388]] 決定 2・4 (#1254 / #1253):
        // 原本が本文を持っていたか（SC-03 の材料）と、原本の所在・データソースの表示名
        // （本文なしの文書の索引テキストの材料）。**台帳（`Document`）の値を写す** ——
        // 属性編集やタグ改名による再発行でも同じ値が乗る。
        bool hasBody = true,
        string? originalPath = null,
        string? dataSourceName = null,
        // FR-19, FR-20, ADR-0036 D-06, ADR-0061 決定 5 / [[IADR-0396]] 決定 3 (#1184):
        // 共有先（`DocumentShare` の被共有主体）。**属性辞書では運べない集合**であり、
        // 索引の側から共有先ベースの分岐を評価できるようにするために独立の項目で運ぶ。
        // 解決は `DocumentEndpoints.PublishUpdatedAsync` の 1 か所だけで行う。
        List<string>? sharedWith = null,
        CancellationToken ct = default);
}
