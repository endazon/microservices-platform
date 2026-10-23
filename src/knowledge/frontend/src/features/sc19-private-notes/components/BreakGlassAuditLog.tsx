import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import {
  Button,
  EmptyState,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { useAuth } from '@foundation/auth/useAuth';
import { PlatformRole, useHasAnyRole } from '@foundation/auth/roles';
import type {
  BreakGlassAuditEntryDto,
  PrivateNoteDto,
} from '@foundation/api/generated/bff.schemas';
import { useResolvedUsers } from '../../../lib/users';
import { useBreakGlassAudit } from '../api/useBreakGlass';
import { useNow } from '../hooks/useNow';
import {
  auditOperationKeyOf,
  auditUsernamesOf,
  canOfferView,
  endedGrantIdsOf,
} from '../types/breakGlass';

// SC-19, SC-03, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841 裁定 5・6, [[IADR-0612]] (#2046 の 3 本目):
// **緊急アクセスの監査ログ。** 発動・終了・有効中の管理者の閲覧（1 件ずつ）を新しい順に出す。
//
// ■ 🔴 **誰の行まで出すかは後段が決める**（platform-admin は全件・本人は自分の資料の行だけ）。画面は絞らず、
//   ロールは**見出しの説明の出し分けにだけ**使う（認可の境界ではない）。
// ■ 🔴 **資料の本文・題名は契約に無い。** 本人の資料なら手元の一覧から題名を引いて出し、引けない
//   （他人の資料＝管理者が見ている行）なら資料の識別子を出す。**他人の資料の題名を引きに行かない**
//   （管理者が全利用者の個人資料を一覧する画面にしない。計画 §SC-19）。
// ■ 🔴 **利用者は表示名で出す**（利用者名は出さない。ADR-0098 決定 1 と同じ向き）。
// ■ SC-03 への導線（管理者の閲覧。planning#841 裁定 2）: **自分が指名された・終了の行が無い・終了予定が未来**の
//   発動の行にだけ「閲覧する」を出す。開けるかどうかは SC-03 の後段が要求ごとに決める（導線は許可ではない）。
// ■ **開いたときだけ引く**（SC-19 を開くたびに引かない）。

export interface BreakGlassAuditLogProps {
  /** 本人の資料（題名の表示に使う。削除済みを含む）。 */
  notes: PrivateNoteDto[];
}

export function BreakGlassAuditLog({ notes }: BreakGlassAuditLogProps) {
  const { t } = useLingui();
  const [open, setOpen] = useState(false);
  const isAdmin = useHasAnyRole(PlatformRole.Admin);

  return (
    <section aria-label={t`緊急アクセスの監査ログ`} className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">
          <Trans>緊急アクセスの監査ログ</Trans>
        </h3>
        <Button
          size="sm"
          variant="secondary"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? t`監査ログを閉じる` : t`監査ログを表示する`}
        </Button>
      </div>
      {open && <AuditTable notes={notes} isAdmin={isAdmin} />}
    </section>
  );
}

function AuditTable({ notes, isAdmin }: { notes: PrivateNoteDto[]; isAdmin: boolean }) {
  const { t } = useLingui();
  const { user } = useAuth();
  const audit = useBreakGlassAudit(true);
  const entries = useMemo(() => audit.data ?? [], [audit.data]);
  // `/bff/users/resolve` の上限（1 回 100 名）: 行は最大 50 件・1 行 3 名で、所有者と指名先は同じ発動の行どうしで重なるため
  // 実際の名前はずっと少ない。理論上の超過分は `auditUsernamesOf` が 100 で切り、「（不明な利用者）」になる（識別子へは落とさない）。
  const resolved = useResolvedUsers(auditUsernamesOf(entries));
  // 「閲覧する」の出し分けにだけ使う時計（15 秒ごと。秒単位の精度は要らない —— 開けるかは後段が決める）。
  const now = useNow(true, 15_000);
  const ended = useMemo(() => endedGrantIdsOf(entries), [entries]);

  function nameOf(username: string): string {
    return (
      resolved.data?.find((u) => u.username.toLowerCase() === username.toLowerCase())
        ?.displayName ?? (resolved.isPending ? t`読み込み中…` : t`（不明な利用者）`)
    );
  }

  function operationLabel(entry: BreakGlassAuditEntryDto): string {
    switch (auditOperationKeyOf(entry.operation)) {
      case 'activate':
        return t`発動`;
      case 'end':
        return t`終了`;
      case 'view':
        return entry.viewTarget === 'content' ? t`閲覧（本文）` : t`閲覧（属性）`;
      default:
        return t`不明な操作`;
    }
  }

  return (
    <>
      <p className="text-xs text-fg-muted">
        {isAdmin ? (
          <Trans>
            システム管理者には、すべての利用者の緊急アクセスの記録が表示されます（新しい順に最大 50
            件）。資料の題名と本文は記録されません。
          </Trans>
        ) : (
          <Trans>
            自分の資料に対する緊急アクセスの記録が表示されます（新しい順に最大 50
            件）。資料の題名と本文は記録されません。
          </Trans>
        )}
      </p>
      <QueryState
        query={audit}
        isEmpty={() => entries.length === 0}
        loadingLabel={t`監査ログを読み込み中…`}
        errorTitle={t`監査ログを取得できませんでした。`}
        empty={<EmptyState title={t`緊急アクセスの記録はありません。`} />}
      >
        {() => (
          <Table>
            <TableCaption>
              <Trans>緊急アクセスの監査ログ（新しい順）</Trans>
            </TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell scope="col">
                  <Trans>日時</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>操作</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>実行者</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>対象の資料</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>所有者</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>指名された管理者</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>理由</Trans>
                </TableHeaderCell>
                <TableHeaderCell scope="col">
                  <Trans>終了（予定）</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {entries.map((entry) => {
                const own = notes.find((n) => n.id === entry.documentId);
                const offerView = canOfferView(entry, user?.name, now, ended);
                return (
                  <TableRow key={entry.id}>
                    <TableCell>{formatDateTime(entry.occurredAt)}</TableCell>
                    <TableCell>{operationLabel(entry)}</TableCell>
                    <TableCell>{nameOf(entry.actorUserId)}</TableCell>
                    <TableCell>
                      {own ? (
                        <span className="font-medium">{own.title}</span>
                      ) : (
                        <code className="text-xs">{entry.documentId}</code>
                      )}
                      {offerView && (
                        <span className="block">
                          {/* SC-03（新しい画面は作らない。planning#841 裁定 2）。`via` は表示の文脈だけを運ぶ。 */}
                          <Link
                            to="/docs/$id"
                            params={{ id: entry.documentId }}
                            search={{ via: 'break-glass' }}
                            className="text-brand underline"
                          >
                            <Trans>指名された資料を閲覧する</Trans>
                          </Link>
                        </span>
                      )}
                    </TableCell>
                    <TableCell>{nameOf(entry.ownerUserId)}</TableCell>
                    <TableCell>{nameOf(entry.nomineeUserId)}</TableCell>
                    <TableCell className="whitespace-pre-wrap break-words">
                      {entry.reason}
                    </TableCell>
                    <TableCell>{formatDateTime(entry.endsAt)}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </QueryState>
    </>
  );
}
