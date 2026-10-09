---
title: 作業仕様書 — SC-12 の有人の MCP クライアントで、ループバックのリダイレクト URI の port 必須を外す（#1859 後段・利用者裁定「外す」）
type: spec
status: done
related_ids: [FR-16, UC-09, SC-12, ADR-0134, IADR-0516, IADR-0524, IADR-0527]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1（リダイレクト URI の完全一致・ループバックは `http://127.0.0.1` / `http://[::1]`）・フォローアップ 3（Keycloak の照合の確かめ）
issue: "#1859"
---

# 作業仕様書 — ループバックのリダイレクト URI の port 必須を外す（#1859 後段）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0527** に置く。
> 基点は MSP `origin/develop` `24a8bee8`（PR #1869 = Keycloak 26.7.4・IADR-0524 と、門 M9 の「port なしのループバック」の対を含む）。
> 🔴 **稼働中のクラスタには何も実行しない。** 稼働での確かめは本 PR のマージ後の integration-stack（門 M9）である。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1859** の後段（「ループバックの redirect URI で port を必須にする規則を外すか」）。**利用者裁定 2026-10-09「外す」**。
- 計画: **ADR-0134** 決定 1（有人のリダイレクト URI は完全一致・ループバックは `http://127.0.0.1` / `http://[::1]`。RFC 8252 §7.3）・フォローアップ 3。
  ループバックの port の扱いは計画が実装へ委ねた細目である（IADR-0516 の #1844 追記）。計画への環流は要らない（計画の文面は port を求めていない）。
- 前提 IADR: [IADR-0516](../adr/IADR-0516_sc12-keycloak-service-account-provisioning.md)（#1844 追記・PR #1854 追記〔port 必須の導入と、緩める条件＝決定 4〕・#1859 追記〔証拠と推奨〕）、
  [IADR-0524](../adr/IADR-0524_keycloak-26-upgrade.md)（26.7.4 への更新・決定 4〔門 M9 の対〕・残余 2〔外す PR で同時に改めるもの〕）。

## 裁定の条件（利用者裁定 2026-10-09）

| # | 条件 | 写像 |
| --- | --- | --- |
| 1 | 外すのは `127.0.0.1` / `[::1]` のループバックの「port 必須」だけ | `RedirectUriRules.Violation` の port の要求を外す。port を書いた形は従来どおり 1〜65535 の数字だけを許す（`:` だけ・`:0`・数字以外は 400 のまま） |
| 2 | userinfo・ワイルドカード・フラグメント・`localhost`（ほか現行が拒むもの全部）は拒み続ける | 判定の順と文言は変えない。試験の否定形はそのまま残す（`127.0.0.1:49152@evil.example/cb` ほか） |
| 3 | 門 M9 の対（port なしの陽性対照＋横取り 4 形 × 2 ホストが 400）を常設する。SC-12 の実際の登録経路で測る | M9 の「port なしの登録は 400・Keycloak に何も作らない」を「port なしの登録は 201・テンプレートどおり」へ改め、master の管理者で直接作る段を廃して、SC-12 で作ったクライアントへ陽性対照・横取り 8 形・path 違い・`localhost` を当てる |
| 4 | 1 PR で同時に変える | 入口（`RedirectUriRules`）・検証器の試験・画面の写し（`isAllowedRedirectUri`・理由の識別子・入力欄の説明）と試験・`openapi.yaml` と生成物（orval）・カタログ（Lingui）・通信／試験／画面の仕様書・`security.md` |
| 5 | 判断を記録する | IADR-0527（新規）＋ IADR-0516 への日付つき追記（参照のみ） |

## 設計（正は IADR-0527）

1. **後段** `RedirectUriRules.Violation`: ループバック（綴りで `http://127.0.0.1` / `http://[::1]` で始まり、直後が `:`・`/`・`?`・末尾）は、
   port なしなら通す。`:` が続くときは従来どおり 1〜5 桁の数字の後が `/`・`?`・末尾で、値が 1〜65535 のときだけ通す。外れれば
   「ループバックのリダイレクト URI '…' の port が不正です（1〜65535 の数字で書くか、port を書かずに登録してください）。」で 400。
   利用者情報・フラグメント・ワイルドカードの判定はループバックの判定より前にあり、変わらない。
2. **画面の写し** `isAllowedRedirectUri`: 綴りの正規表現を `^http://(127\.0\.0\.1|\[::1\])(?::(\d{1,5}))?([/?]|$)` の 1 本にし、port があれば 1〜65535。
   理由の識別子 `redirect-uri-loopback-port-required` は**撤去**する（port なしは通るので出す場面が無い。`:` だけ・`:0` は `redirect-uri-invalid`）。
   入力欄の説明から「ループバックは port を明示してください」を外し、「port は省いてよい（実行時の任意の port で戻る）」を書く。
3. **門 M9**: port なしの `http://127.0.0.1/cb`・`http://[::1]/cb` を SC-12 で登録（201・公開クライアントのテンプレートどおり・入口の印）。
   そのクライアントに、陽性対照（登録どおり・任意の port でログイン画面）・横取り 4 形 × 2（`:<任意>@`・`:1@`・`:@`・`:<任意>:1@`）が 400・path 違いが 400・`localhost` が 400 を当てる。
   port つきの既存の対（完全一致・横取り 4 形）は残す。`:` だけ（`http://127.0.0.1:/cb`）が SC-12 で 400 になり Keycloak に何も作らないことを足す（入口の規則が緩みすぎていない否定形）。
4. **`openapi.yaml`**: `RegisterMcpClientRequest.redirectUris` と登録の口の説明から「port 必須・Keycloak 24 の CVE」を外し、port は任意・横取りの形は認証基盤の照合が拒む（版で固定・結合スタックの門が日次で測る）と書く。
   生成物は `pnpm run codegen`（`src/`）で作り直す（手で書かない）。

## 母集合の走査（規則 9・10）

誤りの側の文字列で全文書を走査した（`git grep -I -E "loopback-port-required|port を明示|ポート必須|port 必須|port の明示|CVE-2024-8883|Keycloak 24|portlessLoopback|loopbackHijack|LoopbackPort"`。
submodule `src/ai-stock-trading`・凍結記録 `.ai-context/specs/`・`CHANGELOG.md` は除く）。当たり 49 ファイル。

| 当たり | 扱い |
| --- | --- |
| `RedirectUriRules.cs:9-24,49-52,60`・`McpClientContracts.cs:8`・`RegisterMcpClientValidatorTests.cs:121,159-180` | 改める（規則・注記・試験） |
| `mcpClientVocabulary.ts:99,122,131-137,161,190`・`mcpClientVocabulary.test.ts:48-71`・`McpClientManagementPage.tsx:93,648` | 改める（写し・識別子の撤去・説明） |
| `locales/{ja,en}/messages.{po,ts}` | `pnpm run i18n` で再生成（手で書かない） |
| `docs/api/openapi.yaml:1178,6091-6092` → 生成物 `bff.schemas.ts:1841-1842`・`mcp-clients.ts:218` | 改めて `pnpm run codegen` で再生成 |
| `docs/api/FR-16_mcp-server.md:152-158`・`docs/screens/SC-12_mcp-client-management.md:228-231`・`docs/tests/FR-16_mcp-server.md:207,215`（C-59・C-67）・`docs/tests/SC-12_mcp-client-management.md:73`（T-33） | 改める（取り消し線＋日付つきで言い直す。画面仕様の慣行に合わせる） |
| `docs/security/security.md:368` | 改める（入口の暫定が外れ、統制は版＋門の 1 段になったこと） |
| `scripts/check-mcp-client-provisioning.js`（M9 の説明 43-56・本体 1223-1326） | 改める（上の設計 3） |
| `scripts/README.md:50`（M9 の説明「ループバックは port を明示して登録し（port なしの登録は 400…）」） | 改める（初回の走査の読みで見落とし、規則 9 の引き直しで拾った） |
| `.github/workflows/integration-stack.yml:354` | 改める（M9 の説明。起動条件・必須チェックは変わらない） |
| `scripts/chunk-budget-baseline.json:305` | **改めない**（床の更新の経緯の記録。文言が減るので床を割らない） |
| `docs/operations/operations.md:2045,2099` | **改めない**（点検の記録。版の更新の事実で、規則には触れない） |
| `Keycloak 24` のその他の当たり（`check-realm-constraints.js`・`KeycloakIdentityAdminClient.cs`・`KeycloakServiceAccountProvisioner.cs`・`ReissueSecret/Endpoint.cs`・`reset-gate.*`・`externalsecret-*.yaml`・`docs/api/FR-16_mcp-server.md:115`・`docs/tests/FR-16_mcp-server.md:568`・`docs/tech/*`・`scripts.*test.js`） | **対象外**（リダイレクト URI と無関係な 24 での実測・ソースの読みの出典） |
| `.ai-context/adr/IADR-0516`・`IADR-0524`・`README.md` | 本文は凍結。IADR-0516 に日付つき追記（IADR-0527 への参照）を置き、索引に IADR-0527 の行を足す |

**この変更で新たに誤りになる自分の記述（規則 10）**: (a) IADR-0524 の「統制と現在の実現手段」表の「入口の port 必須（外すまで残る）」—— 凍結のため IADR-0527 が受ける。
(b) IADR-0516 の PR #1854 追記の決定 1〜4 —— 同上（日付つき追記で IADR-0527 を指す）。(c) M9 の説明の「規則そのものは変えない（外すのは別 PR）」—— 本 PR で改める。
(d) `security.md` の「加えて入口が port の明示を必須にしている」—— 改める。(e) 画面の入力欄の説明（カタログに載る文言）—— 改めてカタログを再生成。

**窓（規則 11）**: 本件は時間差の統制を新たに作らない（門の実行間隔＝最長 1 日の窓は受け入れたリスクとして IADR-0527 に書く）。

## テスト

- `RegisterMcpClientValidatorTests`: port なしのループバック 8 形（`127.0.0.1`・`[::1]` × path あり・`/`・なし・クエリ）が**通る**。`:` だけ・`:0`・`:65536` は「port が不正」で 400。
  利用者情報（`127.0.0.1:49152@evil.example/cb`・`[::1]:49152@…`・port なしの横取り 4 形 × 2）・`localhost`・ワイルドカード・フラグメントは従来どおり 400。
- `mcpClientVocabulary.test.ts`: 同じ並び（port なしは通る・`:` だけ・`:0` は `redirect-uri-invalid`・横取りの形は不可）。
- `check-mcp-client-provisioning.js --self-test`: 既存（`portlessLoopbackHijackProbes` の形・24 の応答で赤）を緑のまま。
- **変異**（実施済み）: 入口で利用者情報の判定だけを外しても、横取りの 8 形は綴りの host の突き合わせと port の形の検査で 400 のまま（赤は `https://user@…` の 1 件だけ）。
  3 つ（利用者情報・host の突き合わせ・port の形）を同時に外すと 14 件が赤（横取り 8 形・port の形 5 件・利用者情報 1 件）。入口で横取りの形が 3 重に止まることを確かめた。

## 検証

`dotnet build`（platform の slnx・警告 0）・`dotnet test`（McpServer.Tests）・`dotnet format --verify-no-changes`・`pnpm run lint` / `typecheck` / `format:check`・
vitest（触ったファイル）・`pnpm run codegen` と `pnpm run i18n` の再生成差分が空・`check-mcp-client-provisioning.js --self-test`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・
`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-updated --base origin/develop`。

🔴 **IADR の番号**: 0525（PR #1866）と 0526（PR #1865）が未マージで番号を予約しているため、本 PR は 0527 を採る。両 PR のマージまで
`check-adr-numbering` の欠番が赤になり得る（本 PR の他の失敗が無いことは、仮の置き場で欠番を埋めて確かめ、仮の置き場は消す）。
