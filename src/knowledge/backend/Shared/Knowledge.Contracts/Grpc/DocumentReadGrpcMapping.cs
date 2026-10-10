using Google.Protobuf.WellKnownTypes;
using Knowledge.Contracts.Dtos;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Knowledge.Contracts.Grpc;

// FR-06, UC-03, SC-03, NFR-09, ADR-0029, ADR-0070, ADR-0075, [[IADR-0290]], [[IADR-0379]],
// [[IADR-0388]] 決定 2, [[IADR-0397]] 決定 5, [[IADR-0400]] 決定 4, [[IADR-0402]] (#1255):
// 文書読み取りの DTO ↔ proto の写像。**写像だけ**を置く
// （キャッシュ・タイムアウト・リトライ・fail-safe は呼び出し元側。`ADR-0029` 2026-08-04 追記）。
//
// 🔴 **両向きを 1 か所に置く。** サーバ（DocumentService）とクライアント（BFF）で写し方が
// 割れると、片方だけが既定値を再適用する形になる —— それが proto3 の既定で静かに壊れる典型である
// （`LlmGrpcMapping` と同じ配置理由）。
//
// 🔴 **proto3 に null は無い（決定の実体はここ）**
//   - `HasBody`: DTO の既定は **true**、proto3 の既定は **false** で**向きが逆**である。
//     `ToProto` は常に明示代入し、`ToDto` はそのまま読む。写し漏れると全文書が「本文なし」に見え、
//     SC-03 が本文の位置へ「本文なし（原本を参照）」を出す（`ADR-0070` 決定 3）。
//   - `MarkdownUri` / `ChangeNote`: `null` と `""` は画面で別物なので **presence** で運ぶ
//     （`optional`。`HasMarkdownUri` / `HasChangeNote` を読む）。
//   - `SharedWith`（#1898）: `repeated` は presence を持たないので、null（共有なし）は 0 件で運び、
//     `ToDto` は 0 件を null へ戻す（空リストはサーバが作らない形で、往復で null へ正規化される）。
//   - 時刻は `Timestamp`。台帳は `DateTimeOffset.UtcNow` で書くのでオフセットは 0 であり、
//     `ToDateTimeOffset()` の戻り（オフセット 0）と一致する。tick 精度は保たれる。
public static class DocumentReadGrpcMapping
{
    public static Pb.DocumentSummary ToProto(DocumentDto d)
    {
        var m = new Pb.DocumentSummary
        {
            Id = d.Id.ToString("D"),
            Title = d.Title,
            Status = d.Status,
            Version = d.Version,
            CreatedAt = Timestamp.FromDateTimeOffset(d.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(d.UpdatedAt),
            // 🔴 明示代入。proto3 の既定（false）は DTO の既定（true）と逆である。
            HasBody = d.HasBody,
        };
        // 🔴 null のときは**代入しない**（presence を立てない）。空文字を書くと画面の縮退文言が変わる。
        if (d.MarkdownUri is not null) m.MarkdownUri = d.MarkdownUri;
        // FR-06, ADR-0050 決定 1 (#1575): 本文指紋も presence で運ぶ（null は「本文なし・不明」）。
        if (d.ContentFingerprint is not null) m.ContentFingerprint = d.ContentFingerprint;
        foreach (var (key, value) in d.Attributes) m.Attributes[key] = value;
        m.Tags.AddRange(d.Tags);
        // FR-06, FR-19, ADR-0036 D-06, ADR-0098 決定 1, [[IADR-0447]] (#1898): 共有先の写し。
        // 🔴 写し漏れると BFF の共有先ベースの分岐が gRPC 経路でだけ一致しない（共有された相手が 404・
        // 所有者への応答から `sharedWith` が消える）。null（共有なし）は 0 件で運ぶ。
        if (d.SharedWith is not null) m.SharedWith.AddRange(d.SharedWith);
        return m;
    }

    public static DocumentDto ToDto(Pb.DocumentSummary m) => new()
    {
        Id = Guid.Parse(m.Id),
        Title = m.Title,
        Status = m.Status,
        MarkdownUri = m.HasMarkdownUri ? m.MarkdownUri : null,
        Version = m.Version,
        Attributes = new Dictionary<string, string>(m.Attributes),
        Tags = [.. m.Tags],
        CreatedAt = m.CreatedAt.ToDateTimeOffset(),
        UpdatedAt = m.UpdatedAt.ToDateTimeOffset(),
        HasBody = m.HasBody,
        ContentFingerprint = m.HasContentFingerprint ? m.ContentFingerprint : null,
        // FR-19 (#1898): 0 件は **null**（`DocumentDto.SharedWith` の契約「null＝共有なし」。
        // サーバは空リストを作らない〔`DocumentEndpoints.NullIfEmpty`〕ので REST と同じ形になる）。
        SharedWith = m.SharedWith.Count > 0 ? [.. m.SharedWith] : null,
    };

    public static Pb.DocumentVersionSnapshot ToProto(DocumentVersionDto v)
    {
        var m = new Pb.DocumentVersionSnapshot
        {
            DocumentId = v.DocumentId.ToString("D"),
            Version = v.Version,
            Title = v.Title,
            Status = v.Status,
            CreatedAt = Timestamp.FromDateTimeOffset(v.CreatedAt),
        };
        if (v.ChangeNote is not null) m.ChangeNote = v.ChangeNote;
        foreach (var (key, value) in v.Attributes) m.Attributes[key] = value;
        m.Tags.AddRange(v.Tags);
        return m;
    }

    public static DocumentVersionDto ToDto(Pb.DocumentVersionSnapshot m) => new()
    {
        DocumentId = Guid.Parse(m.DocumentId),
        Version = m.Version,
        Title = m.Title,
        Status = m.Status,
        Attributes = new Dictionary<string, string>(m.Attributes),
        Tags = [.. m.Tags],
        ChangeNote = m.HasChangeNote ? m.ChangeNote : null,
        CreatedAt = m.CreatedAt.ToDateTimeOffset(),
    };
}
