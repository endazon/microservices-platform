import { Trans, useLingui } from '@lingui/react/macro';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@platform/ui';
import { AttributeDictionaryPanel } from './AttributeDictionaryPanel';
import { PolicyEditorPanel } from './PolicyEditorPanel';
import { useAbacAttributes, useAbacPolicies } from './useAbacAdmin';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）（05_screens: ルート /admin/abac）。
// 区画の切替は @platform/ui の Tabs（#496 で移植済み。hi-fi の seg / seg-opt に対応）。
//
// 実装しない区画（画面仕様書 docs/screens/SC-09_admin-abac-settings.md §hi-fi モックアップとの対応）:
//   - **辺の型辞書**: **着手保留**（IADR-0119）。計画 §SC-09 の当該節は見出し自体が
//     「辺の型（値集合）の管理（起案・2026-08-02。FR-17・ADR-0033 決定3）」であり、画面一覧の
//     SC-09 行も関連要求に FR-17 を挙げる。前提 ADR-0033 の状態は Proposed であり、
//     IADR-0119 決定 2 の着手条件（Accepted）を満たさない。**値集合だけを先に作ることもしない**
//     ——中核 5 種・推奨追加 4 種・逆向きの表示語はいずれも ADR-0033 決定3 の内容そのものである。
//   - **タグ辞書**: **契約の不在**。/bff/admin/authz にあるのは attributes と policies の 2 群だけで、
//     タグ辞書の値集合を返す口・使用件数・改名の追随のいずれも無い。計画が確定した規則
//     （参照 1 件以上は削除拒否・件数 N の提示・改名は既存文書が追随）はそのすべてが契約側の機能である。
//   いずれも**空のタブを置かない**——保留であることを伝えず、むしろ壊れて見える（#502 が確立した
//   「動かない UI を置かない」）。記録は feedback/20260805_sc09-11-admin-ops-contract-gaps.md（起票は親）。

export function AdminAbacSettingsPage() {
  const { t } = useLingui();
  const attributes = useAbacAttributes();
  const policies = useAbacPolicies();

  return (
    <section>
      <h1 className="mb-3 text-lg font-semibold text-[--color-fg]">
        <Trans>管理者設定（ABAC）</Trans>
      </h1>

      {/* 既定は「ポリシー定義」（hi-fi 417 が同区画を選択中に描く）。 */}
      <Tabs defaultValue="policies">
        <TabsList aria-label={t`ABAC 設定の区画`}>
          <TabsTrigger value="attributes">
            <Trans>属性体系</Trans>
          </TabsTrigger>
          <TabsTrigger value="policies">
            <Trans>ポリシー定義</Trans>
          </TabsTrigger>
        </TabsList>

        <TabsContent value="attributes">
          <AttributeDictionaryPanel
            attributes={attributes.data ?? []}
            isPending={attributes.isPending}
            isError={attributes.isError}
            error={attributes.error}
          />
        </TabsContent>

        <TabsContent value="policies">
          <PolicyEditorPanel
            policies={policies.data ?? []}
            attributes={attributes.data ?? []}
            isPending={policies.isPending}
            isError={policies.isError}
            error={policies.error}
          />
        </TabsContent>
      </Tabs>
    </section>
  );
}
