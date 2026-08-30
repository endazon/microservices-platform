import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
} from '@platform/ui';
import {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
  DEFAULT_LIFECYCLE,
  DEPARTMENT_KEY,
  LIFECYCLE_KEY,
  LIFECYCLE_VALUES,
  UNRESOLVED_DEPARTMENT,
} from '../../../lib/abac';
import type { DataSourceDto, PatchDataSourceRequest } from '@foundation/api/generated/bff.schemas';

// FR-05, UC-04, SC-06（#754）: 登録済みデータソースの**既定属性の更新フォーム**。
//
// 計画 05_screens §SC-06「既定属性の入力欄」（確定・2026-08-16。裁定依頼 planning#372）は
// 「**登録・更新フォーム**はデータソースの既定属性 3 つ（`confidentiality` / `department` /
// `lifecycle`）を持つ」と定める。**登録側は #767 / #796 で着地したが、更新側が無かった** ——
// そのため登録済みソースの部門は後から設定できず、供給源②（データソースの既定属性）が
// 登録時の 1 回しか開かなかった。本フォームがそれを開く。
//
// 🔴 **PUT ではなく PATCH を使う。** PUT は `config` の明示を要求し、GET 応答の
// **マスク済みの値（`***`）を書き戻すと秘密を破壊する**（IADR-0053 / IADR-0148 決定 6）。
// PATCH は `config` を省略でき（null＝現状維持）、この経路を踏まない。
//
// 🔴 **`defaultAttributes` は「指定したときのみ差し替える」＝全置換である**
// （バックエンド `DataSource.Patch`）。部分的に送ると**他のキーが落ちる**ため、
// 本フォームは**既存の属性を土台にして、自分が管理する 3 キーだけを重ねた完全な地図**を送る。
// 管理者が API 経由で明示指定した `owner` 等を、画面からの属性更新で消さないためである。

export function DataSourceAttributesForm({
  source,
  submitting,
  onCancel,
  onSubmit,
}: {
  source: DataSourceDto;
  submitting: boolean;
  onCancel: () => void;
  onSubmit: (input: PatchDataSourceRequest) => void;
}) {
  const { t } = useLingui();
  const current = source.defaultAttributes ?? {};
  // lingui/no-expression-in-message: メッセージへ埋められるのは**素の変数**だけである
  // （`source.name` のようなプロパティ参照は抽出時に名前を持てない）。
  const sourceName = source.name;

  const [confidentiality, setConfidentiality] = useState<string>(
    current[CONFIDENTIALITY_KEY] || DEFAULT_CONFIDENTIALITY,
  );

  // FR-05, UC-04, SC-06: **予約値は空欄として見せる。**
  // `unassigned` は「解決できなかったことの記録」であって部門名ではない（abac/department.ts）。
  // そのまま入力欄へ出すと、管理者がそれを実在の部門名と読み、**明示指定として送り返す**。
  // すると「解決できなかった」と「管理者がそう指定した」の区別が消える。
  const storedDepartment = current[DEPARTMENT_KEY] ?? '';
  const [department, setDepartment] = useState(
    storedDepartment === UNRESOLVED_DEPARTMENT ? '' : storedDepartment,
  );

  // FR-05, UC-04, SC-06: `lifecycle` は `department` と扱いが違う。**`active` は予約値ではなく
  // そう決めた既定値**であり（abac/lifecycle.ts）、保存済みの値をそのまま出して送り返しても
  // 記録の意味は変わらない（保存地図には既にこの値が入っている）。**「未指定」も選べる。**
  const [lifecycle, setLifecycle] = useState(current[LIFECYCLE_KEY] ?? '');

  // 値域に無い機密区分が保存されている場合でも、現在値を選択肢として見せる（黙って別の値へ
  // 倒さない）。**実装が値集合を決めない**という abac/confidentiality.ts の方針に従う。
  const confidentialityOptions: string[] = CONFIDENTIALITY_VALUES.includes(
    confidentiality as (typeof CONFIDENTIALITY_VALUES)[number],
  )
    ? [...CONFIDENTIALITY_VALUES]
    : [confidentiality, ...CONFIDENTIALITY_VALUES];

  return (
    <Card className="mb-3">
      <CardHeader>
        <CardTitle>
          <Trans>既定属性を編集: {sourceName}</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <form
          aria-label={t`既定属性の編集`}
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (submitting) return;

            // 全置換セマンティクスに合わせ、**自分が管理しないキーは保った土台**を作る。
            const next: Record<string, string> = { ...current };
            delete next[CONFIDENTIALITY_KEY];
            delete next[DEPARTMENT_KEY];
            delete next[LIFECYCLE_KEY];

            // 未入力の `department` / 未指定の `lifecycle` は**キーごと送らない**（登録側と同じ
            // 規約。#767 / #796）。値の有無ではなく**キーの有無**が「指定しなかった」を表す。
            const trimmedDepartment = department.trim();
            onSubmit({
              defaultAttributes: {
                ...next,
                [CONFIDENTIALITY_KEY]: confidentiality,
                ...(trimmedDepartment ? { [DEPARTMENT_KEY]: trimmedDepartment } : {}),
                ...(lifecycle ? { [LIFECYCLE_KEY]: lifecycle } : {}),
              },
            });
          }}
        >
          <div>
            <Label htmlFor="ds-edit-conf">
              <Trans>既定の機密区分</Trans>
            </Label>
            <Select
              id="ds-edit-conf"
              value={confidentiality}
              onChange={(e) => setConfidentiality(e.target.value)}
            >
              {confidentialityOptions.map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </Select>
          </div>

          <div>
            <Label htmlFor="ds-edit-dept">
              <Trans>既定の部門</Trans>
            </Label>
            <Input
              id="ds-edit-dept"
              value={department}
              aria-describedby="ds-edit-dept-hint"
              onChange={(e) => setDepartment(e.target.value)}
              placeholder={t`例: 開発`}
            />
            <p id="ds-edit-dept-hint" className="text-xs text-[--color-fg-muted]">
              <Trans>未入力のときは予約値 {UNRESOLVED_DEPARTMENT} が入ります。</Trans>
            </p>
          </div>

          <div>
            <Label htmlFor="ds-edit-lifecycle">
              <Trans>既定のライフサイクル状態</Trans>
            </Label>
            <Select
              id="ds-edit-lifecycle"
              value={lifecycle}
              aria-describedby="ds-edit-lifecycle-hint"
              onChange={(e) => setLifecycle(e.target.value)}
            >
              <option value="">{t`未指定`}</option>
              {LIFECYCLE_VALUES.map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </Select>
            <p id="ds-edit-lifecycle-hint" className="text-xs text-[--color-fg-muted]">
              <Trans>未指定のときは既定値 {DEFAULT_LIFECYCLE} が入ります。</Trans>
            </p>
          </div>

          {/* 既定属性が効くのは**これ以降に取り込まれる文書**である。取り込み済みの文書の属性は
              この操作では変わらない（遡及適用は #516 の裁定待ち）。誤解を招くため明示する。 */}
          <p className="text-xs text-[--color-fg-muted]">
            <Trans>
              既定属性は、これ以降に取り込まれる文書に適用されます。取り込み済みの文書は変わりません。
            </Trans>
          </p>

          <div className="flex gap-2">
            <Button type="submit" variant="primary" disabled={submitting}>
              <Trans>更新する</Trans>
            </Button>
            <Button type="button" onClick={onCancel}>
              <Trans>キャンセル</Trans>
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
