namespace DocumentService.Features.PrivateNotes.SetQuota;

// FR-19, NFR-27, #451-a: 上限変更（管理者）の入力。
// **`Knowledge.Contracts` へは置かない** —— **BFF に口を持たない**（計画に載せる画面が無い）ため、
// 契約として共有する相手が居ない。1 操作専用の入力なので、その操作のフォルダに置く（ADR-0065 決定 2）。
public record SetQuotaRequest(long LimitBytes);
