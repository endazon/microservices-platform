namespace ConversionService.Infrastructure.Configuration;

// FR-12, UC-06, ADR-0012, IADR-0316 (#1097): 本文変換（pandoc）の構成。設定セクションは `Conversion`。
public sealed class ConversionOptions
{
    public const string SectionName = "Conversion";

    // 🔴 **既定は fail-closed である。**
    //
    // `PandocConversionService` は pandoc 未導入・原本が解決不能のとき、従前は無条件に
    // プレースホルダ本文（図0件）を返して「成功」していた。配備した実物でそれが起きると
    // FR-12 の主要素（本文の Markdown 化）が 1 度も実行されないまま、変換ジョブ画面には
    // 成功として並ぶ（#1097 の実測）。
    //
    // true にできるのは **pandoc を入れられない開発機**だけである（単体テストは pandoc の
    // 無い CI でも走る必要があるため、縮退そのものは残してある）。
    // 配備（helm / compose）はこの値を注入しない —— 注入する面があると「dev だけ縮退」を
    // 本番側から覆せてしまう。
    public bool AllowDegradedBodyConversion { get; set; }
}
