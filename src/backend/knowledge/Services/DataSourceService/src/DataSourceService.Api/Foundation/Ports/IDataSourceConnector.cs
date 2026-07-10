using DataSourceService.Api.Foundation.Domain;

namespace DataSourceService.Api.Foundation.Ports;

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

// 列挙された 1 対象（所在・更新日時・サイズ）。変更検知と Map の基礎メタ。
public sealed record SourceItem(string Path, DateTimeOffset ModifiedAt, long Size);

// 取得した原本の中身（バイト列と content-type）。
public sealed record RawContent(byte[] Bytes, string ContentType);
