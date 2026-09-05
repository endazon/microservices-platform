import { Trans, useLingui } from '@lingui/react/macro';
import {
  CONFIDENTIALITY_KEY,
  DEPARTMENT_KEY,
  LIFECYCLE_KEY,
  UNRESOLVED_DEPARTMENT,
  UNRESOLVED_OWNER,
} from '../../../lib/abac';
import type { DataSourceDto } from '@foundation/api/generated/bff.schemas';

// FR-05, UC-04, SC-06, ADR-0036, ADR-0074 決定 1 (#1252): 既定属性 3 つと `owner` 写像表の
// **読み取り専用の表示**。
//
// 🔴 **これを置く理由は権限の非対称にある。** ADR-0074 決定 1 は写像表を「既定属性 3 つと
// **同じ面・同じ権限**（**閲覧は管理者・運用者**、登録・更新は管理者限定）」に置くと定める。
// 契約（`DataSourceDto.DefaultAttributes` / `OwnerMappings`）と BFF（一覧・個別取得は
// `RequireRole(Admin, Operator)`）は閲覧を運用者へ開いているのに、**画面はこれらを
// 「既定属性」ボタン（管理者のみ）で開くフォームでしか描いていなかった** ——
// 「同じ権限」が「運用者にはどちらも見えない」という形でしか成立していなかった（#1252）。
//
// **管理者にも同じものを見せる。** 権限で**内容**を出し分けない —— 出し分けるのは
// 編集の口（「既定属性」ボタン）だけである。内容の出し分けを足すと、
// 「管理者が見ている値」と「運用者が見ている値」が別物になり得る面が 1 つ増える。
//
// **色だけで意味を持たせない**（INDEX 決定 21）。ここはラベル文字列と値の対だけで構成し、
// 状態を色で表さない。**表示文言は `@platform/ui` に入れない**（features 側の `<Trans>`）。

/** 値が無いことの表示。**空欄にしない** —— 空欄は「取得できていない」とも読めるため。 */
function Unset() {
  return (
    <span className="text-[--color-fg-muted]">
      <Trans>未設定</Trans>
    </span>
  );
}

export function DataSourceAttributesView({ source }: { source: DataSourceDto }) {
  const { t } = useLingui();
  const attributes = source.defaultAttributes ?? {};
  const mappings = Object.entries(source.ownerMappings ?? {}).sort(([a], [b]) =>
    a < b ? -1 : a > b ? 1 : 0,
  );

  const confidentiality = attributes[CONFIDENTIALITY_KEY] ?? '';
  const department = attributes[DEPARTMENT_KEY] ?? '';
  const lifecycle = attributes[LIFECYCLE_KEY] ?? '';

  return (
    <div className="mt-1 text-xs text-[--color-fg-muted]">
      <dl aria-label={t`既定属性と所有者の写像`} className="flex flex-wrap gap-x-3 gap-y-0.5">
        <div className="flex gap-1">
          <dt>
            <Trans>既定の機密区分</Trans>:
          </dt>
          <dd>{confidentiality ? <code>{confidentiality}</code> : <Unset />}</dd>
        </div>
        <div className="flex gap-1">
          <dt>
            <Trans>既定の部門</Trans>:
          </dt>
          {/* **予約値はそのまま出す。** `unassigned` は「解決できなかったことの記録」であって
              部門名ではない（`lib/abac/department.ts`）。編集フォームは入力欄へ出さない
              （明示指定として送り返されるため）が、**読み取り専用の面では隠すほうが害である** ——
              隠すと「未設定」と区別できず、環流債務の件数を画面から読めなくなる。 */}
          <dd>
            {department ? (
              <code>{department}</code>
            ) : (
              <span>
                <Trans>未設定（予約値 {UNRESOLVED_DEPARTMENT} が入ります）</Trans>
              </span>
            )}
          </dd>
        </div>
        <div className="flex gap-1">
          <dt>
            <Trans>既定のライフサイクル状態</Trans>:
          </dt>
          <dd>{lifecycle ? <code>{lifecycle}</code> : <Unset />}</dd>
        </div>
      </dl>

      <div className="mt-0.5">
        <span>
          <Trans>所有者の写像</Trans>:
        </span>{' '}
        {mappings.length === 0 ? (
          <span>
            <Trans>未登録（写像に無い利用者は予約値 {UNRESOLVED_OWNER} になります）</Trans>
          </span>
        ) : (
          <ul aria-label={t`所有者の写像`} className="inline-flex flex-wrap gap-x-2">
            {mappings.map(([sourceUserId, userId]) => (
              // 読み上げでも対の向きが読めるよう、行そのものにラベルを持たせる
              // （矢印は装飾であり、読み上げでは無音になる）。
              <li key={sourceUserId} aria-label={t`${sourceUserId} は ${userId} に対応します`}>
                <code>{sourceUserId}</code>
                <span aria-hidden="true"> → </span>
                <code>{userId}</code>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
