---
title: 作業仕様書 — #997（形5根治）直後に残った `DockerFact`/`BrokerFact` 陳腐化参照の是正 と IADR-0238 への `remaining` 限界の明記
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0231
  - IADR-0238
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-946_dockerfact-root-fix.md"
issue: "#946"
---

# 作業仕様書 — #946 残作業（陳腐化参照 4 箇所の是正 ＋ IADR-0238 への明記）

## 背景

issue #946 の最終コメント（2026-08-22 13:25:44 UTC）が、#997（`DockerFactAttribute` /
`BrokerFactAttribute` の撤去。作業仕様書 [`20260822_issue-946_dockerfact-root-fix.md`](20260822_issue-946_dockerfact-root-fix.md)）
の**残件**として、属性名を指したまま残った参照 4 箇所を明記した。加えて issue 本体（裁定コメント）は
「`remaining: 0` は『アナライザが見る範囲に限る』」という限界を `IADR-0238` へ明記するよう求めている。

本仕様書は、その 2 点（(a) 4 箇所の是正、(b) IADR-0238 への文言追記）を実施した記録である。

## 母集合の引き方（`traceability.repo.md` 規則 9・10 に従う）

**軸 1（主軸）**: `grep -rn "DockerFact\|BrokerFact" .`（`.git/` を除く。拡張子・パスで絞らない）。

```
$ grep -rn "DockerFact\|BrokerFact" . 2>/dev/null | grep -v "^\./\.git/" | wc -l
43   # ← 是正前（このセッション開始時点）
```

**軸 2（表記ゆれ）**: 空白入り・全角カナ変換・クラス名直書きの別形を疑い、
`Docker\s*Fact|Broker\s*Fact|ﾄﾞｯｶｰﾌｧｸﾄ|DockerFactAttribute|BrokerFactAttribute` で再走査。
→ **ヒットしたファイル集合は軸 1 と同一（22 ファイル、是正後）**。新規の表記ゆれは見つからなかった。

**軸 3（表明メッセージの言い回し違い）**: issue コメントが名指しした `WolverineBrokerEdgeTests.cs:36`
以外にも同種の言い回し（「〜が走った以上」等）が無いか、`走った以上|走った上で|BrokerFact が` で走査。
→ **ヒットは対象の 1 行のみ**（他に同型の表明メッセージなし）。

軸を 3 本使っても対象の総数（是正前 43 件）は変わらず、**issue コメントが挙げた 4 箇所と一致**した。
以下、43 件を「是正する 4 件」と「是正しない 39 件」に分解し、後者は理由ごとに分類する。

## 是正した 4 件（本 PR の変更）

issue コメント（2026-08-22 13:25:44 UTC）が名指しした 4 箇所と完全一致。実ファイルを開いて該当行を確認した
（`Fixtures/DockerRequired.cs` / `Fixtures/BrokerRequired.cs` 内の言及は除外——後述）。

| # | ファイル:行 | 内容 | 種別 |
| --- | --- | --- | --- |
| 1 | `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Messaging/WolverineBrokerEdgeTests.cs:36` | `Should().BeTrue("BrokerFact が走った以上…")` | 表明メッセージ（失敗時に存在しない属性名が出る） |
| 2 | `src/knowledge/backend/Tests/Knowledge.IntegrationTests/DataSourceService/DataSourceSyncSingleWriterTests.cs:13` | コメント「Docker 不在時は `[DockerFact]` がスキップする」 | コード内コメント |
| 3 | `src/knowledge/backend/Services/DataSourceService/tests/DataSourceService.Api.Tests/DatabaseConnectorTests.cs:17` | コメント「統合テスト（DockerFact・follow-up）で確認する」 | コード内コメント |
| 4 | `src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Migrations/20260809123339_MigrateTagsToIdentifiers.cs:22` | doc コメント「統合テスト（`Knowledge.IntegrationTests`、`[DockerFact]`）に置く」 | XML doc コメント |

是正方針: **属性名を、#997 で実際に置き換わった現行の呼び出しへ言い換える**
（`DockerFactAttribute` → `DockerRequired.SkipUnlessAvailable()` / `BrokerFactAttribute` →
`BrokerRequired.SkipUnlessObtainable()`）。表明メッセージ（#1）は「ガードを通過した以上」という
意味を保ったまま呼び出し名だけ差し替えた。コメント（#2〜4）は同様に呼び出し名で言い換えた。

是正後、`grep -rn "DockerFact\|BrokerFact"` の総数は **43 → 39**（4 減）。

## 是正しない 39 件（母集合から除外。理由を明記）

### (A) `Fixtures/DockerRequired.cs`・`Fixtures/BrokerRequired.cs` 内の言及 — 3 件

```
Fixtures/BrokerRequired.cs:11  // 🔴 以前は `BrokerFactAttribute : FactAttribute` として…
Fixtures/DockerRequired.cs:7   // 🔴 以前は `DockerFactAttribute : FactAttribute` として…
Fixtures/DockerRequired.cs:9   // 検査しない**（#946 形 5。`[DockerFact]` → `[Fact]` へ…）
```

**除外理由**: issue コメント（2026-08-22 13:25:44 UTC）が明示的に「経緯の記録として意図的に残した」と
述べている。「以前は〜だった」という過去形の経緯コメントであり、現在の属性名を名乗ってはいない。
陳腐化（誤った現状主張）ではなく正しい履歴記述なので対象外。

### (B) `.ai-context/specs/*.md` — 25 件（11 ファイル）

`20260719_issue-305_...` / `20260821_ci-pr-latency.md` / `20260822_issue-946_dockerfact-root-fix.md`
（9 件・#997 自身の記録） / `20260821_issue-455_xunit-v3-migration.md` /
`20260710_issue-217_wiki-connector.md` / `20260822_issue-441_edge-rawdocumentfetched.md`（5 件） /
`20260822_issue-455_wolverine-broker-integration-harness.md` /
`20260627_FR-01_data-source-catalog-pipeline.md` / `20260818_issue-863_adr-0038-fallback-order-and-429.md` /
`20260710_issue-219_database-connector.md`（2 件） / `20260809_issue-635_tag-identity-migration.md`（2 件）。

**除外理由**: `traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」節が
明記するとおり、**確定済みの `.ai-context/specs/` は書き換えない**（本文プロズを後から書き換えない
という `.ai-context/` の凍結原則、CLAUDE.md 冒頭）。これらは作業当時の状態を正しく記録した凍結記録
であり、当時 `DockerFactAttribute` / `[DockerFact]` が実在したことの記述として**書かれた時点では
誤りではない**。`20260822_issue-441_edge-rawdocumentfetched.md:849` はむしろ**この陳腐化そのものを
issue #997 の担当へ申し送る**表として書かれており、追記の対象ではなく参照すべき経緯である。

### (C) `.ai-context/adr/*.md`（`IADR-0238` を除く） — 6 件（4 ファイル）

`IADR-0237_broker-integration-harness-detection-power.md`（2 件） /
`IADR-0055_database-connector-readonly-sql-mapping.md`（2 件） /
`IADR-0153_tag-identity-storage-and-projection.md`（1 件） /
`IADR-0231_xunit-v3-simultaneous-switch.md`（1 件）。

**除外理由（本作業の権限外）**: 本 issue の担当指示は
「`.ai-context/adr/IADR-0238*`・xUnit1051 の陳腐化参照があるテストファイル・自分の作業仕様書**以外を
触らないでください**」と明記しており、他の IADR への書き込みは許可されていない
（並行して他エージェントが別 issue でリポジトリの別領域を担当中）。
これらは `DockerFactAttribute` を「当時の設計」として記述しており、#997 以降は内容が古くなっている
可能性があるが、**是正には該当 ADR への追記が要り、本 issue の許可範囲外**である。
残件として本仕様書の「申し送り」に記録し、独立 issue での対応を提案する。

### (D) `docs/*.md` — 5 件（4 ファイル）

| ファイル:行 | 内容 | 現状との整合 |
| --- | --- | --- |
| `docs/data/document-and-version.md:189` | 「`TagIdentityMigrationTests.cs`（`[DockerFact]`）が」 | 🔴 **検証: 実ファイルは既に `[Fact]`（53, 146 行目）。事実として誤り** |
| `docs/tests/FR-09_abac-attribute-policy-management.md:155` | 同上を名指し | 🔴 同上、事実として誤り |
| `docs/tests/FR-01_data-source-catalog.md:40` | 「コンテナ非利用環境では `DockerFact` によりスキップされる」（一般的な言及） | 属性名の用語としては陳腐化（実装は `DockerRequired.SkipUnlessAvailable()`） |
| `docs/tests/FR-06_document-crud-versioning.md:79` | 「統合テストは…実行（`DockerFact`）」（一般的な言及） | 同上 |
| `docs/functional/FR-01_data-source-catalog.md:90` | 「実 PostgreSQL 統合テスト（DockerFact）で確認する follow-up」（一般的な言及） | 同上 |

**除外理由（本作業の権限外＋別工程）**: `docs/` は CLAUDE.md が定める「人が読む生きた文書」であり
凍結記録ではないため、(B) と同じ理由では除外できない —— 特に上 2 件は**現在の事実として誤り**であり
本来是正すべきである。しかし、
1. 本 issue への担当指示が触ってよい範囲を「`IADR-0238*`・xUnit1051 の陳腐化参照があるテストファイル・
   自分の作業仕様書」に限定しており、`docs/` はいずれにも該当しない。
2. `docs/` 配下の編集は本リポジトリの trace ブロック規約（表示テキストへ計画 ID 等を書かない・
   frontmatter 直後の trace ブロックへ入れる）に従う必要があり、テストコードの是正と同じ手順では
   済まない別工程である。

以上により**本 PR では触らず、残件として明記する**（下記「申し送り」）。

## 実施した是正（差分）

4 ファイルの該当行のみを変更。`DockerFactAttribute` / `BrokerFactAttribute` という**削除済みの
クラス名**を、#997 で実際にそこへ置き換わった**現行の呼び出し**（`DockerRequired.SkipUnlessAvailable()` /
`BrokerRequired.SkipUnlessObtainable()`）へ言い換えた。コードの実行内容（アセンブリ生成物）は
変わらない —— コメント・表明メッセージの文字列のみの変更である。

## IADR-0238 への追記（(b)）

`IADR-0238_xunit1051-staged-adoption-ratchet.md` 決定 3 に、日付つき追記ブロック
`［2026-08-23 追記 / #946］` を追加し、**`remaining: 0` の意味は「アナライザが見る範囲に限る」**
（＝ `[Fact]`/`[Theory]` が付いたメソッド本体のみ）ことを明記した。`updated:` を `2026-08-23` へ前進。
根拠は #946 の実測（ラムダ／ローカル関数／private ヘルパの計 9 箇所が未移行のまま残ることが
#979 の対照実験で再現確認済み）。

## 検証

```bash
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /home/user/microservices-platform/src && dotnet build platform/backend/backend.slnx
cd /home/user/microservices-platform && node scripts/check-doc-links.js && node scripts/check-cross-repo-refs.js
```

結果は本体レポート（PR 説明 / 担当報告）に記載。**是正 4 件は `knowledge/backend` 側のファイルの
ため `platform/backend/backend.slnx` のビルドはそもそも対象を含まない**。knowledge 側のビルド確認は
別途 `dotnet build knowledge/backend/backend.slnx` で実施した（下記「受け入れ基準」参照）。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| issue が名指しした 4 箇所が是正される | ✅ 4 件とも該当行を確認し是正 |
| 母集合を軸 1 本で終わらせない（規則 5） | ✅ 3 軸（本体語 / 表記ゆれ / 表明メッセージ言い回し）で走査、対象数は変わらず |
| 除外はすべて理由を明記する（規則 6） | ✅ (A)〜(D) の 4 分類、ファイル単位で理由を記載 |
| IADR-0238 に `remaining` の限界（アナライザが見る範囲に限る）を明記 | ✅ 決定 3 へ日付つき追記 |
| ビルドが通る（knowledge/backend） | 実行結果は担当報告を参照 |
| `check-doc-links.js` / `check-cross-repo-refs.js` が通る | 実行結果は担当報告を参照 |

## 申し送り

- **(C) の ADR 4 本**（`IADR-0237` / `IADR-0055` / `IADR-0153` / `IADR-0231`）は、#997 以降
  `DockerFactAttribute` を過去形でなく現在の設計として書いている箇所が残る可能性がある。
  本 issue の権限では触れないため、独立 issue での棚卸しを提案する。
- **(D) の `docs/` 5 件**、特に `docs/data/document-and-version.md:189` と
  `docs/tests/FR-09_abac-attribute-policy-management.md:155` は**現在の事実として誤り**
  （`TagIdentityMigrationTests.cs` は既に `[Fact]`）である。`docs/` の trace ブロック規約に
  従った是正が必要だが、本 issue のスコープ外・許可ファイル範囲外のため未着手。独立 issue を推奨する。
- **`FactAttribute` 派生を新設しないこと**（#997 申し送りの継続）。新設するとその瞬間から
  同型の陳腐化リスクが再生する。
