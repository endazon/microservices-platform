namespace GraphService.Domain.Ports;

// FR-17, ADR-0033 決定 6, IADR-0281 (#912): リンク抽出のための正規化 Markdown 本文の取得口。
//
// 🔴 **取得できないときは null を返す。プレースホルダー本文へ縮退してはならない。**
// WikiService の `StorageMarkdownReader`（IADR-0021）は表示のための読み取りなので
// 「コンテンツは … から取得します」というプレースホルダーへ倒してよいが、**リンク抽出で同じことを
// すると、その本文にはリンクが 1 本も無いため「全リンクが消えた」と解釈され、当該文書起点の
// 自動抽出の辺がすべて削除される**（ADR-0033 決定 6 の差分更新が、縮退を「変更」として実行する）。
// null は「取得できなかった」を表し、呼び出し側は**辺を一切触らずに抜ける**。
//
// 取得の実行時エラー（storage / HTTP の例外）は握り潰さず送出する —— Wolverine のリトライ・
// デッドレター（UsePlatformMessagingDefaults）へ委ねる。null は「配備・指定が無い」の側だけを表す。
public interface IGraphContentReader
{
    Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default);
}
