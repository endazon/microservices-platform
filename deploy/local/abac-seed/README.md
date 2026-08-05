# 経路B ABAC 初期投入（opt-in）

> 起点: [IADR-0133](../../../docs/adr/IADR-0133_abac-dev-seed.md) /
> 作業仕様書 [`docs/specs/20260805_issue-517_abac-dev-seed.md`](../../../docs/specs/20260805_issue-517_abac-dev-seed.md) / Issue #517

経路B（dev）へ **ABAC の属性辞書とポリシーの初期値**を投入するための宣言的データ。

## なぜ要るか

`AuthorizationService` は **ポリシーが 1 件も無いと deny-by-default で縮退する**（`AbacEvaluator`）。
これは仕様どおりだが、投入経路が一度も実行されていない環境では
**認証を通しても文書一覧・横断検索が常に空**になり、「壊れている」のと区別が付かない。

実測（#466 / #517）:

| 利用者 | 投入前 | 投入後 |
| --- | --- | --- |
| `developer`（`clearance=restricted`） | 0 件 | **2,467 件** |
| `poc-user`（`clearance=internal`） | 0 件 | **2,467 件**（実データは全件 `internal`） |
| `poc-operator`（`clearance` 無し） | 0 件 | **0 件**（ポリシー不一致＝deny） |
| 無トークン | 0 件 | **0 件**（同上） |

**投入後も「権限が無い利用者は 0 件」が保たれる**ことが要点である（全開放ではない）。

## 構成

| ファイル | 役割 |
| --- | --- |
| `attributes.json` | 属性辞書（`document` / `user` スコープ）。値集合は計画 [`07_abac-attribute-model`](../../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md) に合わせる |
| `policies.json` | ABAC ポリシー。`clearance` が高いほど読める `confidentiality` が広がる階段 |

`required` は**すべて `false`** にしてある。`/authz/attributes/validate` を呼ぶ取り込み経路は現時点で
存在しないが、将来 `required: true` を入れると属性を 1 つしか付けない既存の取り込みを落としうるため、
必須化は実データ側が属性を備えてから行う（**#516**）。

## 適用（opt-in・既定オフ）

```sh
ABACSEED=1 bash scripts/k8s-local-up.sh     # 起動時に投入する
node scripts/seed-abac-policies.js          # 稼働中のクラスタへ後から投入する（冪等）
node scripts/seed-abac-policies.js --dry-run # 何が入るかだけ見る（副作用なし）
```

- **冪等**。同じ `key`+`scope` の属性・同じ `name` のポリシーが既にあれば作成しない。
- 投入は **管理 API 経由**（`POST /authz/attributes` / `POST /authz/policies`）。**直 DB 書き込みはしない**——
  API 側の検証（`AbacValidation`）を素通りさせないため。
- 既定（`ABACSEED` 未設定）は**何も実行せず挙動不変**。**本番 values には一切影響しない**
  （投入先は経路B の稼働中サービスであって chart ではない）。

## 実運用との関係

本ファイルは **dev の初期値**である。実運用の属性辞書・ポリシーは **SC-09（管理者設定画面）から編集する**
（[FR-09](../../../planning/projects/microservices-platform/02_requirements/01_requirements.md) / UC-05）。
本番環境へ同じ値を入れる意図はない。

## 切り戻し

```sh
# 管理 API から削除する（SC-09 の画面からでもよい）
# DELETE /authz/policies/{id} ・ DELETE /authz/attributes/{id}
```

投入前の状態（ポリシー 0 件）に戻ると、再び deny-by-default で全員 0 件になる。

## 評価の意味論（読み違えないための注記）

`AbacEvaluator.ResolveScope` は **利用者条件を満たすポリシーすべての文書条件を union** する。
したがって `restricted` の利用者は 3 本の read ポリシーすべてにマッチし、許可される
`confidentiality` は 4 値の和になる。**序数比較は導入していない**ため
（計画「評価は集合帰属のまま」）、各段の許可集合を `policies.json` に明示的に列挙している。
