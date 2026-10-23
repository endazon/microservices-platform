import { useMemo, useRef, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Note,
  Textarea,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { ApiError } from '@foundation/api/ApiError';
import { useAuth } from '@foundation/auth/useAuth';
import type { PrivateNoteDto, UserSummaryDto } from '@foundation/api/generated/bff.schemas';
import { useUserLookup } from '../api/useUserLookup';
import { REASON_MAX_LENGTH, reasonStateOf, sameUser } from '../types/breakGlass';

// SC-19 主要素 8, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841, [[IADR-0612]] (#2046 の 3 本目):
// **緊急アクセスの発動の確認ダイアログ。**
//
// ■ 計画（05_screens §SC-19 緊急アクセス）: 「**理由入力を必須**とし、『この操作は監査ログに記録されます』を
//   明示する確認ダイアログを出す」。モックアップ（hi-fi sc-19）は**理由の欄・注記・発動するの 3 つを 1 枚に置く**。
//   本ダイアログはその形に、裁定（planning#841）が足した 2 つ —— **対象の資料（1 件）** と
//   **指名する管理者（1 人）** —— を加えたものである。対象は呼び出し側（`BreakGlassPanel`）で選んでから開く。
//
// ■ 🔴 **指名先は名簿から選ばせる（自由入力にしない）。** 選べるのは `/bff/users/lookup`（有効な利用者の検索）の候補だけで、
//   送るのは候補の利用者名である。**名簿をロールで絞る口は公開 API に無い**（`UserSummaryDto` は意図してロールを
//   運ばない）—— 管理者の一覧を全利用者へ開く口を足すことは、本 PR の範囲を越える新しい露出である（[[IADR-0612]] 決定 2）。
//   **platform-admin かどうかは後段が名簿で確かめる**（違えば 400、確かめられなければ 503。発動しない）。
//   画面は「管理者以外は指名できない」ことを先に告げる。
// ■ 🔴 **識別子（利用者名）は画面に出さない**（ADR-0098 決定 1 と同じ向き）。候補も選択も表示名で出す。
// ■ 🔴 **自分自身は候補から除く**（後段も本人の指名を 400 にする）。
// ■ 初期フォーカスは**理由の欄**へ置く（破壊的な既定操作は無く、利用者が最初にするのは理由の入力である）。

export interface BreakGlassDialogProps {
  note: PrivateNoteDto;
  pending: boolean;
  /** 直前の発動の失敗（無ければ `null`）。 */
  error: unknown;
  onActivate: (reason: string, nomineeUserId: string) => void;
  onClose: () => void;
}

/** 発動の失敗を利用者向けの文言へ。**400 は後段の検証の文言をそのまま出す**（どの欄が悪いかを後段が知っている）。 */
function useActivationErrorMessage(error: unknown): string | null {
  const { t } = useLingui();
  if (error === null || error === undefined) return null;
  if (error instanceof ApiError) {
    if (error.kind === 'validation' && error.details.length > 0) return error.details.join(' ');
    if (error.kind === 'conflict')
      return t`この資料には有効な緊急アクセスが既にあるか、削除済みのため発動できません。`;
    if (error.kind === 'forbidden') return t`この資料に書き込む権限がないため発動できません。`;
    if (error.kind === 'notFound') return t`資料が見つかりませんでした。`;
    if (error.status === 503)
      return t`指名先がシステム管理者かを確認できませんでした。発動していません。時間をおいて再度お試しください。`;
  }
  return t`緊急アクセスを発動できませんでした。時間をおいて再度お試しください。`;
}

export function BreakGlassDialog({
  note,
  pending,
  error,
  onActivate,
  onClose,
}: BreakGlassDialogProps) {
  const { t } = useLingui();
  const { user } = useAuth();
  const reasonFieldRef = useRef<HTMLDivElement>(null);

  const [reason, setReason] = useState('');
  const [reasonTouched, setReasonTouched] = useState(false);
  const [term, setTerm] = useState('');
  const [nominee, setNominee] = useState<UserSummaryDto | null>(null);

  const lookup = useUserLookup(nominee === null ? term : '');
  const candidates = useMemo(
    () => (lookup.query.data ?? []).filter((u) => !sameUser(u.username, user?.name)),
    [lookup.query.data, user?.name],
  );

  const reasonState = reasonStateOf(reason);
  const reasonLength = reason.trim().length;
  const maxLength = REASON_MAX_LENGTH;
  const canSubmit = reasonState === 'ok' && nominee !== null && !pending;
  const errorMessage = useActivationErrorMessage(error);
  const noteTitle = note.title;
  const nomineeName = nominee?.displayName ?? '';

  function submit() {
    setReasonTouched(true);
    if (!canSubmit || nominee === null) return;
    onActivate(reason.trim(), nominee.username);
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent
        initialFocus={() => reasonFieldRef.current?.querySelector('textarea') ?? null}
        className="w-[min(560px,calc(100%-2rem))]"
      >
        <DialogTitle>{t`緊急アクセス（Break-glass）を発動しますか？`}</DialogTitle>
        <DialogDescription>
          <Trans>
            対象の資料「{noteTitle}」を、指名した管理者 1 人が発動から 15
            分間だけ閲覧できるようにします。閲覧だけで、編集・削除・共有の変更・ダウンロードはできません。
          </Trans>
        </DialogDescription>

        {errorMessage && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {errorMessage}
          </Alert>
        )}

        <div ref={reasonFieldRef} className="flex flex-col gap-1">
          <Label htmlFor="break-glass-reason" requiredHint={t`必須`}>
            <Trans>発動理由</Trans>
          </Label>
          <Textarea
            id="break-glass-reason"
            value={reason}
            rows={3}
            aria-invalid={reasonTouched && reasonState !== 'ok'}
            aria-describedby="break-glass-reason-help"
            onChange={(e) => setReason(e.target.value)}
            onBlur={() => setReasonTouched(true)}
          />
          {/* 文字数は**文言で**示す（色だけで上限超過を示さない）。 */}
          <p id="break-glass-reason-help" className="text-xs text-fg-muted">
            <Trans>
              {reasonLength} / {maxLength} 文字
            </Trans>
          </p>
          {reasonTouched && reasonState === 'empty' && (
            <p className="text-xs text-danger" role="alert">
              <Trans>理由は必須です。空欄では発動できません。</Trans>
            </p>
          )}
          {reasonState === 'tooLong' && (
            <p className="text-xs text-danger" role="alert">
              <Trans>理由は {maxLength} 文字以内で入力してください。</Trans>
            </p>
          )}
        </div>

        <section aria-label={t`閲覧を許す管理者`} className="flex flex-col gap-1">
          {nominee === null ? (
            <>
              <Label htmlFor="break-glass-nominee-search" requiredHint={t`必須`}>
                <Trans>閲覧を許す管理者を名前で検索する</Trans>
              </Label>
              <Input
                id="break-glass-nominee-search"
                value={term}
                onChange={(e) => setTerm(e.target.value)}
              />
              <p className="text-xs text-fg-muted">
                <Trans>
                  指名できるのはシステム管理者（platform-admin）1
                  人だけです。管理者でない人を選ぶと発動できません。
                </Trans>
              </p>
              {!lookup.enabled && (
                <p className="text-xs text-fg-muted">
                  <Trans>2 文字以上入力すると候補が出ます。</Trans>
                </p>
              )}
              {lookup.enabled && (
                <QueryState
                  query={lookup.query}
                  isEmpty={() => candidates.length === 0}
                  loadingLabel={t`利用者を検索中…`}
                  errorTitle={t`利用者を検索できませんでした。`}
                  empty={<EmptyState title={t`該当する利用者がいません。`} />}
                >
                  {() => (
                    <ul
                      aria-label={t`管理者の候補`}
                      className="flex max-h-40 flex-col gap-1 overflow-y-auto rounded-md border border-border p-1"
                    >
                      {candidates.map((candidate) => (
                        <li key={candidate.username}>
                          {/* 🔴 候補は表示名だけを出す（利用者名は出さない）。 */}
                          <Button
                            size="sm"
                            variant="ghost"
                            className="w-full justify-start"
                            onClick={() => setNominee(candidate)}
                          >
                            {candidate.displayName}
                          </Button>
                        </li>
                      ))}
                    </ul>
                  )}
                </QueryState>
              )}
            </>
          ) : (
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <span className="text-fg-muted">
                <Trans>閲覧を許す管理者:</Trans>
              </span>
              <span className="font-semibold">{nominee.displayName}</span>
              <Button
                size="sm"
                variant="secondary"
                disabled={pending}
                onClick={() => {
                  setNominee(null);
                  setTerm('');
                }}
              >
                <Trans>選び直す</Trans>
              </Button>
            </div>
          )}
        </section>

        {/* 計画 §SC-19 の固定文言。**ダイアログの中に常に出す**（折りたたまない）。 */}
        <Note>
          <Trans>
            この操作は監査ログに記録されます。アクセスは発動から 15
            分で自動失効し、延長できません。指名した管理者の閲覧も 1 件ずつ記録されます。
          </Trans>
        </Note>

        <DialogActions>
          <DialogClose render={<Button variant="secondary" disabled={pending} />}>
            {t`キャンセル`}
          </DialogClose>
          <Button variant="primary" disabled={!canSubmit} onClick={submit}>
            {nominee === null ? t`発動する` : t`${nomineeName} に閲覧を許して発動する`}
          </Button>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}
