---
title: 作業仕様書 — 同期競合の本文の取り出し失敗と、不正な realm_access を黙って捨てない（#2141）
type: spec
status: done
related_ids: [FR-20, NFR-19, NFR-28, SC-20, UC-11, ADR-0037, IADR-0273]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2141"
---

# 作業仕様書 — 同期競合の本文・realm_access（#2141）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `170aca7f`）。親は #2047（指摘 L-12）。

## 現状（実測）

- `DocumentService/Features/SyncConflicts/Get/Endpoint.cs` の `ReadAsync` は、ストレージの例外を握って空文字を返し、記録も残さなかった。
  - 利用者には「中身が空の版」と見分けが付かない。
  - 🔴 **同じ `ReadAsync` を解決（`Resolve/Endpoint.cs`）も使う。** ストレージが失敗したまま `local` を選ぶと、**空の本文が新しい版として書かれた**（`both` は空の別名資料を作る）。issue に無い実害で、同じ根から出ている。
- `KeycloakRolesClaimsTransformation` は不正な `realm_access`（JSON でない・オブジェクトでない・`roles` が配列でない）を黙って捨てる（fail-closed だが記録なし）。
- `BffSessionExtensions` の `OnTokenValidated`（コード交換のアクセストークンから realm_access を複写する腕）は、解析できないトークンを黙ってロール無しで通す。

## 方針

1. `ReadAsync` は `(Content, Unavailable)` を返す。参照が無いときは空で `Unavailable=false`（取り出せなかったのではない）。例外のときは空で `Unavailable=true` にし、警告を 1 行残す。載せるのはどちらの本文かと例外の型だけで、URI と本文は載せない（個人資料の所在と中身）。
2. 詳細の応答 `SyncConflictDetailDto` に `LocalContentUnavailable` / `ServerContentUnavailable`（既定 false）を足す。契約の検査器では非破壊の追加になる。応答には常に載るので、openapi では required にする。
3. 解決: ローカル版を取り出せないときの `local` / `both` は何も確定せず 503 `{ error: local_content_unavailable }` を返す。競合は未解決のまま残る。`server` は本文を使わないので通す。
4. SC-20 の画面: 取り出せなかった側のペインに「本文を取り出せませんでした（ストレージの障害）。」を出し、ローカル版が無いときは「ローカルを採用」「両方を残す」を押せなくする。
5. realm_access: 変換と BFF の複写の両方で警告を 1 行残す（値・トークンは載せない）。fail-closed は変えない。`roles` を持たないオブジェクト（ロール 0 個）は不正ではないので記録しない。

## 受け入れ基準

- [x] ストレージ失敗時に警告ログが出て、応答が空文字と区別できる（修正前は赤）
- [x] ローカル版を取り出せないとき `local` / `both` は 503 で確定せず、本文は書き換わらない（修正前は赤＝空の本文が書かれた）
- [x] 不正な `realm_access` で警告ログが 1 件出る。値を載せない（修正前は赤）
- [x] BFF の複写で解析できないトークンのとき警告が 1 件出る（修正前は赤）
- [x] SC-20 で取り出せなかった側を示し、local / both を押せない（修正前は赤）

## 母集合（規則 9）

- `git grep -n "GetSyncConflictEndpoint.ReadAsync"` → 詳細と解決の 2 か所。解決も直した。
- `git grep -n "realm_access"`（本番コード）→ 変換・BFF の複写の 2 か所が解析する。他は名前の参照だけ。
- 契約の追随: `SyncConflictDetailDto` を作るのは DocumentService の詳細だけ。BFF は透過する。型付きの写しを持つのは frontend の試験 1・e2e 2 で、どちらも追随した。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
