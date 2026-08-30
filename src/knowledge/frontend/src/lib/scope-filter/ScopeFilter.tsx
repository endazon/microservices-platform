import { Trans } from '@lingui/react/macro';
import { msg } from '@lingui/core/macro';
import { cn } from '@platform/ui';
import { i18n } from '@foundation/i18n';
import {
  SCOPE_AXES,
  scopeAxisLabel,
  selectedCount,
  toggleScopeValue,
  type ScopeAxis,
  type ScopeSelection,
} from './scopeSelection';
import { useScopeCandidates } from './useScopeCandidates';

// FR-04, FR-05, SC-01, SC-08, UC-01, UC-02, #539: 対象範囲フィルタ。
//
// **SC-01（検索・質問）と SC-08（AI 分析）が同じ部品を使う。** 計画が
// 「同じ候補 API を用いる」「SC-01 と一体で扱う」と定めており、
// **画面ごとに違う挙動にすると利用者が操作を覚え直すことになる**（裁定 Q3）。
//
// **軸はタグ・部門・プロジェクトの 3 つで、「フォルダ」は無い**（裁定 Q9）。
//
// **候補は権限内に限る。** 絞り込みはサーバ側（`/bff/attribute-values`）の責務であり、
// 画面はここへ何も足さない（存在秘匿の境界を 2 か所に割らない）。

// `aria-label` は属性なので `<Trans>` で囲めない。マクロで記述子を作り `i18n._` で解決する
// （カタログの網羅検査 [[IADR-0125]] 決定 4 に載せるため、素の文字列にしない）。
const SCOPE_FILTER_LABEL = msg`対象範囲フィルタ`;

export interface ScopeFilterProps {
  selection: ScopeSelection;
  onChange: (next: ScopeSelection) => void;
  /** 送信中などに操作を止める。 */
  disabled?: boolean;
}

export function ScopeFilter({ selection, onChange, disabled = false }: ScopeFilterProps) {
  const { candidates, loading } = useScopeCandidates();
  const count = selectedCount(selection);
  const hasAnyCandidate = SCOPE_AXES.some((axis) => candidates[axis].length > 0);

  return (
    <section aria-label={i18n._(SCOPE_FILTER_LABEL)} className="flex flex-col gap-2">
      <div className="flex items-baseline gap-2">
        <h2 className="text-sm font-medium">
          <Trans>対象範囲</Trans>
        </h2>
        {/* 件数を文字で出す。**選択状態を色だけで表さない**（INDEX 決定 21）。 */}
        <p className="text-xs text-[--color-fg-muted]">
          {count > 0 ? <Trans>{count} 件で絞り込み中</Trans> : <Trans>すべてが対象</Trans>}
        </p>
      </div>

      {loading ? (
        <p className="text-xs text-[--color-fg-muted]">
          <Trans>候補を読み込み中…</Trans>
        </p>
      ) : !hasAnyCandidate ? (
        // **「候補が無い」と「権限が無い」を区別しない中立文言**（存在秘匿。IADR-0009）。
        <p className="text-xs text-[--color-fg-muted]">
          <Trans>絞り込める候補はありません。</Trans>
        </p>
      ) : (
        SCOPE_AXES.filter((axis) => candidates[axis].length > 0).map((axis) => (
          <ScopeAxisRow
            key={axis}
            axis={axis}
            values={candidates[axis]}
            selected={selection[axis]}
            disabled={disabled}
            onToggle={(value) => onChange(toggleScopeValue(selection, axis, value))}
          />
        ))
      )}
    </section>
  );
}

function ScopeAxisRow({
  axis,
  values,
  selected,
  disabled,
  onToggle,
}: {
  axis: ScopeAxis;
  values: string[];
  selected: string[];
  disabled: boolean;
  onToggle: (value: string) => void;
}) {
  const label = i18n._(scopeAxisLabel(axis));
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <span className="text-xs text-[--color-fg-muted] min-w-16">{label}</span>
      {values.map((value) => {
        const isSelected = selected.includes(value);
        return (
          <button
            key={value}
            type="button"
            // **選択状態は `aria-pressed` で機械にも伝える**（色・枠線だけに載せない）。
            aria-pressed={isSelected}
            disabled={disabled}
            onClick={() => onToggle(value)}
            className={cn(
              'inline-flex items-center gap-1 rounded-[--radius-control] px-2 py-0.5 text-xs',
              'disabled:opacity-50',
              isSelected
                ? 'bg-[--color-brand] text-[--color-brand-fg]'
                : 'border border-[--color-border] text-[--color-fg-muted]',
            )}
          >
            {/* **選択を文字（✓）でも示す。** 色を除いても選択中だと分かること（INDEX 決定 21）。 */}
            {isSelected ? <span aria-hidden="true">✓</span> : null}
            {value}
          </button>
        );
      })}
    </div>
  );
}
