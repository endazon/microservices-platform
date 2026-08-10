import { useEffect, useRef, useState } from 'react';
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
import {
  hasRetainedFigures,
  isCorrectable,
  isRetryable,
  jobStatusView,
  JOB_STATUSES,
} from './jobStatus';
import type { JobStatusFilter } from './jobStatus';
import { useConversionJobs, useConversionJobActions } from './useConversionJobs';
import { FigureCorrectionPanel } from './FigureCorrectionPanel';
// SC-07, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { ConversionJobDto } from '@foundation/api/generated/bff.schemas';

// SC-07, UC-06, FR-12: 変換ジョブ画面（05_screens: ルート /admin/conversions）。
// 計画が 2026-08-04 に確定した内容（4 状態モデル・状態フィルタ・retry・**再変換は管理者ロール限定**・
// 同一ジョブの直列化）に従う。API 側の管理者ロール強制の突合は #501（IADR-0128）が済ませている。
//
// **［2026-08-10 / #651］人手補正の 2 ペイン編集を実装した。**
//   Phase 1 = 図コードの編集 ＋ 元の図画像 ＋「補正して再登録」（`FigureCorrectionPanel.tsx`）。
//   契約は #543（`GET .../figures`・`.../figures/{figureId}/image`・`POST .../correction`。IADR-0154）。
//   **Markdown 全体の編集欄は置かない**——`05_screens:330` が Phase 2 へ繰り延べている。
//
// 実装しない要素（画面仕様書 docs/screens/SC-07_conversion-jobs.md §hi-fi モックアップとの対応）:
//   - **「デッドレター」の内訳表示**: **契約は #533 で載った**（`ConversionJobDto.deadLettered` /
//     `maxAttempts`。planning#198 の裁定 Q13）。**画面へ出す作業は本ファイルではまだ行っていない**——
//     契約の追加とは別の作業単位であり、**人手補正とは別の資源**なので #651 でも束ねなかった
//     （[[IADR-0139]] の判定単位は資源。作業仕様書 docs/specs/20260806_issue-533_*.md §未決事項 1）。
//   もとの記録は feedback/20260805_sc05-07-admin-contract-gaps.md（planning#198 で裁定済み）。

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

/**
 * 再変換の 409 が「**補正が失われる**」ものなら、失われる件数を返す（そうでなければ `null`）。
 *
 * **確認ダイアログは 409 を受けてから出す**（[[IADR-0154]] 決定 4 / #651）。
 * `hasCorrection` を画面が読んで**先に**分岐する形にはしない——一覧を取ってから押すまでの間に
 * 補正が入ると**無確認で消える**うえ、件数もサーバの値でなければ嘘になる。
 * #640 のタグ削除（`usageCount` つき 409）と同じ形である。
 *
 * 本文は BFF が透過する（#543 で中継を直した）。`ApiError.body` から読む——`details` は
 * 文字列しか持てず、**翻訳済みの文へ数値を差し込めない**（サーバの日本語をそのまま出すと
 * en ロケールで日本語が混ざる）。
 */
function correctionsWouldBeLost(err: unknown): number | null {
  if (!(err instanceof ApiError) || err.status !== 409) return null;
  const body = err.body as { error?: unknown; correctedFigures?: unknown } | null | undefined;
  if (!body || body.error !== 'corrections_would_be_lost') return null;
  return typeof body.correctedFigures === 'number' ? body.correctedFigures : 0;
}

export function ConversionJobsPage() {
  const { t } = useLingui();
  // IADR-0127 決定 4: 絞り込み条件は URL へ載せない（計画のルートパス表は /admin/conversions に
  // クエリを持たない）。単一の state を useQuery のキーに入れ、情報源を 1 つに保つ。
  const [filter, setFilter] = useState<JobStatusFilter>('');
  const jobs = useConversionJobs(filter);
  const actions = useConversionJobActions();
  const { retry, correct } = actions;
  /** 2 ペインを開いているジョブ（`null`＝閉じている）。 */
  const [correcting, setCorrecting] = useState<string | null>(null);
  /** 直近の再変換の対象。409 の確認後に `discardCorrections=true` で再送するために覚える。 */
  const [retryTarget, setRetryTarget] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // IADR-0127 決定 7: 画面は**直近の操作の結果だけ**を出す。列挙は `useConversionJobActions()` の
  // 戻り値から導く——手書きの配列にすると、3 本目のミューテーションを足したときに同じ穴が空く
  // （SC-05 / SC-06 と同じ形）。#503 時点の「retry の 1 本だけ」という前提は #651 で崩れた。
  const mutations = Object.values(actions);

  /** 新しい操作を始める前に、前回の結果（成功メッセージと各ミューテーションの失敗状態）を捨てる。 */
  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  // 補正が失われる旨の 409 を受けているか（＝確認を出す状態か）。**状態を別に持たない**——
  // 件数はサーバの応答が唯一の情報源であり、複製すると必ずどちらかが古くなる。
  const pendingDiscard = correctionsWouldBeLost(retry.error);
  const failed = mutations.find((m) => m.isError);

  /**
   * 確認が出たら、**非破壊側（取り消す）のボタンへ**焦点を移す。
   *
   * **これが無いとキーボードで確認へ到達できない。** 確認は表より**前**に描かれるのに対し、
   * それを出す「再変換」ボタンは**表の行の中**にある——すなわち確認のボタンは焦点位置より
   * **DOM 上で手前**にあり、**前方 Tab では決して届かない**（Shift+Tab を知っている人しか
   * 操作できない）。読み上げ（`role="alert"`）は届いても、操作手段が保証されない。
   *
   * **移す先は非破壊側（取り消す）である。** 既定の焦点は `Enter` の当たり先でもあるため、
   * 破壊側へ置くと**反射的な連打で補正が消える**（[[IADR-0157]] 決定 2 の追補 2）。
   *
   * **焦点の横取りではない。** 利用者自身の押下に対する応答であり、かつ**破壊的操作を
   * 差し止めている**確認なので、そこへ焦点が移るのは期待される挙動である。
   * これは[[IADR-0157]] 決定 2 の「モーダルにしない」判断とは両立する——背後は操作でき、
   * `Esc` も奪わない。移すのは焦点だけである。
   */
  const confirmRef = useRef<HTMLDivElement>(null);
  const confirmShown = pendingDiscard !== null && retryTarget !== null;
  useEffect(() => {
    if (!confirmShown) return;
    // **非破壊側（取り消す）を名指しで焦点にする。** 「先頭のボタン」に頼らないのは、
    // 並びを変えたときに既定の焦点が**黙って破壊側へ移る**からである。
    // 下の文字列は CSS セレクタであって表示文言ではない。eslint.config.js の ignore は
    // `[` `]` を許さないためここだけ個別に外す（設定側を緩めると属性セレクタ全般が素通りする）。
    // eslint-disable-next-line lingui/no-unlocalized-strings -- CSS セレクタ（表示文言ではない）
    confirmRef.current?.querySelector<HTMLButtonElement>('[data-confirm-default]')?.focus();
  }, [confirmShown]);

  // FR-12, UC-06, SC-07: 05_screens §SC-07（2026-08-04 確定）: 再変換の実行権限は管理者ロールに限る。
  // 計画の「本画面のアクセス制御と API の権限を揃える」は**両側で満たされている**——
  // 画面のこの制御（IADR-0127 決定 1）と、BFF `POST /bff/conversion/jobs/{id}/retry` の
  // admin 限定（#501 / IADR-0128 決定 1。operator は 403）。
  // **実効境界はサーバ側**であり、ここは表示制御にすぎない（IADR-0039 決定 2）。
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

      {/* IADR-0127 決定 7: 直近の操作の結果だけを出す（`beginOperation()` が前回を捨てる）。 */}
      {notice && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          {notice}
        </Alert>
      )}

      {/* **補正が失われる旨の確認**（計画 `05_screens:313`「補正のあるジョブの再実行は、補正が
          失われる旨を示して明示確認を求める」）。**409 を受けてから出す**——先に出して通ったら
          投げる形にはしない（§correctionsWouldBeLost のコメント参照）。

          **`role="alertdialog"` は使わない。** WAI-ARIA の `alertdialog` は「**モーダル**であり、
          応答するまで背後を操作できない」ことを意味する。本確認は
          ページ内に差し込む**非モーダル**の注意であり、**背後は操作でき `Esc` も奪わない**
          （焦点は移すが、それはモーダル性とは別物である。共有 UI に Dialog
          プリミティブが無い）。**実装しない振る舞いを role で宣言しない**——支援技術の利用者に
          嘘をつくことになるためである。`role="alert"` で即時に読み上げ、確認は本文とボタンで表す。 */}
      {pendingDiscard !== null && retryTarget && (
        <Alert tone="warning" role="alert" className="mb-2" label={t`確認`}>
          <p>
            <Trans>
              このジョブには人手補正が {pendingDiscard} 件あります。再変換すると補正は失われます。
            </Trans>
          </p>
          {/* **非破壊側（取り消す）を先に置く。** 破壊的操作の確認では、DOM 順の先頭＝
              既定の焦点＝`Enter` の当たり先になるため、ここに「補正を破棄して再変換」を
              置くと**反射的な `Enter` の連打で補正が消える**（ARIA APG の Alert/Confirm と
              ネイティブダイアログの慣習も既定を非破壊側に置く）。
              `@platform/ui` の `Button` は ref を転送しないので、焦点は包む側の ref から引く
              ——**`data-confirm-default` で明示的に指す**。「先頭のボタン」に頼ると、
              並びを変えたときに既定の焦点が黙って破壊側へ移る。 */}
          <div className="mt-2 flex gap-2" ref={confirmRef}>
            <Button
              type="button"
              size="sm"
              variant="secondary"
              data-confirm-default=""
              onClick={() => {
                beginOperation();
                setRetryTarget(null);
              }}
            >
              <Trans>取り消す</Trans>
            </Button>
            <Button
              type="button"
              size="sm"
              variant="primary"
              disabled={retry.isPending}
              onClick={() => {
                const id = retryTarget;
                beginOperation();
                retry.mutate(
                  { id, params: { discardCorrections: true } },
                  { onSuccess: () => setNotice(t`再変換を受け付けました。`) },
                );
              }}
            >
              <Trans>補正を破棄して再変換</Trans>
            </Button>
          </div>
        </Alert>
      )}

      {/* 確認を出している 409 は上で扱っているので、ここでは**それ以外の失敗**だけを出す。 */}
      {failed && pendingDiscard === null && (
        // INDEX 決定 21: 色（tone）だけで深刻度を伝えない。**ラベルの文言も tone に揃える**
        // ——琥珀のアイコンに「エラー」と書かれていると、色を除いたときに区別が消える。
        <Alert
          tone={isConflict(failed.error) ? 'warning' : 'danger'}
          role="alert"
          className="mb-2"
          label={isConflict(failed.error) ? t`注意` : t`エラー`}
        >
          {isConflict(failed.error) ? (
            // 計画確定の「直列化」の実体。UI 制御をすり抜けた要求もここで扱う。
            <Trans>このジョブは再変換できません（実行中、または失敗以外の状態です）。</Trans>
          ) : (
            toMessages(failed.error, t`操作を受け付けられませんでした。`).join(' / ')
          )}
        </Alert>
      )}

      {/* 人手補正の 2 ペイン（Phase 1）。**管理者だけが開ける**——導線を出すのは JobRow 側。 */}
      {correcting && (
        <FigureCorrectionPanel
          jobId={correcting}
          submitting={correct.isPending}
          onClose={() => setCorrecting(null)}
          onSubmit={(figureId, input) => {
            beginOperation();
            correct.mutate(
              { id: correcting, figureId, data: input },
              { onSuccess: () => setNotice(t`図を補正して再登録しました。`) },
            );
          }}
        />
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
                  onRetry={() => {
                    beginOperation();
                    setRetryTarget(job.id);
                    retry.mutate(
                      { id: job.id },
                      { onSuccess: () => setNotice(t`再変換を受け付けました。`) },
                    );
                  }}
                  onCorrect={() => {
                    beginOperation();
                    setCorrecting(job.id);
                  }}
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
  onCorrect,
  retrying,
}: {
  job: ConversionJobDto;
  canRetry: boolean;
  onRetry: () => void;
  onCorrect: () => void;
  retrying: boolean;
}) {
  const { t } = useLingui();
  // INDEX 決定 21: 状態は色だけで意味を持たせない。StatusBadge が tone ごとの固定アイコンと
  // テキストを型で強制する（呼び出し側はアイコンを省略できない）。
  const view = jobStatusView(job.status);
  // **導出であって `status` の 5 値目ではない**（`05_screens:320`「ジョブ状態モデルは 4 値である」）。
  const retained = hasRetainedFigures(job);
  const coded = job.diagramsCoded ?? 0;

  return (
    <TableRow>
      <TableCell>
        <span title={job.id} className="font-mono text-xs">
          {shortId(job.id)}
        </span>
      </TableCell>
      <TableCell>{job.originalPath}</TableCell>
      <TableCell>
        <div className="flex flex-wrap items-center gap-1">
          <StatusBadge tone={view.tone}>{labelOf(view.label)}</StatusBadge>
          {/* hi-fi:420「✕ 図コード化失敗（画像保持へ縮退済み）」。**状態ではなく併記の標識**である。 */}
          {retained && (
            <StatusBadge tone="danger">{t`図コード化失敗（画像保持へ縮退済み）`}</StatusBadge>
          )}
          {/* hi-fi:422「補正あり」。 */}
          {job.hasCorrection && <StatusBadge tone="success">{t`補正あり`}</StatusBadge>}
        </div>
      </TableCell>
      <TableCell className="text-xs text-[--color-fg-muted]">
        {/* hi-fi:422「Mermaid 2図」——備考は `diagramsCoded` から導出する。
            補間には**素の変数だけ**を置く（`lingui/no-expression-in-message`）。 */}
        {coded > 0 ? <Trans>Mermaid {coded}図</Trans> : (job.error ?? '—')}
      </TableCell>
      <TableCell>
        <div className="flex flex-wrap items-center gap-2">
          {/* 人手補正（Phase 1）。**縮退した図がある行にだけ**・**管理者のみ**。
              条件（対象の有無）と権限を別に判定しているのは、出ない理由を区別して
              文言を選ぶためである（[[IADR-0127]] 決定 1）。 */}
          {isCorrectable(job) &&
            (canRetry ? (
              <Button type="button" size="sm" variant="secondary" onClick={onCorrect}>
                <Trans>人手補正</Trans>
              </Button>
            ) : (
              <span className="text-xs text-[--color-fg-muted]">
                <Trans>人手補正は管理者のみ実行できます</Trans>
              </span>
            ))}
          {isRetryable(job.status) ? (
            canRetry ? (
              <Button
                type="button"
                size="sm"
                variant="primary"
                disabled={retrying}
                onClick={onRetry}
              >
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
            // 補正の導線も出ない行は、操作が 1 つも無いので `—` を出す。
            // **補正の導線がある行では出さない**——「操作なし」と読めてしまうためである。
            !isCorrectable(job) && '—'
          )}
        </div>
      </TableCell>
    </TableRow>
  );
}
