'use strict';

/* ------------------------------------------------------------------ *
 * 外观：主题（跟随系统 / 浅色 / 深色）与配色（四套）
 *
 * 这个文件在 <head> 里同步加载，先于页面渲染把 <html data-theme data-palette>
 * 定下来，深色下不会先闪一下白底。app.js 只用这里的东西，不再自己推断。
 * ------------------------------------------------------------------ */

const PALETTES = [
  /* dot 是这套配色的标记色，深浅两套主题上都看得清。 */
  { id: 'paper', name: '纸白', dot: '#9aa0aa' },
  { id: 'sand', name: '暖砂', dot: '#c08a55' },
  { id: 'mist', name: '雾蓝', dot: '#5a99dd' },
  { id: 'pine', name: '松绿', dot: '#4fa879' },
];

const THEMES = [
  { id: 'system', name: '跟随系统' },
  { id: 'light', name: '浅色' },
  { id: 'dark', name: '深色' },
];

const systemDark = window.matchMedia('(prefers-color-scheme: dark)');

const appearance = { theme: 'system', palette: 'paper' };

(function loadAppearance() {
  try {
    const theme = localStorage.getItem('sevenstation.theme');
    const palette = localStorage.getItem('sevenstation.palette');
    if (THEMES.some((entry) => entry.id === theme)) appearance.theme = theme;
    if (PALETTES.some((entry) => entry.id === palette)) appearance.palette = palette;
  } catch {
    /* 存不了就用默认值：跟随系统加纸白。 */
  }
})();

function resolvedTheme() {
  if (appearance.theme === 'light' || appearance.theme === 'dark') return appearance.theme;
  return systemDark.matches ? 'dark' : 'light';
}

function applyAppearance() {
  const root = document.documentElement;
  root.dataset.theme = resolvedTheme();
  root.dataset.palette = appearance.palette;
}

function setAppearance(patch) {
  if (patch.theme) appearance.theme = patch.theme;
  if (patch.palette) appearance.palette = patch.palette;
  try {
    localStorage.setItem('sevenstation.theme', appearance.theme);
    localStorage.setItem('sevenstation.palette', appearance.palette);
  } catch {
    /* 存不了就只在这次会话里生效。 */
  }
  applyAppearance();
}

applyAppearance();

/* 跟随系统那档要跟着系统开关走。 */
systemDark.addEventListener('change', () => {
  if (appearance.theme === 'system') applyAppearance();
});
