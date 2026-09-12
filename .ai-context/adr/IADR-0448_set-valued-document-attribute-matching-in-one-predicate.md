---
title: IADR-0448 文書側の集合値属性（`shared_with` / `tags`）は契約側の唯一の述語 `AttributeFilterMatch` で交差判定し、BFF・Graph・Wiki の 3 面を同じ述語へ寄せる
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-19, ADR-0080, ADR-0098, IADR-0253, IADR-0385, IADR-0396, IADR-0447, IADR-0448]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0080_set-valued-user-attributes-and-match-semantics.md
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
related_specs:
  - ../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md
---

# IADR-0448: 文書側の集合値属性は契約側の唯一の述語で交差判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-05・FR-19／ADR-0080（決定 2 集合値のマッチは交差が空でないこと）／ADR-0098（§結果 フォローアップ 2 が名指し）
- 関連 issue: #1448（本 IADR）、#1447（IADR-0447。配線）
- 関連 IADR: IADR-0385（利用者側の集合値。カンマ連結の線上表現と `UserAttributeEncoding`）・IADR-0396（索引側の `shared_with` はリスト項目・`Match.Keywords`）・IADR-0253（決定 4 `Document.Attributes` の値型は変えない）
- 作業仕様書: `../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md`

## コンテキストと課題

`AttributeFilter` を文書属性と突き合わせる面は 4 つある。実測（2026-09-12・`origin/develop`）:

| 面 | 実装 | 集合値の扱い |
| --- | --- | --- |
| RetrievalService（Qdrant / InMemory） | `AttributeValueKeys.IsListValued` → `Match.Keywords` / `Any` | **交差** |
| BFF（`BffScopeResolver.MatchesAll`） | `AllowedValues.Contains(value)` | 🔴 単一文字列一致 |
| GraphService（`AbacNodeFilter.MatchesAll`） | 同上 | 🔴 単一文字列一致 |
| WikiService（`AbacPageFilter.MatchesAll`） | 同上 | 🔴 単一文字列一致 |

集合値の `shared_with` は後者 3 面で 1 件も一致しない。**同じスコープが面によって違う答えを出す**。3 面は互いを参照できない（`src/README.md` の依存規則）ため、述語を各面へ写すと食い違いをそのまま再生産する（IADR-0385 が利用者側で踏んだのと同じ形）。

## 検討した選択肢

| 案 | 判定 |
| --- | --- |
| 各面の `MatchesAll` に個別に集合値の分岐を足す | 採らない。3 面が別々に育つ |
| 値にカンマを含めば常に集合として読む | 採らない。単一値属性（`confidentiality` 等）の値にカンマが含まれる場合に意味が変わる。IADR-0385 決定 3 と同じ理由 |
| **契約側に語彙（集合値キー）と述語を 1 つ置き、3 面が委譲する** | **採る** |
| `Document.Attributes` の値型を `List<string>` にする | 採らない。IADR-0253 決定 4 が「値型は変えない」と定める。属性を読む全面の契約が変わる |

## 決定

1. **`Platform.Shared.Contracts.Dtos.DocumentAttributeEncoding`** に文書側の集合値キー（`shared_with` / `tags`）を置く。線上表現は `UserAttributeEncoding` と同じカンマ連結で、分割規則は `UserAttributeEncoding.Split` を再利用する（**表現を 2 つ持たない**。IADR-0385 決定 2）。`WithSharedWith(attrs, sharedWith)` は共有先を `shared_with` として重ねた読み取り用の像を返し、**元の辞書は変更しない・空集合は載せない**。
2. **`AttributeFilterMatch.MatchesAll / MatchesOne`** を同じ契約プロジェクトに置く。集合値キーは交差（`∩ ≠ ∅`。空集合は不一致）、単一値キーは値一致（大小文字無視）で**従前の判定を変えない**。
3. **`BffScopeResolver.MatchesAll`・`AbacNodeFilter.MatchesAll`・`AbacPageFilter.MatchesAll` の 3 か所は自前の判定を消し、`AttributeFilterMatch.MatchesAll` へ委譲する。** `Knowledge.Contracts.AttributeValueKeys.ListValuedKeys` は `DocumentAttributeEncoding.SetValuedKeys` を参照する（語彙を 2 つ持たない）。RetrievalService の判定は変えない（既に交差）。
4. 陽性対照をテストで固定する: 単一値の一致・不一致は不変、集合値は交差、空は不一致、3 面で同じ答え。

## 結果

- **良い影響**: 4 面が同じ意味論。`shared_with` 分岐が BFF・Graph でも効く（IADR-0447 の前提）。次に集合値キーが増えても、足す場所は 1 か所。
- **悪い影響 / 残余リスク**: `tags` を文書属性辞書の集合値キーに数えているが、`Document.Attributes` に `tags` が入る経路は現状無い（タグは `Tags` 列）。索引のリスト項目と綴りを揃えるために語彙へ含めた。将来 `tags` を属性辞書へ入れると、ここで集合として読まれる。
