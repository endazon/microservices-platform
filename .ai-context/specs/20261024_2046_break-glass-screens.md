---
title: 作業仕様書 — 緊急アクセス（Break-glass）の画面（SC-19 の発動・残り時間・本人の終了・監査ログ、SC-03 の指名された管理者の閲覧の導線。#2046 の 3 本目）
type: spec
status: done
related_ids: [FR-19, UC-11, SC-19, SC-03, ADR-0036, ADR-0098, ADR-0102, IADR-0610, IADR-0611, IADR-0612]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-19 主要素 8・§緊急アクセス、SC-03）
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-19.html（緊急アクセスのダイアログ）
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md（D-08）
  - planning#841（2026-10-23T14:21Z の裁定コメント。モックアップに対象の資料を選ぶ欄を足す）
---

# 仕様書: 緊急アクセスの画面（#2046 の 3 本目）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-19（受け入れ基準「緊急アクセス」）。ユースケース: UC-11（例外フロー）。
- 画面: SC-19（主要素 8「緊急アクセス（Break-glass）の導線」・§緊急アクセス）、SC-03（指名された管理者の閲覧の導線。新画面なし）。
- 計画 ADR: ADR-0036 D-08（本人だけが発動・理由必須・15 分で失効・サーバ側の発動記録で判定・監査ログ）、
  ADR-0098 決定 1（画面には表示名を出し識別子は出さない）、ADR-0102（SC-03 の個人資料の表示）。
- 裁定: planning#841（本人同意型の管理者の閲覧・指名した platform-admin 1 人・資料 1 件・本人の早期終了・閲覧も 1 件ずつ記録・
  監査ログは platform-admin と本人・**モックアップに対象の資料を選ぶ欄を足す**）。計画書への反映は計画側。
- 前提: 1 本目 #2179（`39ecb45b`。IADR-0610）、2 本目 #2180（`03620e94`。IADR-0611。orval の生成物は 2 本目で入っている）。
- 実装 ADR: IADR-0612。issue: #2046（本 PR で完結）。

## 範囲

- SC-19: 緊急アクセスの欄（開いたときだけ描く）。対象の資料 1 件の選択、状態（有効なら残り時間・指名先・理由）、本人の早期終了、
  発動の確認ダイアログ（理由必須・500 文字以内・閲覧を許す管理者の選択・「この操作は監査ログに記録されます」）、監査ログの表示。
- SC-03: `?via=break-glass` の文脈で開いたときの告知と、有効でなくなった（404）ときの表示。承認欄を出さない。
- 監査ログから SC-03 への導線（自分が指名された有効な発動の行だけ）。
- i18n（ja / en）、単体試験、ブラウザ E2E 1 本、画面仕様書・テスト仕様書・機能仕様書の追随。
- 含めない: バックエンド・契約（変更なし）。

## 現状の実測（着手時）

- 生成フック（`useBffBreakGlassStatus` / `Activate` / `End` / `Audit`）は 2 本目で `private-notes.ts` に入っている。画面からの呼び出しは 0 件。
- SC-03 の BFF の詳細・本文は 2 本目で後段の判定を素通しする。**応答（`DocumentDto`）に緊急アクセスで開いたことの印は無い**
  （gRPC の印は BFF までで止まる）。
- **管理者に絞った名簿の公開の口は無い。** `GET /bff/users/lookup` は有効な利用者の検索で、`UserSummaryDto` は意図してロールを運ばない
  （`username` / `displayName` / `enabled` だけ）。`GET /bff/admin/users` は platform-admin 限定（本人＝一般利用者は 403）。
- 後段は指名先が platform-admin でなければ 400（ValidationProblem、`nomineeUserId` の文言）、確かめられなければ 503 を返す。
- E2E の `installBffSession` は応答を用意していない `/bff/*` を 500 にし `unhandled` に積む（`expectBffTrafficIsComplete` が落とす）。
  SC-19 を開くたびに緊急アクセスの口を引くと、既存の E2E 全件と a11y の走査が落ちる。

## 受け入れ基準

- [x] AC1 SC-19 の緊急アクセスの欄は開くまで描かず、状態も監査ログも引かない（一覧の取得の成否に関係なく導線は置く）
- [x] AC2 対象に選べるのは本人の利用中の資料だけ（削除済みは出さない）。状態は選んだ 1 件だけ引く
- [x] AC3 第三者が発動する導線・管理者が全利用者の個人資料を一覧する画面は無い（管理者のロールでも）
- [x] AC4 確認ダイアログに「この操作は監査ログに記録されます」と 15 分の失効・延長なしを出す
- [x] AC5 理由は必須（空白だけは空）・500 文字以内（501 で押せず、500 に戻すと押せる）。文字数と理由を文言で出す
- [x] AC6 指名先は名簿（有効な利用者の検索）の候補から選ぶ。自由入力にしない・自分を候補から除く・表示名だけを出す（利用者名は DOM に出ない）
- [x] AC7 発動は前後の空白を落とした理由と候補の利用者名を送る。成功でダイアログを閉じ、状態と監査ログを無効化する
- [x] AC8 拒否（400 は後段の文言・409・403・503）を文言で告げ、ダイアログを閉じない
- [x] AC9 有効中は残り時間（分・秒）と終了時刻・指名先（表示名）・理由を文言で出す（色だけに頼らない）。残りが 0 になったら状態を引き直す
- [x] AC10 本人は 15 分より前に終えられる（失敗は文言で告げる）
- [x] AC11 監査ログは「表示する」まで引かない。操作種別（発動・終了・閲覧〔属性／本文〕）を文言で、利用者を表示名で出す。
      他人の資料の題名は引かず識別子を出す。誰の行まで出るかは後段が決める（画面は絞らない）
- [x] AC12 監査ログのうち、自分が指名された・終了の行が無い・終了予定が未来の発動の行にだけ SC-03（`/docs/<id>?via=break-glass`）への導線を出す
- [x] AC13 SC-03: `?via=break-glass` で開けたら本文より前に緊急アクセスの閲覧である旨を出し、AI 提案の承認欄を出さず提案も引かない。
      知らない `via` は落とす
- [x] AC14 SC-03: `?via=break-glass` で 404 なら、開けないことと考えられる理由を区別せず告げる（`role="alert"`・再試行なし）
- [x] AC15 ja / en のカタログに未翻訳なし。既存の E2E を壊さない（SC-19 を開いただけでは新しい口を引かない）

## 設計

IADR-0612 を正とする。要点:

- 部品: `BreakGlassPanel`（欄・対象の選択・状態・早期終了）、`BreakGlassDialog`（確認ダイアログ）、`BreakGlassAuditLog`（監査ログ）。
  読み書きは `api/useBreakGlass.ts`（生成フックだけ）、純関数は `types/breakGlass.ts`、時計は `hooks/useNow.ts`。
- 指名先: `useUserLookup`（既存。共有先の検索と同じ口）の候補から選ぶ。管理者かどうかは後段が確かめる（IADR-0612 決定 2）。
- SC-03: ルートの `validateSearch` に `via?: 'break-glass'` を足す（任意。既存の導線は変えない）。表示の文脈だけで、許可には使わない（決定 3）。

## 母集合の取り方（規則 9・10）

- 生成フックの呼び出し元: `grep -rn "useBffBreakGlass" src/knowledge src/platform/frontend/src --include=*.ts*`（generated を除く）—
  `api/useBreakGlass.ts` の 4 つだけ。
- `/docs/$id` への導線の母集合: `grep -rn "to=\"/docs/\$id\"\|to: '/docs/\$id'" src/knowledge/frontend src/platform/frontend/src` —
  既存はいずれも `search` を渡さない。`via` を任意にしたので変更不要（`pnpm run typecheck` で確認）。
- SC-19 を開く E2E: `grep -ln "/my/notes" src/platform/frontend/e2e` — `sc19-private-notes.smoke.spec.ts`・`a11y.smoke.spec.ts`・
  `keyboard-navigation.smoke.spec.ts` ほか。欄は開くまで要求を出さないので、どれも手当て不要。
- 文書の記述: `grep -rln "緊急アクセス" docs` — SC-19 画面仕様書（冒頭の注記・主要素・§緊急アクセス・入力項目・バリデーション・アクション・
  遷移図・計画との対応・未決 3）、SC-19 画面テスト仕様書（範囲の注記・実体・ケース 51〜65）、SC-03 画面仕様書（個人資料の表示の経路・
  §緊急アクセスの閲覧・エラー表）、SC-03 テスト仕様書（UC の写像）、FR-19 機能仕様書（⑥の注記・§緊急アクセスの表）。
  FR-19 ライフサイクルのテスト仕様書と BFF 境界の通信仕様書は画面を扱わないので変えない。
  「画面の導線はまだ無い」の語で引き直し（規則 10）、上の機能仕様書の 1 か所を打ち消した。

## 検証

- `src/` で `pnpm run typecheck` / `pnpm run lint` / `pnpm run format:check` / `pnpm run test:coverage`
- `pnpm run i18n` の再生成で差分なし・`node scripts/check-i18n-catalogs.js`・`node scripts/check-knip.js --require`
- 変異: 理由の必須を外す・自分を候補から除かない・終了の行を見ない・via でも承認欄を出す —— それぞれ赤になること（PR 本文に表）
- ブラウザ E2E は CI（手元に Playwright のブラウザが無い）

## 残余リスク・未決

- 指名先の候補は管理者に絞れない（管理者でない人を選ぶとサーバが 400 で拒む）。絞るには管理者の名簿を一般利用者へ開く口が要り、
  露出の判断を伴うので本 PR では足さない（IADR-0612 決定 2）。
- SC-03 の告知は URL の文脈に依る。監査ログ以外の経路（URL の直打ち）で開いた指名された管理者には告知が出ない（閲覧の可否と記録は変わらない）。
- 管理者が指名された発動を知る手段は SC-19 の監査ログ（と所有者からの連絡）である。通知は置いていない（計画に無い）。
