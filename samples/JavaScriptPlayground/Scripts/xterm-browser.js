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
  background: '#0f172a',
  foreground: '#e2e8f0',
  accent: '#38bdf8'
};

const ansiPalette = [
  '#1e293b', '#ef4444', '#22c55e', '#eab308',
  '#3b82f6', '#a855f7', '#06b6d4', '#cbd5e1',
  '#64748b', '#f87171', '#4ade80', '#facc15',
  '#60a5fa', '#c084fc', '#22d3ee', '#f8fafc'
];

const colorCubeSteps = [0, 95, 135, 175, 215, 255];

const byteToHex = (value) => {
  const normalized = Math.max(0, Math.min(255, Math.round(Number(value) || 0)));
  return normalized.toString(16).padStart(2, '0');
};

const toHexColor = (red, green, blue) => `#${byteToHex(red)}${byteToHex(green)}${byteToHex(blue)}`;

const rgbIntToColor = (value) => {
  const color = Math.max(0, Math.floor(Number(value) || 0));
  return toHexColor((color >> 16) & 255, (color >> 8) & 255, color & 255);
};

const paletteColor = (index) => {
  const colorIndex = Math.floor(Number(index));
  if (!Number.isFinite(colorIndex) || colorIndex < 0) {
    return null;
  }

  if (colorIndex < ansiPalette.length) {
    return ansiPalette[colorIndex];
  }

  if (colorIndex >= 16 && colorIndex <= 231) {
    const offset = colorIndex - 16;
    const red = colorCubeSteps[Math.floor(offset / 36) % 6];
    const green = colorCubeSteps[Math.floor(offset / 6) % 6];
    const blue = colorCubeSteps[offset % 6];
    return toHexColor(red, green, blue);
  }

  if (colorIndex >= 232 && colorIndex <= 255) {
    const gray = 8 + (colorIndex - 232) * 10;
    return toHexColor(gray, gray, gray);
  }

  return null;
};

const resolveCellColor = (cell, foreground) => {
  if (!cell) {
    return foreground ? theme.foreground : null;
  }

  const defaultMethod = foreground ? cell.isFgDefault : cell.isBgDefault;
  if (typeof defaultMethod === 'function' && defaultMethod.call(cell)) {
    return foreground ? theme.foreground : null;
  }

  const colorMethod = foreground ? cell.getFgColor : cell.getBgColor;
  const value = typeof colorMethod === 'function' ? colorMethod.call(cell) : null;
  if (!Number.isFinite(Number(value))) {
    return foreground ? theme.foreground : null;
  }

  const rgbMethod = foreground ? cell.isFgRGB : cell.isBgRGB;
  if (typeof rgbMethod === 'function' && rgbMethod.call(cell)) {
    return rgbIntToColor(value);
  }

  const paletteMethod = foreground ? cell.isFgPalette : cell.isBgPalette;
  if (typeof paletteMethod === 'function' && paletteMethod.call(cell)) {
    return paletteColor(value) || (foreground ? theme.foreground : null);
  }

  return paletteColor(value) || rgbIntToColor(value);
};

const getCellColors = (cell) => {
  let foreground = resolveCellColor(cell, true) || theme.foreground;
  let background = resolveCellColor(cell, false);

  if (cell && typeof cell.isInverse === 'function' && cell.isInverse()) {
    const originalForeground = foreground || theme.foreground;
    const originalBackground = background || theme.background;
    foreground = originalBackground;
    background = originalForeground;
  }

  return { foreground, background };
};

const terminal = new TerminalCtor({
  cols: 120,
  rows: 36,
  scrollback: 4000,
  allowProposedApi: true
});

let fontSize = 14;
const fontFamily = 'Menlo, Monaco, Consolas, "Courier New", monospace';
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
const canRunHostShell = hostShell && hostShell.execute != null;
const canStartHostTerminal = hostShell && hostShell.supportsTty === true;
let activeShellSession = null;
let activeShellCommand = '';
let activeShellPollTimer = null;
let lastSessionCols = terminal.cols;
let lastSessionRows = terminal.rows;
let activeMouseButton = null;
let lastMouseMoveKey = '';
let mouseModeParserBuffer = '';
const terminalMouseModes = {
  x10: false,
  normal: false,
  button: false,
  any: false,
  sgr: false
};

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

const tryResizeTerminal = (cols, rows) => {
  if (cols === terminal.cols && rows === terminal.rows) {
    return;
  }

  try {
    terminal.resize(cols, rows);
  } catch (error) {
    // The bundled headless xterm build can expose readonly rows/cols under Jint.
    // Keep the constructor size in that case; it is already large enough for TUIs.
  }
};

const ensureTerminalSize = () => {
  measureCells();
  const width = surface.offsetWidth || surface.clientWidth || surface.desiredWidth || 1120;
  const height = surface.offsetHeight || surface.clientHeight || surface.desiredHeight || 640;
  const contentWidth = Math.max(0, width - horizontalPadding * 2);
  const contentHeight = Math.max(0, height - verticalPadding * 2);
  const cols = Math.max(40, Math.floor(contentWidth / cellWidth));
  const rows = Math.max(12, Math.floor(contentHeight / cellHeight));
  tryResizeTerminal(cols, rows);
  if (activeShellSession && (terminal.cols !== lastSessionCols || terminal.rows !== lastSessionRows)) {
    lastSessionCols = terminal.cols;
    lastSessionRows = terminal.rows;
    activeShellSession.resize && activeShellSession.resize(terminal.cols, terminal.rows);
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

  const buffer = terminal.buffer.active;
  const viewportTop = buffer.viewportY;
  const scratch = buffer.getNullCell();
  const textTop = verticalPadding;
  const textLeft = horizontalPadding;
  let currentFont = '';

  const useCellFont = (cell) => {
    const italic = cell && typeof cell.isItalic === 'function' && cell.isItalic();
    const bold = cell && typeof cell.isBold === 'function' && cell.isBold();
    const font = `${italic ? 'italic ' : ''}${bold ? 'bold ' : ''}${fontSize}px ${fontFamily}`;
    if (font !== currentFont) {
      ctx.font = font;
      currentFont = font;
    }
  };

  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.getLine(viewportTop + row);
    let x = textLeft;
    if (!line) {
      continue;
    }

    for (let col = 0; col < terminal.cols; col++) {
      const cell = line.getCell(col, scratch);
      const widthUnits = cell && typeof cell.getWidth === 'function' ? cell.getWidth() : 1;
      if (widthUnits <= 0) {
        x += cellWidth;
        continue;
      }

      const charWidth = Math.max(1, widthUnits);
      const cellX = x;
      const cellY = textTop + row * cellHeight;
      const text = cell && typeof cell.getChars === 'function' ? cell.getChars() : '';
      const colors = getCellColors(cell);

      if (colors.background && colors.background !== theme.background) {
        ctx.fillStyle = colors.background;
        ctx.fillRect(cellX, cellY, cellWidth * charWidth, cellHeight);
      }

      if (text && text.trim().length > 0 && !(cell && typeof cell.isInvisible === 'function' && cell.isInvisible())) {
        useCellFont(cell);
        ctx.globalAlpha = cell && typeof cell.isDim === 'function' && cell.isDim() ? 0.65 : 1;
        ctx.fillStyle = colors.foreground;
        ctx.fillText(text, cellX, cellY + baselineOffset);
        ctx.globalAlpha = 1;
      }

      if (cell && typeof cell.isUnderline === 'function' && cell.isUnderline()) {
        ctx.fillStyle = colors.foreground;
        ctx.fillRect(cellX, cellY + cellHeight - 3, cellWidth * charWidth, 1);
      }

      x += cellWidth * charWidth;
      col += charWidth - 1;
    }
  }

  const cursorX = buffer.cursorX;
  const cursorY = buffer.cursorY;
  const cursorLeft = textLeft + cursorX * cellWidth;
  const cursorTop = textTop + cursorY * cellHeight;
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

const getExecutableName = (command) => {
  const first = String(command || '').trim().split(/\s+/g)[0] || '';
  const slash = Math.max(first.lastIndexOf('/'), first.lastIndexOf('\\'));
  return slash >= 0 ? first.slice(slash + 1) : first;
};

const shouldStartTerminalSession = (command) => canStartHostTerminal && terminalCommands.indexOf(getExecutableName(command)) >= 0;

const resetTerminalMouseModes = () => {
  terminalMouseModes.x10 = false;
  terminalMouseModes.normal = false;
  terminalMouseModes.button = false;
  terminalMouseModes.any = false;
  terminalMouseModes.sgr = false;
  activeMouseButton = null;
  lastMouseMoveKey = '';
  mouseModeParserBuffer = '';
};

const updateTerminalMouseModes = (data) => {
  if (!data) {
    return;
  }

  const text = `${mouseModeParserBuffer}${String(data)}`;
  const pattern = /\x1b\[\?([0-9;]+)([hl])/g;
  let match;
  while ((match = pattern.exec(text)) !== null) {
    const enabled = match[2] === 'h';
    const codes = match[1].split(';');
    for (const code of codes) {
      switch (Number(code)) {
        case 9:
          terminalMouseModes.x10 = enabled;
          break;
        case 1000:
          terminalMouseModes.normal = enabled;
          break;
        case 1002:
          terminalMouseModes.button = enabled;
          break;
        case 1003:
          terminalMouseModes.any = enabled;
          break;
        case 1006:
          terminalMouseModes.sgr = enabled;
          break;
        default:
          break;
      }
    }
  }

  const tail = text.slice(-64);
  mouseModeParserBuffer = tail.indexOf('\x1b[?') >= 0 ? tail : '';
};

const hasTerminalMouseTracking = () =>
  terminalMouseModes.x10 || terminalMouseModes.normal || terminalMouseModes.button || terminalMouseModes.any;

const stopActiveShellSession = () => {
  if (activeShellPollTimer != null) {
    stopInterval(activeShellPollTimer);
    activeShellPollTimer = null;
  }

  if (activeShellSession) {
    activeShellSession.kill && activeShellSession.kill();
    activeShellSession = null;
    activeShellCommand = '';
  }

  resetTerminalMouseModes();
};

const flushActiveShellOutput = () => {
  if (!activeShellSession || typeof activeShellSession.read !== 'function') {
    return false;
  }

  const output = activeShellSession.read();
  if (!output) {
    return false;
  }

  updateTerminalMouseModes(output);
  terminal.write(output, () => scheduleRender());
  scheduleRender();
  return true;
};

const pollActiveShellSession = () => {
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
};

const startActiveShellSession = (command) => {
  if (!canStartHostTerminal || !hostShell) {
    return false;
  }

  ensureTerminalSize();
  stopActiveShellSession();
  terminal.write('\x1b[2J\x1b[H');
  terminal.write(`${promptLabel}${command}\r\n`);

  let session = null;
  try {
    session = hostShell.start(command, terminal.cols, terminal.rows);
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
  updateStatus(`Started ${activeShellCommand} in ${terminal.cols}x${terminal.rows} terminal context. Focus the canvas for keyboard and mouse; use Ctrl+C or Clear to stop.`);
  return true;
};

const sendToActiveShellSession = (data) => {
  if (!activeShellSession || !data) {
    return false;
  }

  activeShellSession.write && activeShellSession.write(data);
  return true;
};

const terminalModifierCode = (event) => {
  let modifier = 1;
  if (event?.shiftKey) {
    modifier += 1;
  }
  if (event?.altKey || event?.metaKey) {
    modifier += 2;
  }
  if (event?.ctrlKey) {
    modifier += 4;
  }
  return modifier;
};

const terminalModifierPrefix = (event, sequence) => {
  const modifier = terminalModifierCode(event);
  return modifier > 1 ? sequence(modifier) : sequence(0);
};

const ctrlSequenceForKey = (key) => {
  if (!key || key.length !== 1) {
    return '';
  }

  const upper = key.toUpperCase();
  if (upper >= 'A' && upper <= 'Z') {
    return String.fromCharCode(upper.charCodeAt(0) - 64);
  }

  switch (key) {
    case ' ':
    case '2':
      return '\x00';
    case '[':
    case '3':
      return '\x1b';
    case '\\':
    case '4':
      return '\x1c';
    case ']':
    case '5':
      return '\x1d';
    case '^':
    case '6':
      return '\x1e';
    case '_':
    case '7':
      return '\x1f';
    case '?':
    case '8':
      return '\x7f';
    default:
      return '';
  }
};

const keyToTerminalSequence = (event) => {
  const key = event?.key;

  if (event?.ctrlKey) {
    const control = ctrlSequenceForKey(key);
    if (control) {
      return event?.altKey || event?.metaKey ? `\x1b${control}` : control;
    }
  }

  if ((event?.altKey || event?.metaKey) && typeof key === 'string' && key.length === 1) {
    return `\x1b${key}`;
  }

  switch (key) {
    case 'Enter':
      return '\r';
    case 'Backspace':
    case 'Back':
      return '\x7f';
    case 'Tab':
      return event?.shiftKey ? '\x1b[Z' : '\t';
    case 'Escape':
      return '\x1b';
    case 'ArrowUp':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}A` : '\x1b[A');
    case 'ArrowDown':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}B` : '\x1b[B');
    case 'ArrowRight':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}C` : '\x1b[C');
    case 'ArrowLeft':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}D` : '\x1b[D');
    case 'Home':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}H` : '\x1b[H');
    case 'End':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[1;${modifier}F` : '\x1b[F');
    case 'Insert':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[2;${modifier}~` : '\x1b[2~');
    case 'PageUp':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[5;${modifier}~` : '\x1b[5~');
    case 'PageDown':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[6;${modifier}~` : '\x1b[6~');
    case 'Delete':
      return terminalModifierPrefix(event, modifier => modifier ? `\x1b[3;${modifier}~` : '\x1b[3~');
    default:
      break;
  }

  const functionKeyCodes = {
    F1: ['P', '1'],
    F2: ['Q', '1'],
    F3: ['R', '1'],
    F4: ['S', '1'],
    F5: ['15', 'tilde'],
    F6: ['17', 'tilde'],
    F7: ['18', 'tilde'],
    F8: ['19', 'tilde'],
    F9: ['20', 'tilde'],
    F10: ['21', 'tilde'],
    F11: ['23', 'tilde'],
    F12: ['24', 'tilde']
  };
  const functionKey = functionKeyCodes[key];
  if (!functionKey) {
    return '';
  }

  const modifier = terminalModifierCode(event);
  if (functionKey[1] === '1') {
    return modifier > 1 ? `\x1b[1;${modifier}${functionKey[0]}` : `\x1bO${functionKey[0]}`;
  }

  return modifier > 1 ? `\x1b[${functionKey[0]};${modifier}~` : `\x1b[${functionKey[0]}~`;
};

const xtermMouseModifierBits = (event) => {
  let modifiers = 0;
  if (event?.shiftKey) {
    modifiers += 4;
  }
  if (event?.altKey || event?.metaKey) {
    modifiers += 8;
  }
  if (event?.ctrlKey) {
    modifiers += 16;
  }
  return modifiers;
};

const domButtonToXtermButton = (button) => {
  switch (Number(button)) {
    case 0:
      return 0;
    case 1:
      return 1;
    case 2:
      return 2;
    default:
      return -1;
  }
};

const buttonsToXtermButton = (buttons) => {
  const value = Number(buttons) || 0;
  if ((value & 1) !== 0) {
    return 0;
  }
  if ((value & 4) !== 0) {
    return 1;
  }
  if ((value & 2) !== 0) {
    return 2;
  }
  return -1;
};

const getTerminalCellFromEvent = (event) => {
  ensureTerminalSize();
  const pointerX = Number(event?.x ?? event?.clientX);
  const pointerY = Number(event?.y ?? event?.clientY);
  if (!Number.isFinite(pointerX) || !Number.isFinite(pointerY)) {
    return null;
  }

  const col = Math.floor((pointerX - horizontalPadding) / cellWidth) + 1;
  const row = Math.floor((pointerY - verticalPadding) / cellHeight) + 1;
  if (col < 1 || row < 1 || col > terminal.cols || row > terminal.rows) {
    return null;
  }

  return { col, row };
};

const encodeTerminalMouseSequence = (buttonCode, col, row, final, event) => {
  const code = buttonCode + xtermMouseModifierBits(event);
  if (terminalMouseModes.sgr) {
    return `\x1b[<${code};${col};${row}${final}`;
  }

  if (col > 223 || row > 223) {
    return '';
  }

  const legacyCode = final === 'm' ? 3 + xtermMouseModifierBits(event) : code;
  return `\x1b[M${String.fromCharCode(legacyCode + 32)}${String.fromCharCode(col + 32)}${String.fromCharCode(row + 32)}`;
};

const sendTerminalMouseEvent = (event, kind) => {
  if (!activeShellSession || !hasTerminalMouseTracking()) {
    return false;
  }

  const cell = getTerminalCellFromEvent(event);
  if (!cell) {
    return false;
  }

  let buttonCode = 0;
  let final = 'M';
  if (kind === 'down') {
    buttonCode = domButtonToXtermButton(event?.button);
    if (buttonCode < 0) {
      return false;
    }
    activeMouseButton = buttonCode;
  } else if (kind === 'up') {
    buttonCode = domButtonToXtermButton(event?.button);
    if (buttonCode < 0) {
      buttonCode = activeMouseButton ?? 0;
    }
    final = 'm';
    activeMouseButton = null;
  } else if (kind === 'move') {
    if (!terminalMouseModes.any && !(terminalMouseModes.button && Number(event?.buttons))) {
      return false;
    }
    const moveButton = buttonsToXtermButton(event?.buttons);
    buttonCode = 32 + (moveButton >= 0 ? moveButton : 3);
    const moveKey = `${cell.col}:${cell.row}:${buttonCode}:${Number(event?.buttons) || 0}`;
    if (moveKey === lastMouseMoveKey) {
      return false;
    }
    lastMouseMoveKey = moveKey;
  } else if (kind === 'wheel') {
    const deltaX = Number(event?.deltaX) || 0;
    const deltaY = Number(event?.deltaY) || 0;
    if (deltaX === 0 && deltaY === 0) {
      return false;
    }
    if (Math.abs(deltaX) > Math.abs(deltaY)) {
      buttonCode = deltaX < 0 ? 66 : 67;
    } else {
      buttonCode = deltaY < 0 ? 64 : 65;
    }
  } else {
    return false;
  }

  const sequence = encodeTerminalMouseSequence(buttonCode, cell.col, cell.row, final, event);
  if (!sequence) {
    return false;
  }

  return sendToActiveShellSession(sequence);
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
  runWriter('  theme      Display ANSI swatches');
  if (canRunHostShell) {
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

  if (activeShellSession) {
    sendToActiveShellSession(`${raw || ''}\r`);
    clearDraftInput();
    return `Sent input to ${activeShellCommand || 'terminal session'}.`;
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

  if (shouldStartTerminalSession(trimmed)) {
    return startActiveShellSession(trimmed)
      ? `Started ${getExecutableName(trimmed)} in terminal context.`
      : `Unable to start terminal context for ${getExecutableName(trimmed)}.`;
  }

  if (canRunHostShell) {
    try {
      ensureTerminalSize();
      const result = hostShell.execute(trimmed, 15000, terminal.cols, terminal.rows);
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
  if (surface && typeof surface.__xtermCleanup === 'function') {
    try {
      surface.__xtermCleanup();
    } catch (error) {
      // Ignore stale cleanup failures from a previous script execution.
    }
  }

  const cleanupHandlers = [];
  const addListener = (target, type, handler) => {
    if (!target || typeof target.addEventListener !== 'function') {
      return;
    }

    target.addEventListener(type, handler);
    cleanupHandlers.push(() => {
      if (typeof target.removeEventListener === 'function') {
        target.removeEventListener(type, handler);
      }
    });
  };

  surface.__xtermCleanup = () => {
    stopDemoStream();
    stopActiveShellSession();
    while (cleanupHandlers.length > 0) {
      const cleanup = cleanupHandlers.pop();
      try {
        cleanup && cleanup();
      } catch (error) {
        // Continue removing the rest of the previous script handlers.
      }
    }
    if (surface.__xtermCleanup) {
      surface.__xtermCleanup = null;
    }
  };

  const submit = (rawValue) => {
    const value = typeof rawValue === 'string'
      ? rawValue
      : (input && typeof input.value === 'string' ? input.value : draftInput);
    try {
      updateStatus(executeCommand(value));
    } catch (error) {
      updateStatus(`Command failed: ${error}`);
      throw error;
    }
  };

  addListener(sendButton, 'click', submit);
  addListener(input, 'input', () => {
    const value = typeof input.value === 'string' ? input.value : '';
    setDraftInput(value);
  });
  addListener(input, 'keydown', (event) => {
    if (event && event.key === 'Enter') {
      event.preventDefault && event.preventDefault();
      submit();
    }
  });

  addListener(clearButton, 'click', () => {
    stopActiveShellSession();
    clearDraftInput(false);
    commands.clear();
    renderPrompt();
    updateStatus('Terminal cleared and banner restored.');
  });

  addListener(demoButton, 'click', () => {
    stopActiveShellSession();
    clearDraftInput(false);
    commands.demo();
    updateStatus('Streaming demo workload...');
    renderPrompt();
  });

  addListener(surface, 'pointerdown', (event) => {
    surface.focus && surface.focus();
    if (sendTerminalMouseEvent(event, 'down')) {
      event.preventDefault && event.preventDefault();
    }
  });
  addListener(surface, 'click', () => {
    surface.focus && surface.focus();
  });
  addListener(surface, 'pointermove', (event) => {
    if (sendTerminalMouseEvent(event, 'move')) {
      event.preventDefault && event.preventDefault();
    }
  });
  addListener(surface, 'pointerup', (event) => {
    if (sendTerminalMouseEvent(event, 'up')) {
      event.preventDefault && event.preventDefault();
    }
  });
  addListener(surface, 'wheel', (event) => {
    if (sendTerminalMouseEvent(event, 'wheel')) {
      event.preventDefault && event.preventDefault();
    }
  });
  addListener(surface, 'textinput', (event) => {
    const data = typeof event?.data === 'string' ? event.data : '';
    if (!data) {
      return;
    }

    if (activeShellSession) {
      sendToActiveShellSession(data);
      event.preventDefault && event.preventDefault();
      return;
    }

    draftInput += data;
    syncInputBox();
    renderPrompt();
    event.preventDefault && event.preventDefault();
  });
  addListener(surface, 'keydown', (event) => {
    if (activeShellSession) {
      const sequence = keyToTerminalSequence(event);
      if (sequence) {
        sendToActiveShellSession(sequence);
        event.preventDefault && event.preventDefault();
      }
      return;
    }

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
