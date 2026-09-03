namespace DataSourceService.Features.DataSources.Update;

// FR-01, UC-04, SC-06（Q16 / #534）: 更新（全置換）要求。契約側の Knowledge.Contracts.Dtos の
// 同名レコードと JSON 互換である（本サービスは SPA 契約に依存せず自前の入力型を持つ既存の作法に倣う）。
// **Id / CreatedAt / LastSyncedAt / 同期健全性は含まない** —— 更新で履歴を巻き戻せてはならない。
// **Config / DefaultAttributes の省略は 400 で拒否する**（AI レビュー 🟡 / #627）。型としては
// nullable だが、null は「未指定」であって「空にする」ではない —— 消すなら {} を明示させる。
//
// **この操作だけが使う**ため、その操作のフォルダに置く（ADR-0068 決定 2）。
public record UpdateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null,
    // FR-05, SC-06, ADR-0074 決定 1 (#1194): `owner` の写像表。
    // 🔴 **これだけは省略（null）を「現状維持」にする**（`DataSource.Update` の理由書き）——
    // 後から足す項目を必須にすると既存の PUT クライアントが一斉に 400 になる。消すなら {} を送る。
    Dictionary<string, string>? OwnerMappings = null);
