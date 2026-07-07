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
}
