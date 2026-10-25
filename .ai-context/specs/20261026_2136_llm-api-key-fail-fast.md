---
title: 作業仕様書 — LLM ゲートウェイは Claude のエンドポイントが有効なのに API キーが空なら起動時に止まる。鍵なしで動かす検証スタックは明示の opt-out を置く（#2136）
type: spec
status: done
related_ids: [FR-11, ADR-0010, IADR-0641]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2136"
---

# 作業仕様書 — Claude の API キーが空なら起動時に止める（#2136）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。依頼はコーディネーター（2026-10-26）。

## 現状の挙動（実測）

- `AnthropicClientFactory.Create` は `ApiKey = configuration["Llm:ApiKey"] ?? "placeholder"` でクライアントを作る。
  - 空文字はそのまま SDK へ渡る。キーが無い（null）ときは文字列 `placeholder` が渡る。
  - どちらも起動時には何も言わない。
- `Program.cs` はクライアントを `AddSingleton(sp => AnthropicClientFactory.Create(...))` で登録する。
  - シングルトンは最初に解決されたときに作られる。つまり鍵は**最初の Claude 呼び出しのときに初めて**使われる。
  - そこで Anthropic が 401 を返し、呼び出し側には上流の不調として見える。readiness は通ったままである。
- `LlmRoutingOptions` は `Configure` で結び付けるだけで、起動時の検証（`ValidateOnStart`）を持たない。
- 既定の `appsettings.json` は `Llm:ApiKey: ""` で、エンドポイント `claude-managed`（Provider `claude`）は `Enabled: true` である。
- Helm（本番の既定）は Secret が無ければ Pod を作らない（`secretKeyRef` で `optional` なし）。しかし**値が空の Secret は通る**。

## 鍵なしで動く環境（壊してはならない側）

| 環境 | 鍵 | 根拠 |
| --- | --- | --- |
| compose（`deploy/docker-compose.yml`） | `Llm__ApiKey: ${ANTHROPIC_API_KEY:-}`。既定は空 | 「空＝外部 LLM を呼ばない」を fail-safe として書いている |
| ローカル k8s・PoC・CI の使い捨てスタック（`deploy/local/values-local.yaml`。`integration-stack`・`e2e-live`・`cutover-rehearsal` は `k8s-local-up.sh` 経由でこれを使う） | `k8s-local-up.sh` が `anthropic-api-key=${ANTHROPIC_API_KEY:-}` で Secret を作る。既定は空 | 同上。Vault の種（`bootstrap.sh`）も空で作る |
| 出力トークン実測の Runbook の後始末 | 測定の後に鍵を空へ戻し、Pod を作り直す | `docs/operations/llm-output-token-measurement-runbook.md` §止め方 |
| `dotnet run`（`appsettings.Development.json`） | 鍵を置かない | 開発者が手元で起こす |
| 単体試験（`WebApplicationFactory<Program>`） | 4 つの器がすべて `Llm:ApiKey = test-key` を与える | `TestWebApplicationFactory`・`GrpcKestrelFactory`・`AnthropicClientWiringTests`・`ClaudeProviderRequestShapeTests` |

## 方針

- 起動時に検証する。条件は「`Provider == "claude"` かつ `Enabled` のエンドポイントが 1 つ以上ある」かつ「`Llm:ApiKey` が null・空・空白」である。
  - 満たせば、起動を失敗させる（`ValidateOnStart`。既存の `EmbeddingRoutingOptionsValidator` と同じ作り）。
  - Claude のエンドポイントがすべて無効なら、鍵が空でも起動する（鍵を使う経路が無い）。
- opt-out は設定 `Llm:AllowMissingApiKey`（env `Llm__AllowMissingApiKey`）である。値が `true` のときだけ効く。
  - 置いていない・`true` 以外の値は opt-out にしない（fail-closed）。
  - opt-out で起動したときは、warn を 1 行出す（鍵の値は出さない。キー名だけ）。
- opt-out を置く先は、上の表で鍵なしで動く環境だけである: compose・`values-local.yaml`・`appsettings.Development.json`。
  - 本番の既定（`values.yaml`・`appsettings.json`）には置かない。値が空の Secret で起動すると、Pod は起動に失敗する。
  - ローリング更新なら、古い Pod は動き続ける（新しい Pod が ready にならないため）。
- 鍵の値はログにも例外にも出さない。失敗の文言と warn は設定キーの名前とエンドポイント名だけを含む。
  - 失敗するのは鍵が空のときだけなので、値はそもそも無い。それでも文言に値を差し込まない。
- `AnthropicClientFactory` の `?? "placeholder"` は変えない。opt-out で動くときの挙動（最初の呼び出しの 401）を変えないためである。

## 受け入れ基準

- [x] Claude のエンドポイントが有効で、`Llm:ApiKey` が空（`""`・空白・未設定）なら、ホストの起動が失敗する。例外の文言に `Llm:ApiKey` と `Llm:AllowMissingApiKey` の名前を含む。修正前は赤になる
- [x] `Llm:AllowMissingApiKey=true` なら、鍵が空でも起動する。warn を 1 行出す
- [x] `Llm:AllowMissingApiKey` が `true` 以外（例 `yes`）なら opt-out にならず、起動が失敗する
- [x] Claude のエンドポイントがすべて無効なら、鍵が空でも起動する
- [x] 鍵があれば起動する。失敗の文言にも warn にも鍵の値が出ない（空白だけの鍵の場合も含む）
- [x] compose・`values-local.yaml`・`appsettings.Development.json` に opt-out を置く。本番の既定（`values.yaml`・`appsettings.json`）には置かない。これを helm の描画で確かめる
- [x] 運用仕様書とテスト仕様書（T-46）に書く。IADR-0641 を起こす

## 修正前に赤であること（実測 2026-10-26）

- 判定器を「常に通す」骨組みにし、Program.cs へ配線しない状態（＝修正前の挙動）で `ClaudeApiKeyStartupValidationTests` を走らせた。
- 結果は `Failed: 10, Passed: 2, Total: 12`。緑の 2 本は「エンドポイントが無効なら通る」「鍵があれば起動する」で、修正前も成り立つ側である。
- 実装後は 12 本とも緑。LlmGateway.Tests 全体は 523 本とも緑。

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n -E "空=呼ばない|空＝呼ばない|外部 LLM を呼ばない|空文字 = 外部|placeholder|Llm__ApiKey|Llm:ApiKey|anthropic-api-key"` で引いた（submodule・`.ai-context/` は除く）。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `deploy/docker-compose.yml` の `Llm__ApiKey` | **直す**（opt-out を足す） | 鍵なしで動く環境 |
| `deploy/local/values-local.yaml` の「fail-safe: 未設定なら空文字 = 外部 LLM を呼ばない」 | **直す**（opt-out を足し、コメントを改める） | 同上。PoC・CI の使い捨てスタックもここを使う |
| `deploy/helm/microservices-platform/values.yaml` の `Llm__ApiKey` | **直す**（コメントだけ） | 空の値で起動が止まることを書く。opt-out は置かない |
| `appsettings.Development.json` | **直す**（opt-out を足す） | `dotnet run` を壊さない |
| `deploy/local/README.md` の「空=呼ばない」 | **直す** | opt-out が要ることを足す |
| `deploy/local/vault/eso/README.md`・`bootstrap.sh`・`externalsecret-llm.yaml` の「空＝外部 LLM を呼ばない」 | 外す | ローカルの供給側の記述で、values-local の opt-out の下ではいまも正しい |
| `docs/operations/llm-output-token-measurement-runbook.md` の後始末 | 外す | ローカル（values-local）での手順で、opt-out の下で Pod は起動する |
| `deploy/bootstrap/README.md`・`sc22-secret-items.json`・`secret-templates.example.yaml`・SC-22 の Runbook | 外す | 鍵の供給の仕方の記述で、起動時の扱いに触れていない |
| `scripts/helm-llm-provider-keys.test.js` | **足す**（opt-out の置き場の判定と変異 2 本） | 既存の `optional:` と `Llm__ApiKey` の数え方は opt-out の env（リテラル値・`optional` なし）で変わらない。opt-out が本番像の描画に現れない・values-local に `"true"` で 1 回現れることを helm の描画で見る（受け入れ基準の「helm の描画で確かめる」の実体）。helm v3.16.3 で 12 本とも緑 |
| `.github/ISSUE_TEMPLATE`・values.yaml の frontend の placeholder | 外す | 別の語 |

## 規則 11（窓）

- 時間差を扱う是正ではない。鍵は Pod の起動時に 1 回だけ読む（env）。実行中に Secret を空にしても、次の起動まで効かない。
- 次の起動で検証が走る。したがって「起動時の検査」と「実行中の鍵」の間に窓は生まれない。表は描かない。

## 範囲の外

- セルフホスト・Copilot の資格情報（`Llm:SelfHosted:BaseUrl`・`Llm:Copilot:Token`）の起動時検証。既定でエンドポイントが無効で、本件（Claude の鍵）と別である。
- 埋め込み（Voyage）の鍵。鍵なしは取り込みの側で一時障害として扱う設計（IADR-0536）で、起動を止めない。
