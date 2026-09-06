using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotificationService.Common.Observability;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Features.Notifications.Accept;

// FR-22, ADR-0037 決定 6・17・18, IADR-0215 決定 3, IADR-0270 決定 6: 受け口の受理判断。
//
// 発火の**検知**はデータの在る側（DocumentService）が行い、本サービスは**送出の実体**だけを担う
// （IADR-0270 決定 6。DB per Service のため越境読みができない）。本クラスはその境界に立ち、
// 受け取ったペイロードを検証してから既存の NotificationPublisher へ渡す。
//
// ★ **受け口は書き込み専用である。** 既存の通知を読み出す口を持たない —— 読み出しは
// `/notifications`（認証必須・主体で絞る）だけであり、無認証の内部 API から他人の通知を
// 覗く経路を作らない。
public sealed class NotificationIngress(
    NotificationDbContext db,
    NotificationPublisher publisher,
    IValidator<NotificationIngressRequest> validator,
    ILogger<NotificationIngress> logger)
{
    // DB 列（NotificationDbContext）の長さに合わせる。**入口で 400 にする** ——
    // 永続化まで運ぶと Postgres 側で落ち、送信側には 500 が返る（送信側は再送しないので黙って消える）。
    public const int SubjectMaxLength = 255;
    public const int KindMaxLength = 100;

    public async Task<NotificationIngressOutcome> AcceptAsync(
        NotificationIngressRequest? request, CancellationToken ct = default)
    {
        // FR-22, IADR-0371 決定 1・2 / [[IADR-0398]] 決定 1・7: 入力検証（FluentValidation）。
        // 🔴 **判定の位置は移送前と同じ**（AcceptAsync の先頭・DB 照会より前）。
        // 「不正なペイロードは 1 件も永続化しない」はこの位置が担保している。
        //
        // 🔴 **`request is null` だけは検証器へ移せない。** FluentValidation は null インスタンスを
        // 検証できず（`Validate(null)` は例外）、「本文が無い」は DTO の検証ではなく**束縛の失敗**である。
        if (request is null)
            return NotificationIngressOutcome.Invalid(new Dictionary<string, string[]>
            {
                ["body"] = ["要求本文が空である。"],
            });

        // 🔴 **形 β（全違反を鍵ごとに返す）である**（[[IADR-0398]] 決定 1 の後者）。移送前は項目ごとに
        // 独立した `if` で辞書へ積んでおり、**複数の鍵が同時に埋まる**。`ToDictionary()` は
        // `PropertyName` で群化し、**群の出現順と群内のメッセージ順をどちらも保つ** —— したがって
        // 検証器の**宣言順が応答の契約**である。`Errors[0]` へ丸めると鍵が減り、本文が変わる。
        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return NotificationIngressOutcome.Invalid(validation.ToDictionary());

        // 検証を通っているので必須項目は非 null である。
        var subject = request!.Subject!;
        var kind = request.Kind!;
        var occurredAt = request.OccurredAt!.Value;

        // ★ **同一事象の再送だけを畳む。** 判定はペイロード 6 項目の完全一致である。
        //
        // 🔴 `(subject, kind, occurredAt)` の 3 項目で畳んではならない。容量警告は 80% と 95% を
        //    **同一の検知時刻で同時に発火し得る**（送信側は跨いだ閾値を順に送る）ため、3 項目で
        //    畳むと **95% の警告が 80% の重複として消える**。これは「静かに落ちる」側の誤りであり、
        //    FR-22 の受け入れ基準が最も禁じている形である。
        //
        // 索引 (Subject, OccurredAt) で候補を絞り、**NULL を含む 3 項目の比較はメモリ上で行う**
        // —— プロバイダによって NULL 比較の SQL 変換が変わるためで、候補は同一主体・同一時刻・
        // 同一種別に限られるので件数は常に僅かである。
        var candidates = await db.Notifications
            .Where(n => n.Subject == subject && n.Kind == kind && n.OccurredAt == occurredAt)
            .ToListAsync(ct);

        var duplicate = candidates.FirstOrDefault(n =>
            n.Count == request.Count
            && n.ThresholdPercent == request.ThresholdPercent
            && n.Deadline == request.Deadline);

        if (duplicate is not null)
        {
            // **黙って捨てない。** 畳んだ事実が観測できないと「届いていない」と区別できない。
            logger.LogInformation(
                "同一ペイロードの通知が既に存在するため再送を畳みました。kind={Kind}",
                LogSanitizer.Sanitize(kind));
            return NotificationIngressOutcome.Duplicated(duplicate.Id);
        }

        // ★ 配送は既存の経路をそのまま使う。段 1（アプリ内通知の永続化）が「通知が届いた」の定義で
        //   あり、段 2（メール outbox）の失敗はそこへ伝播しない（IADR-0215 決定 3）。
        var notification = await publisher.PublishAsync(
            subject, kind, occurredAt, request.Count, request.ThresholdPercent, request.Deadline, ct);

        return NotificationIngressOutcome.Accepted(notification.Id);
    }
}

// 受理の結末。**3 つに分かれる**（新規・畳んだ・不正）。端点はこれを状態コードへ写すだけである。
public sealed class NotificationIngressOutcome
{
    private NotificationIngressOutcome() { }

    public Guid Id { get; private init; }
    public bool IsDuplicate { get; private init; }
    // 🔴 型は `IDictionary` である（`ValidationResult.ToDictionary()` の戻り値型。
    // [[IADR-0398]] 決定 1 の形 β）。端点の `Results.ValidationProblem` は `IDictionary` を
    // 受けるので、**端点は 1 行も変わらない**。
    public IDictionary<string, string[]>? Errors { get; private init; }

    public bool IsValid => Errors is null;

    public static NotificationIngressOutcome Accepted(Guid id)
        => new() { Id = id };

    public static NotificationIngressOutcome Duplicated(Guid id)
        => new() { Id = id, IsDuplicate = true };

    public static NotificationIngressOutcome Invalid(IDictionary<string, string[]> errors)
        => new() { Errors = errors };
}
