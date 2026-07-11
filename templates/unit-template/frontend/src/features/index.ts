// テンプレート: ユニットの feature 束ね。platform の合成点
// （src/platform/frontend/src/features/index.ts）から `import { features as <unit>Features } from '@<unit>/features'`
// で 1 行合成される（IADR-0056 / IADR-0060）。
import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { sampleFeature } from './sample';

export const features: FeatureModule[] = [sampleFeature];
