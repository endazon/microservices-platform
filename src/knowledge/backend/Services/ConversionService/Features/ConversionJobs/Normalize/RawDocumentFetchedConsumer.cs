using Platform.Shared.Infrastructure.Foundation.Pipeline;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Wolverine;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace ConversionService.Features.ConversionJobs.Normalize;

// FR-12, UC-06: 原本取得イベントを受信し正規化変換を行う（pandoc で本文 Markdown 化、
// 図は LLM で PlantUML/Mermaid 化、不可分は画像保持）。
// SC-07: 変換状況の可視化（成功／失敗）と人手補正のため、ライフサイクルを IConversionJobStore に記録する。
//
// 🔴 ADR-0027 / #441 E1: **購読は Wolverine へ移した。** 発行（DocumentNormalized）は
// MassTransit のままである —— その辺は E2 の射程であり、辺は原子的に動かす（IADR-0234 決定 3）。
public class RawDocumentFetchedConsumer(
    INormalizationService normalizer,
    IDocumentNormalizedPublisher publisher,
    IConversionJobStore jobs,
    ILogger<RawDocumentFetchedConsumer> logger) : IPipelineStep<RawDocumentFetched>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "convert";

    // ADR-0027 / #441 E1: Wolverine のハンドラ。**入力は本イベント、出力は MassTransit のまま**である
    // （DocumentNormalized の辺は E2 の射程であり、本 PR では動かさない）。
    // Envelope を引数に取るのは、これが「1 回の配信の何回目の試行か」を知る唯一の口だからである
    // （MassTransit の ConsumeContext.GetRetryAttempt() に相当する）。
    public async Task Handle(RawDocumentFetched ev, Envelope envelope, CancellationToken ct)
    {

        logger.LogInformation(
            "Converting raw document: SourceId={SourceId} Path={Path} Type={Type}",
            ev.SourceId, ev.OriginalPath, ev.ContentType);

        // SC-07: 変換開始を記録（受信・再試行の都度）。
        await jobs.StartAsync(ev, ct);

        try
        {
            // FR-12: 本文 Markdown 化 ＋ 図のコード化/画像保持 ＋ オブジェクトストレージ保管。
            var result = await normalizer.NormalizeAsync(ev, ct);

            // FR-12: 正規化完了イベント発行 → DocumentService が文書を登録し取り込みへ連鎖する。
            // DocumentId は冪等（再変換で同一）。文書管理側で重複登録を避けられる。
            // ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0381]] (#1254): 本文の有無も運ぶ。
            // 後続（カタログ）が台帳へ保持し、SC-03 の「本文なし（原本を参照）」の材料にする。
            // 🔴 ADR-0070 決定 4 / [[IADR-0381]] 決定 4 (#1253): **題名へ畳む前のパスも一緒に運ぶ。**
            // 従前はここで `GetFileNameWithoutExtension` がパスを捨てており、決定 4 の
            // 「タイトル・**パス**・**データソース**……で検索に載せる」のうち題名しか下流へ
            // 届いていなかった（[[IADR-0358]] 決定 2 のフォローアップ 1）。
            var title = Path.GetFileNameWithoutExtension(ev.OriginalPath);
            await publisher.PublishNormalizedAsync(
                result.DocumentId, ev.SourceId, title, result.MarkdownUri,
                [.. result.AssetUris], ev.Attributes, ev.Tags, result.HasBody,
                ev.OriginalPath, ev.SourceName, ct);

            // SC-07: 成功を記録。IADR-0154 決定 1: 図の記録も渡す（人手補正 Phase 1 の対象を残すため。
            // 従前は件数をログへ出して捨てており、どの図が縮退したかを後から引けなかった）。
            // 🔴 テキスト層の無い PDF も**ここ（成功）**を通る。`failed` にも `deadLettered` にもしない
            // （ADR-0070 決定 3）—— 内訳は `HasBody` が運ぶ。
            await jobs.SucceedAsync(ev.FetchId, result.DocumentId, result.MarkdownUri,
                result.Figures, result.HasBody, ct);

            logger.LogInformation(
                "Conversion complete for {FetchId}: doc={DocumentId} markdown={Uri} coded={Coded} retained={Retained} hasBody={HasBody}",
                ev.FetchId, result.DocumentId, result.MarkdownUri, result.DiagramsCoded, result.DiagramsRetained,
                result.HasBody);
        }
        catch (UnsupportedSourceFormatException ex)
        {
            // FR-12, UC-06, SC-07, IADR-0320 決定 4 (#1097), IADR-0356 (#1192): 原本の形式がどの変換器の
            // 入力にもならない（計画の対応形式表に無い未知の形式）。**PDF はもうここへ来ない** ——
            // ADR-0070 決定 2 によりテキスト層の抽出器へ振り分ける。
            // **再試行しても結果は変わらない**ので、再送出せず恒久失敗として記録する。
            //
            // 🔴 デッドレターへは流さない —— 判る形で拒否したいのであって、原因不明の毒メッセージとして
            // 溜めたいのではない。`DeadLettered = true` は「この失敗の後に自動再試行は起きない」の意であり
            // （IADR-0137 / ADR-0053 決定 2）、それはこの経路でも真である。
            // 変換ジョブ画面には理由つきの failed として並び、原本を直したら /retry で再変換できる。
            await jobs.FailAsync(ev.FetchId, SummarizeError(ex.Message), deadLettered: true,
                CancellationToken.None);

            logger.LogWarning(
                "Conversion rejected for {FetchId}: {Path} ({Type}) — {Reason}",
                ev.FetchId, ev.OriginalPath, ev.ContentType, ex.Message);
        }
        catch (Exception ex)
        {
            // SC-07: 失敗を記録してから再送出する。変換失敗（pandoc/保存の恒久失敗）は MassTransit の
            // 再試行→デッドレターへ委ねる（記録は状況可視化・人手補正のためで、リトライ挙動は変えない）。
            // 例外メッセージは admin/operator UI に露出するため、単一行・長さ上限に要約する（内部詳細の露出抑制）。
            // 失敗記録は best-effort（CancellationToken.None）で行い、元例外を消さずに再送出する
            // （ct 失効時に SaveChanges がキャンセル例外を投げて元の変換失敗を隠さないため）。
            await jobs.FailAsync(ev.FetchId, SummarizeError(ex.Message), IsLastAttempt(envelope),
                CancellationToken.None);
            throw;
        }
    }

    // FR-12, SC-07, IADR-0137: この失敗が 1 回の配信における最後の試行か（＝この後デッドレターへ送られるか）。
    //
    // Wolverine の Envelope.Attempts は **1 始まり**である（MassTransit の GetRetryAttempt() は
    // 0 始まりだったので +1 していた。ここでは足さない）。上限は共通ヘルパの
    // WolverineExtensions.MaxAttempts が単一情報源であり、W1 が MassTransit 側との等価性を
    // 試験で束ねている。
    private static bool IsLastAttempt(Envelope envelope) =>
        envelope.Attempts >= WolverineExtensions.MaxAttempts;

    // SC-07: 変換失敗メッセージを 1 行・最大 300 文字へ丸める（改行・冗長なスタック様文言の UI 露出を避ける）。
    private static string SummarizeError(string message)
    {
        var firstLine = message.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 300;
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }
}
