---
title: IADR-0504 埋め込み（Voyage AI）の鍵は外部 LLM の鍵と同じ Secret・同じ Vault の KV のプロパティ voyage-api-key に置き、LLM ゲートウェイへ optional の secretKeyRef で渡す。鍵が無いときの挙動（起動する・埋め込みは一時障害で DLQ）は変えない
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, SC-22, NFR-18, ADR-0016, ADR-0095, ADR-0127, IADR-0096, IADR-0103, IADR-0256, IADR-0433, IADR-0456, IADR-0494]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md（埋め込みは Voyage voyage-3.5 を既定・ティアB）
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md（秘密情報は Vault を正とし画面から入れる）
related_specs:
  - ../specs/20261006_1764_voyage-key-wiring.md
---

# IADR-0504: 埋め込み（Voyage AI）の鍵の Secret 配線（#1764）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-06
- 決定者: claude（#1764。オーナー裁定 2026-10-06「経路B の再索引の前に Voyage の鍵を Secret で結線する。ゼロ保持の認定〔#1740〕は未了のまま使うことを受け入れる」）

## 起点・関連

- 起点 issue: **#1764**。原因の調査は #1762（経路B の Qdrant が 3 コレクションとも 0 件。文書は 22,564 件）。
- 計画: ADR-0016（埋め込みの既定は Voyage・ティアB）・ADR-0095（秘密情報の投入は Vault を正とし画面から行う）・ADR-0127（高機密文書は埋め込まない）。
- 先例: [IADR-0096](./IADR-0096_vault-eso-secret-supply-k8s-auth.md)（`llm-provider-credentials` の Vault→ESO 供給）・
  [IADR-0433](./IADR-0433_bff-vault-write-scoped-to-sc22-items.md) / [IADR-0456](./IADR-0456_sc22-property-kinds-force-sync-reloader-and-seed-if-absent.md)（SC-22 の項目表・seed は無いときだけ）・
  [IADR-0494](./IADR-0494_eso-force-sync-after-bootstrap-writes.md)（bootstrap が書いた KV の同期を促す）・[IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md)（env は起動時に 1 度だけ解決）・
  [IADR-0256](./IADR-0256_embedding-degradation-vs-backend-failure.md)（埋め込みの縮退の扱い）。
- 作業仕様書: [`20261006_1764_voyage-key-wiring.md`](../specs/20261006_1764_voyage-key-wiring.md)

## コンテキストと課題

LlmGateway の `VoyageEmbeddingProvider` は鍵を `Embedding:Voyage:ApiKey`（既定は空）から読む。compose は `.env` の `VOYAGE_API_KEY` を渡すが、
helm（本番像の `values.yaml`・経路B の `values-local.yaml`）と ESO の `externalsecret-llm.yaml` はこの鍵を一切扱っていなかった。
そのため経路B の埋め込みは常に「鍵が未設定」の例外で失敗し、`public` / `internal` の文書は再試行の後 DLQ へ落ちて索引に入らない。

決めることは 4 つある: (1) 鍵をどの Secret・どの Vault のパス・どのプロパティ名に置くか、(2) secretKeyRef を必須にするか optional にするか
（＝鍵が無いときの起動の挙動）、(3) 既存の KV（プロパティを持たない）と ESO の同期をどう両立させるか、(4) SC-22 の画面で書ける項目に入れるか。

## 決定

1. **置き場所は既存の `llm-provider-credentials` に同居させる。** Secret `llm-provider-credentials`（名前空間 `microservices-platform`）のキー `voyage-api-key`、
   Vault の KV `secret/msp/llm-provider-credentials` のプロパティ `voyage-api-key`、ExternalSecret `llm-provider-credentials` の data に 1 行。
   - 理由: 性質が `anthropic-api-key` / `openai-api-key` と同じ（外部プロバイダの課金鍵・人が差し替える・相手と対で回す秘密ではない）。
     消費者も同じ LlmGateway である。新しい KV にすると、BFF の Vault policy（完全一致パス）・ExternalSecret・同期依頼の RBAC（resourceNames）・
     Reloader の注釈・`k8s-local-up.sh` の同期待ちと作り直しの列挙をすべて 1 つずつ増やすことになり、増やし忘れが静かに供給を切る。同居なら 1 行ずつで済む。
   - 名前はキー名の既存の作法（`<provider>-api-key`）に揃える。
2. **secretKeyRef は `optional: true` にする**（`Llm__ApiKey` は必須のまま変えない）。チャートの `extraEnv` / `extraEnvAppend` に `secretKeyRef.optional` を足し、
   指定した項目にだけ `optional: true` を描く（無指定の項目の描画は 1 バイトも変えない）。
   - 理由: 必須にすると、`voyage-api-key` を持たない既存の Secret（本番像で手で作った Secret・ESO の同期が失敗して古いまま残った Secret）で
     **LLM ゲートウェイ全体が `CreateContainerConfigError` で起動しなくなる**。埋め込みの鍵の欠落でテキスト生成まで止めるのは釣り合わない。
   - 鍵が無いときの挙動は**変えない**: env が無い（または空）→ `Embedding:Voyage:ApiKey` は空 → プロバイダは外部へ送らずに例外 →
     `EmbedUseCase` が `Embedded=false`・`Retryable=true` で応答 → 取り込みは再試行の後 DLQ。起動時の検査は足さない（鍵なしでもテキスト生成は使える）。
     この挙動は `VoyageEmbeddingKeyTests` が固定する。
3. **ESO の同期を壊さないために、Vault の種は `voyage-api-key` を「無いときだけ空で足す」。** ESO（Vault プロバイダ）は data[] のプロパティが KV に無いと
   ExternalSecret 全体を同期失敗にし、`anthropic-api-key` の差し替えまで届かなくなる。`bootstrap.sh` は KV を作るときに `voyage-api-key`（env `VOYAGE_API_KEY` か空）を持たせ、
   在る KV には `vkv_patch_nonempty`（env があれば差し替え）の後に `vkv_patch_if_missing ... voyage-api-key ''`（無ければ空で足す）を行う。
   在る値は書き換えない（IADR-0456 決定 6 と同じ）。`k8s-local-up.sh` は bootstrap を ExternalSecret の適用より前に走らせるので、新しい data は必ず在るプロパティを読む。
   ESO 無し（`ESO` 未設定）の手動 Secret も `voyage-api-key` を持つ。
4. **SC-22 の項目表（`deploy/bootstrap/sc22-secret-items.json`）の `llm-provider-credentials` の `properties` に `voyage-api-key` を足す。**
   分類（items[] / deferred[] / excluded[]）は動かさない（同じ項目の書けるプロパティが 1 つ増えるだけ）ので、IADR-0433 の改定には当たらない。
   policy・RBAC・ExternalSecret の名前はパス／名前単位なので変わらない。
5. **描画の検査を足す**（`scripts/helm-llm-provider-keys.test.js`・CI の `static-checks-units`）: 既定と values-local の両方で env が llmgateway-service にだけ
   secretKeyRef（optional）で 1 回現れ、リテラルの値を持たず、`Llm__ApiKey` が残ること。ExternalSecret のキー集合・項目表の properties・Vault の種・手動 Secret が揃うこと。変異で赤になること。

## 検討した代替案

| 案 | 採らない理由 |
| --- | --- |
| 専用の Secret / KV（例 `embedding-voyage`・キー `api-key`。運用仕様書の旧記述） | 決定 1 の理由。policy・RBAC・ExternalSecret・Reloader・同期待ちの列挙が 1 つずつ増え、片方の増やし忘れが静かに供給を切る |
| secretKeyRef を必須（`Llm__ApiKey` と同じ） | 決定 2 の理由。古い Secret のまま upgrade した瞬間に LLM ゲートウェイ全体が止まる |
| 鍵が無ければ Voyage のエンドポイントを自動で無効にする | 取り込みが「埋め込めない」を「索引しない（恒久）」と区別できなくなる（IADR-0256 の区別）。鍵を後から入れても無効のまま残る。現行の挙動（一時障害で DLQ）を保ち、手順書で再索引の前提として扱う |
| 起動時に鍵の有無を検査して落とす | テキスト生成まで止まる。readiness に入れても同じ（ゲートウェイ全体が外れる） |
| ExternalSecret を `dataFrom.extract` にして、無いプロパティを黙って飛ばす | KV の全プロパティが Secret へ出る（読み手の居ない鍵まで env の候補になる）。data[] の明示を崩す |

## 結果

- 良い: 経路B は鍵を Vault へ入れて同期させるだけで埋め込みが通る（手順は `docs/operations/voyage-embedding-key-runbook.md`）。値はリポジトリに無い。
  本番像のチャートも同じ参照を持つ（値は Vault / ESO 側で用意する）。
- 悪い: 鍵が無い環境は従来どおり「起動するが索引に入らない」で、静かに 0 件のまま動き得る。検出は手順書の確認（env の長さ・ゲートウェイのログ・コレクションの点数）と
  再索引の駆動スクリプトの DLQ 監視に頼る（自動の検知は足していない）。
- 中立: 運用仕様書の旧記述（`embedding-voyage` / `api-key`）は日付つきで改めた。

## 残るもの

- **Voyage のゼロ保持の認定は未了である（#1740）。** 本決定は鍵の配線だけで、認定の代わりにはならない。オーナーは経路B での使用を受け入れた（2026-10-06）。
- 鍵の欠落を自動で検知する仕組み（例: ゲートウェイの埋め込み失敗の率のアラート）は足していない。
- 本番像の Secret（`secret-templates.example.yaml` 由来の手作り）へ `voyage-api-key` を足すのは本番の go-live 側の作業である（無くても起動する）。
