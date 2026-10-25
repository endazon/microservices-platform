namespace Knowledge.Contracts.Dtos;

// FR-06, UC-03, SC-05, 計画 ADR-0141 決定 2・3, [[IADR-0551]] (#1961): **アーカイブした文書を見つかる面から外す判定の単一情報源。**
//
// 計画 ADR-0141 決定 2 は、アーカイブした文書を検索・RAG・グラフ（AI 提案・クラスタ要約を含む）・Wiki・MCP の一覧に載せないと定めた。
// 閲覧（SC-03。ABAC の範囲）と SC-05 の一覧は残す。
//
// 🔴 **判定は台帳の状態（`DocumentUpdated.Status` / `DocumentDto.Status`）で行う。ABAC 属性 `lifecycle` は見ない**
// （[[IADR-0551]] 決定 1。計画 ADR-0141 決定 3 の選択）。SC-05 が「アーカイブ済み」と出す値と同じ値を判定に使うので、表示と判定の根拠が食い違わない。
// `lifecycle` はデータソースの既定から入った値のまま残り、**アーカイブを表さない**（ポリシーの `allowedLifecycles` でアーカイブを扱うことはできない）。
//
// **生産側（取り込み・グラフ・Wiki の受け手）と消費側（MCP の一覧）が同じ関数を呼ぶ。** 判定を 2 か所に書くと、
// 片方だけが改名されて静かに無効化される（`DocumentExposure` と同じ作法）。
public static class DocumentArchival
{
    // 台帳の状態「アーカイブ済み」の値。DocumentService の `DocumentStatus.Archived` はこの定数を指す。
    public const string ArchivedStatus = "archived";

    // **この状態の文書は、見つかる面（検索・RAG・グラフ・Wiki・MCP の一覧）から外すか。**
    // 大文字小文字を区別する（状態は発行側の定数だけが書く。未知・空・null はアーカイブではない）。
    public static bool IsArchived(string? status)
        => string.Equals(status, ArchivedStatus, StringComparison.Ordinal);
}
