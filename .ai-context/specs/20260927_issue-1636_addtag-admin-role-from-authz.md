---
title: gRPC の AddTag で本文の user_roles を信じるのをやめ、承認者が管理者かどうかを認可サービスに引き直させる（#1636 段 2）
type: spec
status: in-progress
related_ids: [FR-05, FR-18, NFR-09, SC-05, ADR-0063, ADR-0086, ADR-0088, ADR-0036, ADR-0119, IADR-0410, IADR-0401, IADR-0329, IADR-0413, IADR-0431, IADR-0474]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1（呼び出し元の主張を判定に用いない）・決定 4
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 3（①所有者 または ②管理者）
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・§残るもの（どこまで運ぶかは実装の裁量）
issue: "#1636"
---

# 仕様書: gRPC の AddTag の管理者の判定を認可サービスへ移す（#1636 段 2）

> 本仕様書は実装着手前に作成する。段 1（許可集合）は `20260927_issue-1636_grpc-trusted-user-context-relays.md`（PR #1645）。本 PR はその上に積む。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**、FR-18 / SC-05（AI タグ提案の承認）、FR-05
- 関連 ADR: **ADR-0088 決定 1**（認可サービスは利用者属性を自ら引き直す。呼び出し元の主張を判定に用いない）、決定 4（`user_id` の詐称は残る半分）、
  **ADR-0063 決定 3**（タグ反映の認可は ①所有者の動的束縛 または ②`platform-admin`）、ADR-0086 決定 1・§残るもの、ADR-0036 D-08 / ADR-0119 決定 3（②は個人資料に及ばない）
- 関連 IADR: **IADR-0410 決定 2**（`user_roles` を別欄で運ぶ。本件で「受け口は評価に用いない」へ改める。追記 2）、**IADR-0401**（`UserDirectory` の狭い読み口。本件で問いを 1 つ足す。追記）、
  IADR-0329 決定 1（IdP を引く主体は認可サービスだけ）、IADR-0431 / IADR-0474（同じ読み口に応答項目・利用者を足した前例）

## 目的・背景

段 1 の後も、`DocumentTagWrite/AddTag` は許可集合の中継者（graph-service）が運んだ `user_roles` に `platform-admin` があれば管理者の上書き（ADR-0063 決定 3 の②）を適用する。
本文のロールは ADR-0088 が閉じた「偽の属性を主張する」と同じ型の主張であり、中継者が 1 つ侵害されれば任意の利用者を管理者として扱える。
#1636 は「`user_roles` を本文から信じること自体をやめる（ロールは認可サービスが引き直す）」を求める。

## 判断: 管理者の上書きは外さず、ロールを認可サービスから引く

### 選択肢

| 案 | 内容 | 評価 |
|---|---|---|
| A | gRPC の経路では管理者の上書きを外す（①所有者だけ） | **採らない。** ②は取り込み文書（`owner=system`）を承認できる**唯一の枝**である（ADR-0063 決定 3）。helm・compose とも graph はタグの反映を gRPC で呼ぶ（`Services__DocumentServiceGrpc`）ので、外すと SC-05 の管理者の承認が配備で壊れる（graph の承認フロー `CanDecideAsync` は ①起点文書の write または ②管理者ロールで通すので、graph は承認を受け付け、document は拒否し 404 になる）。REST へ戻すのは ADR-0086 決定 1・決定 5（利用者トークンを面に通さない・一括移行）に反する |
| **B** | **受け口（DocumentService）が、本文の `user_id` の利用者が `platform-admin` を持つかを認可サービスに問う** | **採用。** ADR-0088 決定 1 の原則（判定の入力となる利用者の性質は認可サービスが IdP から引き直す）を realm ロールへ延ばす。IdP を引けるのは認可サービスだけ（IADR-0329 決定 1）なので、DocumentService は認可サービスの狭い読み口で問う |
| C | 本文のロールを許可集合の中継者に限って信じ続ける（段 1 のまま） | **採らない。** #1636 と依頼の「本文のロールを信じない」に届かない。中継者 1 つの侵害で任意の利用者を管理者にできる |

- **B の後に残るもの**: `user_id` そのものは許可集合の中継者の主張のまま（ADR-0088 決定 4 と同じ残り方）。中継者が侵害されれば、**実在する管理者の名前**を名乗れば②が通る。
  ただし「任意の利用者を管理者にする」ことはできなくなり、名乗った利用者が IdP 上で管理者であり有効であることが要る。閉じる手段は token exchange だけである。

### 設計

1. **認可サービスの `UserDirectory` に rpc `CheckRealmRole(username, role) → (found, has_role)` を足す**（`platform.authz.v1`。追加であり破壊的変更ではない。IADR-0379 決定 2）。
   - 問いは「名指しした 1 人がこの 1 つの realm ロールを持つか」だけ。**ロールの一覧は返さない**（IADR-0401 決定 2「呼び出し元が要る問いだけを面へ出す」）。
   - 実効ロール（合成ロール・既定ロールの展開を含む ＝ トークンの `realm_access.roles` と同じ意味）で答える。Keycloak は `users/{id}/role-mappings/realm/composite`。
   - **無効化された利用者は持たないと答える**（`found=true, has_role=false`）。退職者の名前で②を通さない。
   - 照合: 利用者名は既存の `FindByUsernameAsync`（`exact=true`・大小文字無視・曖昧なら引けなかった）、ロール名は**序数一致**。
   - 「居ない」は `found=false`（応答）、「引けなかった」は gRPC status（既存の読み口と同じ分離）。`username` / `role` の空は `INVALID_ARGUMENT`。
   - 門は既存と同じ `ServiceCaller`（document-service は `platform-service` を持つ）。
   - ポート `IIdentityAdminClient` に `GetEffectiveRealmRolesAsync(userId)` を足す（Keycloak 実装・InMemory 実装・試験の 2 実装）。
2. **呼び出し側の共有クライアント `UserDirectoryGrpcClient.HasRealmRoleAsync(username, role)`**: 持つ → true、持たない・居ない → false、引けなかった → null。書き込みの経路なので締切 5 秒（`WriteTimeLookupTimeout`）。
3. **DocumentService のポート `IApproverRoleDirectory.GetAdminStateAsync(username)` → `Admin` / `NotAdmin` / `Unknown`（0）**。
   - gRPC 実装 `GrpcApproverRoleDirectory`（`Services:AuthorizationServiceGrpc` が在るとき）。未構成なら常に `Unknown` を返す縮退（`UnavailableApproverRoleDirectory`）。
   - 🔴 `Unknown` を 0 に置く（既定値が「管理者」に倒れない）。bool にしない（「管理者でない」と「分からない」を畳まない）。
4. **`AddDocumentTagUseCase` は管理者の判定を遅延で受け取る**（`Func<CancellationToken, ValueTask<bool>>`）。①所有者で書ける、または資料が個人資料（②が及ばない）なら**呼ばない**（認可サービスへの往復を増やさない）。
   REST はトークンのロールを返す関数を渡す（**REST の挙動は変えない**）。
5. **gRPC の `AddTag` は本文の `user_roles` を読まない。** 管理者の判定は `IApproverRoleDirectory` で本文の `user_id` について引く。
   `Unknown` なら `UNAVAILABLE`（graph は `Unavailable` → 502。**「書けない」へ畳まない** —— 認可サービスの障害を「管理者ではない」＝ 404 と記録するのは嘘である）。
6. **proto の `user_roles` は消さない**（フィールド削除は破壊的変更）。「受け口は評価に用いない」と注記する。graph の `GrpcDocumentTagWriter` は**送り続ける**
   （段 1 の document-service は本文のロールを読むので、graph を先に出しても壊れない。撤去は並走が終わった段の判断）。

## 受け入れ基準（段 2）

- AC-1: 許可集合の中継者（graph-service）が `user_roles=["platform-admin"]` を運んでも、認可サービスが管理者でないと答えれば②は通らない（`NOT_WRITABLE`・文書は変わらない）。
- AC-2: 本文に `user_roles` が無くても、認可サービスが管理者と答えれば所有者のいない組織文書へ反映される（②）。
- AC-3: 認可サービスを引けなければ、所有者でない承認者の要求は `UNAVAILABLE`（文書は変わらない）。
- AC-4: 所有者の要求は認可サービスを呼ばずに反映される（引けない状態でも通る）。
- AC-5: 個人資料へは、認可サービスが管理者と答えても管理者は書けず（#1629）、認可サービスも呼ばれない。
- AC-6: `CheckRealmRole`: 管理者 → `found, has_role`、管理者でない → `found, !has_role`、居ない → `!found`、無効化された管理者 → `found, !has_role`、ロール名の大小文字違い → `!has_role`、
  空の引数 → `INVALID_ARGUMENT`、IdP を引けない → 非 OK の status、`platform-service` の無い主体（管理者の利用者トークン）→ `PERMISSION_DENIED`。
- AC-7: Keycloak 実装は実効ロール（`role-mappings/realm/composite`）を読む。
- AC-8: 配備: helm・compose の document-service は `Services__AuthorizationServiceGrpc` を持ち（＝ gRPC 実装が選ばれる）、realm の `document-service` は `platform-service` を持つ。
- AC-9: REST の `POST /documents/{id}/tags` は従来どおりトークンのロールで判定する。
- AC-10: 変異: gRPC の `AddTag` で本文の `user_roles` を再び信じると AC-1 が落ちる。`Unknown` を「管理者ではない」へ畳むと AC-3 が落ちる。無効化の確認を落とすと AC-6 が落ちる。

## 母集合（着手前に引いた）

- `grep -rn "UserRoles\|user_roles" --include=*.cs --include=*.proto src`（`/obj/` を除く）: 書き手は graph の `GrpcDocumentTagWriter` だけ、読み手は DocumentService の `AddTag/GrpcService.cs` だけ（他は試験・proto）。
- `grep -rn ": IIdentityAdminClient" --include=*.cs src`: 実装 4（Keycloak・InMemory・試験の `StubIdentityAdminClient`・`FakeDepartmentIdentity`）。
- `UserDirectory` の呼び出し側: 共有の `UserDirectoryGrpcClient`（DataSourceService・DocumentService・McpServer が登録）。新しい rpc を使うのは DocumentService だけ。
- 配備: document-service は helm `services.document.extraEnv`・compose `document-service` ともに `Services__AuthorizationServiceGrpc: http://authorization-service:8081`
  （退職の窓・同期トークンの所有者・グループの共有先で既に使っている）。

## 配備の順番（段 2）

- **authorization-service を先に配備すること**（新しい rpc）。document-service を先に出すと `CheckRealmRole` が `UNIMPLEMENTED` → `Unknown` → `AddTag` が `UNAVAILABLE` →
  graph の承認が **502**（所有者でない承認者＝管理者の承認だけ。所有者の承認は通る）。authorization-service を出した時点で回復する。
- graph-service の変更は無い（`user_roles` は送り続け、受け口が無視する）。
- 段 1（PR #1645）より後にマージする（本 PR は段 1 の上に積んでいる）。

## 検証

- `dotnet test` 両 slnx、`dotnet format --verify-no-changes` 両 slnx、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異（AC-10）を当てて落ちることを確かめ、`git show HEAD:<path> > <path>` で戻す。
