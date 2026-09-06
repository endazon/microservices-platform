using FluentValidation;

namespace NotificationService.Features.Notifications.Accept;

// FR-22, UC-11, 計画 ADR-0030 §決定（検証 = FluentValidation）/ ADR-0037 決定 6・17・18 /
// IADR-0215 決定 2・4 / IADR-0371 決定 1・2 / IADR-0395 / [[IADR-0398]] 決定 1・7・9:
// 受け口（POST /internal/notifications）の入力規則。
// 従前は `NotificationIngress.Validate` の手書き判定 7 本であった。
//
// 🔴 **本サイトは形 β である**（[[IADR-0398]] 決定 1 の後者）。移送前は項目ごとに**独立した `if`** で
// 辞書へ積んでおり、**複数の鍵が同時に埋まる**（DocumentService の形 α ＝ ガードごとに即 `return` する
// ので常に 1 鍵、とは違う）。したがって呼び出し側は `Errors[0]` ではなく **`ToDictionary()`** で写す。
// 先頭 1 件へ丸めると**鍵が 5 つから 1 つへ減り、応答本文が変わる**。
//
// 🔴 **鍵は必ず明示する。** 推論名は `Subject` / `Kind` / `OccurredAt` / `Count` /
// `ThresholdPercent`（PascalCase）であり、移送前の小文字始まりの鍵と**一致しない**。型では止まらない。
// **本サービスには sink ヘルパが無いので、鍵の正はこの検証器ただ 1 つである**
// （McpServer / AuthorizationService の「鍵は sink が持つ」形とは逆側。[[IADR-0398]] 決定 1 (b)）。
//
// 🔴 **述語の粒度を写す。** 移送前は 1 つの鍵の中だけが `if` / `else if` であり、
// **空白 300 文字の `subject` は「必須」の 1 件だけ**（長さ検査へ到達しない）である。
// FluentValidation は既定で 1 つの `RuleFor` の全 validator を走らせるので、
// **規則レベルの `Cascade(CascadeMode.Stop)`** が要る。外すと 2 件になり件数が変わる。
//
// 🔴 **述語を広げない。** `IsNullOrWhiteSpace` を `NotEmpty()` へ置き換えない。
// `count` / `thresholdPercent` に `NotNull()` を足さない（移送前の `is < 0` /
// `is < 0 or > 100` は **null に対して偽**であり、欠落は正当である）。
// `deadline` には規則を置かない（**過去の期限も正当である**。IADR-0215 決定 4）。
// `kind` の**値そのもの**も検証しない（値集合は開いている。IADR-0215 決定 2）。
//
// 🔴 **`request is null` はここに入れられない。** FluentValidation は null インスタンスを検証できず
// （`Validate(null)` は例外）、「本文が無い」は DTO の検証ではなく**束縛の失敗**である。
// この 1 本だけは `NotificationIngress.AcceptAsync` に残る（[[IADR-0398]] 決定 7・決定 8）。
internal sealed class NotificationIngressValidator : AbstractValidator<NotificationIngressRequest>
{
    // FR-22: 移送前の `Validate` が積んでいた鍵と本文。**この 10 個が応答の契約である。**
    internal const string SubjectKey = "subject";
    internal const string SubjectRequiredMessage = "subject は必須である。";

    internal const string KindKey = "kind";
    internal const string KindRequiredMessage = "kind は必須である。";

    internal const string OccurredAtKey = "occurredAt";
    internal const string OccurredAtRequiredMessage = "occurredAt は必須である。";

    internal const string CountKey = "count";
    internal const string CountNegativeMessage = "count は 0 以上である。";

    internal const string ThresholdPercentKey = "thresholdPercent";
    internal const string ThresholdPercentOutOfRangeMessage = "thresholdPercent は 0〜100 である。";

    // 🔴 `const` にできない（`int` の補間は定数式にならない。CS0133）。移送前と**同じ式**から作る
    // ため `static readonly` にする。数値を直書きすると、DB 列長を変えたときにメッセージだけが古く残る。
    internal static readonly string SubjectTooLongMessage =
        $"subject は {NotificationIngress.SubjectMaxLength} 文字以内である。";
    internal static readonly string KindTooLongMessage =
        $"kind は {NotificationIngress.KindMaxLength} 文字以内である。";

    public NotificationIngressValidator()
    {
        // 宣言順 = 移送前の積み順（subject → kind → occurredAt → count → thresholdPercent）。
        // **形 β では鍵の列そのものが応答の契約**であり、入れ替えると本文が変わる。

        // 宛先が無い通知は誰にも届かない。**空白の主体を「誰か」として扱わない。**
        RuleFor(r => r.Subject)
            .Cascade(CascadeMode.Stop)
            .Must(s => !string.IsNullOrWhiteSpace(s))
                .WithMessage(SubjectRequiredMessage)
            .Must(s => s!.Length <= NotificationIngress.SubjectMaxLength)
                .WithMessage(SubjectTooLongMessage)
            .OverridePropertyName(SubjectKey);

        RuleFor(r => r.Kind)
            .Cascade(CascadeMode.Stop)
            .Must(k => !string.IsNullOrWhiteSpace(k))
                .WithMessage(KindRequiredMessage)
            .Must(k => k!.Length <= NotificationIngress.KindMaxLength)
                .WithMessage(KindTooLongMessage)
            .OverridePropertyName(KindKey);

        // 既定値（0001-01-01）を黙って採ると、一覧の並び（OccurredAt 降順）と保持期間（90 日）の
        // 両方が壊れる。**欠落は欠落として拒否する。**
        RuleFor(r => r.OccurredAt)
            .Must(v => v is not null)
                .WithMessage(OccurredAtRequiredMessage)
            .OverridePropertyName(OccurredAtKey);

        RuleFor(r => r.Count)
            .Must(c => c is not < 0)
                .WithMessage(CountNegativeMessage)
            .OverridePropertyName(CountKey);

        RuleFor(r => r.ThresholdPercent)
            .Must(p => p is not (< 0 or > 100))
                .WithMessage(ThresholdPercentOutOfRangeMessage)
            .OverridePropertyName(ThresholdPercentKey);
    }
}
