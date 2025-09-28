declare function require(path: string): any;
declare const window: any;

type TerminalConstructor = new (options?: Record<string, unknown>) => XtermTerminal;

type TerminalEvent = (callback: () => void) => { dispose(): void };

interface XtermBufferNamespace {
  readonly active: XtermBuffer;
}

interface XtermBuffer {
  readonly cursorX: number;
  readonly cursorY: number;
  readonly viewportY: number;
  readonly length: number;
  getLine(y: number): XtermBufferLine | undefined;
  getNullCell(): XtermCell;
}

interface XtermBufferLine {
  getCell(x: number, cell?: XtermCell): XtermCell | undefined;
  translateToString(trimRight?: boolean, startColumn?: number, endColumn?: number): string;
}

interface XtermCell {
  getWidth(): number;
  getChars(): string;
}

interface XtermTerminal {
  readonly cols: number;
  readonly rows: number;
  readonly buffer: XtermBufferNamespace;
  readonly options: Record<string, unknown>;
  resize(cols: number, rows: number): void;
  reset(): void;
  write(data: string | Uint8Array, callback?: () => void): void;
  writeln(data: string | Uint8Array, callback?: () => void): void;
  onResize: (callback: (size: { cols: number; rows: number }) => void) => Disposable;
  onScroll: (callback: (position: number) => void) => Disposable;
}

const moduleExports = require('https://cdn.jsdelivr.net/npm/@xterm/headless@5.5.0/lib-headless/xterm-headless.js');
const TerminalCtor: TerminalConstructor | undefined = moduleExports?.Terminal;
if (typeof TerminalCtor !== 'function') {
  throw new Error('Failed to load @xterm/headless Terminal constructor.');
}

const surface = document.getElementById('xtermSurface');
const status = document.getElementById('xtermStatus');
const inputBox = document.getElementById('xtermInput');
const sendButton = document.getElementById('xtermSend');
const clearButton = document.getElementById('xtermClear');
const demoButton = document.getElementById('xtermDemo');

if (!surface || typeof (surface as any).getContext !== 'function') {
  throw new Error('xtermSurface element missing or canvas API unavailable.');
}

const ctx = (surface as any).getContext('2d');
if (!ctx) {
  throw new Error('Unable to obtain CanvasRenderingContext2D for xtermSurface.');
}

const theme = {
  background: '#f8fafc',
  foreground: '#0f172a',
  accent: '#2563eb'
};

const terminal: XtermTerminal = new TerminalCtor({
  cols: 90,
  rows: 28,
  scrollback: 4000,
  allowProposedApi: true,
});

let fontSize = 16;
const fontFamily = 'JetBrains Mono, SFMono-Regular, Consolas, monospace';
const verticalPadding = 12;
const horizontalPadding = 14;
let cellWidth = 9;
let cellHeight = 18;
const baselineOffset = 2;

terminal.onResize(() => scheduleRender());
terminal.onScroll(() => scheduleRender());

function updateStatus(message: string) {
  if (status) {
    status.textContent = message;
  }
}

function measureCells() {
  ctx.font = `${fontSize}px ${fontFamily}`;
  ctx.textBaseline = 'top';
  const metrics = ctx.measureText('M');
  const measuredWidth = Number.isFinite(metrics.width) ? metrics.width : fontSize * 0.6;
  cellWidth = Math.max(6, Math.ceil(measuredWidth + 1));
  cellHeight = Math.max(fontSize + 4, Math.ceil(fontSize * 1.4));
}

function ensureTerminalSize(): { width: number; height: number } {
  measureCells();
  const width = (surface as any).offsetWidth ?? 0;
  const height = (surface as any).offsetHeight ?? 0;
  const contentWidth = Math.max(0, width - horizontalPadding * 2);
  const contentHeight = Math.max(0, height - verticalPadding * 2);
  const cols = Math.max(40, Math.floor(contentWidth / cellWidth));
  const rows = Math.max(12, Math.floor(contentHeight / cellHeight));
  if (cols !== terminal.cols || rows !== terminal.rows) {
    terminal.resize(cols, rows);
  }
  return { width, height };
}

let renderPending = false;
function scheduleRender() {
  if (renderPending) {
    return;
  }
  renderPending = true;
  const raf = typeof window !== 'undefined' && typeof window.requestAnimationFrame === 'function'
    ? window.requestAnimationFrame
    : (cb: () => void) => setTimeout(cb, 0);
  raf(() => {
    renderPending = false;
    renderBuffer();
  });
}

function ensureElements(): boolean {
  if (!surface || !ctx) {
    updateStatus('Canvas surface unavailable.');
    return false;
  }
  if (!inputBox || !sendButton || !demoButton || !clearButton) {
    updateStatus('Terminal controls missing from XAML preset.');
    return false;
  }
  return true;
}

function renderBuffer() {
  const { width, height } = ensureTerminalSize();
  ctx.save();
  ctx.fillStyle = theme.background;
  ctx.fillRect(0, 0, width, height);
  ctx.font = `${fontSize}px ${fontFamily}`;
  ctx.textBaseline = 'top';

  const buffer = terminal.buffer.active;
  const viewportTop = buffer.viewportY;
  const scratch = buffer.getNullCell();
  const textTop = verticalPadding;
  const textLeft = horizontalPadding;

  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.getLine(viewportTop + row);
    let x = textLeft;
    if (line) {
      for (let col = 0; col < terminal.cols; col++) {
        const cell = line.getCell(col, scratch);
        const widthUnits = cell?.getWidth?.() ?? 1;
        const charWidth = Math.max(1, widthUnits);
        const cellX = x;
        const cellY = textTop + row * cellHeight;
        const text = cell?.getChars?.() || ' ';
        if (text.trim().length > 0) {
          ctx.fillStyle = theme.foreground;
          ctx.fillText(text, cellX, cellY + baselineOffset);
        }
        x += cellWidth * charWidth;
        col += charWidth - 1;
      }
    }
  }

  const cursorX = buffer.cursorX;
  const cursorY = buffer.cursorY;
  const cursorLeft = textLeft + cursorX * cellWidth;
  const cursorTop = textTop + cursorY * cellHeight;
  ctx.strokeStyle = theme.accent;
  ctx.lineWidth = 2;
  ctx.strokeRect(cursorLeft + 0.5, cursorTop + 0.5, cellWidth - 1, cellHeight - 1);

  ctx.restore();
}

function runWriter(data: string) {
  terminal.writeln(data, () => scheduleRender());
  scheduleRender();
}

type CommandHandler = (args: string[]) => void;

const commands: Record<string, CommandHandler> = Object.create(null);

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
  runWriter('  theme      Cycle sample colors via ANSI escapes');
};

commands.uptime = () => {
  runWriter('system  up  12 days,  4:37,  load average: 0.42, 0.35, 0.18');
  runWriter('process cpu: 3.2%  mem: 214 MB  handles: 423');
  runWriter('network rx: 18.2 MB  tx: 9.7 MB');
};

let demoTimer: number | null = null;

const startInterval = (handler: () => void, delay: number) => {
  if (typeof window !== 'undefined' && typeof window.setInterval === 'function') {
    return window.setInterval(handler, delay);
  }

  return setInterval(handler, delay) as unknown as number;
};

const stopInterval = (id: number) => {
  if (typeof window !== 'undefined' && typeof window.clearInterval === 'function') {
    window.clearInterval(id);
  } else {
    clearInterval(id);
  }
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

commands.theme = () => {
  runWriter('ANSI palette sample:');
  const codes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
  const swatches = codes.map(code => `\x1b[${30 + (code % 8) + (code > 7 ? 60 : 0)}m██\x1b[0m`).join(' ');
  runWriter(swatches);
};

function stopDemoStream() {
  if (demoTimer != null) {
    stopInterval(demoTimer);
    demoTimer = null;
  }
}

function handleCommand(raw: string) {
  const trimmed = raw.trim();
  if (!trimmed) {
    return;
  }
  if (trimmed === 'clear') {
    commands.clear([]);
    return;
  }
  const [command, ...args] = trimmed.split(/\s+/g);
  terminal.writeln('');
  terminal.writeln(`$ ${trimmed}`);
  const handler = commands[command];
  if (handler) {
    handler(args);
  } else {
    runWriter(`Command not found: ${command}`);
  }
  scheduleRender();
}

function printBanner() {
  runWriter('\x1b[96m❯ xterm.js headless (TypeScript) ❮\x1b[0m');
  runWriter('Type "help" for available commands or use the preset buttons.');
}

function bindInput() {
  const submit = () => {
    const value = (inputBox as any)?.value ?? '';
    handleCommand(String(value));
    if (inputBox) {
      (inputBox as any).value = '';
    }
    updateStatus('Command executed.');
  };

  sendButton?.addEventListener('click', submit);
  (inputBox as any)?.addEventListener?.('keydown', (event: any) => {
    if (event?.key === 'Enter') {
      event.preventDefault?.();
      submit();
    }
  });

  clearButton?.addEventListener('click', () => {
    stopDemoStream();
    commands.clear([]);
  });

  demoButton?.addEventListener('click', () => {
    stopDemoStream();
    commands.demo([]);
    updateStatus('Streaming demo workload...');
  });
}

function bootstrap() {
  if (!ensureElements()) {
    return;
  }
  bindInput();
  printBanner();
  updateStatus('xterm.js headless ready — try the demo or type help.');
  scheduleRender();
  if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => scheduleRender());
  }
}

bootstrap();
