namespace DataSourceService.Features.DataSources.Patch;

// FR-01, UC-04, SC-06（Q16 / #534）: 部分更新要求。**null は「変更しない」を意味する**。
// **この操作だけが使う**ため、その操作のフォルダに置く（ADR-0068 決定 2）。
public record PatchDataSourceRequest(
    string? Name = null,
    string? SourceType = null,
    string? ConnectionUri = null,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);
