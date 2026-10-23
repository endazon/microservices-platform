'use strict';
/**
 * NFR-20, ADR-0007, IADR-0605 (#2132): chart を**描くだけ**の試験・検査器が渡すイメージのタグ。
 *
 * chart は自製イメージのタグ（global.image.tag）が無いと描画を落とす（latest へ黙って倒さない。templates/_image.tpl）。
 * 描画の試験はイメージの中身を見ないので、固定の値を 1 つ渡す。**配備には使わない**（この値のイメージはどこにも無い）。
 * 配備の経路（scripts/k8s-local-up.sh）は作業ツリーの git の sha を渡す（scripts/lib/local-image-tag.sh）。
 *
 * 引数の位置は chart の直後に置く —— 後ろの `--set-string` が勝つので、各試験は自分の値で上書きできる。
 */
const RENDER_IMAGE_TAG = 'render-only-0000000';
const RENDER_IMAGE_TAG_ARGS = Object.freeze(['--set-string', `global.image.tag=${RENDER_IMAGE_TAG}`]);

module.exports = { RENDER_IMAGE_TAG, RENDER_IMAGE_TAG_ARGS };
