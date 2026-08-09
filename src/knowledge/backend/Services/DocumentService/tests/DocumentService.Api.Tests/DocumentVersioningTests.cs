using FluentAssertions;
using DocumentService.Api.Foundation.Domain;

namespace DocumentService.Api.Tests;

// FR-06, UC-03: ドメインの版管理（append-only スナップショット）ユニットテスト
public class DocumentVersioningTests
{
    [Fact]
    public void Create_RecordsVersionOne()
    {
        var doc = Document.Create("初版", null, null,
            new Dictionary<string, string> { ["dept"] = "eng" }, ["a"]);

        Assert.Equal(1, doc.Version);
        Assert.Single(doc.Versions);
        Assert.Equal(1, doc.Versions[0].Version);
        Assert.Equal("初版", doc.Versions[0].Title);
    }

    [Fact]
    public void Update_IncrementsVersion_AndAppendsSnapshot()
    {
        var doc = Document.Create("初版", null, null);

        doc.Update("第2版", new Dictionary<string, string> { ["k"] = "v" }, ["t1"], "見直し");

        Assert.Equal(2, doc.Version);
        Assert.Equal(2, doc.Versions.Count);
        // 旧版のスナップショットは初版のまま保持される
        Assert.Equal("初版", doc.Versions[0].Title);
        Assert.Equal("第2版", doc.Versions[1].Title);
        Assert.Equal("見直し", doc.Versions[1].ChangeNote);
    }

    [Fact]
    public void Snapshot_IsImmutableAgainstLaterMutation()
    {
        var attrs = new Dictionary<string, string> { ["k"] = "v1" };
        var doc = Document.Create("doc", null, null, attrs, ["x"]);

        doc.UpdateMetadata(new Dictionary<string, string> { ["k"] = "v2" }, ["y"]);

        // 版 1 のスナップショットは作成時点の属性を保持する（後続更新の影響を受けない）
        var v1 = doc.Versions[0];
        Assert.Equal("v1", v1.Attributes["k"]);
        Assert.Contains("x", v1.Tags);
    }

    [Fact]
    public void UpdateMetadata_DoesNotChangeTitle()
    {
        var doc = Document.Create("タイトル維持", null, null);

        doc.UpdateMetadata(new Dictionary<string, string> { ["a"] = "b" }, ["tag"]);

        Assert.Equal("タイトル維持", doc.Title);
        Assert.Equal(2, doc.Version);
    }

    [Fact]
    public void Publish_AppendsPublishedSnapshot()
    {
        var doc = Document.Create("公開対象", null, null);

        doc.Publish();

        Assert.Equal(DocumentStatus.Published, doc.Status);
        Assert.Equal(DocumentStatus.Published, doc.Versions[^1].Status);
    }

    // SC-05, UC-03: アーカイブ済み文書の再公開は不正遷移として拒否する（レビュー #171 指摘対応）。
    [Fact]
    public void Publish_FromArchived_Throws()
    {
        var doc = Document.Create("公開対象", null, null);
        doc.Archive();

        Assert.False(doc.CanPublish);
        Assert.Throws<InvalidDocumentStateException>(() => doc.Publish());
        Assert.Equal(DocumentStatus.Archived, doc.Status); // 状態は変わらない
    }

    // SC-05: 正規化済み（pipeline 由来）文書は公開可能である（draft のみに絞りすぎない）。
    [Fact]
    public void Publish_FromNormalized_IsAllowed()
    {
        var doc = Document.CreateNormalized(Guid.NewGuid(), "正規化文書", "storage://x.md");

        Assert.True(doc.CanPublish);
        doc.Publish();

        Assert.Equal(DocumentStatus.Published, doc.Status);
    }

    // FR-01, UC-04, SC-05, #637: **再正規化はタグ欄を上書きしない**（計画確定・2026-08-09）。
    //
    // **取り込み経路はタグを生成しない**ので（[[IADR-0153]] 決定 5）、ここで上書きすると
    // **SC-05 で管理者が付けたタグが再同期のたびに空で消える**。
    // **「取り込みはタグを作らない」と「取り込みはタグを消す」は別**である。
    [Fact]
    public void ApplyNormalized_PreservesTags()
    {
        var doc = Document.Create("文書", null, null, null, ["経理", "規程"]);

        doc.ApplyNormalized("再正規化後", "storage://x.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" });

        doc.Tags.Should().Equal(["経理", "規程"],
            "管理者が SC-05 で付けたタグは再同期で消えない");
        doc.Title.Should().Be("再正規化後", "タイトル・属性・本文の所在は取り込みが正本である");
        doc.Attributes["confidentiality"].Should().Be("internal");
    }

    // 画面（SC-05）からの更新は**従来どおりタグを更新できる**。止めるとタグを外せなくなる。
    [Fact]
    public void UpdateMetadata_StillReplacesTags()
    {
        var doc = Document.Create("文書", null, null, null, ["経理"]);

        doc.UpdateMetadata([], ["規程"], "付け替え");
        doc.Tags.Should().Equal(["規程"]);

        doc.UpdateMetadata([], [], "全部外す");
        doc.Tags.Should().BeEmpty("利用者が意図して外せる");
    }
}
