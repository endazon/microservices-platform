using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using NotificationService.Features.Notifications;

namespace NotificationService.Tests.Features.Notifications;

// FR-22, ADR-0037 決定 6: 受け入れ基準「本文が件数と期限のみで構成される。資料のタイトル・本文・
// 検索語・回答内容を含まない」（AC-2）を、**後段の側でも型の形として固定する**。
//
// ★ フロント側の契約テスト（notificationContract.test.ts）は openapi.yaml を読んで同じ集合を固定する。
// **本テストは後段が実際に書き出す JSON を見る** —— 契約の記述と実装の出力が割れていないことは、
// 契約ファイルだけを読むテストでは確かめられない。
[Trait("TestKind", "Unit")]
public class NotificationContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // FR-22: **応答の項目は 7 つちょうどで、自由文の項目が 1 つも無い**（AC-2）。
    [Fact]
    public void 通知の応答は7項目だけで構成される()
    {
        var dto = new NotificationDto(
            Guid.NewGuid(), "private-note-purge-weekly", 3, null,
            DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow, false);

        var json = JsonSerializer.SerializeToNode(dto, WebJson)!.AsObject();

        json.Select(p => p.Key).Should().BeEquivalentTo(
            ["id", "kind", "count", "thresholdPercent", "deadline", "occurredAt", "read"],
            "★ 項目を増やすと契約（openapi.yaml の NotificationDto）と割れる");
    }

    // FR-22: **タイトル・本文に相当する名前の項目が存在しない**（AC-2 の否定形）。
    // 上のテストは集合の一致を見るが、**なぜ落ちるのか**を名指しで残すためにこちらも置く。
    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("message")]
    [InlineData("subject")]
    [InlineData("text")]
    [InlineData("summary")]
    [InlineData("detail")]
    [InlineData("content")]
    [InlineData("caption")]
    public void 通知の応答は自由文の項目を持たない(string forbidden)
    {
        var dto = new NotificationDto(
            Guid.NewGuid(), "storage-quota-warning", null, 95, null, DateTimeOffset.UtcNow, true);

        var json = JsonSerializer.SerializeToNode(dto, WebJson)!.AsObject();

        json.Select(p => p.Key.ToLowerInvariant()).Should().NotContain(forbidden,
            "★ メールは本システムの ABAC の外側へ出る。自由文の口を 1 つでも開けてはならない");
    }

    // FR-22: 封筒（一覧・既読化の結果）も**完全一致**で閉じる。
    // 列挙し忘れた語が素通りするのを防ぐため、禁止語の照合ではなく集合の一致で見る。
    [Fact]
    public void 一覧と既読化の封筒も項目が閉じている()
    {
        var list = JsonSerializer.SerializeToNode(
            new NotificationListDto([], 0), WebJson)!.AsObject();
        list.Select(p => p.Key).Should().BeEquivalentTo(["items", "unreadCount"]);

        var read = JsonSerializer.SerializeToNode(
            new NotificationReadResultDto(Guid.NewGuid(), 0), WebJson)!.AsObject();
        read.Select(p => p.Key).Should().BeEquivalentTo(["id", "unreadCount"]);
    }

    // FR-22: **`kind` は閉じた列挙ではない**（IADR-0215 決定 2 / BFF 面の横断規約 4）。
    // 後段が種別を増やしたときに、契約の型が先に壊れないことを固定する。
    [Fact]
    public void 種別は文字列であり未知の値も表現できる()
    {
        var dto = new NotificationDto(
            Guid.NewGuid(), "some-future-kind", 1, null, null, DateTimeOffset.UtcNow, false);

        var json = JsonSerializer.SerializeToNode(dto, WebJson)!.AsObject();
        json["kind"]!.GetValue<string>().Should().Be("some-future-kind");
    }
}
