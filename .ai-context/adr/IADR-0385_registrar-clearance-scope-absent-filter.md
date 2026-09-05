---
title: IADR-0385 認可スコープに `confidentiality` フィルタが無いことを「無制限」と読まず、軸ごとに許可の根拠を確かめる
type: impl-adr
status: Accepted
related_ids:
  - FR-16
  - FR-05
  - FR-09
  - UC-09
  - SC-12
  - ADR-0062
  - ADR-0036
  - ADR-0004
  - IADR-0366
  - IADR-0253
  - IADR-0373
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# IADR-0385: 認可スコープに `confidentiality` フィルタが無いことを「無制限」と読まず、軸ごとに許可の根拠を確かめる

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装セッション）

## 起点・関連

- 起点 issue: #1242（出所は #1185 のフェーズ末監査）
- 計画 ADR: `ADR-0062` 決定 2・3（部分集合の判定を後段が持つ）／`ADR-0036` D-01・D-02
  （`read` への所有者条件と `${current_user}` 束縛）／`ADR-0004`
- 計画技術文書: `06_technical/07_abac-attribute-model`（§ポリシー評価モデル・§選言の暫定統制）
- 関連 IADR: `IADR-0366`（本判定の置き場所と形。**決定 3 が本件の誤りの出所である**）／
  `IADR-0253`（認可スコープ契約の選言＝`Branches`）／`IADR-0373`（MCP の `project` 一律除外）
- 実装仕様書: `.ai-context/specs/20260905_issue-1242_registrar-clearance-scope-fail-open.md`

## コンテキストと課題

`AuthorizationServiceRegistrarAttributes` は「登録者が無人アカウントへ渡してよい `clearance` の
集合」を `POST /authz/scope`（action=read）の応答から読む。従前の読み方はこうだった。

```csharp
var filter = scope.AllowedFilters.FirstOrDefault(f => f.Key == "confidentiality");
return filter is null ? (true, []) : (false, filter.AllowedValues);
```

🔴 **`confidentiality` のフィルタが「無いだけ」で `ClearanceUnrestricted = true`（無制限）へ倒れる。**
`ServiceAccountAttributeSubset.Validate` は無制限のとき `clearance` の突き合わせを丸ごと飛ばすため、
**所有者ベースの `read` ポリシーだけにマッチする登録者が `restricted` の無人アカウントを作れる**。
`ADR-0062` が塞いだ昇格経路そのものである。

契約（`AccessScopeResponse`）が「条件無しで許可（全件可）」と定めるのは **`AllowedFilters` が空**の
ときだけであり、**`owner` だけを持つ**（空ではないが `confidentiality` を持たない）場合は含まれない。
従前のコードは**その 2 つを区別していなかった**。

**これは fail-safe の向きの取り違えである** ——「その軸のフィルタが無い」を「その軸では制約が無い」と
読んだ。正しくは「**その軸で許可する根拠が無い**」であり、deny 側へ倒す。

### 顕在化の条件（現 seed では発現しない）

`deploy/local/abac-seed/policies.json` の `read` ポリシーは 4 本とも
`userConditions: {clearance: […]}` × `documentConditions: {confidentiality: […]}` の階段であり、
所有者ベースは `write` の 1 本だけである。したがって read のスコープには必ず `confidentiality`
フィルタが載り、`filter is null` の枝へ入らない。**#1185 の稼働再測で `clearance=internal` の
管理者が `restricted` を拒否されたのはこのためであって、判定が正しかったからではない。**

`ADR-0036` D-01 が定める所有者ベースの `read` ポリシー（`userConditions` を持たない）が 1 本でも
入ると、`AbacEvaluator.MatchesUserConditions` が条件なしを**全利用者マッチ**として扱うため、
`Granted=true` かつ `confidentiality` フィルタ無しのスコープが実際に返る
（`AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter` が固定）。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) 従前どおり「フィルタ不在＝無制限」 | 変更しない | **採らない。** 潜在 fail-open。`ADR-0036` の read 所有者条件が入った時点で昇格経路が開く |
| (b) キー単位 union（`AllowedFilters`）から引き、**不在なら空集合**へ倒す | 1 行の反転 | **採らない。** `AllowedFilters` は**単一の連言しか表せない**（`IADR-0253` 決定 2 の反例）。`{owner, confidentiality}` の連言と「単独の confidentiality 条件」を union が区別できず、**所有権が混ざった値を配れてしまう** |
| (c) **`Branches`（選言）を分岐ごとに読み、単一キー `confidentiality` の分岐だけ数える** | `IADR-0253` が運んだ選言をそのまま使う | **採用** |
| (d) 階段表を実装側へ持ち、`clearance` から配れる集合を計算する | 評価器へ問い合わせない | **採らない。** `IADR-0366` 決定 2 が退けた形（計画が排した序数比較をコードへ再導入する） |
| (e) `ClearanceUnrestricted` を廃し、全値を列挙して返す | 型を単純化 | **採らない。** 値域の正がポリシーから実装の列挙へ移る（(d) と同じ帰結） |

## 決定

### 決定 1 — 読み方を 1 か所（`ReadAssignableConfidentiality`）へ閉じ、次の 3 段で読む

1. `Granted == false` → **空集合**（読めるものが無い＝配れるものも無い）。
2. `Branches` が 1 件以上 → **分岐ごとに**見る（分岐＝マッチしたポリシー 1 本の連言）。
   - フィルタを 1 つも持たない分岐 → **無制限**（計画 `07_abac-attribute-model` §ポリシー評価モデル
     「マッチしたポリシーに文書条件が無い場合は全件許可する」がここに当たる）。
   - フィルタがちょうど 1 つで、そのキーが `confidentiality` → **その許可値を足す**（分岐間は union）。
   - **それ以外の分岐は何も足さない。**
3. `Branches` が空／null（未移行の発行者。契約の後方互換規則） →
   `AllowedFilters` が**空**なら無制限、キーが `confidentiality` **ただ 1 つ**ならその許可値、
   それ以外（`owner` を含む等）は**空集合**。

### 決定 2 — 「その軸のフィルタが無い」は deny 側へ倒す。ただし「フィルタが 1 つも無い」は倒さない

🔴 **不在から「制約なし」を推論しない。** 一方で **`AllowedFilters` / 分岐のフィルタが空 ＝ 無制限は
残す** —— これは契約 `AccessScopeResponse` の明文であり、計画も「文書条件が無いポリシーは全件許可」と
定める。**ここまで deny へ倒すと、計画が許可と定めた形を実装が黙って狭める**（`ADR-0062` は
「配れない」の側の統制であって、計画の許可を実装が縮める権限は与えていない）。

**倒さない代わりに、形を変えた** —— 「見つからなかった」ではなく **`Count == 0` を条件に書く**。
不在を推論する枝を 1 本も残さない。

### 決定 3 — `confidentiality` と他キーの**連言**からは値を取り出さない

`{owner: u1, confidentiality: [restricted]}` は「**自分が持つ** restricted 文書を読める」であって
「restricted を読める」ではない。**サービスアカウントは登録者の所有権も部門も継がない。**
継がない条件が混ざった分岐から値を取り出すのは、`IADR-0366` 決定 3 が避けようとした誤りと同型である。

**過小に倒れうることは受容する。** `07_abac-attribute-model` は「消費側が選言へ対応するまで
**多キーの文書条件を持つポリシーを運用しない**」を暫定の統制として定めており、多キーの分岐は
運用上そもそも存在しない。**現 seed の階段ポリシーは 1 件も落ちない**（陽性対照で実測）。

### 決定 4 — 実行経路の一律除外（`ServiceAccountDocumentFilter`）とは独立の軸として二重に掛ける

本変更が効くのは**登録時の割当**だけである。`ADR-0034` 決定 9 の `private-note` と
`IADR-0373` の `project=ai-stock-trading` は**実行経路の後段**であり、どちらも変えない。
**登録者が無制限でも実行時の除外は外れない**ことを退行防止のテストで固定した
（緩い側の登録者で確かめる —— 割当が通る条件でなお除外が効くことを見ないと、
「割当で弾かれていただけ」を「除外が効いている」と読み違える）。

### 決定 5 — 解決器そのものの単体テストを新設する

🔴 **`AuthorizationServiceRegistrarAttributes` に対する単体テストは 1 本も無かった。**
経路は `StubRegistrarAttributeResolver`（ヘッダで集合を注入する）でだけ試験されており、
**「スコープをどう読むか」は 1 本も試験されていなかった**。判定の**入力を作る側**が試験されないと、
fail-open は緑のまま通る。本物の解決器に `HttpMessageHandler` をスタブして 14 本を置いた。

## 理由

- **(c) を採ったのは `IADR-0253` が既に選言を運んでいるからである。** 分岐の意味論（分岐内 AND・
  分岐間 OR）は「どのポリシーで許可されたか」を保っており、**射影（キー → 値集合）に必要な情報は
  分岐にしか無い**。`AllowedFilters` は据え置きの近似であり（`IADR-0253` 決定 2）、ここから射影すると
  混成を許す反例がそのまま昇格経路になる。
- **「1 件が分岐の連言を満たすか」を見る他の 5 経路は変えない。** それらではキーの不在は正しく
  「その文書には条件を課さない」である。**射影だけが「不在＝無制限」という別の意味を持ち込む**
  （仕様書 §母集合 B に走査と陽性対照）。

## 結果

- 良い影響:
  - **潜在 fail-open が塞がった。** `confidentiality` フィルタを持たない登録者は `restricted` を
    配れない（陰性対照 3 本）。
  - **変異試験で守られていることを実測した。** 旧実装へ戻すと 14 本中 5 本が落ちる。
  - **入力を作る側が初めて試験された**（決定 5）。
- 悪い影響・トレードオフ:
  - 🔴 **多キーの文書条件を持つポリシーの運用が始まると、本設計は過小に倒れる**
    （`{owner, confidentiality}` の連言を数えないため、所有者条件つきで運用された階段が
    「配れない」と読まれる）。暫定統制が生きている間は現実の欠落が無い。
  - `AllowedFilters` からの後方互換の読みが残る（未移行の発行者のため）。**`Branches` を運ばない
    発行者が消えたら、この枝ごと落とせる。**
- フォローアップ:
  1. **暫定統制「多キーの文書条件を持つポリシーを運用しない」の解除時に、本設計を見直す。**
     解除の際は「所有権・共有先に依存しない分岐」を契約側で見分けられる形が要る。
     **今は planning へ環流しない** —— 暫定が生きている間は現実の欠落が無く、
     「将来こうなる」だけを送っても裁定材料にならない。
  2. **稼働 k3s での実測は未実施**（既存 seed に所有者 `read` ポリシーを足さずには再現できない）。

## 関連

- Supersedes: なし
- Superseded by: なし
