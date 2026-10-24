---
title: IADR-0626 live-ro のログインの異常系とログアウトは e2e-revocable の新しいセッションで行い、その spec は trace・スクリーンショットを残さない
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-10, NFR-13, ADR-0026, ADR-0032, IADR-0273, IADR-0569, IADR-0570, IADR-0624, IADR-0625]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0026（認証は IdP。MFA 必須）
related_specs:
  - ../specs/20261025_2027_live-ro-shell-and-auth-errors.md
---

# IADR-0626: live-ro のログインの異常系とログアウトの被験者と成果物（#2027）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2027。親 #2013 の E-40）

## 起点・関連

- 起点 issue: #2027（親 #2013）。関連する計画書 ID: NFR-10（MFA）、NFR-13（ログイン失敗の固定文言）。計画 ADR: ADR-0026、ADR-0032。
- 関連する実装判断: [IADR-0273](./IADR-0273_bff-session-completion.md)（backchannel logout は subject の全セッションを失効）・
  [IADR-0570](./IADR-0570_e2e-accounts-placeholder-credentials.md)（試験アカウント）・[IADR-0624](./IADR-0624_live-playwright-config-setup-and-readiness.md)（setup・teardown・`fillSecret`）・
  [IADR-0625](./IADR-0625_e2e-live-workflow-disposable-stack.md)（`e2e-live.yml` と成果物の走査）。
- 作業仕様書: [20261025_2027_live-ro-shell-and-auth-errors](../specs/20261025_2027_live-ro-shell-and-auth-errors.md)。

## コンテキストと課題

網羅表（#2013 コメント 2）は、誤りのパスワード（M-003）と誤りの OTP（M-004）を `e2e-noaccess` で、ログアウト（M-010）を `e2e-user` で流すとしている。
これらは `live-setup` が作った storageState では試せない（ログインそのものを試す）ので、spec の中で**新しくログインする**。
次の 4 つが同時に問題になる。

1. **ログアウトの巻き添え**: BFF の backchannel logout は logout_token の `sub` で**その利用者の全セッション**を消す（IADR-0273 決定 2。
   `BackchannelLogoutProcessor.RemoveAllForSubjectAsync`）。`e2e-user` で新しいセッションをログアウトすると、`live-setup` が作った `e2e-user` の
   storageState も失効する。live-ro は `workers: 2` で、同じ役割を E-41・E-42 の spec も使うので、並列に走る他の test が 401 → `/login` で落ちる。
2. **アカウントの状態**: 誤りの入力は brute force の数え（5 回で 900 秒ロック）を進める。数えを持つ役割を他の spec と共有すると、retry や並列で
   ロックに近づく。
3. **成果物に秘密が載る**: 層の trace（`retain-on-failure`）は、Keycloak のフォームの POST 本文（パスワード）とセッションの `Set-Cookie` を記録する。
4. **OTP の再利用**: realm は `otpPolicyCodeReusable: false`。`live-setup` が同じ役割で使った周期の 6 桁は 2 度目が拒否される。

## 検討した選択肢

| 論点 | 選択肢 | 判断 |
| --- | --- | --- |
| 被験者 | A. 網羅表のとおり `e2e-noaccess`（M-003/004）・`e2e-user`（M-010） | 採らない（1 と 2。並列の他 spec を巻き添えにする） |
| | **B. `e2e-revocable`（live-ro では他に誰も使わない。基準書 §5「状態を変える試験は専用アカウント」）** | **採る** |
| | C. 異常系だけを別 project にして live-ro の後に流す | 採らない（config と skip 検査の層の対応を変える。B で足りる） |
| 成果物 | A. 層の既定（trace `retain-on-failure`・失敗時スクリーンショット） | 採らない（3。走査が赤になって成果物が上がらない＝失敗の調べ物が消える） |
| | **B. この spec だけ trace・スクリーンショット・動画を切り、失敗の段は文字で attach する（`live-setup` と同じ形）** | **採る** |
| OTP | A. `freshOtp`（残り 5 秒未満のときだけ待つ） | 採らない（4。setup と同じ周期になりうる） |
| | **B. 次の周期の始まりまで待ってから出す（`nextPeriodOtp`）** | **採る**（最長 30 秒。test の時間は 120 秒） |

## 決定

1. **ログインの異常系とログアウトの live-ro は `e2e-revocable` の新しいセッションで流す**（`e2e/live/auth-errors.ro.spec.ts`）。網羅表の役割（`e2e-noaccess`・`e2e-user`）は
   本決定で差し替える。他の live-ro の spec は `e2e-revocable` を使わない。
2. **誤りは 1 回ずつ**（実在の ID の誤りのパスワード 1・誤りの OTP 1。存在しない ID は利用者が無く数えに入らない）、**直後に正しい入力でログインして数えを戻す**。
   file は `retries: 0`・直列。
3. **この spec は trace・スクリーンショット・動画を残さない**（`test.use({ trace: 'off', screenshot: 'off', video: 'off' })`）。失敗の段は `support/login.ts` の `stage` が
   URL の origin と path・Keycloak の表示文言だけを attach する。秘密は `fillSecret` で入れる（`fill` は値を step の題に載せる）。
4. **OTP は次の周期を待ってから出す**（`nextPeriodOtp`）。誤りの OTP は正しい 6 桁と同じ時刻から導き、その前後 1 周期（`otpPolicyLookAheadWindow: 1`）の正しい値のどれとも違う値（`wrongOtpFor`）。
   待つことで誤りどうしの間が 1 秒以上空き、Keycloak の quick login check（既定 1 秒以内の連続失敗で 60 秒ロック）にも掛からない。
5. **作ったセッションは test の中でログアウトして失効させる**（M-010 そのもの）。途中で落ちたら finally で `/bff/auth/me` の `logoutUrl` を開く。
   巻き添えで `live-setup` の `e2e-revocable` の state も失効しうるが、teardown は「ログアウト前から 401」を失効済みとして扱うので赤にならない。
6. ログインの部品（`fillSecret`・`stage`・`submitPassword`・`submitOtp`）は `e2e/live/support/login.ts` へ置き、`auth.setup.ts` と共有する。

### brute force の失敗回数（どの役割で何回か）

| 役割 | 1 実行で数えられる失敗 | 内訳 | 戻し方 |
| --- | --- | --- | --- |
| `e2e-revocable` | **最大 2 回**（閾値 `failureFactor: 5` 未満） | 実在 ID の誤りのパスワード 1・誤りの OTP 1 | 直後の正しいログインの成功で数えが 0 に戻る |
| 存在しない ID（`e2e-absent-<乱数>`） | 0 回 | 利用者が無いので数えられない | — |
| 他の 5 役割 | 0 回 | 誤りの入力をしない（`live-setup` の 1 回のログインは成功だけ） | — |

- `retries: 0`・直列・1 test なので、1 実行で `e2e-revocable` の失敗が 2 回を超えることは無い（成功の前に落ちた場合も、残る数えは最大 2）。
- live-ro で `e2e-revocable` を使う test は本 spec だけであり、同じ実行の後続の test が `e2e-revocable` のロック（5 回で 900 秒）に当たることは無い。
  落ちて数えが残った場合も、次の実行の本 spec が足す失敗は 2 回なので、合計は 4 回で閾値 5 に届かない（その成功で 0 に戻る）。
- 2 回の失敗の間は `nextPeriodOtp` の待ち（1.2 秒以上）で空くので、quick login check（1 秒以内の連続失敗で 60 秒ロック）にも掛からない。
- 誤りの OTP は正しい 6 桁と**同じ時刻**から導き、その時刻の前後 1 周期の正しい値のどれとも違う（L1 の `live-opt-in.smoke.spec.ts` が固定）。

## 理由

- 巻き添えの失効は**並列の別の test を落とす**ので、原因が離れた場所に現れる（flaky に見える）。被験者を分けるのが最も局所的な直し方である。
- 成果物に秘密を載せない形は IADR-0624 の setup と同じであり、成果物の走査（IADR-0625）を「最後の網」に保てる。

## 影響

- 網羅表の M-003/M-004/M-010 の「役割」列は、本決定で `e2e-revocable` に読み替える（#2013 へ注記する）。
- live-rw で `e2e-revocable` を使う E-53（#2033）は、`E2E_LAYER=ro,rw` で live-ro と同時に流れると本 spec のログアウトの巻き添えを受けうる。
  E-53 は自分で新しくログインする（共有の state を使わない）か、ro と rw を同時に流さない。

## 残余リスク

- Keycloak の親テーマの ja の文言（誤りの OTP）が版で変わると本 spec が赤になる（文言は欄に紐づく 1 要素 `#input-error-otp` / `#input-error-username` で見る。根拠は Keycloak 26.7.4 の keycloak.v2 `login/field.ftl`：外側 `#input-error-container-<name>` の中に内側 `#input-error-<name>` が入れ子で、`[id^="input-error"]` は 2 要素に当たる。PR #2227 の e2e-live で実測）。
- 次の周期を待つので本 spec は最長 30 秒長くなる（live-ro の予算 20 分の中）。
