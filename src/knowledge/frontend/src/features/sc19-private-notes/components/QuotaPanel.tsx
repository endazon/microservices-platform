import { Trans, useLingui } from '@lingui/react/macro';
import { Alert } from '@platform/ui';
import type { PrivateNoteUsageDto } from '@foundation/api/generated/bff.schemas';
import { quotaLevel, toGb } from '../types/quota';

// SC-19, FR-19, ADR-0037 決定 16〜20: 保存容量の表示（05_screens §SC-19「保存容量と版履歴」）。
//
// ■ 常時出すもの: 使用量と上限の**両方**、および内訳「うち削除済み」。
//   内訳が無いと、利用者は完全削除で空く容量を見積もれない。
// ■ 段階警告: 80%（予告）/ 95%（警告色）/ 100%（新規作成の拒否と固定文言）。
//   🔴 **同時に 2 段を出さない**（強い警告が弱い警告に埋もれる）。
// ■ 版履歴が容量に算入されないことを明示する。
//   明示しないと、利用者は編集のたびに容量が減ると誤解し、編集を控える（計画の明記）。

export interface QuotaPanelProps {
  usage: PrivateNoteUsageDto | undefined;
  /** 削除済み行の bytes の合算（画面が数える。契約は内訳を持たない）。 */
  deletedBytes: number;
}

export function QuotaPanel({ usage, deletedBytes }: QuotaPanelProps) {
  const { t } = useLingui();
  if (!usage) return null;

  const level = quotaLevel(usage.percent);
  const used = toGb(usage.usedBytes);
  const limit = toGb(usage.limitBytes);
  const deleted = toGb(deletedBytes);

  return (
    <section aria-label={t`保存容量`} className="flex flex-col gap-2">
      <p className="text-sm text-[--color-fg]">
        {/* 使用量・上限・内訳を 1 文に収める（例: 0.80 / 1.00 GB（うち削除済み 0.20 GB））。 */}
        <Trans>
          保存容量: {used} / {limit} GB（うち削除済み {deleted} GB）
        </Trans>
      </p>
      <p className="text-xs text-[--color-fg-muted]">
        <Trans>過去の版（版履歴）は保存容量に含まれません。</Trans>
      </p>

      {level === 'notice' && (
        <Alert tone="info" label={t`お知らせ`} role="status">
          <Trans>
            保存容量の使用量が 80% を超えました。不要な資料を整理しておくことをおすすめします。
          </Trans>
        </Alert>
      )}
      {level === 'warning' && (
        <Alert tone="warning" label={t`警告`} role="status">
          <Trans>
            保存容量の使用量が 95% を超えました。上限に達すると新しい資料を作成できなくなります。
          </Trans>
        </Alert>
      )}
      {level === 'full' && (
        <Alert tone="danger" label={t`保存容量の上限`} role="alert">
          {/* 05_screens §SC-19 の固定文言（2 段落）。**折りたたまない。** */}
          <span className="flex flex-col gap-2">
            <span>
              <Trans>
                保存容量の上限に達しました。新しい資料は作成できませんが、編集中の資料は保存できます。
                容量を空けるには、削除済み一覧から「完全に削除（即時）」を実行してください（通常の削除では容量は空きません）。
                管理者へ上限の引き上げを依頼することもできます。
              </Trans>
            </span>
            <span>
              <Trans>
                過去の版（版履歴）は容量に含まれませんが、削除済みの資料は 90
                日間は容量に含まれます。
              </Trans>
            </span>
          </span>
        </Alert>
      )}
    </section>
  );
}
