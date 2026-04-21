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

interface HostShellResult {
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  readonly output: string;
  readonly timedOut: boolean;
  readonly timeoutMs: number;
  readonly shell: string;
  readonly cwd: string;
  readonly success: boolean;
}

interface HostShellBridge {
  readonly shell: string;
  readonly cwd: string;
  readonly supportsTty: boolean;
  execute(command: string): HostShellResult;
  execute(command: string, timeoutMs: number): HostShellResult;
  execute(command: string, timeoutMs: number, columns: number, rows: number): HostShellResult;
  start?(command: string, columns: number, rows: number): HostShellSession | null;
}

interface HostShellSession {
  readonly isRunning: boolean;
  readonly exitCode: number;
  read(): string;
  write(data: string): boolean;
  resize?(columns: number, rows: number): boolean;
  kill?(): void;
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
  cols: 120,
  rows: 36,
  scrollback: 4000,
  allowProposedApi: true,
});

let fontSize = 14;
const fontFamily = 'JetBrains Mono, SFMono-Regular, Consolas, monospace';
const verticalPadding = 12;
const horizontalPadding = 14;
let cellWidth = 9;
let cellHeight = 18;
const baselineOffset = 2;
let draftInput = '';
const promptLabel = '$ ';
const hostShell: HostShellBridge | null = (typeof window !== 'undefined' && window?.hostShell)
  || (typeof globalThis !== 'undefined' ? globalThis?.hostShell ?? null : null);
const canRunHostShell = !!hostShell && hostShell.execute != null;
const canStartHostTerminal = !!hostShell && hostShell.supportsTty === true;
let activeShellSession: HostShellSession | null = null;
let activeShellCommand = '';
let activeShellPollTimer: number | null = null;
let lastSessionCols = terminal.cols;
let lastSessionRows = terminal.rows;

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

function tryResizeTerminal(cols: number, rows: number) {
  if (cols === terminal.cols && rows === terminal.rows) {
    return;
  }

  try {
    terminal.resize(cols, rows);
  } catch (error) {
    // The bundled headless xterm build can expose readonly rows/cols under Jint.
    // Keep the constructor size in that case; it is already large enough for TUIs.
  }
}

function ensureTerminalSize(): { width: number; height: number } {
  measureCells();
  const width = (surface as any).offsetWidth || (surface as any).clientWidth || (surface as any).desiredWidth || 1120;
  const height = (surface as any).offsetHeight || (surface as any).clientHeight || (surface as any).desiredHeight || 640;
  const contentWidth = Math.max(0, width - horizontalPadding * 2);
  const contentHeight = Math.max(0, height - verticalPadding * 2);
  const cols = Math.max(40, Math.floor(contentWidth / cellWidth));
  const rows = Math.max(12, Math.floor(contentHeight / cellHeight));
  tryResizeTerminal(cols, rows);
  if (activeShellSession && (terminal.cols !== lastSessionCols || terminal.rows !== lastSessionRows)) {
    lastSessionCols = terminal.cols;
    lastSessionRows = terminal.rows;
    activeShellSession.resize?.(terminal.cols, terminal.rows);
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

function writeCommandOutput(value: unknown): boolean {
  const text = typeof value === 'string' ? value : String(value ?? '');
  if (!text) {
    return false;
  }

  const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const body = normalized.endsWith('\n') ? normalized : `${normalized}\n`;
  terminal.write(body.replace(/\n/g, '\r\n'), () => scheduleRender());
  scheduleRender();
  return true;
}

function syncInputBox() {
  if (inputBox) {
    (inputBox as any).value = draftInput;
  }
}

function renderPrompt() {
  terminal.write(`\r\x1b[2K${promptLabel}${draftInput}`);
  scheduleRender();
}

function clearDraftInput(render = true) {
  draftInput = '';
  syncInputBox();
  if (render) {
    renderPrompt();
  }
}

function setDraftInput(value: string, render = true) {
  draftInput = value;
  syncInputBox();
  if (render) {
    renderPrompt();
  }
}

type CommandHandler = (args: string[]) => void;

const commands: Record<string, CommandHandler> = Object.create(null);

commands.clear = () => {
  stopDemoStream();
  stopActiveShellSession();
  terminal.write('\x1b[2J\x1b[H');
  printBanner();
};

commands.help = () => {
  runWriter('Available commands:');
  runWriter('  help       Show this help message');
  runWriter('  demo       Stream sample task output');
  runWriter('  uptime     Display a fake uptime report');
  runWriter('  clear      Reset and show the banner again');
  runWriter('  theme      Cycle sample colors via ANSI escapes');
  if (canRunHostShell && hostShell) {
    runWriter(`  <other>    Run via host shell (${hostShell.shell || 'shell'})`);
    runWriter(`  cwd        ${hostShell.cwd || ''}`);
    if (canStartHostTerminal) {
      runWriter('  mc/btop    Start in a terminal context; focus canvas to send keys.');
    } else {
      runWriter('  note       No PTY bridge: full-screen TUIs like mc or btop are not supported.');
    }
  }
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

const terminalCommands = [
  'btop',
  'htop',
  'top',
  'mc',
  'vim',
  'nvim',
  'nano',
  'less',
  'man',
  'tig',
  'ssh'
];

function getExecutableName(command: string): string {
  const first = String(command || '').trim().split(/\s+/g)[0] || '';
  const slash = Math.max(first.lastIndexOf('/'), first.lastIndexOf('\\'));
  return slash >= 0 ? first.slice(slash + 1) : first;
}

function shouldStartTerminalSession(command: string): boolean {
  return canStartHostTerminal && terminalCommands.indexOf(getExecutableName(command)) >= 0;
}

function stopActiveShellSession() {
  if (activeShellPollTimer != null) {
    stopInterval(activeShellPollTimer);
    activeShellPollTimer = null;
  }

  if (activeShellSession) {
    activeShellSession.kill?.();
    activeShellSession = null;
    activeShellCommand = '';
  }
}

function flushActiveShellOutput(): boolean {
  if (!activeShellSession || typeof activeShellSession.read !== 'function') {
    return false;
  }

  const output = activeShellSession.read();
  if (!output) {
    return false;
  }

  terminal.write(output, () => scheduleRender());
  scheduleRender();
  return true;
}

function pollActiveShellSession() {
  if (!activeShellSession) {
    return;
  }

  flushActiveShellOutput();
  if (activeShellSession.isRunning) {
    return;
  }

  flushActiveShellOutput();
  const exitCode = Number(activeShellSession.exitCode);
  const command = activeShellCommand || 'command';
  if (activeShellPollTimer != null) {
    stopInterval(activeShellPollTimer);
    activeShellPollTimer = null;
  }

  activeShellSession = null;
  activeShellCommand = '';
  terminal.write(`\r\n[${command} exited with code ${Number.isFinite(exitCode) ? exitCode : 0}]\r\n`);
  renderPrompt();
  updateStatus(`${command} exited.`);
}

function startActiveShellSession(command: string): boolean {
  if (!canStartHostTerminal || !hostShell) {
    return false;
  }

  ensureTerminalSize();
  stopActiveShellSession();
  terminal.write('\x1b[2J\x1b[H');
  terminal.write(`${promptLabel}${command}\r\n`);

  let session: HostShellSession | null = null;
  try {
    session = hostShell.start?.(command, terminal.cols, terminal.rows) ?? null;
  } catch (error) {
    updateStatus(`Unable to start terminal context: ${error}`);
    renderPrompt();
    return false;
  }
  if (!session) {
    renderPrompt();
    return false;
  }

  activeShellSession = session;
  activeShellCommand = getExecutableName(command) || command;
  lastSessionCols = terminal.cols;
  lastSessionRows = terminal.rows;
  activeShellPollTimer = startInterval(pollActiveShellSession, 50);
  pollActiveShellSession();
  updateStatus(`Started ${activeShellCommand} in ${terminal.cols}x${terminal.rows} terminal context. Focus the canvas to send keys; use Ctrl+C or Clear to stop.`);
  return true;
}

function sendToActiveShellSession(data: string): boolean {
  if (!activeShellSession || !data) {
    return false;
  }

  activeShellSession.write(data);
  return true;
}

function keyToTerminalSequence(event: any): string {
  const key = event?.key;
  if (event?.ctrlKey && (key === 'c' || key === 'C')) {
    return '\x03';
  }
  if (event?.ctrlKey && (key === 'd' || key === 'D')) {
    return '\x04';
  }
  if (event?.ctrlKey && (key === 'l' || key === 'L')) {
    return '\x0c';
  }

  switch (key) {
    case 'Enter':
      return '\r';
    case 'Backspace':
    case 'Back':
      return '\x7f';
    case 'Tab':
      return '\t';
    case 'Escape':
      return '\x1b';
    case 'ArrowUp':
      return '\x1b[A';
    case 'ArrowDown':
      return '\x1b[B';
    case 'ArrowRight':
      return '\x1b[C';
    case 'ArrowLeft':
      return '\x1b[D';
    case 'Home':
      return '\x1b[H';
    case 'End':
      return '\x1b[F';
    case 'PageUp':
      return '\x1b[5~';
    case 'PageDown':
      return '\x1b[6~';
    case 'Delete':
      return '\x1b[3~';
    default:
      return '';
  }
}

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

function executeCommand(raw: string): string {
  const trimmed = raw.trim();
  if (!trimmed) {
    clearDraftInput();
    return 'Enter a command.';
  }

  if (activeShellSession) {
    sendToActiveShellSession(`${raw || ''}\r`);
    clearDraftInput();
    return `Sent input to ${activeShellCommand || 'terminal session'}.`;
  }

  stopDemoStream();
  clearDraftInput(false);

  if (trimmed === 'clear') {
    commands.clear([]);
    renderPrompt();
    return 'Terminal cleared and banner restored.';
  }
  const [command, ...args] = trimmed.split(/\s+/g);
  terminal.write('\r\x1b[2K');
  terminal.writeln(`${promptLabel}${trimmed}`);
  const handler = commands[command];
  if (handler) {
    handler(args);
    renderPrompt();
    return `Ran ${command}.`;
  }

  if (shouldStartTerminalSession(trimmed)) {
    return startActiveShellSession(trimmed)
      ? `Started ${getExecutableName(trimmed)} in terminal context.`
      : `Unable to start terminal context for ${getExecutableName(trimmed)}.`;
  }

  if (canRunHostShell && hostShell) {
    try {
      ensureTerminalSize();
      const result = hostShell.execute(trimmed, 15000, terminal.cols, terminal.rows);
      const exitCode = Number(result?.exitCode);
      const timedOut = !!result?.timedOut;
      const wroteOutput = writeCommandOutput(result?.output ?? '');

      if (!wroteOutput && timedOut) {
        runWriter(`Command timed out after ${result?.timeoutMs ?? 0} ms.`);
      } else if (!wroteOutput && Number.isFinite(exitCode) && exitCode !== 0) {
        runWriter(`Command exited with code ${exitCode}.`);
      }

      renderPrompt();

      if (timedOut) {
        return `Shell command timed out after ${result?.timeoutMs ?? 0} ms.`;
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
}

function printBanner() {
  runWriter('\x1b[96m❯ xterm.js headless (TypeScript) ❮\x1b[0m');
  runWriter('Type "help" for available commands or use the preset buttons.');
}

function bindInput() {
  const submit = (rawValue?: string) => {
    const value = typeof rawValue === 'string'
      ? rawValue
      : String((inputBox as any)?.value ?? draftInput);
    try {
      updateStatus(executeCommand(value));
    } catch (error) {
      updateStatus(`Command failed: ${error}`);
      throw error;
    }
  };

  sendButton?.addEventListener('click', submit);
  (inputBox as any)?.addEventListener?.('input', () => {
    const value = String((inputBox as any)?.value ?? '');
    setDraftInput(value);
  });
  (inputBox as any)?.addEventListener?.('keydown', (event: any) => {
    if (event?.key === 'Enter') {
      event.preventDefault?.();
      submit();
    }
  });

  clearButton?.addEventListener('click', () => {
    stopDemoStream();
    stopActiveShellSession();
    clearDraftInput(false);
    commands.clear([]);
    renderPrompt();
    updateStatus('Terminal cleared and banner restored.');
  });

  demoButton?.addEventListener('click', () => {
    stopDemoStream();
    stopActiveShellSession();
    clearDraftInput(false);
    commands.demo([]);
    updateStatus('Streaming demo workload...');
    renderPrompt();
  });

  (surface as any)?.addEventListener?.('pointerdown', () => {
    (surface as any)?.focus?.();
  });
  (surface as any)?.addEventListener?.('click', () => {
    (surface as any)?.focus?.();
  });
  (surface as any)?.addEventListener?.('textinput', (event: any) => {
    const data = typeof event?.data === 'string' ? event.data : '';
    if (!data) {
      return;
    }

    if (activeShellSession) {
      sendToActiveShellSession(data);
      event.preventDefault?.();
      return;
    }

    draftInput += data;
    syncInputBox();
    renderPrompt();
    event.preventDefault?.();
  });
  (surface as any)?.addEventListener?.('keydown', (event: any) => {
    if (activeShellSession) {
      const sequence = keyToTerminalSequence(event);
      if (sequence) {
        sendToActiveShellSession(sequence);
        event.preventDefault?.();
      }
      return;
    }

    const key = event?.key;
    if (event?.ctrlKey && (key === 'l' || key === 'L')) {
      clearDraftInput(false);
      commands.clear([]);
      renderPrompt();
      updateStatus('Terminal cleared and banner restored.');
      event.preventDefault?.();
      return;
    }

    if (key === 'Enter') {
      event.preventDefault?.();
      submit(draftInput);
      return;
    }

    if (key === 'Backspace' || key === 'Back') {
      if (draftInput.length > 0) {
        draftInput = draftInput.slice(0, -1);
        syncInputBox();
        renderPrompt();
      }
      event.preventDefault?.();
      return;
    }

    if (key === 'Escape') {
      clearDraftInput();
      updateStatus('Input cleared.');
      event.preventDefault?.();
    }
  });
}

function bootstrap() {
  if (!ensureElements()) {
    return;
  }
  bindInput();
  printBanner();
  renderPrompt();
  updateStatus('xterm.js headless ready — try the demo or type help.');
  scheduleRender();
  if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => scheduleRender());
  }
}

bootstrap();
