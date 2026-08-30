---
title: UC-11 自分の資料を作成・管理し公開範囲を自ら設定する テスト仕様書
type: test-spec
status: completed
created: 2026-08-23
updated: 2026-08-30
author: Claude
---
<!-- trace:
ids: [FR-19, FR-20, FR-22, UC-11, SC-19, SC-20]
adrs: [ADR-0036, ADR-0037, ADR-0046, ADR-0054]
iadrs: [IADR-0270]
specs: [20260823_issue-451_private-note-obsidian-sync-core]
issues: [#451]
-->

# テスト仕様書: 自分の資料を作成・管理し公開範囲を自ら設定する

## テスト対象・範囲

個人資料ユースケースの基本フロー（作成 → 編集〔同期〕→ 公開範囲の設定 → 削除 → 復元／完全削除）と
例外フロー（容量上限・競合・トークン失効）。**機能別の写像はテスト仕様書 2 冊が正である**
（本書は重複させず、ユースケースの流れとテスト群の対応だけを持つ）。

| フロー | 写像先 |
| --- | --- |
| 作成・削除・復元・完全削除・容量 | [FR-19_private-notes-lifecycle](FR-19_private-notes-lifecycle.md) |
| 同期（編集）・トークン・競合 | [FR-20_obsidian-sync](FR-20_obsidian-sync.md) |
| 公開範囲（共有の付与・取り消し・再共有不可） | [FR-20_document-sharing](FR-20_document-sharing.md) |
| Wiki への非露出 | [FR-19_private-note-wikijs-exclusion](FR-19_private-note-wikijs-exclusion.md) |

## 実行

```bash
dotnet test src/knowledge/backend/Services/DocumentService/Tests
```
