import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import { Link } from '@tanstack/react-router';
import {
  Alert,
  Button,
  Label,
  Select,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { PlatformRole, useHasAnyRole } from '@foundation/auth/roles';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
import { isRetryable, jobStatusView, JOB_STATUSES } from './jobStatus';
import type { JobStatusFilter } from './jobStatus';
import { useConversionJobs, useRetryConversionJob } from './useConversionJobs';
import type { ConversionJob } from './useConversionJobs';

// SC-07, UC-06, FR-12: 変換ジョブ画面（05_screens: ルート /admin/conversions）。
// 計画が 2026-08-04 に確定した内容（4 状態モデル・状態フィルタ・retry・**再変換は管理者ロール限定**・
// 同一ジョブの直列化）に従う。API 側の管理者ロール強制の突合は #501 が担当する。
//
// 実装しない要素（画面仕様書 docs/screens/SC-07_conversion-jobs.md §hi-fi モックアップとの対応）:
//   - **人手補正の 2 ペイン編集**（変換結果の編集 ＋ 原本プレビュー ＋「補正して再登録」）:
//     補正済み Markdown を受け取る API も、原本・変換結果の本文を返す API も無い。`retry` は
//     「変換を最初からやり直す」もので編集結果を受け取らない。保存先の無い編集欄を置くと、
//     管理者は補正したつもりで何も反映されない。
//   - **「デッドレター」の内訳表示**: `ConversionJobDto` にデッドレターの標識が無い（`Attempts` は試行回数）。
//   いずれも feedback/20260805_sc05-07-admin-contract-gaps.md に環流の記録を作成した。

/** 絞り込みの選択肢。**既定は「すべて」**（理由は画面仕様書 §絞り込みの既定値）。 */
const FILTERS: readonly JobStatusFilter[] = ['', ...JOB_STATUSES];

/** GUID をそのまま出すと表が読めないため先頭 8 桁で示す（完全な値は title 属性で残す）。 */
function shortId(id: string): string {
  return id.slice(0, 8);
}

/**
 * 状態の表示名を解決する。
 *
 * `nav.ts` と同じ作法で `MessageDescriptor` を**描画時に**解決する（モジュール初期化時に
 * 文字列へ確定させるとロケール切替に追随しない）。未知の状態は生値（`string`）がそのまま返る。
 */
function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/** 409（`not_retryable`）＝直列化による拒否か。 */
function isConflict(err: unknown): boolean {
  return err instanceof ApiError && err.status === 409;
}

export function ConversionJobsPage() {
  const { t } = useLingui();
  // IADR-0127 決定 4: 絞り込み条件は URL へ載せない（計画のルートパス表は /admin/conversions に
  // クエリを持たない）。単一の state を useQuery のキーに入れ、情報源を 1 つに保つ。
  const [filter, setFilter] = useState<JobStatusFilter>('');
  const jobs = useConversionJobs(filter);
  const retry = useRetryConversionJob();

  // 05_screens §SC-07（2026-08-04 確定）: 再変換の実行権限は管理者ロールに限る。
  // **計画は「本画面のアクセス制御と API の権限を揃える」と確定している**が、API（/bff/conversion/jobs）は
  // まだ admin/operator であり、**この確定事項は未達である**——API を直接叩ける運用者は依然 retry でき、
  // 画面のこの制御はその穴を塞がない。解消は #501（IADR-0127 決定 1）。
  const canRetry = useHasAnyRole(PlatformRole.Admin);

  const items = jobs.data ?? [];

  return (
    <section>
      <div className="mb-3 flex flex-wrap items-end justify-between gap-2">
        <h1 className="text-lg font-semibold text-[--color-fg]">
          <Trans>変換ジョブ（pandoc＋LLM）</Trans>
        </h1>
        <div className="flex items-center gap-2">
          <Label htmlFor="job-filter" className="shrink-0">
            <Trans>状態で絞り込み</Trans>
          </Label>
          <Select
            id="job-filter"
            selectSize="sm"
            value={filter}
            onChange={(e) => setFilter(e.target.value as JobStatusFilter)}
          >
            {FILTERS.map((f) => (
              <option key={f || 'all'} value={f}>
                {f === '' ? t`すべて` : labelOf(jobStatusView(f).label)}
              </option>
            ))}
          </Select>
        </div>
      </div>

      {/* IADR-0127 決定 7: 本画面のミューテーションは retry の 1 本だけであり、成功と失敗は
          同じミューテーションの状態として排他である（古い結果が並ぶ余地が無い）。**2 本目を
          足すときは SC-05 / SC-06 の `beginOperation()` と同じ形へ移すこと**——複数の
          ミューテーションを並べて読むと、別の操作の成功後も古い失敗バナーが残る。 */}
      {retry.isSuccess && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          <Trans>再変換を受け付けました。</Trans>
        </Alert>
      )}
      {retry.isError && (
        // INDEX 決定 21: 色（tone）だけで深刻度を伝えない。**ラベルの文言も tone に揃える**
        // ——琥珀のアイコンに「エラー」と書かれていると、色を除いたときに区別が消える。
        <Alert
          tone={isConflict(retry.error) ? 'warning' : 'danger'}
          role="alert"
          className="mb-2"
          label={isConflict(retry.error) ? t`注意` : t`エラー`}
        >
          {isConflict(retry.error) ? (
            // 計画確定の「直列化」の実体。UI 制御をすり抜けた要求もここで扱う。
            <Trans>このジョブは再変換できません（実行中、または失敗以外の状態です）。</Trans>
          ) : (
            toMessages(retry.error, t`再変換を受け付けられませんでした。`).join(' / ')
          )}
        </Alert>
      )}

      {jobs.isPending && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}

      {/* BFF は後段障害を空一覧へ縮退させない（502 で可視化する）。画面もこれに合わせ、
          取得失敗を「ジョブ無し」と見せない。 */}
      {jobs.isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(jobs.error, t`変換ジョブを取得できませんでした。`).join(' / ')}
        </Alert>
      )}

      {jobs.isSuccess &&
        (items.length === 0 ? (
          <p className="text-sm">
            <Trans>該当する変換ジョブはありません。</Trans>
          </p>
        ) : (
          <Table>
            <TableCaption>
              <Trans>変換ジョブの一覧</Trans>
            </TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell>
                  <Trans>ジョブ</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>原本</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>状態</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>備考</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>操作</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {items.map((job) => (
                <JobRow
                  key={job.id}
                  job={job}
                  canRetry={canRetry}
                  onRetry={() => retry.mutate(job.id)}
                  retrying={retry.isPending}
                />
              ))}
            </TableBody>
          </Table>
        ))}

      {/* 遷移図 SC06 → SC07 の逆方向の導線（計画のパンくずが示す階層。パンくず自体は #452 系）。 */}
      <p className="mt-4 text-sm">
        <Link to="/admin/sources" className="text-[--color-brand] hover:underline">
          <Trans>← データソース管理へ戻る</Trans>
        </Link>
      </p>
    </section>
  );
}

function JobRow({
  job,
  canRetry,
  onRetry,
  retrying,
}: {
  job: ConversionJob;
  canRetry: boolean;
  onRetry: () => void;
  retrying: boolean;
}) {
  const { t } = useLingui();
  // INDEX 決定 21: 状態は色だけで意味を持たせない。StatusBadge が tone ごとの固定アイコンと
  // テキストを型で強制する（呼び出し側はアイコンを省略できない）。
  const view = jobStatusView(job.status);

  return (
    <TableRow>
      <TableCell>
        <span title={job.id} className="font-mono text-xs">
          {shortId(job.id)}
        </span>
      </TableCell>
      <TableCell>{job.originalPath}</TableCell>
      <TableCell>
        <StatusBadge tone={view.tone}>{labelOf(view.label)}</StatusBadge>
      </TableCell>
      <TableCell className="text-xs text-[--color-fg-muted]">{job.error ?? '—'}</TableCell>
      <TableCell>
        {isRetryable(job.status) ? (
          canRetry ? (
            <Button type="button" size="sm" variant="primary" disabled={retrying} onClick={onRetry}>
              <Trans>再変換</Trans>
            </Button>
          ) : (
            // 無言でボタンを消すと「このジョブは再変換できない（状態の問題）」と読めてしまい、
            // 権限の問題と区別できない。理由を書く（IADR-0127 決定 1。存在秘匿の対象ではない）。
            <span className="text-xs text-[--color-fg-muted]">
              <Trans>再変換は管理者のみ実行できます</Trans>
            </span>
          )
        ) : job.status === 'succeeded' && job.documentId ? (
          // 計画の遷移図 SC07 -- 変換結果 --> SC03。
          <Link
            to="/docs/$id"
            params={{ id: job.documentId }}
            aria-label={t`変換結果の文書を開く`}
            className="text-sm text-[--color-brand] hover:underline"
          >
            <Trans>結果 →</Trans>
          </Link>
        ) : (
          '—'
        )}
      </TableCell>
    </TableRow>
  );
}
