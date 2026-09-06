using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Push;
using FluentValidation;

namespace DocumentService.Tests.Features.ObsidianSync.Push;

// FR-20, UC-11, SC-20, ADR-0037 決定 7・8, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・3・9: push の入力検証の**振る舞い同値**（#1278 PR-B）。
[Trait("TestKind", "Unit")]
public class PushNoteValidatorTests
{
    private readonly PushNoteValidator _validator = new();

    private static PushNoteRequest Request(string title = "ok", string vaultPath = "notes/a.md",
        Guid? noteId = null, int? baseVersion = null, List<SyncEditRequest>? edits = null)
        => new(noteId, vaultPath, title, baseVersion, edits ?? [new SyncEditRequest("# 本文")]);

    // 陽性対照: 既定集合・`baseVersion` 集合のどちらも満たす要求は通る。
    [Fact]
    public void ValidRequest_PassesBothRuleSets()
    {
        var req = Request(noteId: Guid.NewGuid(), baseVersion: 1);

        _validator.Validate(req).IsValid.Should().BeTrue();
        _validator.Validate(req, o => o.IncludeRuleSets(PushNoteValidator.BaseVersionRuleSet))
            .IsValid.Should().BeTrue();
    }

    // 🔴 鍵は `errors` である（属性名ではない）。`OverridePropertyName` を消すと `Title` になる。
    public static TheoryData<PushNoteRequest> InvalidEntryRequests() =>
    [
        Request(title: ""),
        Request(title: "   "),
        Request(vaultPath: ""),
        Request(vaultPath: "   "),
        Request(edits: []),
        Request(edits: [new SyncEditRequest(null)]),
        Request(edits: [new SyncEditRequest("ok"), new SyncEditRequest(null)]),
    ];

    [Theory]
    [MemberData(nameof(InvalidEntryRequests))]
    public void InvalidEntryFields_FailWithOriginalKeyAndMessage(PushNoteRequest req)
    {
        var result = _validator.Validate(req);

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(PushNoteValidator.ErrorsKey);
        PushNoteValidator.ErrorsKey.Should().Be("errors");
        result.Errors[0].ErrorMessage.Should().Be(PushNoteValidator.RequiredFieldsMessage);
        PushNoteValidator.RequiredFieldsMessage.Should()
            .Be("title / vaultPath / edits（1 件以上・content 必須）を指定してください。");
    }

    // 🔴 **述語の粒度（G 軸）。** 移送前は 4 項を 1 本の `||` で見て **1 件**を返していた。
    // 4 本の `RuleFor` に割ると全部不正な要求で失敗が 4 件になり、ここで止まる。
    [Fact]
    public void AllFourInvalid_ReportsOneFailure()
    {
        var result = _validator.Validate(Request(title: "", vaultPath: "", edits: []));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
    }

    // 🔴 **短絡評価を保っていること。** `Edits` が null のとき `req.Edits.Any(...)` は評価されない
    // （`||` の 3 項目めで確定する）。順を入れ替えると `NullReferenceException` でここが赤になる。
    [Fact]
    public void NullEdits_FailsWithoutThrowing()
    {
        var result = _validator.Validate(new PushNoteRequest(null, "notes/a.md", "ok", null, null!));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(PushNoteValidator.ErrorsKey);
    }

    // 🔴 **位置の固定（P 軸）。** `baseVersion` の規則は `RuleSet` に居るので、既定の `Validate(req)`
    // では**走らない** —— 移送前も `baseVersion` の検査は更新分岐の 404 の後ろに居たからである。
    // `RuleSet` の外へ出すとここで止まる（そして不存在 noteId の 404 が 400 に化ける）。
    [Fact]
    public void DefaultRuleSet_DoesNotRunBaseVersionRule()
    {
        var req = Request(noteId: Guid.NewGuid(), baseVersion: null);

        _validator.Validate(req).IsValid.Should().BeTrue("baseVersion は既定集合に入っていない");
        _validator.Validate(req, o => o.IncludeRuleSets(PushNoteValidator.BaseVersionRuleSet))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void MissingBaseVersion_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(Request(noteId: Guid.NewGuid(), baseVersion: null),
            o => o.IncludeRuleSets(PushNoteValidator.BaseVersionRuleSet));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(PushNoteValidator.BaseVersionKey);
        PushNoteValidator.BaseVersionKey.Should().Be("baseVersion");
        result.Errors[0].ErrorMessage.Should().Be(PushNoteValidator.BaseVersionRequiredMessage);
        PushNoteValidator.BaseVersionRequiredMessage.Should()
            .Be("既存資料の更新には baseVersion が必須です。");
    }

    // 🔴 述語を写していること: **`baseVersion = 0` は有効**である（`NotEmpty()` なら落ちる）。
    [Fact]
    public void ZeroBaseVersion_Passes()
    {
        _validator.Validate(Request(noteId: Guid.NewGuid(), baseVersion: 0),
            o => o.IncludeRuleSets(PushNoteValidator.BaseVersionRuleSet))
            .IsValid.Should().BeTrue();
    }

    // 集合名の定数がリテラルと一致すること（端点はこの定数で第 2 の集合を呼ぶ）。
    [Fact]
    public void RuleSetName_IsTheOriginalLiteral()
    {
        PushNoteValidator.BaseVersionRuleSet.Should().Be("baseVersion");
    }
}
