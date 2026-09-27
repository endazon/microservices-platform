---
title: SC-09 のポリシーの編集器で動的束縛（${current_user}・${current_groups}）と owner・shared_with の条件を作れるようにする（#1666）
type: spec
status: done
related_ids: [FR-05, FR-09, FR-19, SC-09, UC-05, ADR-0121, ADR-0036, ADR-0098, IADR-0129, IADR-0253, IADR-0341, IADR-0447, IADR-0480]
author: claude
created: 2026-09-27
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 1・決定 6・実測 7・フォローアップ 5
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-02・D-03・D-06・フォローアップ 5
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §動的束縛（read 規則の第 2・第 3 節）
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-09（必須のポリシー・入力 / バリデーション）
issue: "#1666"
---

# 仕様書: SC-09 のポリシーの編集器で動的束縛と owner・shared_with の条件を作れるようにする（#1666）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、`origin/main`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-05**（ABAC）、**FR-09**（属性・ポリシー管理）、FR-19（個人資料の共有）、UC-05、**SC-09**
- 計画 ADR: **ADR-0121** 決定 1（所有者の read ポリシーは本番でもシステム管理者が SC-09 から投入する。SC-09 が動的束縛の入力に対応した後）・
  決定 6（それまでは API へ直接投入）・実測 7（今の SC-09 では作れない）・フォローアップ 5。
  **ADR-0036** D-02（所有者は `doc.owner ∈ { ${current_user} }`＝許容値の集合の側に束縛変数。共有先は主体の側を束縛で照合）・
  D-03（束縛変数は `${current_user}` と `${current_groups}` の 2 つだけ。任意の式は書けない）・D-06（共有は個人とグループ）・フォローアップ 5。
  ADR-0098 決定 1（`${current_groups}` は Keycloak のグループ ID）。
- 計画 07_abac-attribute-model §動的束縛: 所有者 `doc.owner ∈ { ${current_user} }`／共有先 `${current_user} ∈ doc.shared_with` または
  `doc.shared_with ∩ ${current_groups} ≠ ∅`（＝ `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅`）。
- 関連 IADR: IADR-0129（構造化エディタ・自由記述の条件式を実装しない）、IADR-0341（下書きのフック）、IADR-0253（束縛は評価器の中だけ）、
  IADR-0447（共有先の分岐を 1 本のポリシーで表す）、IADR-0480（所有者の read ポリシーの seed と配備の手順）
- 起点 issue: #1666

## 目的・背景

- SC-09 のポリシーの編集器は、条件の属性を属性辞書から選び、値も辞書の許可値から選ぶ。`owner` は辞書に無く、`${current_user}` を選ぶ手段も無い。
  したがって今の画面からは所有者の read ポリシーも共有先のポリシーも作れない（ADR-0121 実測 7）。
- ポリシーの API（SC-09 と同じ口）は、未定義のキーも束縛の値も素通しで受け付ける（`AbacValidation.ValidateConditions` は未定義キーを許容する）。
  **`${current_usr}` のような綴り違いも保存でき、評価器はそれをリテラルとして残す**（どの文書にも一致しない＝静かに効かないポリシー）。

## 計画の判断が要る点の確認（着手前）

issue は「表示の形・どの属性に動的束縛を許すか」に判断が要るなら止めよと書く。次のとおり、どちらも計画から一意に導けるので止めない。

| 点 | 計画の記述 | 実装の形 |
| --- | --- | --- |
| どの属性に許すか | 計画が束縛を置く場所は 2 つだけ: 所有者 `doc.owner ∈ { ${current_user} }`（D-02）、共有先 `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups})`（07 §動的束縛・D-06）。どちらも**文書の条件**である | `owner` → `${current_user}` のみ。`shared_with` → `${current_user}`・`${current_groups}`。**文書の条件だけ**。利用者の条件と他の属性には許さない |
| 表示の形 | issue: 色だけでなくテキストでも示す。画面の既存の規約（INDEX 決定 21・`Tag` は分類の名前） | 条件の値の選択肢・積んだ条件・一覧の要約の 3 か所で、束縛の値を「動的束縛」という語と平易な説明（「操作する利用者本人」「操作する利用者の所属グループ」）で示し、記法（`${current_user}`）も併記する |
| 自由入力 | issue: 許さない。計画の入力表「対象属性｜選択｜定義済み属性のみ」 | 値は `Select` だけ。`owner`・`shared_with` の選択肢は束縛の値だけ（辞書にそのキーが定義されていれば辞書の許可値も） |

`owner`・`shared_with` を「定義済み属性」に含めてよいかについて: 両者は計画 07 §文書属性に**定義された属性**であり（`owner` は必須属性・`shared_with` は任意属性）、
属性辞書（管理者が値集合を定義するもの）に載らないのは値が利用者名・グループ ID で列挙できないためである（seed の注記）。
画面は「計画が定義する束縛の位置」だけを辞書の外から足す。辞書の外の任意のキーは引き続き選べない。

## 設計

### 画面（`src/knowledge/frontend/src/features/sc09-admin-abac/`）

1. `types/abacVocabulary.ts` に束縛の語彙を置く（純関数・値集合は計画の写し）。
   - `DYNAMIC_BINDINGS = { owner: ['${current_user}'], shared_with: ['${current_user}', '${current_groups}'] }`（文書の条件だけ）。
   - `isDynamicBinding(value)`・`dynamicBindingLabel(value)`（「操作する利用者本人」「操作する利用者の所属グループ」）。
   - `policyAttributeOptions(attributes)`: 属性辞書の各属性（値＝許可値）に、束縛の位置を足した選択肢の一覧を返す。
     - 辞書に同じキーの**文書**属性があれば、その許可値の後ろへ束縛の値を足す（重複しない）。
     - 辞書にそのキーが**どのスコープにも無い**ときだけ、文書属性の選択肢（ラベル「所有者」「共有先」）を足す。
     - 利用者属性には束縛を足さない。
2. `hooks/usePolicyDraft.ts` は属性辞書ではなく上の選択肢を引く（scope は選択肢から採る規則は変えない）。
3. `components/PolicyEditorPanel.tsx`
   - 条件の値の `option` は、束縛なら「動的束縛: 操作する利用者本人（${current_user}）」の形の文にする。
   - 積んだ条件のチップと一覧の要約は、束縛の値を `Tag`（`outline`）の「動的束縛」＋説明＋記法で示す（色だけにしない）。
4. i18n カタログ（ja / en）を再生成してコミットする。記法（`${…}`）は翻訳文へ入れない（ICU の `{}` と衝突するため。部品で並べる）。

### サーバー（`AuthorizationService/Domain/AbacValidation.cs`。最小）

`ValidatePolicy` に束縛の検証を足す（dry-run と保存は同じ関数を通るので両方に効く）。

- 値に `${` を含むものを「束縛の形」とみなす。
  - `${current_user}`・`${current_groups}` 以外は拒否（D-03。綴り違いが静かに効かないポリシーになるのを止める）。
  - 利用者の条件に束縛があれば拒否（評価器は利用者の条件を束縛しない＝どの利用者にも一致しない）。
  - 文書の条件で、束縛を許す位置は `owner` → `${current_user}`、`shared_with` → 両方。それ以外の組は拒否。
- 辞書に `owner`・`shared_with` が定義されている場合でも、上で許した束縛の値は「辞書外の値」として拒否しない（画面が出す選択肢と整合させる）。
- 評価器・契約・BFF は変えない。**既存の保存済みポリシーは検証し直さない**（検証は作成と dry-run の時だけ）。

### 文書

- `docs/screens/SC-09_admin-abac-settings.md`: 入力表（対象属性・条件の値）、§ポリシー定義、hi-fi 対応 #8・#10 の備考。
- `docs/tests/SC-09_admin-abac-settings.md`: 画面・純関数・認可サービスの節に行を足す（テスト ID は push 直前に develop の最大を再確認）。
- `docs/tests/FR-09_abac-attribute-policy-management.md`: `AbacValidationTests` の行を足す。
- IADR を新設（push 直前に develop の最大＋1）。

## 受け入れ基準

- AC-1: 画面の操作だけで所有者の read ポリシー（`action=read`・`userConditions={}`・`documentConditions={owner:["${current_user}"]}`）を保存できる（送信本文で固定）。
- AC-2: 共有先のポリシー（`documentConditions={shared_with:["${current_user}","${current_groups}"]}`）も画面の操作だけで作れる。
- AC-3: 値は選択だけで、自由入力の欄が無い。辞書の属性（例 `confidentiality`）の選択肢に束縛の値は出ない。利用者属性にも出ない。
- AC-4: 束縛の値は、選択肢・積んだ条件・一覧の要約で「動的束縛」という語で示される（色だけでない）。未知の値は生値のまま出す。
- AC-5: サーバーは未知の束縛変数・利用者の条件の束縛・許されない位置の束縛を 400（dry-run では `valid=false`）にし、所有者と共有先の形は通す。
  辞書に `owner`・`shared_with` が定義されていても許した束縛の値は通る。dev seed の全ポリシーが検証を通る。
- AC-6: 変異 3 件以上で試験が赤になる。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n -E '画面からは作れない|画面から作れない|画面からは(この)?ポリシーを作れない'`（この変更で誤りになる記述）
- `git grep -n -E 'SC-09 が動的束縛|動的束縛に対応'`
- `git grep -n -E 'owner は attributes.json|辞書に入れる意味が無い'`
- `git grep -n -E '定義済み属性のみ'`
- `git grep -n -E 'current_user|current_groups|動的束縛' -- 'src/*.ts' 'src/*.tsx' docs/screens docs/tests/SC-09* docs/functional/FR-09* docs/data/abac-policy.md`
- `git grep -n 'ValidatePolicy('`（検証の呼び出し元）・`git grep -n -E '"\$\{' -- '*.cs'`（束縛の値を使う試験）

### 結果（変えるもの）

- 画面: `abacVocabulary.ts`・`usePolicyDraft.ts`・`PolicyEditorPanel.tsx` とその試験 3 本、`platform/frontend/e2e/sc09-admin-abac.smoke.spec.ts`、
  i18n カタログ（`knowledge/frontend` と `platform/frontend` のうち抽出先）。
- サーバー: `AbacValidation.cs`・`AbacValidationTests.cs`。呼び出し元は `AuthzEndpoints.cs`（保存）と `ValidatePolicy/Endpoint.cs`（dry-run）の 2 つで、同じ関数を通る（変えない）。
- 文書: `docs/screens/SC-09_admin-abac-settings.md`（入力表 164–165 行・hi-fi 対応）、`docs/tests/SC-09_admin-abac-settings.md`、
  `docs/tests/FR-09_abac-attribute-policy-management.md`、`scripts/test-spec-coverage-baseline.json`（必要なら `--update`）。
- `deploy/local/abac-seed/policies.json` の注記 22–23 行（「owner は attributes.json へ登録しない。検証器は未定義キーを許容し」）: 記述は本件の後も正しい
  （未定義キーの許容は変えない）。［2026-09-28 追記］**変えないことにした** —— 並行の #1665 が同じ seed の所有者のポリシーを扱っており、
  正しい記述に触って衝突の面を増やさない。seed の全ポリシーが新しい検証を通ることは試験で固定する（T-70）。

### 除外したもの（理由）

- **`docs/operations/operations.md:425`（「画面からはこのポリシーを作れない」）**: 本件で**誤りになる**が、並行の #1665 が同じファイルを触っているため本件では触らない
  （指示による）。報告に残し、どちらかのマージ後に追随する。
- `.ai-context/adr/IADR-0480`・`IADR-0133:61`・`IADR-0253:186`: 凍結記録。当時の状態の記述であり誤りではない（IADR-0253 は write の画面追随の話で本件と別）。
- `.ai-context/specs/` の過去の作業仕様書: point-in-time の記録。
- `AbacEvaluatorTests.cs` の `${current_department}`（未知のプレースホルダをリテラルで残すことの試験）: 評価器を直接呼び、検証器を通らない。変えない。
- SC-19 の共有・`lib/abac/owner.ts`・生成物 `bff.schemas.ts` の `current_*` の言及: 本件の編集器と無関係（束縛の意味の説明）。
- `docs/tests/SC-12_mcp-client-management.md` T-06（属性の選択肢）: 利用者属性だけを出す別画面。束縛は利用者属性へ足さないので影響しない。
- 対象アクションの `write` の画面追随（画面仕様書 163 行・純関数 P1）: 別作業として記録済み。本件は read の所有者・共有先で足り、範囲を広げない。
- 認可サービスの評価器・契約・BFF: 変えない（#1665 と #1671 の並行作業と重ならないよう最小にする）。

## 検証

- `src/` で `pnpm run lint`・`typecheck`・`format:check`・`test:coverage`・`i18n` の差分なし・`node scripts/check-i18n-catalogs.js`、SC-09 の E2E
- `AuthorizationService.Tests` と `dotnet format --verify-no-changes`（platform）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、check-trace-blocks / check-test-spec-coverage / check-test-traceability / check-cross-repo-refs /
  check-plan-id-qualification / gen-knowledge-graph --check / check-commit-messages --range=origin/develop..HEAD
- 変異 3 件以上（コミット後に当て、`git show HEAD:<path> > <path>` で戻す）

### 結果（2026-09-28・ローカル）

- `src/`: `pnpm run lint`（0 エラー・警告 12 件は develop と同数）・`typecheck`（全ワークスペース。`src/ai-stock-trading` の submodule を初期化して実行）・
  `format:check`・`test:coverage`（148 ファイル・1807 件合格・しきい値内。exit 0）・`pnpm run i18n` の再生成差分なし・`node scripts/check-i18n-catalogs.js` OK
- E2E: `sc09-admin-abac.smoke.spec.ts` 5 件合格（新設 E4 を含む。ビルド済みプレビュー・Chromium は `/opt/pw-browsers/chromium` を一時設定で指定）
- `AuthorizationService.Tests` 511 件合格、`dotnet format src/platform/backend/backend.slnx --verify-no-changes` exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 843 件合格、check-trace-blocks / check-test-spec-coverage（`--update` で床の対 415 件。
  SC-09 のテスト仕様書 × `AbacValidationTests`・`PolicyDryRunValidationTests` の 2 対が増えた）/ check-test-traceability / check-cross-repo-refs /
  check-plan-id-qualification / gen-knowledge-graph --check / check-commit-messages --range=origin/develop..HEAD: OK
- 変異（コミット済みの状態で当て、`git show HEAD:<path> > <path>` で戻した）:
  - M1 辞書に無い `owner`・`shared_with` の選択肢の値を空にする: 4 件赤（純関数 P13・下書き・画面の所有者／共有先）
  - M2 サーバの束縛の検証を外す: 8 件赤（T-69 の 7 件・dry-run と保存の一致）
  - M3 下書きが選択肢に無い値も積む: 1 件赤（`refuses to stack a value that is not one of the offered choices`）
  - M4 サーバの表で `owner` に `${current_groups}` を許す: 1 件赤（位置違いの拒否）
  - M5 画面が束縛を「動的束縛」の文言で示さない: 2 件赤（選択肢・チップ・一覧）
  - M6 辞書の許可値の検証が表の束縛も「辞書外」とする: 1 件赤（T-70）

