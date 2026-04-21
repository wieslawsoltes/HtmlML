const moduleExports = require('https://cdn.jsdelivr.net/npm/@xterm/headless@5.5.0/lib-headless/xterm-headless.js');
const TerminalCtor = moduleExports && moduleExports.Terminal;

if (typeof TerminalCtor !== 'function') {
  throw new Error('Failed to load @xterm/headless terminal constructor.');
}

const surface = document.getElementById('xtermJsSurface');
const status = document.getElementById('xtermJsStatus');
const input = document.getElementById('xtermJsInput');
const sendButton = document.getElementById('xtermJsSend');
const demoButton = document.getElementById('xtermJsDemo');
const clearButton = document.getElementById('xtermJsClear');

if (!surface || typeof surface.getContext !== 'function') {
  throw new Error('xtermJsSurface element missing or does not expose getContext.');
}

const ctx = surface.getContext('2d');
if (!ctx) {
  throw new Error('Unable to obtain 2D context for xtermJsSurface.');
}

const theme = {
  background: '#f8fafc',
  foreground: '#0f172a',
  accent: '#2563eb'
};

const terminal = new TerminalCtor({
  cols: 90,
  rows: 28,
  scrollback: 4000,
  allowProposedApi: true
});

let fontSize = 16;
const fontFamily = 'JetBrains Mono, SFMono-Regular, Consolas, monospace';
const verticalPadding = 12;
const horizontalPadding = 14;
let cellWidth = 9;
let cellHeight = 18;
const baselineOffset = 2;
let renderPending = false;
let demoTimer = null;
let draftInput = '';
const promptLabel = '$ ';
const hostShell = (typeof window !== 'undefined' && window && window.hostShell)
  || (typeof globalThis !== 'undefined' && globalThis && globalThis.hostShell)
  || null;
const canRunHostShell = hostShell && typeof hostShell.execute === 'function';

const scheduleRender = () => {
  if (renderPending) {
    return;
  }
  renderPending = true;
  const raf = typeof window !== 'undefined' && typeof window.requestAnimationFrame === 'function'
    ? window.requestAnimationFrame.bind(window)
    : (cb) => setTimeout(cb, 0);
  raf(() => {
    renderPending = false;
    renderBuffer();
  });
};

const updateStatus = (message) => {
  if (status) {
    status.textContent = message;
  }
};

const ensureElements = () => {
  if (!surface || !ctx) {
    updateStatus('Canvas surface unavailable.');
    return false;
  }
  if (!input || !sendButton || !demoButton || !clearButton) {
    updateStatus('Terminal controls missing from preset XAML.');
    return false;
  }
  return true;
};

const measureCells = () => {
  ctx.font = `${fontSize}px ${fontFamily}`;
  ctx.textBaseline = 'top';
  const metrics = ctx.measureText('M');
  const measuredWidth = Number.isFinite(metrics.width) ? metrics.width : fontSize * 0.6;
  cellWidth = Math.max(6, Math.ceil(measuredWidth + 1));
  cellHeight = Math.max(fontSize + 4, Math.ceil(fontSize * 1.4));
};

const ensureTerminalSize = () => {
  measureCells();
  const width = surface.offsetWidth || surface.clientWidth || surface.desiredWidth || 720;
  const height = surface.offsetHeight || surface.clientHeight || surface.desiredHeight || 360;
  const contentWidth = Math.max(0, width - horizontalPadding * 2);
  const contentHeight = Math.max(0, height - verticalPadding * 2);
  const cols = Math.max(40, Math.floor(contentWidth / cellWidth));
  const rows = Math.max(12, Math.floor(contentHeight / cellHeight));
  if (cols !== terminal.cols || rows !== terminal.rows) {
    terminal.resize(cols, rows);
  }
  return { width, height };
};

const renderBuffer = () => {
  const { width, height } = ensureTerminalSize();
  ctx.save();
  ctx.fillStyle = theme.background;
  ctx.fillRect(0, 0, width, height);
  ctx.font = `${fontSize}px ${fontFamily}`;
  ctx.textBaseline = 'top';
  ctx.fillStyle = theme.foreground;

  const buffer = terminal.buffer.active;
  const viewportTop = buffer.viewportY;
  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.getLine(viewportTop + row);
    const text = line ? line.translateToString(false) : '';
    const y = verticalPadding + row * cellHeight + baselineOffset;
    ctx.fillText(text || '', horizontalPadding, y);
  }

  const cursorX = buffer.cursorX;
  const cursorY = buffer.cursorY;
  const cursorLeft = horizontalPadding + cursorX * cellWidth;
  const cursorTop = verticalPadding + cursorY * cellHeight;
  ctx.strokeStyle = theme.accent;
  ctx.lineWidth = 2;
  ctx.strokeRect(Math.floor(cursorLeft) + 0.5, Math.floor(cursorTop) + 0.5, cellWidth - 1, cellHeight - 1);

  ctx.restore();
};

const startInterval = (handler, delay) => {
  if (typeof window !== 'undefined' && typeof window.setInterval === 'function') {
    return window.setInterval(handler, delay);
  }
  return setInterval(handler, delay);
};

const stopInterval = (id) => {
  if (typeof window !== 'undefined' && typeof window.clearInterval === 'function') {
    window.clearInterval(id);
  } else {
    clearInterval(id);
  }
};

const stopDemoStream = () => {
  if (demoTimer != null) {
    stopInterval(demoTimer);
    demoTimer = null;
  }
};

const runWriter = (data) => {
  terminal.writeln(data, () => scheduleRender());
  scheduleRender();
};

const writeCommandOutput = (value) => {
  const text = typeof value === 'string' ? value : String(value ?? '');
  if (!text) {
    return false;
  }

  const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const body = normalized.endsWith('\n') ? normalized : `${normalized}\n`;
  terminal.write(body.replace(/\n/g, '\r\n'), () => scheduleRender());
  scheduleRender();
  return true;
};

const syncInputBox = () => {
  if (input) {
    input.value = draftInput;
  }
};

const renderPrompt = () => {
  terminal.write(`\r\x1b[2K${promptLabel}${draftInput}`);
  scheduleRender();
};

const clearDraftInput = (render = true) => {
  draftInput = '';
  syncInputBox();
  if (render) {
    renderPrompt();
  }
};

const setDraftInput = (value, render = true) => {
  draftInput = typeof value === 'string' ? value : String(value ?? '');
  syncInputBox();
  if (render) {
    renderPrompt();
  }
};

const commands = Object.create(null);

commands.clear = () => {
  stopDemoStream();
  terminal.reset();
  printBanner();
};

commands.help = () => {
  runWriter('Available commands:');
  runWriter('  help       Show this help message');
  runWriter('  demo       Stream sample task output');
  runWriter('  uptime     Display a fake uptime report');
  runWriter('  clear      Reset and show the banner again');
  runWriter('  theme      Display ANSI swatches');
  if (canRunHostShell) {
    runWriter(`  <other>    Run via host shell (${hostShell.shell || 'shell'})`);
    runWriter(`  cwd        ${hostShell.cwd || ''}`);
    runWriter('  note       No PTY: full-screen TUIs like mc or vim are not supported.');
  }
};

commands.uptime = () => {
  runWriter('system  up  15 days,  3:42,  load average: 0.42, 0.35, 0.18');
  runWriter('process cpu: 2.9%  mem: 198 MB  handles: 407');
  runWriter('network rx: 21.2 MB  tx: 10.7 MB');
};

commands.theme = () => {
  runWriter('ANSI palette sample:');
  const codes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
  const swatches = codes.map((code) => `\x1b[${30 + (code % 8) + (code > 7 ? 60 : 0)}m██\x1b[0m`).join(' ');
  runWriter(swatches);
};

commands.demo = () => {
  stopDemoStream();
  const frames = ['⠋', '⠙', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
  let index = 0;
  let line = 0;
  runWriter('Launching orchestrated workload...');
  demoTimer = startInterval(() => {
    const frame = frames[index % frames.length];
    terminal.write(`\r${frame} task-${line + 1}  status: running`);
    index++;
    if (index % frames.length === 0) {
      terminal.writeln('\r✔ task completed successfully');
      line++;
    }
    if (line >= 4) {
      if (demoTimer != null) {
        stopInterval(demoTimer);
        demoTimer = null;
      }
      terminal.writeln('\x1b[32mAll tasks finished.\x1b[0m');
      renderPrompt();
      updateStatus('Demo workload finished.');
    }
    scheduleRender();
  }, 200);
};

const executeCommand = (raw) => {
  const trimmed = (raw || '').trim();
  if (!trimmed) {
    clearDraftInput();
    return 'Enter a command.';
  }

  stopDemoStream();
  clearDraftInput(false);

  if (trimmed === 'clear') {
    commands.clear();
    renderPrompt();
    return 'Terminal cleared and banner restored.';
  }

  const [command, ...args] = trimmed.split(/\s+/g);
  terminal.write('\r\x1b[2K');
  terminal.writeln(`${promptLabel}${trimmed}`);
  const handler = command && commands[command];
  if (handler) {
    handler(args);
    renderPrompt();
    return `Ran ${command}.`;
  }

  if (canRunHostShell) {
    try {
      const result = hostShell.execute(trimmed);
      const exitCode = Number(result && result.exitCode);
      const timedOut = Boolean(result && result.timedOut);
      const wroteOutput = writeCommandOutput(result && typeof result.output === 'string' ? result.output : '');

      if (!wroteOutput && timedOut) {
        runWriter(`Command timed out after ${result.timeoutMs || 0} ms.`);
      } else if (!wroteOutput && Number.isFinite(exitCode) && exitCode !== 0) {
        runWriter(`Command exited with code ${exitCode}.`);
      }

      renderPrompt();

      if (timedOut) {
        return `Shell command timed out after ${result.timeoutMs || 0} ms.`;
      }

      return Number.isFinite(exitCode)
        ? `Shell command exited with code ${exitCode}.`
        : 'Shell command completed.';
    } catch (error) {
      runWriter(`Shell bridge error: ${error}`);
      renderPrompt();
      return `Shell bridge error: ${error}`;
    }
  }

  runWriter(`Command not found: ${command}`);
  renderPrompt();
  return `Unknown command: ${command}`;
};

const bindInput = () => {
  const submit = (rawValue) => {
    const value = typeof rawValue === 'string'
      ? rawValue
      : (input && typeof input.value === 'string' ? input.value : draftInput);
    updateStatus(executeCommand(value));
  };

  sendButton && sendButton.addEventListener && sendButton.addEventListener('click', submit);
  input && input.addEventListener && input.addEventListener('input', () => {
    const value = typeof input.value === 'string' ? input.value : '';
    setDraftInput(value);
  });
  input && input.addEventListener && input.addEventListener('keydown', (event) => {
    if (event && event.key === 'Enter') {
      event.preventDefault && event.preventDefault();
      submit();
    }
  });

  clearButton && clearButton.addEventListener && clearButton.addEventListener('click', () => {
    clearDraftInput(false);
    commands.clear();
    renderPrompt();
    updateStatus('Terminal cleared and banner restored.');
  });

  demoButton && demoButton.addEventListener && demoButton.addEventListener('click', () => {
    clearDraftInput(false);
    commands.demo();
    updateStatus('Streaming demo workload...');
    renderPrompt();
  });

  surface && surface.addEventListener && surface.addEventListener('pointerdown', () => {
    surface.focus && surface.focus();
  });
  surface && surface.addEventListener && surface.addEventListener('click', () => {
    surface.focus && surface.focus();
  });
  surface && surface.addEventListener && surface.addEventListener('textinput', (event) => {
    const data = typeof event?.data === 'string' ? event.data : '';
    if (!data) {
      return;
    }

    draftInput += data;
    syncInputBox();
    renderPrompt();
    event.preventDefault && event.preventDefault();
  });
  surface && surface.addEventListener && surface.addEventListener('keydown', (event) => {
    const key = event?.key;
    if (event?.ctrlKey && (key === 'l' || key === 'L')) {
      clearDraftInput(false);
      commands.clear();
      renderPrompt();
      updateStatus('Terminal cleared and banner restored.');
      event.preventDefault && event.preventDefault();
      return;
    }

    if (key === 'Enter') {
      event.preventDefault && event.preventDefault();
      submit(draftInput);
      return;
    }

    if (key === 'Backspace' || key === 'Back') {
      if (draftInput.length > 0) {
        draftInput = draftInput.slice(0, -1);
        syncInputBox();
        renderPrompt();
      }
      event.preventDefault && event.preventDefault();
      return;
    }

    if (key === 'Escape') {
      clearDraftInput();
      updateStatus('Input cleared.');
      event.preventDefault && event.preventDefault();
    }
  });
};

const printBanner = () => {
  runWriter('\x1b[96m❯ xterm.js headless (JavaScript) ❮\x1b[0m');
  runWriter('Type "help" for available commands or use the preset buttons.');
};

const bootstrap = () => {
  if (!ensureElements()) {
    return;
  }
  bindInput();
  printBanner();
  renderPrompt();
  updateStatus('xterm.js headless JavaScript demo ready.');
  scheduleRender();
  if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => scheduleRender());
  }
  terminal.onResize(() => scheduleRender());
  terminal.onScroll(() => scheduleRender());
};

bootstrap();
