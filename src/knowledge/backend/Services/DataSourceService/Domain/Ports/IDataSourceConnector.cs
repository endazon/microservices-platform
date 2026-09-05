using DataSourceService.Domain;

namespace DataSourceService.Domain.Ports;

// FR-01, UC-04, 09_datasource-connectors（fixed）, IADR-0051: データソースコネクタの共通ポート。
// 設計の共通インタフェース `Discover / Fetch / Watch|Poll / Map` のうち、Discover（変更検知付き列挙）と
// Fetch（原本取得）をコネクタが担い、Map（ソースメタ→ABAC 属性/タグ）は同期オーケストレータへ集約する
// （コネクタはソース固有 I/O に専念し、属性方針の一貫性はオーケストレータ側で担保する）。
// 新規ソースは本ポートを実装したコネクタを追加するだけで対応する（コア改修不要・プラグイン方式）。
public interface IDataSourceConnector
{
    // このコネクタが担うソース種別（DataSource.SourceType と一致）。例: "filesystem"。
    string SourceType { get; }

    // Discover + 変更検知: `since` より後に更新された対象を列挙する（`since` が null なら初回フルスキャン）。
    // ルート未存在・アクセス不可などは例外にせず空列挙で縮退する（同期サイクルを止めない）。
    Task<IReadOnlyList<SourceItem>> DiscoverAsync(
        DataSource source, DateTimeOffset? since, CancellationToken ct);

    // Fetch: 列挙された 1 件の原本バイト列と content-type を取得する。
    Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct);
}

// 列挙された 1 対象（所在・更新日時・サイズ・更新者）。変更検知と Map の基礎メタ。
//
// FR-05, UC-04, #752: `UpdatedBy` は**ソース側の更新者**である。計画
// 09_datasource-connectors §システム投入経路は `owner` の既定を
// 「ソース側の更新者・作成者を利用者識別子へ解決して入れる」と定めるが、**従前の契約は
// 所在・更新日時・サイズの 3 つしか運んでおらず、器そのものが無かった**。
//
// 🔴 **null 許容である。運べないコネクタは null を返す。** **取れないものを取れたことにしない**
// —— null なら `owner` は予約値 `system` へ倒れる（計画「解決できないとき」）。
//
// ［2026-09-05 更新 / #752］**値が載るようになった。** 従前ここには「🔴 どのコネクタもこの値を
// 載せていない」「`filesystem` / `wiki` / `saas` の 3 本は構造上取れない」「解決器が入るまで
// `db` に更新者列を足さない」と書いていたが、**後ろ 2 つは誤りになった。**
//
// | コネクタ | 更新者 |
// | --- | --- |
// | `filesystem` | 🔴 **運べない**（Linux でファイル所有者を取る自明な手段が無く、そもそも「ファイル所有者」は「最終更新者」ではない）。**意図的な縮退であり欠陥ではない**（ADR-0074 決定 3） |
// | `wiki` / `saas` | **運ぶ。** REST 契約は外部組織のものではなく**自前 DTO** であり、読む項目を増やせば取れた。項目名は `Config["updatedByField"]`（既定 `updatedBy`）で構成可能 |
// | `db` | **運ぶ（opt-in）。** `Config["updatedByColumn"]` が在るときだけ SELECT へ列を 1 本足す |
//
// **`db` の先後条件（ADR-0074 決定 5）は満たされた** —— 解決段は `DataSource.ResolveOwner`
// （#1194 / ADR-0074 決定 1・4）として `DataSourceSyncService.PerItemAttributes` の中に在り、
// **写像表に当たらない生の値は 1 件も `owner` へ入らない**（ADR-0036。「別名前空間の識別子を
// そのまま `owner` へ入れてはならない」）。解決順は ① Keycloak ユーザー検索（未配備）
// → ② データソース単位の写像表 → 予約値 `system`（2026-08-16。planning#371）。
//
// 🔴 **「取れなかった」と「取ったら空だった」を混ぜない。** どちらもここでは null になるが、
// 由来は `SourceUpdatedBy`（4 値）が分類し、コネクタが 1 サイクル 1 行で記録する。
public sealed record SourceItem(string Path, DateTimeOffset ModifiedAt, long Size, string? UpdatedBy = null);

// 取得した原本の中身（バイト列と content-type）。
public sealed record RawContent(byte[] Bytes, string ContentType);
