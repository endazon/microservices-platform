---
title: DocumentSearch の許可集合を 1 つの値の構成で起動時に止め、DocumentRead / DocumentSearch の許可集合を共有の TrustedUserContextRelay へ寄せる（#1658）
type: spec
status: done
related_ids: [NFR-09, FR-03, FR-05, FR-19, ADR-0086, ADR-0119, IADR-0426, IADR-0476, IADR-0420]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・§結果（中継サービスが正直であることへの依存）
issue: "#1658"
---

# 仕様書: DocumentRead / DocumentSearch の許可集合を共有部品へ寄せる（#1658）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（全 API で文書・データ単位の認可）、FR-03（ハイブリッド検索）、FR-05（ABAC）、FR-19（個人資料）
- 関連 ADR: **ADR-0086 決定 1・§結果**（利用者文脈を本文で運ぶ／中継者が正直であることへの依存）、ADR-0119 決定 3（判定の主体）
- 関連 IADR: **IADR-0426 追記 1**（`DocumentSearch` の許可集合。本件の追記 2 を置く）、**IADR-0476 追記 1**（`DocumentRead` の許可集合。追記 2 を置く）、
  IADR-0420（`MachinePrincipal`）。新しい IADR は起こさない（判断の新設ではなく、既存の決定の実装の置き場を寄せるだけ）。
- 先行: #1628（PR #1631、`DocumentRead`）・#1635（PR #1638、`DocumentSearch`）・#1636 段 1（PR #1645、共有の `TrustedUserContextRelay` と 3 面）
- 起点 issue: #1658（PR #1645 のセキュリティ監査の非ブロッキング N2 と、同 PR の「残るもの」）

## 目的・背景

- PR #1645 は 3 面（AddTag・ExpandNeighbors・ListValues）の許可集合の判定・既定の解決・1 つの値の検査を共有の
  `TrustedUserContextRelay`（`Platform.Shared.Infrastructure`）へ置いた。先行 2 面は各サービスに同じ規則を写したまま残った。
- **`DocumentSearchRelayOptions` は 1 つの値の検査を持たない。** `DocumentSearch__TrustedUserContextClients=foo` は配列へ束縛されず、
  プロパティは null のまま既定の `aianalysis-service` へ静かに戻る。集合が最小なので閉じる側には倒れているが、
  運用者の意図した集合と実際の集合の食い違いが検知されず、#1631 / #1645 の規約（1 つの値は起動時に例外）と食い違う。

## 設計

1. `DocumentSearchRelayOptions` に `ThrowIfScalar(IConfiguration)` を足し、RetrievalService の `Program.cs` で
   **`ThrowIfScalar()` → `Configure<T>()` の順**に呼ぶ（`AttributeValues` と同じ形）。
2. `DocumentReadRelayOptions` / `DocumentSearchRelayOptions` の本体（`EffectiveClients`・`TrustsUserContextFrom`・`ThrowIfScalar`）を
   `TrustedUserContextRelay.Effective` / `Trusts` / `ThrowIfScalar` への委譲にする。面ごとの型に残すのは節名・既定・委譲だけ。
3. **挙動は変えない。** 判定（機械 ∧ 序数一致）・既定の解決（null なら既定）・置き換え（足し合わせない）・空白の扱い（前後を落とし空白だけの要素を捨てる。
   残らなければ誰も信じない）は、先行 2 面の実装と共有部品で同値である（両者の本体を並べて確かめた）。
   `DocumentRead` の 1 つの値の例外の文言も共有部品が同じ文字列を組み立てる（`DocumentRead__TrustedUserContextClients__0=bff`・「既定の bff へ戻ってしまう」）。
4. 公開面（`SectionName`・`DefaultTrustedUserContextClients`・`TrustedUserContextClients`・`EffectiveClients`・`TrustsUserContextFrom`・`ThrowIfScalar`）は変えない。
   呼び出し側（各 `GrpcService.cs`）は変えない。

## 受け入れ基準

- AC-1: `DocumentSearch:TrustedUserContextClients` を 1 つの値で構成すると `ThrowIfScalar` が例外を投げ、文言が配列の書き方
  （`DocumentSearch__TrustedUserContextClients__0`）を示す。配列の形・未構成は通る。
- AC-2: RetrievalService の `Program.cs` が `DocumentSearchRelayOptions.ThrowIfScalar` を `Configure<DocumentSearchRelayOptions>` より前に呼ぶ
  （DocumentService の `DocumentReadRelayOptions` も同じ順であることを併せて固定する）。
- AC-3: 既定・置き換え・空白の扱いが現行と同値（未構成なら既定だけ・構成は既定を置き換える・前後空白を落とす・空白だけの要素を捨てる・
  空白だけなら誰も信じない・大小文字と接頭辞の変種は信じない）。既存の `DocumentReadRelayOptionsTests` / `DocumentSearchRelayOptionsTests` と
  追加の複数要素の試験がそのまま通る。
- AC-4: `DocumentRead` の 1 つの値の例外は従前どおり起きる。
- AC-5: 変異（1 つの値の検査を素通し・`Program.cs` の呼び出しを落とす・判定を大小文字無視にする・既定の解決を足し合わせに変える）で試験が赤。
- AC-6: 既存の試験（`DocumentReadTrustedRelayTests`・`DocumentSearchTrustedRelayTests`・各 `*RelayDeploymentWiringTests`・`TrustedUserContextRelayTests`）が通る。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n -l -e RelayOptions -e TrustedUserContextClients -e TrustedUserContextRelay`（全追跡ファイル。コード・docs・deploy・helm・compose・realm・`.ai-context` を含む）
- `git grep --recurse-submodules ... -- src/ai-stock-trading`（submodule。`git submodule update --init` 後）
- `git grep -n -e 'DocumentSearch__' -e 'DocumentRead__' -e TrustedUserContextClients -- deploy docs '*.yml' '*.yaml' '*.json'`

### 結果

- 実装: `DocumentReadRelayOptions.cs`・`DocumentSearchRelayOptions.cs`（本件で変える）、`DocumentTagWriteRelayOptions.cs`・`GraphNeighborsRelayOptions.cs`・
  `AttributeValuesRelayOptions.cs`（既に共有部品へ委譲済み）、`TrustedUserContextRelay.cs`（共有部品。変えない）。
- 構成の登録: RetrievalService `Program.cs`（`DocumentSearch` に `ThrowIfScalar` が無い ⇒ 足す）、DocumentService `Program.cs`・GraphService `Program.cs`（順序済み）。
- 呼び出し側: 各面の `GrpcService.cs`・`DocumentReadPrincipal.cs`（公開面を変えないので不変）。
- 試験: 5 面の `*RelayOptionsTests`・`*RelayDeploymentWiringTests`、`TrustedUserContextRelayTests`。
- 配備: compose・helm・realm に `TrustedUserContextClients` の構成は無い（ヒット 0。既定のまま動く）。submodule（AST）もヒット 0。
- 文書: `docs/api/east-west-grpc.md`（`DocumentSearch` の節に「1 つの値で書くと起動時に止まる」を足す）、`docs/security/security.md`（構成の形は書いていない ⇒ 不変）、
  `docs/tests/FR-03_hybrid-search.md`・`docs/tests/FR-06_document-crud-versioning.md`（行を足す）、`scripts/test-spec-coverage-baseline.json`（`--update`）、
  `.ai-context/adr/IADR-0426`・`IADR-0476`（日付つき追記）、`IADR-0410`・`IADR-0417`（3 面の記録。変えない）、proto 3 本（コメントのみ。変えない）。

### 除外したもの

- `.ai-context/specs/` の過去の作業仕様書（#1636 の「残るもの」を含む）: point-in-time の記録で書き換えない。
- 並行中の PR #1660（#1646）が触るファイル（BFF・`AuthzScopeGrpcClient`・`UserDirectoryGrpcClient`・`QdrantVectorStore` と健全性の検査・
  DocumentService の `Grpc*Directory` / `GrpcDocumentReadScopeSource`）: 本件の母集合に入らない（ヒットしない）。DocumentService の `Program.cs` も変えない
  （順序は既に正しく、試験で読むだけ）。

## 配備の順番

- 構成の値が変わらない配備（helm・compose は既定のまま）には影響しない。
- **`DocumentSearch__TrustedUserContextClients=<値>` のように 1 つの値で書いていた配備は、retrieval-service が起動しなくなる**
  （従前は既定の `aianalysis-service` へ静かに戻っていた）。配列（`DocumentSearch__TrustedUserContextClients__0=<値>`）へ書き直してから配備すること。

## 残るもの

- なし（先行 2 面の写しは本件で解消する）。

## 検証

- `dotnet build`（knowledge・platform）、DocumentService.Tests・RetrievalService.Tests・Platform.Shared.Infrastructure.Tests 全件
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と各検査器
- 変異 2 件以上を当てて赤を確かめ、`git show HEAD:<path> > <path>` で戻す。

### 結果（2026-09-27・ローカル）

- `dotnet build src/knowledge/backend/backend.slnx` / `src/platform/backend/backend.slnx`: 0 エラー（knowledge の警告 1 件は既存の `IngestToSearchQdrantTests` の CS0618 で本件と無関係）
- DocumentService.Tests 817・RetrievalService.Tests 394・Platform.Shared.Infrastructure.Tests 465: 全件合格
- `dotnet format <slnx> --verify-no-changes`: 両ユニット exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 841 tests passed
- `check-trace-blocks` / `check-test-spec-coverage`（床の対 400 件は不変。`--update` で差分なし —— 行を足した 4 クラスは既に同じ仕様書から参照済み）/
  `check-test-traceability` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-commit-messages --range=origin/develop..HEAD`: OK
- 修正前の赤: develop の `DocumentSearchRelayOptions` に新しい試験を当てると `ThrowIfScalar` が無くコンパイルが通らない（CS0117）。
- 変異（`FullyQualifiedName~Relay` で絞って実測。どれも `git show HEAD:<path> > <path>` で戻し、`//MUT` の残りが 0 件であることを確かめた）:
  - M1 `DocumentSearchRelayOptions.ThrowIfScalar` を素通し: RetrievalService 1 件赤（1 つの値の試験）
  - M2 RetrievalService `Program.cs` から `ThrowIfScalar` の呼び出しを落とす: 1 件赤（`Program_csは束縛より前に一つの値の検査を呼ぶ`）
  - M3 共有の判定を大小文字無視（`OrdinalIgnoreCase`）: RetrievalService 6 件・DocumentService 4 件赤（`DocumentReadRelayOptionsTests` を含む）
  - M4 `DocumentSearch` の既定の解決を足し合わせに変える（構成 ∪ 既定）: 3 件赤（置き換え・空白だけ・複数要素）
  - M5 `DocumentReadRelayOptions.ThrowIfScalar` を素通し: 2 件赤（1 つの値の試験・文言の試験）
  - M6 `DocumentSearch` の判定を接頭辞一致・機械の確認なしに変える: 4 件赤
