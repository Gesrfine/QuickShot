import { keyFromEvent } from './shared.mjs';

export function recordShortcut(input, reportError) {
  input.readOnly = true;
  input.autocomplete = 'off';
  input.title = '点击后按下快捷键，最多三个键，例如 Ctrl+Alt+C';
  input.addEventListener('keydown', event => {
    if (event.code === 'Tab' && !event.ctrlKey && !event.altKey && !event.metaKey) return;
    event.preventDefault(); event.stopPropagation();
    if (event.repeat) return;
    try {
      const key = keyFromEvent(event);
      if (key) { input.value = key; reportError(''); }
    } catch (error) { reportError(error.message); }
  });
}
