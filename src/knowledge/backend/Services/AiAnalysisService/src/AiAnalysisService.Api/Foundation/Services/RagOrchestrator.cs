using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AiAnalysisService.Api.Foundation.Services;

// FR-04, FR-07, UC-01, UC-02: RAG 回答生成オーケストレーター
// フロー: ABAC スコープ解決 → （FR-07: データ範囲と交差）→ ハイブリッド検索 → LLM 回答生成
public class RagOrchestrator(
    IHttpClientFactory httpFactory,
    IConfiguration config) : IRagOrchestrator
{
    // FR-04: 質問回答で文脈に取り込む既定チャンク数。
    private const int DefaultAskTopK = 5;

    // FR-04, UC-01: 自然文質問に対する RAG 回答。
    public async Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
    {
        // FR-05: ABAC 権限スコープ解決。閲覧可能文書が無ければ空回答へ縮退（多値 allow-list を破棄しない）。
        var resolved = await ResolveScopeAsync(userId, userAttributes, ct);
        if (!resolved.Granted)
            return EmptyAnswer();

        var scope = new AccessScope(resolved.AllowedFilters, resolved.Granted);
        // FR-11, UC-01: 用途は rag-answer。呼び出し先は LlmGateway が機密区分に応じて切り替える。
        return await GenerateAsync(question, scope, DefaultAskTopK,
            context => BuildAskPrompt(question, context), "rag-answer", ct);
    }

    // FR-07, UC-02: 指定データ範囲での分析・比較・抽出。
    public async Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
    {
        // FR-05: ABAC 権限スコープ解決。
        var resolved = await ResolveScopeAsync(userId, userAttributes, ct);

        // FR-07/FR-05: データ範囲を ABAC スコープと交差させ、権限を広げない実効スコープを得る。
        var scope = DataRangeScopeResolver.Resolve(resolved, request.Range);
        if (!scope.GrantsAccess)
            return EmptyAnswer();

        // 範囲の検索クエリが未指定なら指示文を流用して関連箇所を絞る。
        var query = string.IsNullOrWhiteSpace(request.Range?.Query)
            ? request.Instruction
            : request.Range!.Query!;
        var topK = request.Range?.TopK is > 0 ? request.Range.TopK : DefaultAskTopK;

        // FR-11, UC-02: 用途は analysis。機密区分の高いデータは LlmGateway が外部送信を抑止する。
        return await GenerateAsync(query, scope, topK,
            context => AnalysisPromptBuilder.Build(request, context), "analysis", ct);
    }

    // IADR-0037, FR-04, UC-01: 自然文質問への回答をストリーミングする。
    // フロー: ABAC スコープ解決 →（不許可なら空回答）→ 検索 → 出典イベント → LLM ストリーム（token）→ done。
    // 出典は LLM 生成前に確定するため先に送り、フロントは本文表示中に出典を先行併記できる。
    // 反復子内で yield を跨ぐ try/catch は使えないため、失敗は下位ヘルパが done(Sent=false) へ縮退させる。
    public async IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
        Dictionary<string, string> userAttributes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var defaultModel = config["Llm:DefaultModel"] ?? "claude-opus-5";

        // FR-05: ABAC 権限スコープ解決。閲覧可能文書が無ければ空回答へ縮退（外部送信しない）。
        var resolved = await ResolveScopeAsync(userId, userAttributes, ct);
        if (!resolved.Granted)
        {
            // IADR-0009 存在秘匿を破らない中立文言。非ストリーミング版 AskAsync（EmptyAnswer）と挙動を揃え、
            // 本文（token）が空のまま done になって理由不明の空白回答が表示されるのを防ぐ。
            yield return new AskCitationsEvent([]);
            yield return new AskTokenEvent("閲覧権限のある文書が見つかりませんでした。");
            yield return new AskDoneEvent(Guid.NewGuid(), defaultModel, 0, 0);
            yield break;
        }

        var scope = new AccessScope(resolved.AllowedFilters, resolved.Granted);

        // 検索 → 出典（本文より先に送出）。
        var results = await SearchAsync(question, scope, DefaultAskTopK, ct);
        var citations = CitationMapper.ToCitations(results);
        yield return new AskCitationsEvent(citations);

        // FR-11: 文脈の最高機密区分と用途をゲートウェイへ渡し、越境判定を委ねる（送信可否はゲートウェイ側）。
        var confidentiality = HighestConfidentiality(results);
        var contextText = CitationMapper.BuildContext(citations);
        var prompt = BuildAskPrompt(question, contextText);

        var model = defaultModel;
        var inputTokens = 0;
        var outputTokens = 0;
        var emittedAny = false;

        await foreach (var ev in StreamCompletionAsync(prompt, confidentiality, "rag-answer", ct))
        {
            if (!string.IsNullOrEmpty(ev.Delta))
            {
                emittedAny = true;
                yield return new AskTokenEvent(ev.Delta);
            }
            if (ev.Done)
            {
                // FR-11 縮退: ゲートウェイが送信拒否・不調なら理由本文を出典付きで表示（外部送信していない）。
                if (!ev.Sent && !string.IsNullOrWhiteSpace(ev.Text))
                    yield return new AskTokenEvent(ev.Text!);

                // FR-11, IADR-0104 (#379): 送信は成立したがモデルが拒否した場合（stopReason="refusal"）。
                // 拒否は末尾の done で確定するため既出デルタは撤回できない。拒否である旨を末尾へ追記して、
                // 「空応答」や「途中で切れた回答」と取り違えられないようにする。
                // 部分本文が既に流れているときは空行で区切る（フロントは token を 1 つの文字列へ連結し
                // white-space: pre-wrap で表示するため、区切らないと注記が地の文へ溶け込む）。
                else if (CompletionStopReasons.IsRefusal(ev.StopReason))
                {
                    var separator = emittedAny ? "\n\n" : string.Empty;
                    emittedAny = true;
                    yield return new AskTokenEvent($"{separator}（AI が回答の生成を拒否しました。）");
                }

                model = string.IsNullOrEmpty(ev.Model) ? defaultModel : ev.Model;
                inputTokens = ev.InputTokens;
                outputTokens = ev.OutputTokens;
            }
        }

        // 本文が全く出なかった場合の中立フォールバック（出典があればその旨）。
        if (!emittedAny && citations.Count > 0)
            yield return new AskTokenEvent("関連文書が見つかりました。");

        yield return new AskDoneEvent(Guid.NewGuid(), model, inputTokens, outputTokens);
    }

    // FR-03: 実効スコープでハイブリッド検索を実行し、結果チャンクを返す（失敗時は空）。
    private async Task<IReadOnlyList<SearchResultDto>> SearchAsync(
        string query, AccessScope scope, int topK, CancellationToken ct)
    {
        var retrievalClient = httpFactory.CreateClient("RetrievalService");
        try
        {
            var searchResp = await retrievalClient.PostAsJsonAsync("/search",
                new SearchRequest(query, topK, null, scope), ct);
            var searchResult = searchResp.IsSuccessStatusCode
                ? await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct)
                : new SearchResponse([], 0, 0);
            return searchResult?.Results ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    // IADR-0037: LlmGateway /complete/stream の SSE を消費し、CompletionStreamEvent を逐次返す。
    // 反復子内で yield を跨ぐ try/catch を避けるため、送信・読み取りの失敗は捕捉後に done(Sent=false) を
    // yield して終了する（呼び出し側は縮退表示に切り替えられる）。egress 判定はゲートウェイ側で保持される。
    private async IAsyncEnumerable<CompletionStreamEvent> StreamCompletionAsync(
        string prompt, string confidentiality, string purpose, [EnumeratorCancellation] CancellationToken ct)
    {
        var llmClient = httpFactory.CreateClient("LlmGateway");
        // IADR-0101: MaxTokens は思考トークンと本文の合算上限（thinking が既定有効な Opus 5 / Sonnet 5 の場合）。
        // 本経路の purpose は rag-answer で、現行設定の割当は claude-sonnet-5（ADR-0022 追随済み・IADR-0106）。
        // Sonnet 5 も thinking が既定有効で、かつ新トークナイザ（同一テキストで約 +30% トークン）のため、
        // 4096 は実測前の出発値である（再調整は #380）。
        var body = new CompletionApiRequest(prompt, MaxTokens: 4096, Model: null,
            Confidentiality: confidentiality, Purpose: purpose);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/complete/stream")
        {
            Content = JsonContent.Create(body),
        };

        HttpResponseMessage? resp = null;
        var sendFaulted = false;
        try
        {
            resp = await llmClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            sendFaulted = true;
        }

        if (sendFaulted || resp is null || !resp.IsSuccessStatusCode)
        {
            resp?.Dispose();
            yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                Text: "LLM が現在利用できません。");
            yield break;
        }

        using (resp)
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream))
        {
            while (true)
            {
                string? line = null;
                var readFaulted = false;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
                {
                    readFaulted = true;
                }

                if (readFaulted)
                {
                    yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                        Text: "LLM 応答の受信に失敗しました。");
                    yield break;
                }

                if (line is null)
                    yield break; // ストリーム終端

                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue; // SSE の空行・コメント行は読み飛ばす

                CompletionStreamEvent? ev = null;
                try
                {
                    ev = JsonSerializer.Deserialize<CompletionStreamEvent>(
                        line["data: ".Length..], SseJson);
                }
                catch (JsonException)
                {
                    ev = null; // 壊れた行は無視
                }

                if (ev is not null)
                    yield return ev;
            }
        }
    }

    // SSE data 行の JSON（camelCase）を CompletionStreamEvent へ復元する。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    // FR-05: ABAC スコープ解決。解決失敗時も deny-by-default（Granted=false）へ縮退する。
    private async Task<AccessScopeResponse> ResolveScopeAsync(string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct)
    {
        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttributes), ct);
            return (scopeResp.IsSuccessStatusCode
                ? await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null) ?? new AccessScopeResponse(userId, [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 認可サービスへの通信失敗（ネットワーク障害・タイムアウト）も deny-by-default へ縮退し、
            // 500 を伝播させない。呼び出し側のキャンセル要求は通常どおり伝播させる。
            return new AccessScopeResponse(userId, [], false);
        }
    }

    // FR-04, FR-07: 実効スコープで検索 → 番号付き出典へ写像 → LLM で本文生成、の共通パイプライン。
    // FR-11: 文脈文書の最高機密区分と用途を LLM ゲートウェイへ渡し、呼び出し先の切替を委ねる。
    private async Task<AiAnswerDto> GenerateAsync(string query, AccessScope scope, int topK,
        Func<string, string> buildPrompt, string purpose, CancellationToken ct)
    {
        // FR-11: 明示モデルは指定しない。実際の呼び出しモデルは LlmGateway が用途（purpose）と
        // 機密区分に応じて選択する（Llm:Routing:PurposeModels）。既定モデル名は縮退応答の表示用のみ。
        var defaultModel = config["Llm:DefaultModel"] ?? "claude-opus-5";

        // FR-03: 実効スコープでハイブリッド検索（ストリーミング版と同じ SearchAsync に集約。失敗時は空へ縮退）。
        var results = await SearchAsync(query, scope, topK, ct);

        // FR-04: 検索結果を番号付き出典へ写像（回答本文の [1][2] と一致させる）
        var citations = CitationMapper.ToCitations(results);

        // FR-11: 文脈に含む文書の最も高い機密区分を求める。ゲートウェイはこれで送信先ティアを判定する。
        var confidentiality = HighestConfidentiality(results);

        // FR-04: 関連チャンクを文脈にして LLM ゲートウェイで本文生成
        var context = CitationMapper.BuildContext(citations);
        var prompt = buildPrompt(context);

        var llmClient = httpFactory.CreateClient("LlmGateway");
        // FR-11: Model は明示せず（null）、用途（purpose）と機密区分をゲートウェイへ渡して呼び出し先・モデル選択を委ねる。
        // IADR-0101: MaxTokens は思考トークンと本文の合算上限（thinking が既定有効な Opus 5 / Sonnet 5 の場合）。
        // 本経路の purpose は rag-answer で、現行設定の割当は claude-sonnet-5（ADR-0022 追随済み・IADR-0106）。
        // Sonnet 5 も thinking が既定有効で、かつ新トークナイザ（同一テキストで約 +30% トークン）のため、
        // 4096 は実測前の出発値である（再調整は #380）。
        var completionResp = await llmClient.PostAsJsonAsync("/complete",
            new CompletionApiRequest(prompt, MaxTokens: 4096, Model: null,
                Confidentiality: confidentiality, Purpose: purpose), ct);

        if (completionResp.IsSuccessStatusCode)
        {
            var completion = await completionResp.Content
                .ReadFromJsonAsync<CompletionApiResponse>(ct);

            // FR-11 縮退: ゲートウェイが送信を拒否（許容ティアに送信可能な呼び出し先が無い）した場合は、
            // 外部送信せず検索結果（出典）のみ返す。
            if (completion is { Sent: false })
            {
                var reason = citations.Count > 0
                    ? "機密区分により AI 送信を行わなかったため、関連文書の一覧を返します。"
                    : "機密区分により AI 送信を行いませんでした。";
                return new AiAnswerDto(reason, citations, defaultModel, 0, 0);
            }

            // FR-11, IADR-0104 (#379): 送信は成立したがモデルが拒否した場合（stopReason="refusal"）。
            // sent=true・本文は空で返るため、sent だけを見ると「空応答」と区別できず理由不明の
            // 空白回答になる。拒否である旨を明示し、出典は通常どおり併記する。
            if (CompletionStopReasons.IsRefusal(completion?.StopReason))
            {
                var refused = citations.Count > 0
                    ? "AI が回答の生成を拒否したため、関連文書の一覧を返します。"
                    : "AI が回答の生成を拒否しました。";
                return new AiAnswerDto(refused, citations, completion?.Model ?? defaultModel,
                    completion?.InputTokens ?? 0, completion?.OutputTokens ?? 0);
            }

            return new AiAnswerDto(
                completion?.Text ?? "回答を生成できませんでした。",
                citations,
                completion?.Model ?? defaultModel,
                completion?.InputTokens ?? 0,
                completion?.OutputTokens ?? 0);
        }

        // FR-04/FR-07 縮退: LLM 不調時は検索結果（出典）のみ返す
        var fallback = citations.Count > 0
            ? "LLM が現在利用できないため、関連文書の一覧を返します。"
            : "関連する情報が見つかりませんでした。";
        return new AiAnswerDto(fallback, citations, defaultModel, 0, 0);
    }

    // FR-11: 検索結果の confidentiality 属性から最も高い機密区分を求める。
    // 値の序列は public < internal < confidential < restricted。
    // 08_data-egress-policy「既定は安全側」/ FR-05 deny-by-default に従い、属性欠落・未知値は restricted 扱い。
    private static string HighestConfidentiality(IReadOnlyList<SearchResultDto> results)
    {
        if (results.Count == 0)
            return "public";

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["public"] = 0,
            ["internal"] = 1,
            ["confidential"] = 2,
            ["restricted"] = 3
        };

        var highest = "public";
        var highestRank = 0;
        foreach (var r in results)
        {
            // 属性が欠落・空文字の文書は安全側（restricted）とみなす。
            var value = r.Attributes.TryGetValue("confidentiality", out var c) && !string.IsNullOrWhiteSpace(c)
                ? c
                : "restricted";
            // 未知の値も安全側（restricted 相当）に倒す。
            var rank = order.TryGetValue(value, out var v) ? v : 3;
            if (rank > highestRank)
            {
                highestRank = rank;
                highest = order.ContainsKey(value) ? value : "restricted";
            }
        }
        return highest;
    }

    // FR-05: 閲覧可能文書が無い場合の空回答（検索・LLM を呼ばずコストを抑える縮退）。
    private AiAnswerDto EmptyAnswer()
        => new(
            "閲覧権限のある文書が見つかりませんでした。",
            [],
            config["Llm:DefaultModel"] ?? "claude-opus-5",
            0, 0);

    private static string BuildAskPrompt(string question, string context)
        => $"""
            以下の参照文書を根拠に、質問に答えてください。根拠のない情報は含めないでください。

            ## 参照文書
            {context}

            ## 質問
            {question}

            ## 回答（日本語で、出典番号 [1][2] を付けてください）
            """;
}
