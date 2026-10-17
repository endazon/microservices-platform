---
title: 計画 ADR-0127 に食い違う live 文書を追随させ（段 S4 の文書の追随）、語彙索引への書き込みに埋め込みの総枠と同じ時間の上限を掛ける（#1746）
type: spec
status: done
related_ids: [FR-02, FR-03, FR-05, UC-04, SC-02, SC-11, NFR-06, ADR-0127, ADR-0017, ADR-0016, ADR-0092, ADR-0027, IADR-0560, IADR-0497, IADR-0536, IADR-0478, IADR-0085, IADR-0025, IADR-0467, IADR-0422]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 1・2・5・6）
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-11.html（429 行。モックの文言が古い）
issue: "#1746"
---

# 仕様書: 段 S4 の文書の追随と、語彙索引への書き込みの総枠（#1746）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。判断の記録は **IADR-0560** に置く。
> 計画は project-planning の `main`（2026-10-17 に pull）の ADR-0127 を読んだ。実装の基点は MSP `origin/develop` `7fce8972`。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取り込み）・FR-03（横断検索）・FR-05（ABAC）
- ユースケース（UC）: UC-04（取り込み）
- 画面（SC）: SC-11（構成ビューア。モックの文言）・SC-02（検索の並び。前提として）
- 非機能: NFR-06（取り込みの可用性。待ちの点の経路）
- 関連 ADR: ADR-0127（決定 1: 高機密は埋め込まず語彙索引だけ／決定 5: ADR-0017 を置き換え、セルフホストは opt-in で残す／決定 6: nDCG は「全文のみ＋Claude」対「ハイブリッド（voyage）」）・ADR-0017（Superseded by ADR-0127）・ADR-0027（Wolverine の受け口）
- 関連 IADR: IADR-0497（語彙索引。§残るもの「巨大な高機密文書の打ち切りが無い」）・IADR-0536（待ちの点。同じ残余リスクの拡大）・IADR-0478（受け口の期限と総枠）・IADR-0085（Ruri の配備物）

## 目的・背景

#1746 の残りのうち 2 点を 1 PR で行う。

- **(b) 段 S4 の文書の追随。** ADR-0127 は Ruri v3（ADR-0017）を置き換え、高機密文書（`confidential`・`restricted`・未指定・未知）を埋め込まずに語彙索引へだけ載せると決めた。
  S1（IADR-0497）で多くの live 文書に 2026-10-05 の追記が入ったが、**「Ruri v3（高機密）」「高機密は Ruri のコレクションを束ねないと検索されない」「Ruri の nDCG が voyage 比で劣化しないこと」**など、
  追記の無い記述が残っている（例: `docs/screens/SC-11_configuration-viewer.md:116`）。
- **(c) 語彙索引への書き込みの打ち切り。** IADR-0497 §残るもの（186 行）は「語彙索引への書き込み（`IndexLexicallyAsync`）は埋め込みの総枠（`EmbeddingBudget`）を通らず、
  チャンク数に比例する Qdrant の書き込みを受け口の実行期限（既定 420 秒）だけが上から抑える」と記録した。IADR-0536（120 行）は、`public` / `internal` の待ちの点の書き込みにも同じことが起きると記録した。
  実際に起こる経路を実測・計算で評価し、打ち切りの要否を IADR で決める。

## 対象範囲

- 対象:
  - (b) 規則 9 で引いた母集合のうち、ADR-0127 と食い違う live 文書（`docs/`・`perf/`・`deploy/` の注記・`scripts/` の注記）。
  - (c) `DocumentUpdatedConsumer` の語彙索引の 2 つの書き込みの輪（高機密の `IndexLexicallyAsync`・待ちの点の輪）、`EmbeddingBudgetDeadLetterPolicy`、`IngestionTimeouts`、試験、IADR-0560、テスト仕様書、運用仕様書。
- 対象外:
  - `.ai-context/` の凍結記録の本文（IADR-0497 / IADR-0536 へは日付つきの追記ブロックで後継を指すだけ。#580 の作法）。
  - 計画のモック（`project-planning` の `05_screens/mockups/*/sc-11.html`）の是正（計画側の引き受け。下の「計画書との差異」）。
  - Ruri のコレクション（`knowledge_chunks_ruri_v3`）を取り込みが今も作る件（IADR-0497 §残るもの 189 行「片付けは段 S4」）。本 PR の担当 2 点の外（報告に残す）。
  - 段 S3（nDCG と遅延の計測）・段 S5（再順位付けの有効化）。

## (b) 母集合の引き方と結果（規則 9・10）

### 引き方

誤りの側の文字列で、追跡下の全ファイルを走査した（`.ai-context/` を除く。基点 `7fce8972`）。

```console
git grep -n -i -E 'ruri' -- ':!.ai-context'                       # 51 ファイル
git grep -n -E '高機密' -- ':!.ai-context'                          # 62 ファイル
git grep -n -i -E 'ruri|高機密|セルフホスト|ティア ?A' -- '*.md' ':!.ai-context' ':!CHANGELOG.md'
git grep -n -i -E '(高機密|confidential).{0,40}(ruri|ティア ?A|セルフホスト)|(ruri|ティア ?A|セルフホスト).{0,40}高機密' -- src scripts ':!*/Tests/*' ':!*.test.js'
```

### 是正する（ADR-0127 と食い違い、追記が無いもの）

| # | 箇所 | 食い違い | 是正 |
| --- | --- | --- | --- |
| 1 | `docs/screens/SC-11_configuration-viewer.md:116`（対応表 #11） | モックの文言「Ruri v3（高機密）」を引いたまま、改まったことが読めない | 備考に日付つきで「モックの文言は古い。高機密文書は埋め込まない。画面は構成が返すポートをそのまま出し、モックの文言は再現しない。モックの是正は計画側」を足す。trace ブロックに ADR-0127・#1746 を足す |
| 2 | `docs/security/security.md:688`（脅威の表「検索クエリ文の外部埋め込み API への送信」） | 「［2026-09-26 更新］高機密（ruri/768）コレクションは検索が束ねて読む」——高機密の文書は Ruri のコレクションに入らない | 日付つきの追記で改まったことを書く（語彙索引はクエリを埋めない） |
| 3 | `docs/security/security.md:708-709`（未決事項） | 「高機密コレクションを引くときだけセルフホストで埋める」と確定し、検索は高機密コレクションを束ねる | 同上 |
| 4 | `docs/operations/operations.md:1177-1183` | 「検索側も同時に束ねる: 検索サービスが Ruri のコレクションも読まないと、高機密の文書は索引されるが検索されない」 | 日付つきの追記。束ねる設定は残る（セルフホストへ回った `public` / `internal` の点のため）が、高機密文書の理由は消えた |
| 5 | `docs/operations/operations.md:1185-1187` | 「有効化後に nDCG を実測し voyage-3.5 比で大幅劣化しないことを確認する（計画 ADR が求める事前 PoC の代替）。劣る場合は BGE-M3 へ切替」——その計画 ADR は置き換えられ、フォローアップは対象が無くなった | 取り消し線と日付つきの追記。今の計測の対象は「全文のみ＋Claude の再順位付け」対「ハイブリッド（voyage）」 |
| 6 | `perf/ndcg/README.md`（冒頭・前提 3・束ね方の節・結果の記録・制約） | 「ADR-0016 / ADR-0017 は埋め込みモデルの確定を PoC に委ねている」「#336 の受け入れ基準」「#336 は実測完了まで OPEN」「劣化時は BGE-M3 へ切替」「高機密の文書にだけ付いた正解ラベルは束ねた run で…」——#336 は not planned で閉じ、ADR-0017 は置き換えられた | 冒頭に日付つきの注記（測る対象が ADR-0127 決定 6 に改まった・Ruri の A/B は opt-in の参考）。#336 の受け入れ基準を引く行に取り消し線と注記。束ね方の節の「高機密の文書」の読み方に注記 |
| 7 | `scripts/measure-search-ndcg.js:8-12`（冒頭の注釈） | 6 と同じ（背景が ADR-0017 の PoC のまま） | 日付つきの注記を 1 段足す（コードは変えない） |
| 8 | `deploy/docker-compose.yml:788-789` | 「与えないと高機密の文書は索引されるが検索されない」——同じ塊の上に 2026-10-05 の追記はあるが、この行は単独で読むと誤り | 「高機密の文書」を「セルフホストへ回った文書」に直し、日付を付ける |

### 是正しない（除外理由）

| 箇所 | 除外理由 |
| --- | --- |
| `.ai-context/`（IADR・作業仕様書・superpowers） | 凍結記録。本文を書き換えない（IADR-0497 / IADR-0536 は (c) の後継を指す日付つき追記だけ） |
| `CHANGELOG.md:203` | 生成物。当時のコミット件名の記録であり誤りではない |
| `docs/api/openapi.yaml:47` | 生成物（手で書き足さない）。「高機密は fail-closed」はゲートウェイの越境判定の記述として今も正しい（二重の守り。IADR-0497 決定 2） |
| `docs/functional/FR-03_hybrid-search.md:109` | 同じ節の 132 行に 2026-10-05 の追記があり、改まったことが読める |
| `docs/functional/FR-03_hybrid-search.md:122・229`、`docs/tests/FR-03_hybrid-search.md:149`（T-88） | セルフホストのコレクションを束ねる仕組みそのもの（opt-in）の記述。今も正しい |
| `docs/operations/operations.md:1063・1151・1165・1194-1195・1250・1256・1352` | opt-in の配備物（Ruri のコレクション・A/B の切り替え・起動時の作成・点検）の説明として正しい。1063 は 2026-10-05 の表で改まったことを書いている |
| `docs/security/security.md:682` | 2026-10-05 の追記があり、改まったことが読める |
| `docs/functional/FR-11_llm-egress-routing.md`・`docs/screens/SC-08_*.md`・`docs/tests/SC-08_*.md`・`docs/tests/UC-02_*.md`・`docs/tests/FR-16_*.md`・`docs/tests/FR-11_*.md` | **LLM（回答生成・分析）の越境**の記述（セルフホスト LLM）。ADR-0127 は埋め込みの経路を改めたが、LLM の越境マトリクスの値は改めていない |
| `docs/tech/*.md`・`docs/operations/object-storage-seaweedfs-cutover-runbook.md` | プロバイダの一覧・`--set` の例。opt-in のセルフホストが在る記述として正しい |
| `docs/functional/FR-02_ingestion.md`・`docs/tests/FR-02_ingestion.md`・`docs/operations/voyage-embedding-key-runbook.md`・`docs/functional/FR-01_*.md`・`FR-04_*.md` | ADR-0127 の後の記述（語彙索引）。食い違いは無い |
| `deploy/` の他の注記（`docker-compose.yml:504・767-771・780-785`、`helm/.../deployment.yaml:314-319・382`、`embedding.yaml:1-5・36`、`values.yaml:704・1231-1246`） | ADR-0017 の引用は「Superseded by ADR-0127」を併記済み（#580）で、塊ごとに 2026-10-05 の追記がある。値の説明（Ruri の配線・コレクション名）は opt-in の配備物として正しい |
| `src/platform/backend/Services/LlmGateway/**`（`EmbeddingEgress.cs`・`EmbeddingRouter.cs`・`EmbeddingRoutingOptions.cs`・`SelfHostedEmbeddingProvider.cs`・`Program.cs`）・`Platform.Shared.Contracts/Dtos/EmbedDto.cs` | ゲートウェイの越境判定（`confidential` / `restricted` はティア A だけ）の記述。判定は残っており（取り込みの分岐と二重の守り）、正しい |
| `src/knowledge/**` のコード・試験の Ruri（`EmbeddingCollectionsOptions`・`FusedCollections`・試験の値） | opt-in の構成値・試験の材料。食い違いは無い（取り込みが Ruri のコレクションを作る件は対象外に書いた） |
| `scripts/republish-document-updated.js:830`・`scripts/k8s-local-up.test.js`・`scripts/scripts.repo.test.js` | 点検のコマンド・配線の静的検査（opt-in の値の一致）。正しい |
| `scripts/verify-tool-oidc-logins.sh` | `ruri` は変数名（redirect URI）。無関係 |

### 規則 10（この変更で新たに誤りになる自分の記述）

- IADR-0497 §残るもの 186-187 行・IADR-0536 120 行の「打ち切りが無い」は、(c) の実装で誤りになる → 日付つき追記で IADR-0560 を指す。
- `IngestionTimeouts.cs` / `DocumentUpdatedConsumer.cs` / `EmbeddingBudgetDeadLetterPolicy.cs` の注釈「埋め込みの総枠を通らない」「`embedding-budget` だけ」→ 書き直す。
- テスト仕様書 FR-02 の T-20・T-22 の「呼び出し先 `embedding-budget`」→ 新しい行（T-52・T-53）で語彙索引の総枠を足す（既存行は変えない）。
- 運用仕様書の「埋め込みの総枠」の説明: 走査では `EmbeddingBudget` を名指す運用文書は無い（`git grep -n -E 'EmbeddingBudget|embedding-budget' -- docs deploy`＝テスト仕様書だけ）。総枠の使い切りの運用の手当て（デッドレター・再発行で同じ結果）を語彙索引の節に 1 項目足す。

## (c) 評価（実測と計算）

### 1 文書のチャンク数

チャンク化（`MarkdownChunkingService`。見出しで分け、2048 文字を超える節は文で詰める・重なり 200 文字）の写しで数えた
（スクリプトは作業用。scratchpad の `chunk-count.js`）。

| 本文 | 文字数 | チャンク数 |
| --- | --- | --- |
| 日本語の地の文 1 MiB（直接投入の上限） | 349,544 | 190 |
| 英語の地の文 1 MiB | 1,048,580 | 556 |
| 英語の地の文 16 MiB | 16,777,243 | 8,891 |
| 1 行の節が並ぶ 1 MiB（`## 見出し\n本文の一行。\n` の繰り返し） | 458,752 | **32,768** |
| 見出しだけの 1 MiB（`# a\n` の繰り返し） | 1,048,576 | **262,144** |

本文の大きさの上限は**直接投入の 1 MiB（`DocumentBodyIntake.MaxBytes`）だけ**である。取り込みの読み手（`StorageDocumentContentReader`）は上限なしで読み、
変換・コネクタの経路にも大きさの上限は見当たらない（`git grep` で `Max*Bytes` を引いた）。チャンク数の上限も無い。

### 1 点の書き込みの所要時間（実測）

実機 Qdrant v1.18.1（本番と同じイメージ・同じ digest。手元の docker・4 コア）に、語彙索引と同じ形
（ベクトル無し・`text` multilingual 1..40・`text_ngram` prefix 1..2・属性のキーワード索引）のコレクションを作り、
取り込みと同じく **1 呼び出し 1 点・`wait=true`** で書いた（REST。scratchpad の `measure-lexical-upsert.js`）。

| 点の本文 | 点数 | 合計 | 平均 | p50 | p95 | p99 |
| --- | --- | --- | --- | --- | --- | --- |
| 日本語 2048 文字（＋2-gram） | 3,000 | 16.9 秒 | 5.6 ms | 4.8 ms | 10.4 ms | 14.6 ms |
| 数文字（見出しだけの節） | 10,000 | 49.7 秒 | 5.0 ms | 3.7 ms | 11.4 ms | 21.5 ms |

### 計算（平均 5 ms／点で見積もる。クラスタではメッシュとネットワークの分だけ遅い）

| 本文 | 書き込みの所要 | 受け口の実行期限 420 秒 |
| --- | --- | --- |
| 地の文 1 MiB（190〜556 点） | 1〜3 秒 | 収まる |
| 地の文 16 MiB（8,891 点） | 約 45 秒 | 収まる |
| 1 行の節が並ぶ 1 MiB（32,768 点） | 約 164 秒（10 ms／点なら 328 秒） | 収まるが総枠 300 秒を超え得る |
| 見出しだけの 1 MiB（262,144 点） | 約 1,311 秒 | **超える** |

**受け口の実行期限を超えると何が起きるか**（既定の構成）: 受け口の ct が立ち、取り消しとして落ちる → 共通の既定で 4 試行（2/10/30 秒の待ち）→ デッドレター。
1 回の配信が「4 × 420 ＋ 42 ＝ 1,722 秒」配信を握り（Wolverine の RabbitMQ の受信は Inline）、その間キューは塞がる。各試行は全コレクションから消してから
書き直すので、Qdrant への書き込みは 4 試行で約 4 × 84,000 点（420 秒 ÷ 5 ms）になる。**埋め込みの経路で同じ文書は、総枠 300 秒で 1 試行目にデッドレターへ入る**
（`EmbeddingBudgetDeadLetterPolicy`）。語彙索引の経路だけが、同じ文書について約 5.7 倍の時間キューを塞ぎ、約 4 倍書く。

**起こる経路**: 1 MiB の直接投入（利用者が書ける）・変換やコネクタから来る大きな本文。見出しが密な本文（変換した表・ログ・目次の長い文書）は、地の文よりチャンクが多い。
高機密の文書と、鍵の無い間の `public` / `internal` の文書（待ちの点）が語彙索引の経路を通る。鍵の無い PoC では全文書がこの経路である。

→ **打ち切りは要る**（IADR-0560 で決める）。

## 設計（(c)）

- **語彙索引への書き込みの輪にも総枠を掛ける。** 値は埋め込みの総枠と同じ `Ingestion:EmbeddingBudgetSeconds`（既定 300 秒）を使う。
  新しい構成キーは足さない —— 受け口の最悪の所要時間の式（`EnsureFits`。削除＋本文＋総枠＋最後の 1 チャンク）が変わらず、起動時の検査がそのまま効く。
- **呼び出し先の名前は `lexical-budget` を足す**（計器 `messaging.consumer.timeout` の `target` で埋め込みと分けて読める）。
  `EmbeddingBudgetDeadLetterPolicy` は `embedding-budget` と `lexical-budget` の両方をデッドレターへ直行させる。
- 判定は書き込みの**前**（埋め込みの総枠と同じ。超過は最後の 1 点の期限までに収まる）。
  - 高機密の輪（`IndexLexicallyAsync`）: 輪の始めから測る。
  - 待ちの点の輪: **埋め込みの輪の始め（`embeddingStarted`）から続けて測る**（1 文書のチャンクの輪全体で 1 つの総枠）。使い切ったら、それまでに書いた待ちの点を計器に数えてから投げる。
- 使い切った文書は、それまでに書いた点を残してデッドレターへ入る（埋め込みの総枠と同じ。完了イベントは出さない）。再発行は同じ結果になる —— 運用は総枠と受け口の期限を延ばすか、本文を分ける。
- メタデータ点（1 点）・埋め込みの輪そのものは変えない。

## 受け入れ基準

- [x] (b) 規則 9 の母集合と除外理由を本書に書いた（上）。是正 8 件を入れた。`.ai-context/` の凍結記録の本文は書き換えていない。`docs/` は trace ブロック規約を守る。
- [x] (c) 実際に起こる経路を実測・計算で評価し、要否を IADR-0560 に残した。
- [x] (c) 高機密の文書の語彙索引への書き込みは、総枠を使い切ったら残りを書かずに `lexical-budget` の時間切れとして投げ、完了を出さない（T-52）。
- [x] (c) 待ちの点の書き込みも、埋め込みの輪から続けて同じ総枠で打ち切る。書いた待ちの点は計器に数える（T-52）。
- [x] (c) 総枠に収まる文書は全点を書き、完了を出す（T-52 の対照）。
- [x] (c) `lexical-budget` の使い切りは再試行せずデッドレターへ入る。本番の Program.cs の配線でも同じ（T-53）。
- [x] `dotnet build` / `dotnet test`（knowledge の slnx）・`dotnet format --verify-no-changes`・文書系の検査が通る。

## テスト方針

- `IngestionTimeoutTests`（偽の時計）: 書き込み 1 回ごとに時計を進める店で、高機密の文書（10 節）と、埋め込みが 1 チャンク目で拒否される `internal` の文書を流す。
  総枠 3 秒で 3 点書いた後に `lexical-budget` で投げる。総枠 11 秒なら全点を書き完了を出す（対照）。判定を外す変異で赤になることを確かめる。
- `EmbeddingBudgetDeadLetterPipelineTests`: 本番の配線の受け口の規則が `lexical-budget` の `ConsumerTimeoutException` を `MoveToErrorQueue` へ送る。
  方針の述語から `lexical-budget` を外す変異で赤になることを確かめる。

## 計画書との差異

- 差異: あり（計画のモックの文言）。`project-planning` の `05_screens/mockups/hi-fi/sc-11.html:429` と `wireframe/sc-11.html:127` の「Voyage AI（既定）／Ruri v3（高機密）」が ADR-0127 と食い違う。
  実装はモックの文言を再現しない（構成が返すポートを出す）。**モックの是正は計画側の引き受け**であり、環流（planning への issue）が要る。既存 issue（planning#718・planning#719 ほか）に同件が無いことを確かめ、planning#807 で環流した（2026-10-17）。

## 未決事項

- なし（Ruri のコレクションの片付けは本 PR の対象外。報告に残す）。
- ［2026-10-17 追記 / #1991］総枠（`embedding-budget` / `lexical-budget`）の使い切りの警報は未整備。PR #1989 の AI レビューの指摘を受け、本 PR では受容して #1991 へ切り出した。
