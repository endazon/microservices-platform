import type { FeatureModule } from '@foundation/routing/featureRegistry';
// FR-14, IADR-0056: ユニット合成点 — 可変機能ユニットの features をここで束ねる。
// ユニット追加時は src/<unit>/（frontend/ を含む）を submodule 配置し、ここへ import を 1 行追加する。
import { features as knowledgeFeatures } from '@knowledge/features';

export const features: FeatureModule[] = [...knowledgeFeatures];
