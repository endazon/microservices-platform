import { useCallback, useEffect, useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button, Label, Note, Panel, Select, StatusBadge } from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import type {
  BreakGlassGrantDto,
  BreakGlassStatusResponse,
  PrivateNoteDto,
} from '@foundation/api/generated/bff.schemas';
import { useResolvedUsers } from '../../../lib/users';
import {
  EXPIRY_POLL_LIMIT_MS,
  useBreakGlassActions,
  useBreakGlassStatus,
} from '../api/useBreakGlass';
import { useNow } from '../hooks/useNow';
import { remainingSeconds, splitMinutesSeconds } from '../types/breakGlass';
import { BreakGlassAuditLog } from './BreakGlassAuditLog';
import { BreakGlassDialog } from './BreakGlassDialog';

// SC-19 主要素 8, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841, [[IADR-0612]] (#2046 の 3 本目):
// **緊急アクセス（Break-glass）の導線。** 本人が自分の資料 1 件について発動し、残り時間を見て、早く終えられる。
//
// ■ 🔴 **本人が自分の資料に対して発動する導線だけを置く**（計画 §SC-19）。管理者を含む第三者が発動する導線、
//   管理者が全利用者の個人資料を閲覧する画面は**描かない**。対象の選択肢は本人の利用中の資料だけである
//   （一覧の応答そのもの。削除済みは後段が 409 にするので選ばせない）。
// ■ 🔴 **状態は資料を選んでから 1 件だけ引く**（一覧の行ごとに引かない）。有効性はサーバ側の発動記録で決まり、
//   画面の残り時間は表示にすぎない。**0 になったら状態を引き直す**（画面だけで「失効した」と言い切らない）。
// ■ 監査ログ（`BreakGlassAuditLog`）は本欄を開いたときだけ引く。
// ■ 色だけで状態を示さない: 有効中は `StatusBadge`（アイコン＋文言）と残り時間の文言で示す。

export interface BreakGlassPanelProps {
  /** 本人の資料（一覧の応答そのもの。削除済みを含む —— 監査ログの題名の表示に使う）。 */
  notes: PrivateNoteDto[];
}

/** 有効な発動の表示（残り時間・指名先・理由・終了）。 */
function ActiveGrant({
  grant,
  pending,
  pollSince,
  onEnd,
  onExpired,
}: {
  grant: BreakGlassGrantDto;
  pending: boolean;
  /** 残り 0 になって状態の引き直しを始めた時刻（始めていなければ null）。 */
  pollSince: number | null;
  onEnd: () => void;
  /** 残り 0 になった（`restart` は上限に達した後の「状態を再確認する」）。 */
  onExpired: (grantId: string, restart?: boolean) => void;
}) {
  const { t } = useLingui();
  const now = useNow(true);
  const left = remainingSeconds(grant.endsAt, now);
  const expired = left === 0;
  const resolved = useResolvedUsers([grant.nomineeUserId]);
  const nomineeName =
    resolved.data?.find((u) => u.username === grant.nomineeUserId)?.displayName ??
    (resolved.isPending ? t`読み込み中…` : t`（不明な利用者）`);

  // 残り 0 になったら状態を引き直し始める（失効を決めるのはサーバ側の記録である）。
  // 引き直しは間隔を置いて上限まで続く（`useBreakGlassStatus` の `pollSince`）。
  const grantId = grant.id;
  useEffect(() => {
    if (expired) onExpired(grantId);
  }, [expired, grantId, onExpired]);
  // 上限まで引いてもサーバが有効を返す（端末の時計が大きく進んでいる等）。固着させず、理由と再確認の導線を出す。
  const gaveUp = expired && pollSince !== null && now - pollSince >= EXPIRY_POLL_LIMIT_MS;

  const { minutes, seconds } = splitMinutesSeconds(left);
  const endsAt = formatDateTime(grant.endsAt);
  const reason = grant.reason;

  return (
    <div className="flex flex-col gap-2" aria-live="polite">
      <p className="flex flex-wrap items-center gap-2 text-sm">
        <StatusBadge tone="warning">{t`緊急アクセス有効中`}</StatusBadge>
        <span>
          {gaveUp ? (
            <Trans>
              失効を確認できません。端末の時計がずれている可能性があります。終えるには「今すぐ終了する」を押してください。
            </Trans>
          ) : expired ? (
            <Trans>まもなく失効します（状態を確認しています）</Trans>
          ) : (
            <Trans>
              残り {minutes} 分 {seconds} 秒（{endsAt} に自動失効）
            </Trans>
          )}
        </span>
      </p>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <dt className="text-fg-muted">
          <Trans>閲覧を許した管理者</Trans>
        </dt>
        <dd>{nomineeName}</dd>
        <dt className="text-fg-muted">
          <Trans>発動理由</Trans>
        </dt>
        <dd className="whitespace-pre-wrap break-words">{reason}</dd>
      </dl>
      <div className="flex flex-wrap gap-2">
        <Button variant="danger" disabled={pending} onClick={onEnd}>
          <Trans>今すぐ終了する</Trans>
        </Button>
        {gaveUp && (
          <Button variant="secondary" onClick={() => onExpired(grantId, true)}>
            <Trans>状態を再確認する</Trans>
          </Button>
        )}
      </div>
    </div>
  );
}

export function BreakGlassPanel({ notes }: BreakGlassPanelProps) {
  const { t } = useLingui();
  const [targetId, setTargetId] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  // 🔴 **対象に選べるのは利用中の資料だけ**（削除済みは後段が 409 にする）。
  // 選んでいた資料が利用中から外れた（削除した）ら選択も外れる。
  const live = useMemo(() => notes.filter((n) => !n.deleted), [notes]);
  const target = live.find((n) => n.id === targetId) ?? null;
  // 残り 0 の後の引き直し（発動ごと）。**表示中の発動と同じ発動のときだけ**引き直す ——
  // 失効が確かめられて状態が変われば（有効なし・別の発動）、引き直しは自然に止まる。
  const [pollFor, setPollFor] = useState<{ grantId: string; since: number } | null>(null);
  const status = useBreakGlassStatus(target?.id ?? null, pollFor);
  const { activate, end, refresh } = useBreakGlassActions(target?.id ?? null);
  const activeId = status.data?.active?.id ?? null;
  const pollSince = pollFor !== null && pollFor.grantId === activeId ? pollFor.since : null;

  const startExpiryPoll = useCallback(
    (grantId: string, restart = false) => {
      setPollFor((prev) =>
        !restart && prev?.grantId === grantId ? prev : { grantId, since: Date.now() },
      );
      refresh();
    },
    [refresh],
  );
  const pending = activate.isPending || end.isPending;

  function selectTarget(id: string) {
    setTargetId(id === '' ? null : id);
    setNotice(null);
    activate.reset();
    end.reset();
  }

  function runActivate(reason: string, nomineeUserId: string) {
    if (target === null) return;
    setNotice(null);
    end.reset();
    activate.mutate(
      { id: target.id, data: { reason, nomineeUserId } },
      {
        onSuccess: () => {
          setDialogOpen(false);
          setNotice(t`緊急アクセスを発動しました。`);
        },
      },
    );
  }

  function runEnd() {
    if (target === null) return;
    setNotice(null);
    activate.reset();
    end.mutate({ id: target.id }, { onSuccess: () => setNotice(t`緊急アクセスを終了しました。`) });
  }

  return (
    <Panel heading={<Trans>緊急アクセス（Break-glass）</Trans>}>
      <div className="flex flex-col gap-3">
        <p className="text-sm text-fg-muted">
          <Trans>
            自分の資料 1 件について、理由を書き、システム管理者（platform-admin）を 1
            人指名して発動します。指名した管理者だけが、発動から 15
            分間その資料を閲覧できます（閲覧だけです）。15
            分より前に終えることもできます。発動・終了・管理者の閲覧はすべて監査ログに記録されます。
          </Trans>
        </p>

        <div className="flex flex-col gap-1">
          <Label htmlFor="break-glass-target" requiredHint={t`必須`}>
            <Trans>対象の資料</Trans>
          </Label>
          <Select
            id="break-glass-target"
            value={target?.id ?? ''}
            onChange={(e) => selectTarget(e.target.value)}
          >
            <option value="">{t`資料を選んでください`}</option>
            {live.map((n) => (
              <option key={n.id} value={n.id}>
                {n.title}
              </option>
            ))}
          </Select>
        </div>

        {notice && (
          <Alert tone="success" label={t`完了`} role="status">
            {notice}
          </Alert>
        )}
        {end.isError && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {t`緊急アクセスを終了できませんでした。既に失効しているか、時間をおいて再度お試しください。`}
          </Alert>
        )}

        {target !== null && (
          <QueryState
            query={status}
            loadingLabel={t`緊急アクセスの状態を読み込み中…`}
            errorTitle={t`緊急アクセスの状態を取得できませんでした。`}
          >
            {(data: BreakGlassStatusResponse) =>
              data.active ? (
                <ActiveGrant
                  grant={data.active}
                  pending={pending}
                  pollSince={pollSince}
                  onEnd={runEnd}
                  onExpired={startExpiryPoll}
                />
              ) : (
                <div className="flex flex-col gap-2">
                  <p className="text-sm">
                    <StatusBadge tone="neutral">{t`有効な緊急アクセスはありません`}</StatusBadge>
                  </p>
                  <div>
                    <Button
                      variant="primary"
                      disabled={pending}
                      onClick={() => {
                        activate.reset();
                        setDialogOpen(true);
                      }}
                    >
                      <Trans>この資料に緊急アクセスを発動する…</Trans>
                    </Button>
                  </div>
                </div>
              )
            }
          </QueryState>
        )}

        <Note>
          <Trans>
            管理者を含む他の人がこの資料に緊急アクセスを発動することはできません。発動できるのは資料の所有者本人だけです。
          </Trans>
        </Note>

        <BreakGlassAuditLog notes={notes} />
      </div>

      {dialogOpen && target !== null && (
        <BreakGlassDialog
          note={target}
          pending={activate.isPending}
          error={activate.error}
          onActivate={runActivate}
          onClose={() => setDialogOpen(false)}
        />
      )}
    </Panel>
  );
}
