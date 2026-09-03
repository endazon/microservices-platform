namespace DataSourceService.Features.DataSources.Patch;

// FR-01, UC-04, SC-06（Q16 / #534）: 部分更新要求。**null は「変更しない」を意味する**。
// **この操作だけが使う**ため、その操作のフォルダに置く（ADR-0068 決定 2）。
public record PatchDataSourceRequest(
    string? Name = null,
    string? SourceType = null,
    string? ConnectionUri = null,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null,
    // FR-05, SC-06, ADR-0074 決定 1 (#1194): `owner` の写像表。null は現状維持。
    // **既定属性とは独立に部分更新できる**（片方だけ送っても、もう片方は消えない）。
    Dictionary<string, string>? OwnerMappings = null);
