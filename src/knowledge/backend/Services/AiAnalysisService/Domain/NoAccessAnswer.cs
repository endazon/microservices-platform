using AiAnalysisService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Domain;

// FR-04, FR-05, FR-07, UC-01, UC-02, IADR-0009, IADR-0111, IADR-0335 (#1318):
// 「閲覧できる文書が無い」ときに返す縮退応答の**唯一の定義**。
//
// 文言は**存在秘匿を破らない中立文言**である（IADR-0009）——「権限が無い」と「該当が無い」を
// 区別させない。モデル名は空（IADR-0111 の「モデル未使用」）であり、**ゲートウェイを一度も
// 呼んでいない**ことを表す。
//
// 🔴 **なぜ 1 箇所に寄せるか。** 従来この値は `RagOrchestrator` の中だけに在り、
// 非ストリーミング（`EmptyAnswer`）とストリーミング（SSE 3 イベント）の 2 形を持っていた。
// #1318 で**端点側にも同じ縮退が要る**ようになった（未認証は認可サービスを呼ぶ前に倒すため、
// オーケストレーターを通らない）。端点へ書き写すと同じ利用者向け文言が 3 箇所へ増え、
// 片方だけ直す事故の余地ができる。**値も意味も変えずに寄せるだけ**である。
public static class NoAccessAnswer
{
    // IADR-0009: 存在秘匿を破らない中立文言。非ストリーミングと SSE で**同一の文字列**を使う。
    public const string Message = "閲覧権限のある文書が見つかりませんでした。";

    // IADR-0111 (#403): 「モデル未使用（AI へ送信していない）」を表す応答契約上の値。
    public const string NoModel = "";

    // 非ストリーミング（`/analysis/ask`・`/analysis/analyze`）の縮退応答。
    public static AiAnswerDto Answer() => new(Message, [], NoModel, 0, 0);

    // ストリーミング（`/analysis/ask/stream`）の縮退応答。
    // 本文（token）が空のまま done になって**理由不明の空白回答**が表示されるのを防ぐため、
    // 中立文言を 1 トークンとして流す（非ストリーミング版と挙動を揃える）。
    public static IEnumerable<AskEvent> StreamEvents()
    {
        yield return new AskCitationsEvent([]);
        yield return new AskTokenEvent(Message);
        yield return new AskDoneEvent(Guid.NewGuid(), NoModel, 0, 0);
    }
}
