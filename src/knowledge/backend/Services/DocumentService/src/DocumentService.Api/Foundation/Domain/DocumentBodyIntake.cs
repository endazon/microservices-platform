using System.Text;

namespace DocumentService.Api.Foundation.Domain;

// FR-21, UC-03, ADR-0014/ADR-0015, ADR-0036: 文書本文の直接受け入れ経路の規則。
// 「本文をどこへ置くか」「どこまで受けるか」「誰が書けるか」の 3 点を 1 か所へ集める。
//
// 本文は**オブジェクトストレージ（MinIO。ADR-0015）へ格納し、DB は参照（storage:// URI）のみ持つ**
// （FR-21 受け入れ基準 ④）。参照の置き場は既存の `Document.MarkdownUri` である —— 新しい欄を作ると
// 取り込み（`DocumentUpdatedConsumer` は `MarkdownUri` の有無で起動する）の分岐が 2 本になり、
// 受け入れ基準 ①（取り込み・分割・埋め込みが起動する）を成立させるために取り込み側の改修が要る。
public static class DocumentBodyIntake
{
    // FR-21 受け入れ基準 ⑥: 本文のサイズ上限は 1 MB。**超過は 413 で拒否し、切り詰めて成功を返さない。**
    public const int MaxBytes = 1024 * 1024;

    // 正規化 Markdown として格納する（ConversionService の正規化結果と同じ Content-Type）。
    public const string ContentType = "text/markdown; charset=utf-8";

    // FR-05, ADR-0036 D-05: 所有者の属性キー。DataSourceService.DataSource.OwnerKey と同一語彙。
    public const string OwnerKey = "owner";

    // FR-21 受け入れ基準 ⑥: **UTF-8 のバイト数**で測る。文字数で測ると日本語の本文が
    // 実サイズの 3 分の 1 で通り、上限が 3 MB へ化ける。
    public static bool ExceedsLimit(string body) => Encoding.UTF8.GetByteCount(body) > MaxBytes;

    // FR-21 受け入れ基準 ④: オブジェクトキー。文書 ID で決まるため再投入は同じキーを上書きする
    // （バケットのバージョニングが履歴を持つ。ADR-0014）。
    public static string StorageKey(Guid documentId) => $"documents/{documentId:D}/body.md";

    // FR-21, ADR-0036 D-02/D-07: 本文の書き込み可否を **ABAC の動的束縛** `doc.owner ∈ { ${current_user} }`
    // で判定する。**ロールによる判定を追加しない**（FR-21 要求文・ADR-0036 D-07）。
    //
    // 🔴 **主体は判定の入力である。** 同じ文書でも主体が変われば結果が変わる。
    // ADR-0036 D-14 は「認可判定の結果をキャッシュする箇所では、キャッシュキーに主体
    // （`current_user` および `current_groups`）を必ず含める」と課している ——
    // **本経路は判定をキャッシュしない**ことでこれを満たす。キャッシュを足すときは D-14 を先に読むこと。
    //
    // `owner` を持たない文書（取り込み経路の既定は `system`）は**誰も本文を書けない**
    // （deny-by-default）。その種の文書の編集は SC-05 の管理者経路が担う。
    public static bool CanWrite(IReadOnlyDictionary<string, string>? attributes, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        if (attributes is null) return false;
        if (!attributes.TryGetValue(OwnerKey, out var owner)) return false;
        if (string.IsNullOrWhiteSpace(owner)) return false;
        return string.Equals(owner, subject, StringComparison.Ordinal);
    }
}
