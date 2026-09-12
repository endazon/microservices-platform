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
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import { useAuth } from '@foundation/auth/useAuth';
import type { DocumentShareDto, PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import { usePrivateNoteShareActions, usePrivateNoteShares } from '../api/usePrivateNoteShares';
import { useResolvedUsers, useUserLookup } from '../api/useUserLookup';
import { useGroupLookup, useResolvedGroups } from '../api/useGroupLookup';

// SC-19 主要素 3, UC-11, FR-19, ADR-0036 D-06 / ADR-0098 決定 1 / IADR-0445 / IADR-0447 (#1447):
// 公開範囲の変更ダイアログ。
//
// ■ 🔴 **個人とグループの両方を扱う**（ADR-0098 決定 2 の暫定手段はここで解除された）。
//   決定 2 が「配線まで描かない」と定めていた理由は「未実装だから」ではなく **「効かないから」**
//   —— `${current_groups}` の束縛が実装に無く、共有先ベースの分岐が認可スコープへ載っていなかった。
//   #1447 でその 2 つが入ったため、**グループ共有は実際に閲覧を許可する**。
//   よって導線を置く（併せて「本画面では変更できません」の告知を撤去した）。
//
// ■ 🔴 **`subjectId`（利用者名・グループ ID）を画面へ出さない**（ADR-0098 決定 1「画面には
//   表示名を出し、識別子は出さない」）。`title` 属性にも入れない —— **ツールチップは画面である。**
//   表示名は `/bff/users/resolve`・`/bff/groups/resolve` で引く。引けなかった指定先は
//   **「（不明な利用者）」「（見つからないグループ）」**と出す。🔴 **行を消さない** ——
//   台帳には残っており、取り消せなくなると「見えない共有」が恒久化する。
//
// ■ 🔴 **無効化済み（`enabled=false`）は表示名に併記する。** `resolve` は無効化済みも返す
//   （契約の非対称。`lookup` は返さない）。退職者への共有が残っていることは、
//   **利用者が気付いて取り消せる**必要がある情報である。
//
// ■ 🔴 **グループは表示名を主・パスを副（muted）で出す。** 同名のグループを区別する手掛かりは
//   パスだけだが、**パスを主に出すと木の形を知らない利用者には読めない**（`GroupSummaryDto` の
//   注記と同じ向き）。パスは識別子ではないので出してよい。
//
// ■ 候補から除くのは 3 つ: **自分自身**（自分に共有する意味が無い）・**既に共有済み**
//   （契約は重複付与を 409 にする）・**種別の違う指定先**（グループ ID と利用者名は別の名前空間で
//   あり、混ぜて除くと同名の取りこぼしが起きる）。本人の利用者名は身元（`/bff/auth/me` の
//   `preferred_username`）から取る —— 台帳の `subjectId` と同じ名前空間である。
//
// ■ 開いている間だけ描く（`ConfirmDialog` と同じ作法）。`open` は常に真で、閉じる要求
//   （閉じる・Esc・背景クリック）は `onOpenChange` を通って `onClose` へ出る。
//   初期フォーカスは**検索入力**へ置く（`ConfirmDialog` が取消へ置くのとは別の規律である ——
//   ここに破壊的な既定操作は無く、利用者が最初にすることは検索である）。

/** 台帳が運ぶ `subjectType`。**画面はこの 2 値だけを送る。** */
const USER_SUBJECT = 'user';
const GROUP_SUBJECT = 'group';

/** 指定先の種別（切替の値）。 */
type SubjectType = typeof USER_SUBJECT | typeof GROUP_SUBJECT;

/** 1 行の表示。**識別子は `key` にしか使わない**（DOM へは出さない）。 */
interface TargetRow {
  key: string;
  /** 主たる手掛かり（表示名、または引けなかったことを示す文言）。 */
  primary: string;
  /** 副の手掛かり（グループのパス）。個人は持たない。 */
  secondary: string | null;
}

/**
 * 指定先の一覧（種別ごと）。**空の種別は見出しごと描かない。**
 *
 * **入れ子のコンポーネントにしない** —— 描画のたびに型が変わると、同じ行でも React が
 * 作り直す（状態を持たない今は無害だが、行に状態が増えた瞬間に消える形である）。
 */
function TargetList({
  heading,
  rows,
  type,
  pending,
  onRevoke,
}: {
  heading: string;
  rows: TargetRow[];
  type: string;
  pending: boolean;
  onRevoke: (type: string, subjectId: string) => void;
}) {
  if (rows.length === 0) return null;
  return (
    <div className="flex flex-col gap-1">
      <h4 className="text-xs font-semibold text-fg-muted">{heading}</h4>
      <ul className="flex flex-col gap-1">
        {rows.map((row) => (
          <li key={row.key} className="flex items-center justify-between gap-2 text-sm">
            {/* 🔴 ここに出るのは表示名（とグループのパス）だけである（識別子は出さない）。 */}
            <span className="flex flex-col">
              <span>{row.primary}</span>
              {row.secondary !== null && (
                <span className="text-xs text-fg-muted">{row.secondary}</span>
              )}
            </span>
            <Button
              size="sm"
              variant="secondary"
              disabled={pending}
              onClick={() => onRevoke(type, row.key)}
            >
              <Trans>取り消す</Trans>
            </Button>
          </li>
        ))}
      </ul>
    </div>
  );
}

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
  // 🔴 **種別の切替（ラジオ）はこの枠の外に置く** —— 中に入れると `querySelector('input')` が
  // ラジオを先に拾い、初期フォーカスが検索入力から外れる。
  const searchFieldRef = useRef<HTMLDivElement>(null);

  const shares = usePrivateNoteShares(note.id, true);
  const { grant, revoke } = usePrivateNoteShareActions(note.id);

  const [subjectType, setSubjectType] = useState<SubjectType>(USER_SUBJECT);
  const [term, setTerm] = useState('');
  const [picked, setPicked] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const addingUsers = subjectType === USER_SUBJECT;

  const userShares = useMemo(
    () => (shares.data ?? []).filter((s: DocumentShareDto) => s.subjectType === USER_SUBJECT),
    [shares.data],
  );
  const groupShares = useMemo(
    () => (shares.data ?? []).filter((s: DocumentShareDto) => s.subjectType === GROUP_SUBJECT),
    [shares.data],
  );
  const sharedNames = useMemo(() => userShares.map((s) => s.subjectId), [userShares]);
  const sharedGroupIds = useMemo(() => groupShares.map((s) => s.subjectId), [groupShares]);
  const resolvedUsers = useResolvedUsers(sharedNames);
  const resolvedGroups = useResolvedGroups(sharedGroupIds);

  // 🔴 **選んでいない種別は問い合わせない。** 空の検索語を渡すと `enabled` が偽になり、
  // 要求が飛ばない（種別を切り替えたときは検索語と選択を捨てるので、実際に空になる）。
  const userLookup = useUserLookup(addingUsers ? term : '');
  const groupLookup = useGroupLookup(addingUsers ? '' : term);
  const lookup = addingUsers ? userLookup : groupLookup;

  const userCandidates = useMemo(
    () =>
      (userLookup.query.data ?? []).filter(
        (u) => u.username !== user?.name && !sharedNames.includes(u.username),
      ),
    [userLookup.query.data, sharedNames, user?.name],
  );
  const groupCandidates = useMemo(
    () => (groupLookup.query.data ?? []).filter((g) => !sharedGroupIds.includes(g.id)),
    [groupLookup.query.data, sharedGroupIds],
  );

  const mutations = [grant, revoke];
  const failed = mutations.find((m) => m.isError);
  const pending = mutations.some((m) => m.isPending);

  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  /** 種別を切り替える。**検索語と選択は捨てる**（別の名前空間の値を持ち越さない）。 */
  function switchSubjectType(next: SubjectType) {
    setSubjectType(next);
    setTerm('');
    setPicked(null);
  }

  /** 台帳の利用者名を表示名へ。引けなかった名前は「（不明な利用者）」。 */
  function displayNameOf(username: string): string {
    const found = (resolvedUsers.data ?? []).find((u) => u.username === username);
    if (found === undefined) return t`（不明な利用者）`;
    // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
    const displayName = found.displayName;
    return found.enabled ? displayName : t`${displayName}（無効化済み）`;
  }

  const userRows: TargetRow[] = userShares.map((share) => ({
    key: share.subjectId,
    primary: displayNameOf(share.subjectId),
    secondary: null,
  }));

  // 台帳のグループ ID を表示名（＋パス）へ。引けなかった ID は「（見つからないグループ）」。
  // **Keycloak 側で消されたグループがここに現れる**（台帳の行は残る）。
  const groupRows: TargetRow[] = groupShares.map((share) => {
    const found = (resolvedGroups.data ?? []).find((g) => g.id === share.subjectId);
    return {
      key: share.subjectId,
      primary: found?.displayName ?? t`（見つからないグループ）`,
      secondary: found?.path ?? null,
    };
  });

  const targetCount = userRows.length + groupRows.length;

  function addPicked() {
    if (picked === null) return;
    beginOperation();
    grant.mutate(
      { id: note.id, data: { subjectType, subjectId: picked } },
      {
        onSuccess: () => {
          setPicked(null);
          setTerm('');
          setNotice(t`指定先を追加しました。`);
        },
      },
    );
  }

  function revokeShare(type: string, subjectId: string) {
    beginOperation();
    revoke.mutate(
      { id: note.id, subjectType: type, subjectId },
      { onSuccess: () => setNotice(t`指定先を取り消しました。`) },
    );
  }

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
  const noteTitle = note.title;

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

        <section aria-label={t`現在の指定先`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">
            <Trans>現在の指定先</Trans>
          </h3>
          <QueryState
            query={shares}
            isEmpty={() => targetCount === 0}
            loadingLabel={t`共有先を読み込み中…`}
            errorTitle={t`共有先を取得できませんでした。`}
            empty={<EmptyState title={t`共有している相手はいません。`} />}
          >
            {() => (
              <div className="flex flex-col gap-2">
                <TargetList
                  heading={t`個人`}
                  rows={userRows}
                  type={USER_SUBJECT}
                  pending={pending}
                  onRevoke={revokeShare}
                />
                <TargetList
                  heading={t`グループ`}
                  rows={groupRows}
                  type={GROUP_SUBJECT}
                  pending={pending}
                  onRevoke={revokeShare}
                />
              </div>
            )}
          </QueryState>
        </section>

        <section aria-label={t`指定先を追加`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">
            <Trans>指定先を追加</Trans>
          </h3>

          {/*
            種別の切替。**色だけで選択を示さない** —— ラジオそのものが選択状態を持ち、
            文言（「個人」「グループ」）が意味を担う。`@platform/ui` にセグメント部品は無く、
            ネイティブのラジオは矢印キーでの移動と `aria-checked` を素で満たす
            （`GraphViewPage` の辺の型の絞りと同じ形である）。
          */}
          <fieldset role="radiogroup" className="flex items-center gap-3 border-0 p-0">
            <legend className="float-left mr-2 text-xs text-fg-muted">
              <Trans>指定先の種別</Trans>
            </legend>
            <label className="inline-flex items-center gap-1 text-sm">
              <input
                type="radio"
                name="share-subject-type"
                checked={addingUsers}
                onChange={() => switchSubjectType(USER_SUBJECT)}
              />
              <Trans>個人</Trans>
            </label>
            <label className="inline-flex items-center gap-1 text-sm">
              <input
                type="radio"
                name="share-subject-type"
                checked={!addingUsers}
                onChange={() => switchSubjectType(GROUP_SUBJECT)}
              />
              <Trans>グループ</Trans>
            </label>
          </fieldset>

          <div ref={searchFieldRef} className="flex flex-col gap-1">
            <Label htmlFor="share-target-search">
              {addingUsers ? t`名前で検索する` : t`グループ名で検索する`}
            </Label>
            <Input
              id="share-target-search"
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

          {lookup.enabled && addingUsers && (
            <QueryState
              query={userLookup.query}
              isEmpty={() => userCandidates.length === 0}
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
                  {userCandidates.map((candidate) => {
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

          {lookup.enabled && !addingUsers && (
            <QueryState
              query={groupLookup.query}
              isEmpty={() => groupCandidates.length === 0}
              loadingLabel={t`グループを検索中…`}
              errorTitle={t`グループを検索できませんでした。`}
              empty={<EmptyState title={t`該当するグループがありません。`} />}
            >
              {() => (
                <ul
                  role="listbox"
                  aria-label={t`グループの候補`}
                  className="flex max-h-40 flex-col gap-1 overflow-y-auto rounded-md border border-border p-1"
                >
                  {groupCandidates.map((candidate) => {
                    const selected = candidate.id === picked;
                    return (
                      <li
                        key={candidate.id}
                        role="option"
                        aria-selected={selected}
                        tabIndex={0}
                        className={
                          selected
                            ? 'cursor-pointer rounded-sm bg-surface-muted px-2 py-1 text-sm text-fg'
                            : 'cursor-pointer rounded-sm px-2 py-1 text-sm text-fg'
                        }
                        onClick={() => setPicked(candidate.id)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            setPicked(candidate.id);
                          }
                        }}
                      >
                        {/* 🔴 候補も表示名とパスだけを出す（グループ ID は出さない）。 */}
                        <span className={selected ? 'font-semibold' : undefined}>
                          {candidate.displayName}
                        </span>{' '}
                        <span className="text-xs text-fg-muted">{candidate.path}</span>
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
