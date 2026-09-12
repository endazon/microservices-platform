using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.SyncSettings.Update;

// FR-20, SC-20 主要素 3, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1, #1442: 同期対象フォルダの置き換えの入力規則。
//
// 🔴 **判定は正規化した値に対して行う**（`Domain.SyncSettings.NormalizeFolder`）——
// `/notes` と `notes/` を別物として通すと、保存後に重複したフォルダが 2 行できる。
// **正規化そのものは端点が行う**（検証器は値を書き換えられない）。同じ関数を両方が呼ぶ。
//
// 🔴 **宣言順が応答の契約である**（`ValidationProblems.FirstViolation` は先頭 1 件を返す）。
internal sealed class UpdateSyncSettingsValidator : AbstractValidator<UpdateSyncSettingsRequest>
{
    internal const string TargetFoldersKey = "targetFolders";
    internal const string RequiredMessage = "同期対象フォルダの一覧は必須です（空配列なら全資料が対象になります）。";
    internal const string EmptyElementMessage = "同期対象フォルダに空の要素は指定できません。";
    internal const string TooLongMessage = "同期対象フォルダは 1 件あたり 1024 文字以内で指定してください。";
    internal const string DuplicateMessage = "同期対象フォルダが重複しています。";
    internal const string TooManyMessage = "同期対象フォルダは 100 件以内で指定してください。";

    public UpdateSyncSettingsValidator()
    {
        RuleFor(r => r.TargetFolders)
            .Must(f => f is not null)
            .WithMessage(RequiredMessage)
            .Must(f => f is null || f.All(p => Normalize(p).Length > 0))
            .WithMessage(EmptyElementMessage)
            .Must(f => f is null
                || f.All(p => Normalize(p).Length <= Domain.SyncSettings.MaxFolderPathLength))
            .WithMessage(TooLongMessage)
            .Must(f => f is null
                || f.Select(Normalize).Distinct(StringComparer.Ordinal).Count() == f.Count)
            .WithMessage(DuplicateMessage)
            .Must(f => f is null || f.Count <= Domain.SyncSettings.MaxTargetFolders)
            .WithMessage(TooManyMessage)
            .OverridePropertyName(TargetFoldersKey);
    }

    private static string Normalize(string? path) => Domain.SyncSettings.NormalizeFolder(path);
}
