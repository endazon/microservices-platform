using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Tests.Features.Notifications.Accept;

// FR-22, UC-11, 計画 ADR-0030 §決定（検証 = FluentValidation）/ ADR-0037 決定 6・17・18 /
// IADR-0215 決定 2・4 / IADR-0270 決定 6 / IADR-0371 決定 2 / [[IADR-0398]] 決定 1・7・9:
// **受け口（POST /internal/notifications）の手書き検証 → FluentValidation の移送が、
// 応答の契約を 1 バイトも変えていないことを固定する**（#1278 PR-D）。
//
// 🔴 **既存の 400 の試験（`NotificationIngressTests`）は状態コードしか見ていない。**
// 鍵（`errors` の下のプロパティ名）・メッセージ・**鍵の件数**が変わる退行は 400 のままなので、
// 状態コードでは捕まらない。呼び出し元は画面ではなく**送信側サービス（DocumentService の
// `HttpPrivateNoteNotifier`）**であり、本文の形が変わっても誰も画面で気づかない。
//
// 🔴 **本サイトは形 β である**（[[IADR-0398]] 決定 1 の後者）。移送前の `NotificationIngress.Validate` は
// 項目ごとに**独立した `if`** で辞書へ積むため、**複数の鍵が同時に埋まる** —— DocumentService の
// 形 α（ガードごとに即 `return` するので常に 1 鍵）とは違う。したがって
// `ValidationProblems.FirstViolation` 相当の「先頭 1 件へ丸める」写像を当てると**鍵が 5 つから
// 1 つへ減る**。本ファイルの `AllFiveFieldsInvalid_*` がそれを止める。
//
// 🔴 **本ファイルは移送の前に書き、`NotificationIngress.cs` を `origin/develop` へ戻した状態でも
// 緑になることを実測した**（等価性の直接の証拠。PR-A / PR-B / PR-C と同じ作法）。
[Trait("TestKind", "Integration")]
public class NotificationIngressValidationProblemContractTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // 🔴 送信側 DocumentService.Infrastructure.ExternalServices.HttpPrivateNoteNotifier.IngressPath の値。
    private const string IngressPath = "/internal/notifications";

    private const string When = "\"occurredAt\":\"2026-08-28T09:00:00+00:00\"";

    // 5 項目が同時に不正な要求。**形 β なので 5 鍵すべてが出る。**
    private const string AllFiveInvalidJson =
        "{\"subject\":\"   \",\"kind\":\"\",\"count\":-1,\"thresholdPercent\":101}";

    private Task<HttpResponseMessage> PostAsync(string json)
        => _factory.CreateClient().PostAsync(IngressPath,
            new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    // 🔴 **鍵の列と各鍵のメッセージ列**（[[IADR-0398]] 決定 9 の K・M・O・C 軸）。
    // 宣言順は移送前の `Validate` の積み順（subject → kind → occurredAt → count → thresholdPercent）である。
    [Fact]
    public async Task AllFiveFieldsInvalid_ReturnsFiveKeysInDeclarationOrder()
    {
        var response = await PostAsync(AllFiveInvalidJson);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(
            [
                "subject=subject は必須である。",
                "kind=kind は必須である。",
                "occurredAt=occurredAt は必須である。",
                "count=count は 0 以上である。",
                "thresholdPercent=thresholdPercent は 0〜100 である。",
            ],
            "★ 形 β である —— 先頭 1 件へ丸めると鍵が 5 つから 1 つへ減る");
    }

    // 🔴 **生の JSON をバイト比較する**（器ごと固定する）。RFC7807 の封筒（`type` / `title` / `status`）と
    // `errors` の中身が、移送前後で 1 バイトも変わらないことの直接の証拠である。
    [Fact]
    public async Task ValidationProblem_KeepsRfc7807Envelope_ByteForByte()
    {
        var response = await PostAsync(AllFiveInvalidJson);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\","
            + "\"title\":\"One or more validation errors occurred.\",\"status\":400,"
            + "\"errors\":{\"subject\":[\"subject は必須である。\"],"
            + "\"kind\":[\"kind は必須である。\"],"
            + "\"occurredAt\":[\"occurredAt は必須である。\"],"
            + "\"count\":[\"count は 0 以上である。\"],"
            + "\"thresholdPercent\":[\"thresholdPercent は 0〜100 である。\"]}}");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // 🔴 **述語の粒度**（[[IADR-0398]] 決定 7 / IADR-0395 決定 8）。空白 300 文字の `subject` は
    // `SubjectMaxLength`（255）を超えるが、移送前は `IsNullOrWhiteSpace` が真になった時点で
    // `else if` の長さ検査へ**到達しない**ので **1 件**（「必須」）である。
    // 規則レベルの `Cascade(CascadeMode.Stop)` を外すと 2 件になり、ここで止まる。
    [Fact]
    public async Task WhitespaceSubjectOverTheLimit_ReportsRequiredOnly()
    {
        var whitespace = new string(' ', 300);
        var response = await PostAsync($"{{\"subject\":\"{whitespace}\",\"kind\":\"x\",{When}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(
            ["subject=subject は必須である。"],
            "★ 「必須」と「長さ」の 2 件にしてはならない（移送前は else if で 1 件だった）");
    }

    // 同じ粒度を `kind` 側でも固定する（`subject` だけ直して `kind` を忘れる事故を塞ぐ）。
    [Fact]
    public async Task WhitespaceKindOverTheLimit_ReportsRequiredOnly()
    {
        var whitespace = new string(' ', 300);
        var response = await PostAsync($"{{\"subject\":\"alice\",\"kind\":\"{whitespace}\",{When}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(["kind=kind は必須である。"]);
    }

    // 長さ超過（空白でない）は「長さ」1 件である。**数値は DB 列長の定数から作られている。**
    [Fact]
    public async Task TooLongSubject_ReportsLengthMessageBuiltFromTheColumnLimit()
    {
        var tooLong = new string('a', NotificationService.Features.Notifications.Accept
            .NotificationIngress.SubjectMaxLength + 1);
        var response = await PostAsync($"{{\"subject\":\"{tooLong}\",\"kind\":\"x\",{When}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(
            [
                "subject=subject は "
                + NotificationService.Features.Notifications.Accept.NotificationIngress.SubjectMaxLength
                + " 文字以内である。",
            ]);
        // リテラルにも当てる（定数だけ書き換えても片方が残るように）。
        (await ErrorsOf(response)).Should().Equal(["subject=subject は 255 文字以内である。"]);
    }

    [Fact]
    public async Task TooLongKind_ReportsLengthMessageBuiltFromTheColumnLimit()
    {
        var tooLong = new string('k', NotificationService.Features.Notifications.Accept
            .NotificationIngress.KindMaxLength + 1);
        var response = await PostAsync($"{{\"subject\":\"alice\",\"kind\":\"{tooLong}\",{When}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(["kind=kind は 100 文字以内である。"]);
    }

    // 🔴 **`request is null` は移していない**（[[IADR-0398]] 決定 7）。FluentValidation は null
    // インスタンスを検証できないので、この 1 本だけは `AcceptAsync` に残る。**鍵は `body` である。**
    // 検証器へ押し込むと `Validate(null)` が例外になり 500 へ倒れる。
    [Fact]
    public async Task MissingBody_ReturnsBodyKey()
    {
        var response = await PostAsync("null");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(["body=要求本文が空である。"]);
    }

    // 🔴 **位置**（[[IADR-0398]] 決定 9 の P 軸）: 検証は DB 照会・永続化より**前**に居る。
    [Fact]
    public async Task Invalid_ValidationRunsBeforeAnyPersistence()
    {
        var response = await PostAsync(AllFiveInvalidJson);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        (await db.Notifications.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await db.EmailOutbox.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // 陽性対照 —— **検証を通る要求は通り続ける。** 述語を広げていないことと、検証器が受理側の
    // 経路（畳み込み）を壊していないことを同時に見る。
    [Fact]
    public async Task ValidPayload_IsAcceptedAndResentPayloadIsFolded()
    {
        var client = _factory.CreateClient();
        var payload = new
        {
            subject = "alice",
            kind = "private-note.purge.weekly",
            occurredAt = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero),
            count = 3,
        };

        var first = await client.PostAsJsonAsync(IngressPath, payload, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(IngressPath, payload, TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "同一事象の再送は畳む（検証の移送で変わらない）");
    }

    // 陽性対照 —— 🔴 **述語を広げていない。** `count` / `thresholdPercent` の null、過去の `deadline`、
    // 未知の `kind` はいずれも移送前から**正当**である（IADR-0215 決定 2・4）。
    // `NotNull()` を足す・`deadline` に規則を置く・`kind` の値集合を閉じる、のいずれをやってもここで赤になる。
    [Fact]
    public async Task NullCountAndThresholdWithPastDeadlineAndUnknownKind_AreAccepted()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(IngressPath, new
        {
            subject = "alice",
            kind = "some-future-kind",
            occurredAt = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero),
            count = (int?)null,
            thresholdPercent = (int?)null,
            deadline = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero),
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 境界 —— **0 と 100 は正当である**（`is < 0` / `is < 0 or > 100` の境界を写している）。
    [Fact]
    public async Task ZeroCountAndBoundaryThreshold_AreAccepted()
    {
        var response = await PostAsync(
            $"{{\"subject\":\"alice\",\"kind\":\"x\",{When},\"count\":0,\"thresholdPercent\":100}}");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 境界 —— 上限ちょうどの長さは正当である（`> SubjectMaxLength` を `>=` にすると赤）。
    [Fact]
    public async Task SubjectAndKindAtTheExactLimit_AreAccepted()
    {
        var subject = new string('a', NotificationService.Features.Notifications.Accept
            .NotificationIngress.SubjectMaxLength);
        var kind = new string('k', NotificationService.Features.Notifications.Accept
            .NotificationIngress.KindMaxLength);

        var response = await PostAsync($"{{\"subject\":\"{subject}\",\"kind\":\"{kind}\",{When}}}");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 🔴 **2 鍵だけが埋まる中間の形**も固定する（5 鍵の 1 例だけだと「全部か 1 つか」しか見ていない）。
    [Fact]
    public async Task SubjectAndCountInvalid_ReturnsExactlyThoseTwoKeys()
    {
        var response = await PostAsync(
            $"{{\"subject\":\"   \",\"kind\":\"x\",{When},\"count\":-1}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().Equal(
            ["subject=subject は必須である。", "count=count は 0 以上である。"]);
    }
}
