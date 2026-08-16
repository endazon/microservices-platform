import { useEffect, useRef, useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Bell } from 'lucide-react';
import { Alert, Button, StatusBadge } from '@platform/ui';
import { formatDateTime } from '@foundation/ui/formatDateTime';
import { notificationText, notificationTone, notificationToneLabel } from './notificationMessages';
import { useMarkNotificationRead, useNotificationList } from './useNotifications';

// FR-22, UC-11, IADR-0215 決定 2: **アプリ内通知の受け皿**（共通シェル横断）。
//
// ★ 置き場は `platform/frontend`（foundation）である。理由は 3 つ（IADR-0215 決定 2）:
//   1. 通知の受け皿は**アプリシェル横断**の要素であり（05_screens §共通シェル）、画面 1 枚に属さない。
//   2. 契約が「件数と期限のみ」で**ドメイン語彙を持たない**（knowledge のドメインに依存しない）。
//   3. **platform → 可変ユニットの参照は禁止**（IADR-0056）。knowledge 側へ置くと共通シェルが
//      可変ユニットを参照することになり、この禁止に触れる。
//
// ★ 同ディレクトリ外の `foundation/ui/notifications.tsx` とは**別物**である。
//   あちらは**一過性のトースト**（操作の結果を知らせて消える。sonner）、
//   こちらは**永続するアプリ内通知**（既読になるまで残り、保持期間は 90 日）。
//
// 表示規約: **色だけで意味を持たせない**（INDEX 決定 21）。tone ごとに `StatusBadge` が
// 色 ＋ アイコン ＋ テキストの 3 点セットを強制する。アイコンは lucide-react（外部 CDN・
// Web フォント・analytics は使わない。08_data-egress-policy）。

export function NotificationBell() {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const list = useNotificationList();
  const markRead = useMarkNotificationRead();

  const items = list.data?.items ?? [];
  const unreadCount = list.data?.unreadCount ?? 0;

  // 開いているあいだだけ、Escape と外側クリックで閉じる（WAI-ARIA の disclosure の作法）。
  // **閉じ手段がベルの再クリックだけだと、画面の他の場所へ移った利用者がパネルを残したままになる。**
  // Escape のときは**ベルへフォーカスを戻す** —— 戻さないとキーボード利用者の位置が失われる。
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      setOpen(false);
      buttonRef.current?.focus();
    };
    const onPointerDown = (e: PointerEvent) => {
      // ベル自身のクリックは button の onClick が処理する（ここで閉じると二重で反転する）。
      if (rootRef.current?.contains(e.target as Node)) return;
      setOpen(false);
    };
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('pointerdown', onPointerDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('pointerdown', onPointerDown);
    };
  }, [open]);

  return (
    <div className="relative" ref={rootRef}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        // 未読件数を**ラベルにも入れる**——バッジの数字だけだと、支援技術には「6」としか読まれない。
        aria-label={i18n._(msg`通知（未読 ${unreadCount} 件）`)}
        className="flex items-center gap-1.5 rounded px-2 py-1 text-sm text-[--color-fg-muted] hover:underline"
      >
        <Bell className="size-5" aria-hidden />
        {/* 未読が 0 のときは数字を出さない（0 を出すと「未読がある」と誤読される）。 */}
        {unreadCount > 0 ? (
          <span className="rounded-full bg-[--color-danger] px-1.5 text-xs font-medium text-[--color-surface]">
            {unreadCount}
          </span>
        ) : null}
      </button>

      {open ? (
        <div
          // 見出しで領域に名前を付ける（ナビと同じ作法）。
          aria-label={i18n._(msg`通知一覧`)}
          role="region"
          className="absolute right-0 z-10 mt-1 w-96 rounded-[--radius-control] border border-[--color-border] bg-[--color-surface] p-3 shadow"
        >
          {list.isError ? (
            <Alert tone="danger" label={i18n._(msg`エラー`)} role="alert">
              {i18n._(msg`通知を取得できませんでした`)}
            </Alert>
          ) : items.length === 0 ? (
            // **空であることを明示する**（無言で何も描かない形にしない）。
            <p className="text-sm text-[--color-fg-muted]">{i18n._(msg`通知はありません`)}</p>
          ) : (
            <ul>
              {items.map((n) => {
                const tone = notificationTone(n);
                return (
                  <li key={n.id} className="border-b border-[--color-border] py-2 last:border-b-0">
                    <div className="flex items-start gap-2">
                      <StatusBadge tone={tone}>{notificationToneLabel(tone)}</StatusBadge>
                      <div className="grow">
                        {/* ★ 文言は種別・件数・閾値・期限だけから組み立てる。
                            資料のタイトル・本文・検索語・回答内容は契約に存在しない。 */}
                        <p className="text-sm text-[--color-fg]">{notificationText(n)}</p>
                        <p className="text-xs text-[--color-fg-muted]">
                          {formatDateTime(n.occurredAt)}
                        </p>
                      </div>
                      {n.read ? (
                        <span className="text-xs text-[--color-fg-muted]">{i18n._(msg`既読`)}</span>
                      ) : (
                        <Button
                          size="sm"
                          onClick={() => markRead.mutate({ id: n.id })}
                          disabled={markRead.isPending}
                        >
                          {i18n._(msg`既読にする`)}
                        </Button>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
          {markRead.isError ? (
            <Alert tone="danger" label={i18n._(msg`エラー`)} role="alert" className="mt-2">
              {i18n._(msg`既読にできませんでした`)}
            </Alert>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
