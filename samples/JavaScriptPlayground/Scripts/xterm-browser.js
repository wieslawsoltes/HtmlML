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

const commands = Object.create(null);

commands.clear = () => {
  stopDemoStream();
  terminal.reset();
  printBanner();
  updateStatus('Terminal cleared and banner restored.');
  scheduleRender();
};

commands.help = () => {
  runWriter('Available commands:');
  runWriter('  help       Show this help message');
  runWriter('  demo       Stream sample task output');
  runWriter('  uptime     Display a fake uptime report');
  runWriter('  clear      Reset and show the banner again');
  runWriter('  theme      Display ANSI swatches');
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
    }
    scheduleRender();
  }, 200);
};

const handleCommand = (raw) => {
  const trimmed = (raw || '').trim();
  if (!trimmed) {
    return;
  }
  if (trimmed === 'clear') {
    commands.clear();
    return;
  }

  const [command, ...args] = trimmed.split(/\s+/g);
  terminal.writeln('');
  terminal.writeln(`$ ${trimmed}`);
  const handler = command && commands[command];
  if (handler) {
    handler(args);
  } else {
    runWriter(`Command not found: ${command}`);
  }
  scheduleRender();
};

const bindInput = () => {
  const submit = () => {
    const value = input && typeof input.value === 'string' ? input.value : '';
    handleCommand(value);
    if (input) {
      input.value = '';
    }
    updateStatus('Command executed.');
  };

  sendButton && sendButton.addEventListener && sendButton.addEventListener('click', submit);
  input && input.addEventListener && input.addEventListener('keydown', (event) => {
    if (event && event.key === 'Enter') {
      event.preventDefault && event.preventDefault();
      submit();
    }
  });

  clearButton && clearButton.addEventListener && clearButton.addEventListener('click', () => {
    commands.clear();
  });

  demoButton && demoButton.addEventListener && demoButton.addEventListener('click', () => {
    commands.demo();
    updateStatus('Streaming demo workload...');
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
  updateStatus('xterm.js headless JavaScript demo ready.');
  scheduleRender();
  if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => scheduleRender());
  }
  terminal.onResize(() => scheduleRender());
  terminal.onScroll(() => scheduleRender());
};

bootstrap();
