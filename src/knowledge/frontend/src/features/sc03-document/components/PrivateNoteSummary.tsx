import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { Kv, KvItem, Panel, StatusBadge } from '@platform/ui';
import { useAuth } from '@foundation/auth/useAuth';
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';
import { isPrivateNote } from '../../../lib/abac';
import { useResolvedUsers } from '../../../lib/users';
import {
  VISIBILITY_TONES,
  usePrivateNoteVisibility,
  visibilityKeyOf,
} from '../../../lib/private-notes';

// FR-19, UC-11, SC-03 §個人資料の表示, 計画 ADR-0102 決定 1〜5 / [[IADR-0451]] (#1455):
// **個人資料の表示。** 対象が個人資料（`doc_scope=private-note`）のときだけ描く。
//
// 🔴 **所有者以外が本画面を開く経路は 2 つある** —— 共有された相手（ADR-0036 D-06）と、
// 退職・無効化から 30 日の窓で閲覧する管理者（同 D-09）。**平時の管理者は読めない**（D-08）。
// したがって「自分の資料しか開かれない」前提を置いてはならない。
//
// 描くもの:
//   - **ラベル**: 👤 ＋「個人資料」。**括弧書き「（自分のみ）」は所有者にだけ**（決定 5）。
//   - **所有者**: **表示名**（決定 3）。🔴 **利用者名（識別子）は出さない**（ADR-0098 決定 1 と同じ向き）。
//   - **公開範囲**: **所有者にだけ**（決定 1）。所有者以外には**欄ごと出さない** —— 空欄も既定値も描かない
//     （描いた形そのものが「共有の有無」を伝えるため。ADR-0101 決定 2 と同じ理由）。
//   - **SC-19 への導線**（計画 §個人資料の表示）。

/** 所有者の属性キー。値は基盤の利用者名であり、**画面へは出さない**（表示名へ引く）。 */
const OWNER_KEY = 'owner';

export function PrivateNoteSummary({ doc }: { doc: DocumentDto }) {
  const { t } = useLingui();
  const { user } = useAuth();
  const privateNote = isPrivateNote(doc.attributes);
  const owner = doc.attributes?.[OWNER_KEY] ?? '';
  // 🔴 **所有者かどうかは表示の分岐にだけ使う。** 認可の判定はサーバにあり（`ADR-0036` D-05 の
  // 束縛は認可サービスが行う）、画面は 2 本目の判定軸を持たない。
  const isOwner = owner !== '' && owner === user?.name;

  // 公開範囲は**所有者だけが読める口**から引く（決定 2）。門は無駄な取得を避けるためのもので、
  // **可視性の統制ではない** —— 口は本人の資料しか返さないので、門が誤っても他人の公開範囲は出ない。
  const note = usePrivateNoteVisibility(doc.id, privateNote && isOwner);
  const resolved = useResolvedUsers(privateNote && owner !== '' ? [owner] : []);
  const displayName = resolved.data?.find((u) => u.username === owner)?.displayName;

  if (!privateNote) return null;

  return (
    <Panel heading={<Trans>個人資料</Trans>}>
      <p className="mb-n2 text-sm text-fg">
        <span aria-hidden="true" className="mr-1">
          👤
        </span>
        {/* 計画 ADR-0102 決定 5: 括弧書きは所有者にだけ付す。 */}
        {isOwner ? <Trans>個人資料（自分のみ）</Trans> : <Trans>個人資料</Trans>}
      </p>
      <Kv columns={1}>
        <KvItem label={t`所有者`}>
          {/* 🔴 引けなかったときに利用者名へフォールバックしない（決定 3）。
              フォールバックを許すと「識別子を出さない」が失敗時にだけ破れる。
              無効化済み（退職者）の利用者も `resolve` は返す。 */}
          {displayName ?? (resolved.isPending ? t`読み込み中…` : t`（不明な利用者）`)}
        </KvItem>
        {/* 🔴 所有者以外には**欄ごと出さない**（決定 1）。空欄・既定値でも描かない。 */}
        {isOwner && note.data ? (
          <KvItem label={t`公開範囲`}>
            <VisibilityBadge visibility={note.data.visibility} />
          </KvItem>
        ) : null}
      </Kv>
      <p className="mt-n2 text-xs text-fg-muted">
        <Link to="/my/notes" search={{ tab: 'active', q: '' }} className="underline">
          <Trans>個人資料の管理へ</Trans>
        </Link>
      </p>
    </Panel>
  );
}

/**
 * 公開範囲のバッジ。**文言と色の対応は `lib/private-notes` が持つ**（SC-19 と同じ語彙）。
 *
 * 🔴 **件数は出さない。** 計画が SC-03 に求めるのは**区分**であり、指定先と件数は SC-19 の持ち場である。
 */
function VisibilityBadge({ visibility }: { visibility: string }) {
  const { t } = useLingui();
  const key = visibilityKeyOf(visibility);
  const label =
    key === 'private'
      ? t`非公開`
      : key === 'users'
        ? t`個人指定`
        : key === 'groups'
          ? t`グループ指定`
          : t`公開範囲を判定できません`;
  return <StatusBadge tone={VISIBILITY_TONES[key]}>{label}</StatusBadge>;
}
