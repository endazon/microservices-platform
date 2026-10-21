---
title: 作業仕様書 — 起動器が Keycloak の管理者・RabbitMQ のブローカの Secret を公知の既定値で作らない（fail-closed。GHSA-rh44-px44-p48q）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, IADR-0573, IADR-0572, IADR-0485, IADR-0286]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る）
issue: "GHSA-rh44-px44-p48q"
---

# 作業仕様書 — 基盤の資格情報を公知の既定値で作らない（GHSA-rh44-px44-p48q）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0573**（新規）に置く。
> 多層防御の強化（既定を fail-closed にする）の 2 本目。参照は GHSA の ID だけとする。
> 🔴 稼働中のクラスタには何も実行しない。試験はすべて PATH のスタブの下で行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**。計画 ADR: **ADR-0124 決定 1**（矛盾しない。データストアの資格情報は対で書く・初期投入は無いときだけ作る形を変えない）。
- 実装 ADR: **IADR-0573**（新設）。前提 **IADR-0485**（無いときだけ作る）・**IADR-0286**（既定の資格情報を埋め込まない）・**IADR-0572**（同じ一連の 1 本目）。

## 要件（オーナー指示 2026-10-22）

`scripts/k8s-local-up.sh` は keycloak-admin と rabbitmq の Secret を既定値で作らない。値が未指定なら止まり、明示の opt-in（使い捨ての開発環境だけ）でだけ開発用の値を許す。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 判定（明示 × 既存 × opt-in）の真理値表。開発用の値と同じ明示・既存は「指定なし」と同じ | `k8s-local-up.test.js`「IADR-0573: 判定の真理値表」 |
| AC2 | 指定も既存の Secret も無く opt-in も無ければ、[1/7] の直後・何も書かず・[2/7] のビルドの前に、2 つを名指して止まる（dev の context でも） | 同「指定も既存の Secret も無く…」 |
| AC3 | 明示の値で作り、rabbitmq-app も同じ値。値は出力・引数に出ない | 同「明示の値（開発用でない）で作り…」 |
| AC4 | 指定が無くても既存の Secret が開発用でない値なら使う（再実行で稼働中の値を戻さない）。明示は既存に勝つ | 同「既存の Secret が開発用でない値を持てば…」 |
| AC5 | 既存・明示が開発用の値なら止める | 同「既存の Secret・明示の値が開発用の値と同じなら止める」 |
| AC6 | opt-in は `1` だけ。通すときは警告する | 同「opt-in（ALLOW_DEV_INFRA_CREDENTIALS=1）だけが…」 |
| AC7 | 起動器の Secret の作成に既定値の代わり（`${…:-admin}` 等）が残らない | 同「起動器の Secret の作成に公知の既定値の代わりが残っていない」 |
| AC8 | CI の使い捨てスタック（integration-stack / cutover-rehearsal）は opt-in を付ける（止まらない） | 同「陽性対照: CI の…」 |
| AC9 | Vault の種（`bootstrap.sh`）は無い KV だけを判定し、止めるときは Vault へ何も書かない。明示・opt-in・在る KV で進む | `scripts.repo.test.js` の IADR-0573 節・#1793 の既定値の試験（opt-in つきへ改めた） |

## 方式

- 判定は新設の `scripts/lib/infra-credentials.sh` 1 本（`infra_cred_decide` は純関数）。起動器は `infra_cred_resolve`（既存の Secret を読み、選んだ値を export）、種は `infra_cred_guard`（無い KV だけ・明示の値だけで判定）を使う。
- 選んだ値を export するので、[5/7] の `rabbitmq-app`・ESO の段の `bootstrap.sh` が同じ値を使う（ブローカとアプリが食い違わない）。
- 利用者名（`KEYCLOAK_ADMIN_USER` / `RABBITMQ_USER`）は識別子なので対象外（従来どおり既定 admin / guest）。
- `postgres` などほかの基盤の資格情報は今回の対象外（指示の範囲）。

## 母集合（規則 9・10）

走査語: `KEYCLOAK_ADMIN_PASSWORD:-` / `RABBITMQ_PASSWORD:-` / `k8s-local-up.sh --live`（追跡ファイル全体）。

| 場所 | 扱い |
| --- | --- |
| `scripts/k8s-local-up.sh`（3 か所の `:-admin` / `:-guest`） | 直す（`:?` にし、値は判定器が決める） |
| `deploy/local/vault/eso/bootstrap.sh`（`msp/rabbitmq`・`msp/rabbitmq-app`・`msp/keycloak-admin` の種） | 前に判定を置く（種の字面は C# の `SecretItemBootstrapSeedTests` が固定するので変えない。opt-in のときだけ既定値になる） |
| `.github/workflows/integration-stack.yml`・`cutover-rehearsal.yml`（起動器の 3 呼び出し） | opt-in を付ける |
| `deploy/docker-compose.yml` | 対象外（kube context を持たない手元専用の経路。IADR-0517 残余 4 と同じ扱い） |
| `docs/operations/*` の `bash scripts/k8s-local-up.sh --live` の例（多数） | 直さない（止めたときのメッセージが対処を告げる。正本は `deploy/local/README.md`「起動」と下の 2 文書） |
| `deploy/local/README.md`「起動」・`docs/security/security.md`・`docs/operations/paired-secret-rotation-runbook.md` | 直す |

## 変異

| 変異 | 赤になる試験 |
| --- | --- |
| M1: 起動器の判定の呼び出しを外す | AC2・AC5・AC7 |
| M2: 開発用の値と同じ既存の値を使う（`existing` の条件から `!= dev` を外す） | AC1・AC5 |
| M3: opt-in を `1` 以外でも通す | AC1・AC6 |
| M4: `bootstrap.sh` の判定を外す | AC9 |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: **オーナーの入れ替えの後に配備する**（既存の Secret が開発用の値のままだと、次の起動器の実行が止まる）。

## 残余

- `postgres`・`object-storage-credentials` 等は従来どおり既定値で作る（今回の範囲外）。
- 既存の Secret の値を使う形は、Secret が開発用でない値を持つ限り env を求めない（運用の負担を増やさない）。
