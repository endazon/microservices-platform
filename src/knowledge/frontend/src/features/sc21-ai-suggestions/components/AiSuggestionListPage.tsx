import { useMemo } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { RotateCcw } from 'lucide-react';
import { Alert, Label, Select, StatusBadge } from '@platform/ui';
import type { AiSuggestion } from '@foundation/api/generated/bff.schemas';
import { DataTable } from '../../../components/DataTable';
import type { DataTableColumns } from '../../../components/DataTable';
import { useAiSuggestions, useEdgeTypeCatalog } from '../api/useAiSuggestions';
import { KIND_OPTIONS, STATE_OPTIONS } from '../routes/sc21AiSuggestionsRoute';
import type { AiSuggestionSearch, KindOption, StateOption } from '../routes/sc21AiSuggestionsRoute';

// SC-21, UC-10, FR-18/FR-05: AI 提案一覧（05_screens: ルート /ai-suggestions）。
//
// ■ 🔴 **本画面は書き込みを一切しない。** 承認・却下のボタンを置かない ——
//   05_screens §SC-21 入力/バリデーション 第 3 行が「本画面では実行しない。各行の導線から
//   SC-03 へ遷移して実行する」と定める。**一括承認・一括却下は「描いてはいけないもの」**である
//   （FR-18。一覧の 1 行に収まる情報では承認を判断できず、タイトルだけを見た機械的な承認に落ちる）。
//   不在は `AiSuggestionListPage.test.tsx` が陽性対照つきで固定する。
// ■ リンク提案とタグ提案は**同じ一覧**に同居させる（画面を分けない。分けると片方が忘れられる）。
// ■ URL（state / kind）が絞り込みの単一情報源である。フィルタの変更で再取得する。
// ■ 辺の型の表示名は辞書（カタログ）で解決する（ADR-0033 決定 9。改名に追随させる）。
// ■ 状態は**色だけで意味を持たせない**（StatusBadge が色 ＋ アイコン ＋ テキストを強制する）。

/**
 * 却下からの再提示に添える**固定文言**（05_screens §SC-21・ADR-0033 決定 10）。
 *
 * 🔴 **理由を示さない再提示を起こさない。** 理由がないと利用者は「却下したのにまた出てきた」と
 * 受け取り、承認作業そのものへの信頼を失う。
 *
 * ⚠️ **この文言が実際に出る経路は、まだ通っていない。** 却下解除を発火させるのは文書更新イベントの
 * 購読（#911）であり、そのイベントは「本文が変更されたこと」を判定できない（ADR-0050 待ち）。
 * 表示側だけが先に在る状態である —— 実装状況は画面仕様書に記載する。
 */
function ReinstatedNotice() {
  return (
    <p
      className="mt-1 flex items-start gap-1 text-xs text-[--color-fg-muted]"
      data-testid="reinstated-notice"
    >
      <RotateCcw className="mt-0.5 size-3.5 shrink-0" aria-hidden />
      <Trans>この提案は一度却下されましたが、文書が更新されたため再度提示しています。</Trans>
    </p>
  );
}

export function AiSuggestionListPage() {
  const { t } = useLingui();
  const search: AiSuggestionSearch = useSearch({ from: '/_shell/ai-suggestions' });
  const navigate = useNavigate({ from: '/ai-suggestions' });

  const suggestions = useAiSuggestions(search);
  const edgeTypes = useEdgeTypeCatalog();

  const rows = useMemo(() => suggestions.data ?? [], [suggestions.data]);

  // 型 ID → 表示名。辞書が引けないときは ID を出さず「型不明」に倒す
  //（GUID を利用者へ見せても判断の役に立たない）。
  const edgeTypeNames = useMemo(() => {
    const map = new Map<string, string>();
    for (const type of edgeTypes.data ?? []) map.set(type.id, type.name);
    return map;
  }, [edgeTypes.data]);

  const stateLabels: Record<StateOption, string> = {
    pending: t`承認待ち`,
    approved: t`承認済み`,
    // 却下は**再提示の抑止**に用いる（ADR-0033 決定 7）。状態名だけだと「終わった」と読めるため添える。
    rejected: t`却下（再提示を抑止中）`,
    all: t`すべて`,
  };

  const kindLabels: Record<KindOption, string> = {
    all: t`すべて`,
    link: t`リンク`,
    tag: t`タグ`,
  };

  const describe = (s: AiSuggestion): string => {
    if (s.kind === 'tag') {
      return t`${s.sourceDocumentTitle} に「${s.tagValue ?? ''}」を付与`;
    }
    const typeName = (s.edgeTypeId && edgeTypeNames.get(s.edgeTypeId)) || t`型不明`;
    return t`${s.sourceDocumentTitle} → ${s.targetDocumentTitle ?? ''}（${typeName}）`;
  };

  const columns: DataTableColumns<AiSuggestion> = useMemo(
    () => [
      {
        id: 'kind',
        accessorKey: 'kind',
        header: t`種類`,
        cell: ({ row }) => (row.original.kind === 'tag' ? kindLabels.tag : kindLabels.link),
      },
      {
        id: 'content',
        accessorKey: 'sourceDocumentTitle',
        header: t`提案`,
        cell: ({ row }) => (
          <div>
            <span>{describe(row.original)}</span>
            {/* SC-21 主要素 6: 提案の根拠（なぜ関連と判断したか）。 */}
            <p className="text-xs text-[--color-fg-muted]" data-testid="rationale">
              {row.original.rationale}
            </p>
            {row.original.reinstatedReason ? <ReinstatedNotice /> : null}
          </div>
        ),
      },
      {
        id: 'state',
        accessorKey: 'state',
        header: t`状態`,
        cell: ({ row }) => {
          const value = row.original.state;
          const tone =
            value === 'approved' ? 'success' : value === 'rejected' ? 'danger' : 'neutral';
          const label =
            value === 'approved'
              ? stateLabels.approved
              : value === 'rejected'
                ? stateLabels.rejected
                : stateLabels.pending;
          return <StatusBadge tone={tone}>{label}</StatusBadge>;
        },
      },
      {
        id: 'detail',
        header: t`文書詳細`,
        // 並べ替えの対象にならない（導線の列に順序は無い）。
        enableSorting: false,
        // 🔴 SC-21 主要素 4: **全行が必ず SC-03 への導線を持つ。**
        // 「SC-03 への導線を持たない行」は描いてはいけないものに挙げられている。
        // 承認・却下はこの遷移の先（SC-03 の承認欄）でのみ実行される。
        cell: ({ row }) => (
          <Link
            to="/docs/$id"
            params={{ id: row.original.sourceDocumentId }}
            className="text-[--color-accent] underline"
          >
            <Trans>文書詳細で確認</Trans>
          </Link>
        ),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- t / ラベル / describe は言語と辞書に従属する
    [t, edgeTypeNames],
  );

  const setParams = (patch: Partial<AiSuggestionSearch>) =>
    navigate({ search: (prev: AiSuggestionSearch) => ({ ...prev, ...patch }) });

  return (
    <section className="space-y-3">
      <div>
        <h1 className="text-lg font-semibold text-[--color-fg]">
          <Trans>AI 提案一覧</Trans>
        </h1>
        {/* 位置づけの固定文言。**なぜ一覧で承認できないのか**を必ず示す（05_screens §SC-21）。 */}
        <p className="text-xs text-[--color-fg-muted]" data-testid="suggestions-help">
          <Trans>
            AI が提案したリンク候補・タグ候補を棚卸しするための一覧です。
            承認・却下はこの画面では行いません。各行の「文書詳細で確認」から文書詳細へ移動し、
            両端の文書の内容を見たうえで判断してください。まとめて承認する操作は提供していません。
            閲覧権限のない文書に関する提案は、件数を含め表示されません。
          </Trans>
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-4">
        <div>
          <Label htmlFor="suggestion-state">
            <Trans>状態</Trans>
          </Label>
          <Select
            id="suggestion-state"
            selectSize="sm"
            value={search.state}
            onChange={(e) => void setParams({ state: e.target.value as StateOption })}
          >
            {STATE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {stateLabels[option]}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="suggestion-kind">
            <Trans>種類</Trans>
          </Label>
          {/* 🔴 種類は**同じ一覧の絞り込み**である。リンクとタグで画面（ルート）を分けない。 */}
          <Select
            id="suggestion-kind"
            selectSize="sm"
            value={search.kind}
            onChange={(e) => void setParams({ kind: e.target.value as KindOption })}
          >
            {KIND_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {kindLabels[option]}
              </option>
            ))}
          </Select>
        </div>
      </div>

      {suggestions.isError ? (
        // 🔴 **空の一覧へ縮退しない。**「提案が 1 件も無い」と「一覧が引けない」は別の意味である。
        <Alert tone="danger" label={t`エラー`} data-testid="suggestions-error">
          <Trans>提案の一覧を取得できませんでした。時間をおいて再度お試しください。</Trans>
        </Alert>
      ) : suggestions.isPending ? (
        <p className="text-sm text-[--color-fg-muted]" data-testid="suggestions-loading">
          <Trans>読み込み中です。</Trans>
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-[--color-fg-muted]" data-testid="suggestions-empty">
          <Trans>該当する提案はありません。</Trans>
        </p>
      ) : (
        <DataTable
          caption={t`AI 提案の一覧`}
          sortHint={t`並べ替え`}
          columns={columns}
          data={rows}
        />
      )}
    </section>
  );
}
