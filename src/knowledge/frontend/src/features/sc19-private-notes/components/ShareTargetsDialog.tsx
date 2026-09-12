import { useMemo, useRef, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Note,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import { useAuth } from '@foundation/auth/useAuth';
import type { DocumentShareDto, PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import { usePrivateNoteShareActions, usePrivateNoteShares } from '../api/usePrivateNoteShares';
import { useResolvedUsers, useUserLookup } from '../api/useUserLookup';

// SC-19 主要素 3, UC-11, FR-19, ADR-0036 D-06 / ADR-0098 / IADR-0445: 公開範囲の変更ダイアログ。
//
// ■ 🔴 **個人指定だけを扱う。グループの導線も文言も置かない**（ADR-0098 決定 2）。
//   理由は「未実装だから」ではなく **「効かないから」** である —— `${current_groups}` の束縛が
//   実装に無く、共有先ベースの分岐が認可スコープへ載っていないため、`subjectType=group` を
//   台帳へ入れても**閲覧できるようになる主体は 1 人も居ない**。
//   **設定できるのに何も起きない面は、利用者に誤った安心を与える。**
//   - 台帳・契約は `group` を受け付けたままである（拒否する改修はしない。決定 2）。
//     既にグループ共有がある資料は**そのまま残す**。消さないことを `Note` で告知する。
//   - 「置いていない」ことは単体テストが**陽性対照（個人の追加が在る）と対で**固定する。
//
// ■ 🔴 **`subjectId`（利用者名）を画面へ出さない**（ADR-0098 決定 1「画面には表示名を出し、
//   識別子は出さない」）。`title` 属性にも入れない —— **ツールチップは画面である。**
//   表示名は `/bff/users/resolve` で引く。引けなかった名前（退職後に利用者ごと消えた等）は
//   **「（不明な利用者）」**と出す。🔴 **行を消さない** —— 台帳には残っており、
//   取り消せなくなると「見えない共有」が恒久化する。
//
// ■ 🔴 **無効化済み（`enabled=false`）は表示名に併記する。** `resolve` は無効化済みも返す
//   （契約の非対称。`lookup` は返さない）。退職者への共有が残っていることは、
//   **利用者が気付いて取り消せる**必要がある情報である。
//
// ■ 候補から除くのは 2 つだけ: **自分自身**（自分に共有する意味が無い）と**既に共有済み**
//   （契約は重複付与を 409 にする）。本人の利用者名は身元（`/bff/auth/me` の
//   `preferred_username`）から取る —— 台帳の `subjectId` と同じ名前空間である。
//
// ■ 開いている間だけ描く（`ConfirmDialog` と同じ作法）。`open` は常に真で、閉じる要求
//   （閉じる・Esc・背景クリック）は `onOpenChange` を通って `onClose` へ出る。
//   初期フォーカスは**検索入力**へ置く（`ConfirmDialog` が取消へ置くのとは別の規律である ——
//   ここに破壊的な既定操作は無く、利用者が最初にすることは検索である）。

/** 台帳が運ぶ `subjectType` のうち、本画面が扱う唯一の値。 */
const USER_SUBJECT = 'user';

export interface ShareTargetsDialogProps {
  note: PrivateNoteDto;
  onClose: () => void;
}

export function ShareTargetsDialog({ note, onClose }: ShareTargetsDialogProps) {
  const { t } = useLingui();
  const { user } = useAuth();
  // 🔴 **初期フォーカスは検索入力へ置く**（`ConfirmDialog` が取消へ置くのとは別の規律 —— ここに
  // 破壊的な既定操作は無く、利用者が最初にすることは検索である）。`@platform/ui` の `Input` は
  // `ref` を型で受けない（`InputHTMLAttributes` ベース。`Button` だけが `ComponentPropsWithRef`）ので、
  // **入力を囲む枠へ ref を置き、Base UI の `initialFocus` の関数形で中の `input` を返す。**
  // 共有 UI の側を直す選択は採らなかった（本作業の担当範囲外である）。
  const searchFieldRef = useRef<HTMLDivElement>(null);

  const shares = usePrivateNoteShares(note.id, true);
  const { grant, revoke } = usePrivateNoteShareActions(note.id);

  const [term, setTerm] = useState('');
  const [picked, setPicked] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  /** 個人指定だけを取り出す（グループ共有は本画面の対象外だが、消さない）。 */
  const userShares = useMemo(
    () => (shares.data ?? []).filter((s: DocumentShareDto) => s.subjectType === USER_SUBJECT),
    [shares.data],
  );
  const sharedNames = useMemo(() => userShares.map((s) => s.subjectId), [userShares]);
  const resolved = useResolvedUsers(sharedNames);

  const lookup = useUserLookup(term);
  const candidates = useMemo(
    () =>
      (lookup.query.data ?? []).filter(
        (u) => u.username !== user?.name && !sharedNames.includes(u.username),
      ),
    [lookup.query.data, sharedNames, user?.name],
  );

  const mutations = [grant, revoke];
  const failed = mutations.find((m) => m.isError);
  const pending = mutations.some((m) => m.isPending);

  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  /** 台帳の利用者名を表示名へ。引けなかった名前は「（不明な利用者）」。 */
  function displayNameOf(username: string): string {
    const found = (resolved.data ?? []).find((u) => u.username === username);
    if (found === undefined) return t`（不明な利用者）`;
    // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
    const displayName = found.displayName;
    return found.enabled ? displayName : t`${displayName}（無効化済み）`;
  }

  function addPicked() {
    if (picked === null) return;
    beginOperation();
    grant.mutate(
      // 🔴 `subjectType` は `user` 固定である（グループを送る経路をここに作らない）。
      { id: note.id, data: { subjectType: USER_SUBJECT, subjectId: picked } },
      {
        onSuccess: () => {
          setPicked(null);
          setTerm('');
          setNotice(t`指定先を追加しました。`);
        },
      },
    );
  }

  function revokeShare(subjectId: string) {
    beginOperation();
    revoke.mutate(
      { id: note.id, subjectType: USER_SUBJECT, subjectId },
      { onSuccess: () => setNotice(t`指定先を取り消しました。`) },
    );
  }

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
  const noteTitle = note.title;
  const groupCount = note.sharedGroupCount;

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent
        initialFocus={() => searchFieldRef.current?.querySelector('input') ?? null}
        className="w-[min(560px,calc(100%-2rem))]"
      >
        <DialogTitle>{t`「${noteTitle}」の共有先`}</DialogTitle>

        <p className="text-sm text-fg-muted">
          <Trans>
            共有した相手はこの資料を閲覧できます。共有された相手が、さらに別の人へ共有することはできません。
          </Trans>
        </p>

        {notice && (
          <Alert tone="success" label={t`完了`} role="status">
            {notice}
          </Alert>
        )}
        {failed && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {toMessages(
              failed.error,
              t`共有先を変更できませんでした。時間をおいて再度お試しください。`,
            ).join(' ')}
          </Alert>
        )}

        {/* 🔴 グループ共有は本画面で変更できないが、**残っていることは伝える**（ADR-0098 決定 2）。 */}
        {groupCount > 0 && (
          <Note>
            <Trans>
              この資料にはグループへの共有が {groupCount} 件あります。本画面では変更できません。
            </Trans>
          </Note>
        )}

        <section aria-label={t`現在の指定先`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">
            <Trans>現在の指定先</Trans>
          </h3>
          <QueryState
            query={shares}
            isEmpty={() => userShares.length === 0}
            loadingLabel={t`共有先を読み込み中…`}
            errorTitle={t`共有先を取得できませんでした。`}
            empty={<EmptyState title={t`個人への共有はありません。`} />}
          >
            {() => (
              <ul className="flex flex-col gap-1">
                {userShares.map((share) => (
                  <li
                    key={share.subjectId}
                    className="flex items-center justify-between gap-2 text-sm"
                  >
                    {/* 🔴 ここに出るのは表示名だけである（利用者名も `title` 属性も出さない）。 */}
                    <span>{displayNameOf(share.subjectId)}</span>
                    <Button
                      size="sm"
                      variant="secondary"
                      disabled={pending}
                      onClick={() => revokeShare(share.subjectId)}
                    >
                      <Trans>取り消す</Trans>
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </QueryState>
        </section>

        <section aria-label={t`利用者を追加`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">
            <Trans>利用者を追加</Trans>
          </h3>
          <div ref={searchFieldRef} className="flex flex-col gap-1">
            <Label htmlFor="share-user-search">
              <Trans>名前で検索する</Trans>
            </Label>
            <Input
              id="share-user-search"
              value={term}
              onChange={(e) => {
                setTerm(e.target.value);
                setPicked(null);
              }}
            />
            {/* 2 文字未満では問い合わせない。**なぜ候補が出ないのかを書く**（黙って空にしない）。 */}
            {!lookup.enabled && (
              <p className="text-xs text-fg-muted">
                <Trans>2 文字以上入力すると候補が出ます。</Trans>
              </p>
            )}
          </div>

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
                  role="listbox"
                  aria-label={t`利用者の候補`}
                  className="flex max-h-40 flex-col gap-1 overflow-y-auto rounded-md border border-border p-1"
                >
                  {candidates.map((candidate) => {
                    const selected = candidate.username === picked;
                    return (
                      <li
                        key={candidate.username}
                        role="option"
                        aria-selected={selected}
                        tabIndex={0}
                        className={
                          selected
                            ? 'cursor-pointer rounded-sm bg-surface-muted px-2 py-1 text-sm font-semibold text-fg'
                            : 'cursor-pointer rounded-sm px-2 py-1 text-sm text-fg'
                        }
                        onClick={() => setPicked(candidate.username)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            setPicked(candidate.username);
                          }
                        }}
                      >
                        {/* 🔴 候補も表示名だけを出す（利用者名は出さない）。 */}
                        {candidate.displayName}
                      </li>
                    );
                  })}
                </ul>
              )}
            </QueryState>
          )}

          <div>
            <Button variant="primary" disabled={picked === null || pending} onClick={addPicked}>
              <Trans>追加</Trans>
            </Button>
          </div>
        </section>

        <DialogActions>
          <DialogClose render={<Button variant="secondary" disabled={pending} />}>
            {t`閉じる`}
          </DialogClose>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}
