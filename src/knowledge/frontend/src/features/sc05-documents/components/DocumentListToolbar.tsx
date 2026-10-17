import { useEffect, useId, useRef, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import { Button, Input, Label, Select } from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { DOCUMENT_TITLE_QUERY_MAX, hasConditions } from '../api/useDocumentAdmin';
import type {
  DocumentConfidentialityFilter,
  DocumentListConditions,
  DocumentSort,
  DocumentStatusFilter,
} from '../api/useDocumentAdmin';
import { DOCUMENT_STATUSES, documentStatusView } from '../types/documentStatus';
import { NO_PROJECT, sourceLabel } from '../types/documentSource';
import { CONFIDENTIALITY_VALUES } from '../../../lib/abac';

// SC-05, FR-06（#1923 段 2・[[IADR-0545]]）: 文書一覧の**操作の帯**（表題の検索・状態の絞り込み・並べ替え・条件の解除）。
// ［2026-10-17 / #1923・[[IADR-0561]]］データソース（件数つき）・機密区分・タグ（辞書）・プロジェクト（件数つき）・保存日の範囲を足した。
//
// 並べ替えは文書サービス、絞り込みは BFF が ABAC の後に当てる。件数（状態ごとの数）は BFF が権限で絞った後の数であり、
// ここは受け取った数を描くだけで、権限の有無を推し量る表示を足さない。
// 画面の部品だけで組む（`Input` / `Select` / `Button` / `Label`）。共有 UI へ新しいプリミティブは足さない。

/** 表題の検索を打ち終えてから引くまでの待ち（ミリ秒）。1 打鍵ごとに台帳を 1 周させない（IADR-0537 の残余）。 */
const TITLE_QUERY_DEBOUNCE_MS = 300;

interface DocumentListToolbarProps {
  sort: DocumentSort;
  /** 確定した条件。`q` は確定した（引いた）検索語で、入力中の値は帯の中で持つ。 */
  conditions: DocumentListConditions;
  /** 状態ごとの件数（絞り込み前・ABAC 後）。取得前は undefined。 */
  statusCounts?: Record<string, number>;
  /** データソースの鍵ごとの件数（絞り込み前・ABAC 後）。取得前は undefined。 */
  sourceCounts?: Record<string, number>;
  /** プロジェクトごとの件数（絞り込み前・ABAC 後）。取得前は undefined。 */
  projectCounts?: Record<string, number>;
  /** 絞り込み前の件数（「すべて」に添える）。取得前は undefined。 */
  unfilteredTotal?: number;
  /** タグ辞書の名前（タグの選択肢）。 */
  tagOptions: readonly string[];
  onSortChange: (sort: DocumentSort) => void;
  /** 検索語以外の条件を 1 つ変える（検索語は入力が止まってから `onQueryChange`）。 */
  onConditionChange: (change: Partial<Omit<DocumentListConditions, 'q'>>) => void;
  onQueryChange: (q: string) => void;
  /** 帯の条件をすべて解除する（並べ替えとタブは残す —— 条件ではなく見せ方である）。 */
  onClear: () => void;
}

/** 件数の多い順（同数は鍵の順）に並べた鍵。選んでいる値が件数に無くても選択肢に残す（選んだ値が消えて見えないように）。 */
function countedKeys(counts: Record<string, number> | undefined, selected: string): string[] {
  const keys = Object.entries(counts ?? {})
    .sort(([a, x], [b, y]) => y - x || a.localeCompare(b))
    .map(([key]) => key);
  return selected !== '' && !keys.includes(selected) ? [...keys, selected] : keys;
}

export function DocumentListToolbar({
  sort,
  conditions,
  statusCounts,
  sourceCounts,
  projectCounts,
  unfilteredTotal,
  tagOptions,
  onSortChange,
  onConditionChange,
  onQueryChange,
  onClear,
}: DocumentListToolbarProps) {
  const { t } = useLingui();
  const { q, status } = conditions;
  const searchId = useId();
  const statusId = useId();
  const sortId = useId();
  const sourceId = useId();
  const confidentialityId = useId();
  const tagId = useId();
  const projectId = useId();
  const fromId = useId();
  const toId = useId();
  const [draft, setDraft] = useState(q);
  // 帯が自分で確定させた検索語（直近に `onQueryChange` へ渡した値）。
  const emitted = useRef(q);

  // 🔴 確定した検索語が**外から**変わったら（空の表示の「条件を解除」など）、入力欄をそれに合わせる。
  //   合わせないと入力欄に古い語が残り、300 ms 後にその語で絞り込みが戻ってしまう。
  //   帯が自分で確定させた値（`emitted`）が戻ってきただけのときは合わせない —— 打鍵の途中の入力を上書きしないため。
  useEffect(() => {
    if (q === emitted.current) return;
    emitted.current = q;
    setDraft(q);
  }, [q]);

  // 入力が止まってから引く。確定した値と同じなら何もしない（解除で '' に戻したときに二度引かない）。
  useEffect(() => {
    if (draft.trim() === q.trim()) return;
    const timer = setTimeout(() => {
      emitted.current = draft;
      onQueryChange(draft);
    }, TITLE_QUERY_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [draft, q, onQueryChange]);

  const sortOptions: { value: DocumentSort; label: string }[] = [
    { value: 'updated-desc', label: t`更新日時が新しい順` },
    { value: 'updated-asc', label: t`更新日時が古い順` },
    { value: 'created-desc', label: t`作成日時が新しい順` },
    { value: 'created-asc', label: t`作成日時が古い順` },
    { value: 'title-asc', label: t`表題の昇順` },
    { value: 'title-desc', label: t`表題の降順` },
  ];
  const withCount = (label: string, count: number | undefined) =>
    count === undefined ? label : t`${label}（${count} 件）`;
  const filtered = hasConditions(conditions) || draft.trim() !== '';
  const translate = (descriptor: MessageDescriptor) => i18n._(descriptor);
  const projectLabel = (key: string) => (key === NO_PROJECT ? t`プロジェクトなし` : key);

  return (
    <div
      className="mb-n3 flex flex-wrap items-end gap-n2"
      role="search"
      aria-label={t`文書の絞り込み`}
    >
      <span className="flex min-w-[12rem] flex-1 flex-col gap-1">
        <Label htmlFor={searchId}>
          <Trans>表題で検索</Trans>
        </Label>
        <Input
          id={searchId}
          type="search"
          inputSize="sm"
          value={draft}
          maxLength={DOCUMENT_TITLE_QUERY_MAX}
          placeholder={t`表題の一部を入力`}
          onChange={(e) => setDraft(e.target.value)}
        />
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={statusId}>
          <Trans>状態</Trans>
        </Label>
        <Select
          id={statusId}
          selectSize="sm"
          value={status}
          onChange={(e) => onConditionChange({ status: e.target.value as DocumentStatusFilter })}
        >
          <option value="">{withCount(t`すべて`, unfilteredTotal)}</option>
          {DOCUMENT_STATUSES.map((value) => {
            const view = documentStatusView(value);
            const label = typeof view.label === 'string' ? view.label : i18n._(view.label);
            return (
              <option key={value} value={value}>
                {withCount(label, statusCounts ? (statusCounts[value] ?? 0) : undefined)}
              </option>
            );
          })}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={sourceId}>
          <Trans>データソース</Trans>
        </Label>
        <Select
          id={sourceId}
          selectSize="sm"
          value={conditions.source}
          onChange={(e) => onConditionChange({ source: e.target.value })}
        >
          <option value="">{withCount(t`すべて`, unfilteredTotal)}</option>
          {countedKeys(sourceCounts, conditions.source).map((key) => (
            <option key={key} value={key}>
              {withCount(sourceLabel(key, translate), sourceCounts?.[key] ?? 0)}
            </option>
          ))}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={confidentialityId}>
          <Trans>機密区分で絞り込み</Trans>
        </Label>
        {/* 値は訳さない（生値。`lib/abac/confidentiality.ts`）。 */}
        <Select
          id={confidentialityId}
          selectSize="sm"
          value={conditions.confidentiality}
          onChange={(e) =>
            onConditionChange({
              confidentiality: e.target.value as DocumentConfidentialityFilter,
            })
          }
        >
          <option value="">{t`すべて`}</option>
          {CONFIDENTIALITY_VALUES.map((value) => (
            <option key={value} value={value}>
              {value}
            </option>
          ))}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={tagId}>
          <Trans>タグで絞り込み</Trans>
        </Label>
        <Select
          id={tagId}
          selectSize="sm"
          value={conditions.tag}
          onChange={(e) => onConditionChange({ tag: e.target.value })}
        >
          <option value="">{t`すべて`}</option>
          {(tagOptions.includes(conditions.tag) || conditions.tag === ''
            ? tagOptions
            : [...tagOptions, conditions.tag]
          ).map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={projectId}>
          <Trans>プロジェクト</Trans>
        </Label>
        <Select
          id={projectId}
          selectSize="sm"
          value={conditions.project}
          onChange={(e) => onConditionChange({ project: e.target.value })}
        >
          <option value="">{withCount(t`すべて`, unfilteredTotal)}</option>
          {countedKeys(projectCounts, conditions.project).map((key) => (
            <option key={key} value={key}>
              {withCount(projectLabel(key), projectCounts?.[key] ?? 0)}
            </option>
          ))}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={fromId}>
          <Trans>保存日（から）</Trans>
        </Label>
        <Input
          id={fromId}
          type="date"
          inputSize="sm"
          value={conditions.createdFrom}
          max={conditions.createdTo || undefined}
          onChange={(e) => {
            // 範囲を逆向きにしない（BFF は逆向きを 400 にする）。「から」が「まで」を越えたら「まで」も合わせる。
            const value = e.target.value;
            const { createdTo } = conditions;
            onConditionChange(
              value !== '' && createdTo !== '' && value > createdTo
                ? { createdFrom: value, createdTo: value }
                : { createdFrom: value },
            );
          }}
        />
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={toId}>
          <Trans>保存日（まで）</Trans>
        </Label>
        <Input
          id={toId}
          type="date"
          inputSize="sm"
          value={conditions.createdTo}
          min={conditions.createdFrom || undefined}
          onChange={(e) => {
            const value = e.target.value;
            const { createdFrom } = conditions;
            onConditionChange(
              value !== '' && createdFrom !== '' && value < createdFrom
                ? { createdFrom: value, createdTo: value }
                : { createdTo: value },
            );
          }}
        />
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={sortId}>
          <Trans>並べ替え</Trans>
        </Label>
        <Select
          id={sortId}
          selectSize="sm"
          value={sort}
          onChange={(e) => onSortChange(e.target.value as DocumentSort)}
        >
          {sortOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
      </span>
      <Button
        type="button"
        size="sm"
        disabled={!filtered}
        onClick={() => {
          setDraft('');
          onClear();
        }}
      >
        <Trans>条件を解除</Trans>
      </Button>
    </div>
  );
}
