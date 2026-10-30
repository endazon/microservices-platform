using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Domain.Ports;

// FR-04, FR-07: RAG オーケストレーションポート
public interface IRagOrchestrator
{
    // FR-04, UC-01: 自然文の質問に対し、権限内文書を根拠に回答＋出典を返す。
    //
    // **［#539］`attributeFilters` は利用者が指定した対象範囲**（SC-01 の対象範囲フィルタ）。
    // **ABAC 許可スコープと交差し、権限を広げない（narrowing-only）** —— `AnalyzeAsync` と同じ規則である。
    Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes,
        Dictionary<string, List<string>>? attributeFilters = null,
        CancellationToken ct = default);

    // FR-07, UC-02: 指定データ範囲（range）に対する分析・比較・抽出を行い、回答＋出典を返す。
    // データ範囲は ABAC 許可スコープと交差し、権限を広げない（narrowing-only）。
    Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default);

    // IADR-0037, FR-04, UC-01: 自然文質問への回答を SSE 用のイベント列としてストリーミングする。
    // 出典（citations）は LLM 生成前に確定するため先に送出し、続いて本文トークン、最後に done を送る。
    IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
        Dictionary<string, string> userAttributes,
        Dictionary<string, List<string>>? attributeFilters = null,
        CancellationToken ct = default);
}

// IADR-0037: ask ストリームのイベント（SSE の event 名 + data JSON へ写像する）。
public abstract record AskEvent;

// 出典（本文ストリームより先に送出。フロントは本文表示中に出典を先行併記できる）。
public sealed record AskCitationsEvent(IReadOnlyList<CitationDto> Citations) : AskEvent;

// 本文の増分トークン。
// NFR-02, ADR-0079 決定 1, [[IADR-0654]] 決定 4 (#2335): `FromModel` は **LLM が出したトークンか**。
// 縮退の文（閲覧できる文書が無い・合成の抑止・ゲートウェイの拒否や不調の理由・拒否の注記・空本文の注記）は false である。
// 端点は `FromModel` のトークンでだけ初回トークンの計器（[[IADR-0354]]）を進める —— 縮退の文を積むと、
// LLM を呼ばない 60 秒の合成と権限の無い要求が「速い初回トークン」として p95 を下振れさせる。既定は true（LLM の増分）。
public sealed record AskTokenEvent(string Text, bool FromModel = true) : AskEvent;

// 完了（回答 ID・モデル・トークン数）。回答 ID は 👍/👎 の紐付け先（FR-08）。
public sealed record AskDoneEvent(Guid AnswerId, string Model, int InputTokens, int OutputTokens) : AskEvent;

// 取得・生成の失敗（中立表示用）。
public sealed record AskErrorEvent(string Message) : AskEvent;
