namespace DataSourceService.Features.DataSources.Create;

// FR-01, UC-04: データソース登録の入力。**この操作だけが使う**ため、その操作のフォルダに置く
// （ADR-0068 決定 2）。契約側の `Knowledge.Contracts.Dtos` の同名レコードと JSON 互換である
// （本サービスは SPA 契約に依存せず自前の入力型を持つ既存の作法に倣う）。
public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config,
    // FR-05: 原本へ付与する既定 ABAC 文書属性（confidentiality 等）。未指定時は internal を補完。
    Dictionary<string, string>? DefaultAttributes = null,
    // FR-05, SC-06, ADR-0074 決定 1 (#1194): `owner` の写像表（ソース側の識別子 → 基盤の利用者識別子）。
    // **既定属性とは別の器である**（混ぜると片方の更新がもう片方を消す）。未指定は空。
    Dictionary<string, string>? OwnerMappings = null);
