---
title: タグ辞書の初期投入器を置き、外部ユニットの文書が 400 で弾かれる状態を解く
issue: "#1359"
plan_refs:
  - FR-06
  - FR-09
  - SC-05
  - SC-09
adr_refs:
  - ADR-0004
  - IADR-0133
  - IADR-0152
  - IADR-0284
status: in-progress
created: 2026-09-09
---

# 作業仕様書: タグ辞書の初期投入器（#1359）

## 起点

- issue #1359。発端は AST#705（KB 保存が 100% 失敗）。
- 実測（2026-09-09・稼働クラスタ）: `辞書: 既存 0 件`。送り手のログは
  `KB 保存: 0/3 件を platform 文書管理へ登録（未保存は fail-safe 縮退）` を出し続けていた。

## 何が塞がっていたか

`POST /documents` は #635 でタグ辞書検証を持つに至り、辞書に無い名前は `TagResolver` が
`UnknownTagsProblem`（400）にする。**自動登録へは落ちない**（IADR-0152 決定どおり）。

一方で **`Tags` への書き込み口は `POST /tags` だけ**であり、初期投入の仕組みが存在しなかった。
`db.Tags.Add` / `Tag.Create(` を走査した結果、試験以外の書き手は `CreateTagEndpoint` の 1 箇所のみ。
EF の `HasData`・起動時シーダ・helm 値・シード JSON はいずれも無い。

さらに **送り手は自分では登録できない**。`POST /tags` は AdminOnly で、ai-stock-trading の
サービスアカウントは `platform-operator` のみ（IADR-0075 が `platform-admin` の付与を明示的に断っている）。

つまり「検証を入れたが、検証を通せる状態にする経路を用意していなかった」という穴である。

## 設計

**既存の 2 つの投入器と同じ形にする。新しい作法を増やさない。**

| 要素 | 内容 |
| --- | --- |
| `deploy/local/tag-seed/tags.json`（新設） | 宣言的な単一情報源。19 件（収集の種別 6 ＋ 出所 9 ＋ 報告書 4） |
| `scripts/seed-tag-dictionary.js`（新設） | `POST /tags` 経由で投入。**直 DB 書き込みはしない**。冪等（既存名は送らない・競合の 409 も no-op）。`--dry-run` あり |
| 資格情報 | `abac-seeder`（client_credentials・`platform-admin`）。解決は `seed-abac-policies.js` を `require` して使う（`seed-search-documents.js` と同じ作法。値を写し取らない） |
| `scripts/k8s-local-up.sh` | `TAGSEED=1` のオプトインを `ABACSEED` / `SEARCHSEED` の隣へ。既定は挙動不変 |

### 名前の扱い

`Tag.Normalize` は **Trim のみ**で、**大文字小文字を区別する**。したがって `Quote` は `Quote` のまま入れる。
投入器側で正規化を増やさない —— 送り手の綴りと 1 文字でも違えば、辞書に在っても 400 に戻るためである。

### 採らなかった案

- **起動時シーダ（DocumentService に組み込む）**。辞書は人が管理する値集合（SC-09）であり、
  アプリの起動に紐づけると dev の初期値が本番像へ混ざる。既存 2 器が外部スクリプトなのと揃えない理由が無い。
- **送り手（AST）側で起動時に登録する**。`platform-admin` が要る。IADR-0075 の決定に反する。

## 走査した母集合（規則 2・9）

「タグ辞書へ行を入れる経路」を、**誤りの側の文字列で**走査した。

| 走査 | 結果 |
| --- | --- |
| `db.Tags.Add` / `Tag.Create(`（`src/` 全体） | 試験を除き `CreateTagEndpoint` の 1 箇所のみ |
| `HasData`（DocumentService 配下） | 0 件 |
| `deploy/helm` 配下のタグ値 | 0 件 |
| 既存シード器（`scripts/seed-*.js`） | `seed-abac-policies` / `seed-search-documents` の 2 件。**後者はタグを付けていない**（`documents.json` に `tags` が無い。辞書に無いタグで 400 になるのを避けるため、と当該ファイルのコメントが明記している） |

**除外**: `src/ai-stock-trading`（submodule。本リポジトリからは変更しない）。

## 実測（受け入れ基準の証跡）

- 初回: `辞書: 既存 0 件 / 追加 19 件` → 19 件すべて作成
- 再実行: `辞書: 既存 19 件 / 追加 0 件` → `投入済みのため変更はありません（冪等・no-op）`
- 送り手側の効果: `KB 保存: 0/3 件` → **`3/3 件`**（送り手のイメージを develop から焼き直したうえで）
- 切り分け: 同じ形の文書を手で POST したところ、`confidentiality` を落としたときだけ 400
  （`{"errors":{"confidentiality":["機密区分（confidentiality）は必須です。"]}}`）。
  タグ由来の 400 は消えている。

## 受け入れ基準

- [x] 投入が冪等である（再実行で no-op）
- [x] 直 DB 書き込みをしていない（`POST /tags` 経由）
- [x] 資格情報を写し取っていない（`seed-abac-policies.js` を `require`）
- [x] 稼働クラスタで送り手の 400 が消えた
- [ ] `TAGSEED=1` が `k8s-local-up.sh` に配線されている
- [ ] 回帰テストがある
- [ ] `scripts/README.md` の一覧に載っている

## 計画書との差異

差異なし。SC-09 の「辞書は管理者が管理する値集合」「追加はシステム管理者限定」を変えていない。
**変えたのは「初期値を入れる手段が無かった」ことだけ**である。

## 未決事項・残余リスク

- 🔴 **この 19 件は ai-stock-trading の `KnowledgeTagVocabulary` の複製であり、
  リポジトリを跨ぐこの複製を固定する検査は無い**（本リポジトリは AST に依存しない）。
  送り手が語彙を増やせば 400 が再発する。再発は `KB 保存: N/M 件` の縮退として表面化する。
- **本番・共有クラスタへの登録は別の運用行為である。** 本器は `deploy/local/` 配下の dev 用であり、
  既存 2 器と同じ射程に留める。
