---
title: doc_scope の可変性を「計画は述べていない」と書いた注記が腐っていたので、裁定済みとして引き取る
type: spec
status: done
related_ids:
  - FR-06
  - FR-13
  - FR-19
  - UC-07
  - ADR-0046
  - ADR-0054
  - ADR-0058
  - IADR-0278
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0058_doc-scope-immutability.md (決定 1・2・3)
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-wikijs-sync.md (D-01)
---

# 作業仕様書: 腐った「計画は述べていない」注記の引き取り（#449）

## 背景

#449（knowledge 文書管理・Wiki 閲覧の再実装）の残る 1 点は
「**`doc_scope` は文書の生涯で可変か。組織文書が個人資料になったとき、既に作られた Wiki.js ページはどうなるか**」
であり、2026-09-05 の棚卸しまで「計画へ環流すべき未決」として扱われていた。

🔴 **環流しようとして重複検索したところ、既に裁定済みだった。**

```console
$ gh issue list --repo endazon/project-planning --state all --search "doc_scope 可変"
planning#472 [CLOSED 2026-08-23] [裁定依頼] doc_scope は作成後に変更できるのか
```

裁定は **`ADR-0058`**（planning#477 マージ済み）である。

| 決定 | 内容 |
| --- | --- |
| 1 | **`doc_scope` は作成時に確定し、以後変更できない。** 移したい場合は移し先の `doc_scope` で新しい文書を作る |
| 2 | **更新経路は `doc_scope` の変更要求を拒否する** |
| 3 | **SC-05 の属性編集フォームで編集不可とする**（決定 2 と併せた二層） |

**実装も着地している。**

```console
$ git grep -n "ADR-0058\|DocScopeUnchanged" -- src/
DocumentService/Domain/DocumentAttributes.cs:62,72          ← ValidateDocScopeUnchanged
DocumentService/Features/Documents/DocumentEndpoints.cs:98,104
DocumentService/Features/Documents/Update/Endpoint.cs:32          ← 更新経路
DocumentService/Features/Documents/UpdateMetadata/Endpoint.cs:28  ← 更新経路
DocumentService/Tests/Features/Documents/DocScopeImmutabilityTests.cs:8
knowledge/frontend/.../sc05-documents/components/DocumentManagementPage.test.tsx:505  ← 決定 3
```

判定は**一致（`==`）で書かれており、3 つの抜け道が 1 本の規則で閉じている**
（`DocumentAttributes.cs:66-71`）—— **値の変更**・**既存値の削除**（落とすと組織文書へ化ける）・
**後からの新規付与**（決定 1 に反する）。

**したがって「組織文書が後から個人資料へ変わる」経路は構造的に存在しない。**

## 事象 —— 実装側に「計画は述べていない」と書いた注記が残っている

裁定から 13 日たっても、実装側の注記は「未決」のままである。
**この注記を読んだ人は「まだ未決」と判断する**（本棚卸しの直前まで、実際にそう判断していた）。

## 母集合（規則 9。誤りの側の文字列で走査した。記憶で挙げていない）

基点 `origin/develop` `b70dd07a`。

```console
$ git rev-parse --is-shallow-repository
false
```

### 軸 1 —— 「計画は述べていない」型の言い回し

```console
$ git grep -nE "doc_scope が(生涯で)?変わり得る|計画は述べていない|実装で決めず.*計画へ問う" \
    -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'
.ai-context/specs/20260822_issue-986_private-note-wikijs-sync-exclusion.md:89
docs/tests/FR-19_private-note-wikijs-exclusion.md:75
src/knowledge/backend/Services/WikiService/Features/Wiki/SyncDocument/DocumentSyncConsumer.cs:83,84
src/knowledge/backend/Services/WikiService/Tests/Features/Wiki/SyncDocument/DocumentSyncConsumerTests.cs:221
→ 5 行 / 4 ファイル
```

### 軸 2 —— 「既にあるページを消す／残る」型（規則 5: 軸を 1 本で終わらせない）

```console
$ git grep -nE "既に(ある|作られた).*ページ|ページを消す|孤児" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'
→ 軸 1 の 4 ファイルに加えて 8 件。うち「孤児」の 7 件は別事象
  （IADR-0245 の待ち行列 / IADR-0296 の再変換資産 / IADR-0298 の golden / #455 の .cs / #921 の走査）。
  残る 1 件は同じ凍結仕様書の :87-88 で、軸 1 と同一ファイル
```

### 軸 3 —— `ADR-0058` 申し送り 3（台帳を持たない `private-note` の棚卸し）

```console
$ git grep -nE "台帳を持たない|台帳の無い" -- . ':!CHANGELOG.md'
.ai-context/specs/20260823_planning-adr-0056-0058-followup.md:156
  「ADR-0058 フォローアップ 1（台帳を持たない private-note の棚卸し）—— 稼働 DB が要る」
```

**→ 既に「環境が要る」として記録済み。本 PR の射程外**（作り直さない）。

**陽性対照**（「4 ファイルしか無い」を「無い」と読む前に走査器が生きていることを確かめた）:
同じ走査範囲で `ADR-0058` は **34 ファイル**に当たる。

### 是正対象と除外理由

| # | 場所 | 種別 | 扱い |
| --- | --- | --- | --- |
| 1 | `DocumentSyncConsumer.cs:83-84` | live コード | ✅ **書き直す** |
| 2 | `DocumentSyncConsumerTests.cs:221` | live テスト | ✅ **書き直す**（テスト自体は消さない。後述） |
| 3 | `docs/tests/FR-19_private-note-wikijs-exclusion.md:75` | live 文書 | ✅ **書き直す**（🔴 表示テキストへ ID を書かず trace ブロックへ入れる） |
| 4 | `.ai-context/specs/20260822_issue-986_*.md:89` | **凍結記録** | ⚠️ **本文は書き換えない。**`［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記のみ（`traceability.repo.md` §凍結の射程が `.ai-context/specs/` に限り可としている） |
| 5 | `.ai-context/specs/20260823_planning-adr-0056-0058-followup.md:156` | 凍結記録・別事象 | ⛔ **触らない**（申し送り 3 は環境待ちとして既に正しく記録されている） |

**規則 10 の引き直し**（この変更で新たに誤りになる自分の記述）: 本 PR は「注記の意味を変える」だけで
新しい規約も導出値も作らないため、**新たに誤りになる記述は無い**。走査で確認した。

## 設計

### 🔴 消去の分岐は足さない

`ADR-0046` D-01 は「ページは作られない」と定めるが「既にあるページを消す」とは定めていない。
そして**遷移そのものが起こり得なくなった**以上、消去の分岐は
**起こり得ないケースへの防御的実装**であり CLAUDE.md が禁じている。

さらに悪い性質がある —— **上流の門（`DocScopeImmutabilityTests`）が回帰したとき、
消去の分岐は「気づかせずに証拠を消す」方向に働く。**

### テスト 6 は残す。意味を読み替える

`Consumer_LeavesExistingPage_WhenDocumentBecomesPrivateNote_CurrentBehaviour` は、
従前「計画が未裁定なので観測を固定するだけ」だった。**いまは二層目である** ——
第一層は上流の門、第二層が「上流が破れたときに WikiService が黙って挙動を変えないこと」。

**テスト名は変えない**（改名は追跡を切るだけで何も守らない）。コメントで意味を書く。

## 受け入れ基準

- [x] 是正対象 3 箇所（live）に「計画は述べていない」型の記述が残っていない
- [x] 凍結記録は本文を書き換えず、日付つき追記のみである
- [x] `docs/` の表示テキストに計画 ID / IADR / 仕様書名が現れず、trace ブロックへ入っている
- [x] **消去の分岐を足していない**（差分に `Delete` / `Remove` の新規呼び出しが無い）
- [x] テスト件数が減っていない（WikiService のテストは 1 本も消していない）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで成功する

## テスト方針

**新しいテストは足さない。** 本 PR は振る舞いを 1 バイトも変えないからである
（変えていないことは既存テストが全件緑であることで示す）。

機械検査は置かない。**「腐った注記」を機械で検出する検査器は、注記の意味を読む必要があり作れない。**
同型の事故（裁定が降りたのに実装側が気づかない）は上流ガイド §6 が
「**計画 pin の前進と裁定確認を定期の定型タスクにする**」として運用側で受けており、
**本件はその定型タスクが 1 回機能した実例**である（3 回再発の 4 例目にはしない）。

## 計画書との差異

- 差異: なし（計画は正しく、実装側の引用が腐っていた）

## 未決事項

- `ADR-0058` 申し送り 3（既存データに「台帳を持たない `private-note` 文書」が無いかの棚卸し）は
  **稼働 DB が要る**ため本 PR では実施しない。`.ai-context/specs/20260823_planning-adr-0056-0058-followup.md:156`
  に既に記録済みであり、**新たに記録を作らない**（同じことを 2 箇所に持たない）。
