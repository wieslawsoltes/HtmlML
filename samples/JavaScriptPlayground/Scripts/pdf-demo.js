const PDFJS_URL = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@2.10.377/legacy/build/pdf.min.js';
const PDFJS_WORKER_URL = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@2.10.377/legacy/build/pdf.worker.min.js';
const PDFJS_STANDARD_FONTS_URL = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@2.10.377/standard_fonts/';
const BUILT_IN_PDF_BASE64 = 'JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvUmVzb3VyY2VzIDw8IC9Gb250IDw8IC9GMSA0IDAgUiA+PiA+PiAvQ29udGVudHMgNSAwIFIgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9udCAvSGVsdmV0aWNhID4+CmVuZG9iago1IDAgb2JqCjw8IC9MZW5ndGggMTkxID4+CnN0cmVhbQpCVAovRjEgMjggVGYKNzIgNzAwIFRkCihQREYuanMgaW4gSmF2YVNjcmlwdC5BdmFsb25pYSkgVGoKL0YxIDE0IFRmCjAgLTQwIFRkCihUaGlzIGJ1aWx0LWluIGRvY3VtZW50IGlzIHJlbmRlcmVkIGJ5IHBkZi5qcy4pIFRqCjAgLTI0IFRkCihVc2UgT3BlbiBQREYgdG8gcHJldmlldyBhbiBleHRlcm5hbCBkb2N1bWVudC4pIFRqCkVUCmVuZHN0cmVhbQplbmRvYmoKeHJlZgowIDYKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDE1IDAwMDAwIG4gCjAwMDAwMDAwNjQgMDAwMDAgbiAKMDAwMDAwMDEyMSAwMDAwMCBuIAowMDAwMDAwMjQ3IDAwMDAwIG4gCjAwMDAwMDAzMTcgMDAwMDAgbiAKdHJhaWxlcgo8PCAvU2l6ZSA2IC9Sb290IDEgMCBSID4+CnN0YXJ0eHJlZgo1NTgKJSVFT0YK';

const installPdfJsBrowserShims = () => {
  const root = typeof globalThis !== 'undefined' ? globalThis : window;
  if (typeof root.location === 'undefined') {
    root.location = { href: 'https://localhost/' };
  }

  if (typeof root.Blob === 'undefined') {
    root.Blob = function Blob(parts, options) {
      this.parts = parts ?? [];
      this.type = options?.type ?? '';
    };
  }

  if (typeof root.ReadableStream === 'undefined') {
    root.ReadableStream = class ReadableStream {
      constructor(source) {
        this._source = source ?? {};
        this._queue = [];
        this._reads = [];
        this._closed = false;
        this._error = null;
        this._controller = {
          get desiredSize() {
            return 1;
          },
          enqueue: chunk => {
            if (this._closed || this._error) {
              return;
            }

            const pending = this._reads.shift();
            if (pending) {
              pending.resolve({ value: chunk, done: false });
            } else {
              this._queue.push(chunk);
            }
          },
          close: () => {
            this._closed = true;
            while (this._reads.length) {
              this._reads.shift().resolve({ value: undefined, done: true });
            }
          },
          error: error => {
            this._error = error ?? new Error('ReadableStream error');
            while (this._reads.length) {
              this._reads.shift().reject(this._error);
            }
          }
        };

        try {
          const startResult = this._source.start?.(this._controller);
          Promise.resolve(startResult).catch(this._controller.error);
        } catch (error) {
          this._controller.error(error);
        }
      }

      getReader() {
        const stream = this;
        return {
          read() {
            if (stream._queue.length) {
              return Promise.resolve({ value: stream._queue.shift(), done: false });
            }

            if (stream._error) {
              return Promise.reject(stream._error);
            }

            if (stream._closed) {
              return Promise.resolve({ value: undefined, done: true });
            }

            const pending = new Promise((resolve, reject) => {
              stream._reads.push({ resolve, reject });
            });

            try {
              const pullResult = stream._source.pull?.(stream._controller);
              Promise.resolve(pullResult).catch(stream._controller.error);
            } catch (error) {
              stream._controller.error(error);
            }

            return pending;
          },
          cancel(reason) {
            stream._closed = true;
            while (stream._reads.length) {
              stream._reads.shift().resolve({ value: undefined, done: true });
            }

            try {
              return Promise.resolve(stream._source.cancel?.(reason));
            } catch (error) {
              return Promise.reject(error);
            }
          },
          releaseLock() {
          }
        };
      }
    };
  }

  if (typeof root.URL === 'undefined') {
    const parseParts = value => {
      const match = String(value ?? '').match(/^([a-z][a-z0-9+.-]*:)?(?:\/\/([^/?#]*))?/i);
      const protocol = match?.[1] ?? '';
      const host = match?.[2] ?? '';
      const origin = protocol && host ? `${protocol}//${host}` : 'null';
      return { protocol, host, origin };
    };

    const resolveUrl = (value, base) => {
      const text = String(value ?? '');
      if (/^[a-z][a-z0-9+.-]*:/i.test(text)) {
        return text;
      }

      const baseText = base && typeof base === 'object' && 'href' in base
        ? String(base.href)
        : String(base ?? root.location.href ?? 'https://localhost/');
      const baseParts = parseParts(baseText);
      if (text.startsWith('//')) {
        return `${baseParts.protocol || 'https:'}${text}`;
      }

      if (text.startsWith('/')) {
        return `${baseParts.origin === 'null' ? 'https://localhost' : baseParts.origin}${text}`;
      }

      const baseDirectory = baseText.endsWith('/') ? baseText : baseText.slice(0, baseText.lastIndexOf('/') + 1);
      return `${baseDirectory}${text}`;
    };

    root.URL = function URL(value, base) {
      const href = resolveUrl(value, base);
      const parts = parseParts(href);
      this.href = href;
      this.protocol = parts.protocol;
      this.host = parts.host;
      this.origin = parts.origin;
    };
    root.URL.createObjectURL = () => 'blob:javascript-avalonia-pdfjs-worker';
    root.URL.revokeObjectURL = () => {};
  }

  if (typeof window !== 'undefined') {
    window.location = root.location;
    window.Blob = root.Blob;
    window.ReadableStream = root.ReadableStream;
    window.URL = root.URL;
  }
};

installPdfJsBrowserShims();

const surface = document.getElementById('pdfSurface');
const openBtn = document.getElementById('pdfOpen');
const builtInBtn = document.getElementById('pdfBuiltIn');
const prevBtn = document.getElementById('pdfPrev');
const nextBtn = document.getElementById('pdfNext');
const zoomInBtn = document.getElementById('pdfZoomIn');
const zoomOutBtn = document.getElementById('pdfZoomOut');
const info = document.getElementById('pdfInfo');
const status = document.getElementById('pdfStatus');

if (!surface) {
  throw new Error('pdfSurface element not found');
}

const context = surface.getContext('2d');
if (!context) {
  throw new Error('CanvasRenderingContext2D unavailable for PDF.js demo');
}

surface.width = surface.offsetWidth;
surface.height = surface.offsetHeight;

const report = message => {
  if (status) {
    status.textContent = message ?? '';
  }
};

const setDebugStage = value => {
  if (typeof globalThis !== 'undefined') {
    globalThis.pdfDemoStage = value;
  }
};

const updateInfo = message => {
  if (info) {
    info.textContent = message ?? '';
  }
};

const clearSurface = () => {
  const width = surface.offsetWidth || 640;
  const height = surface.offsetHeight || 460;
  context.clearRect(0, 0, width, height);
  context.fillStyle = '#e2e8f0';
  context.fillRect(0, 0, width, height);
};

const drawEmptyState = message => {
  clearSurface();
  context.fillStyle = '#475569';
  context.font = '16px Segoe UI';
  context.textBaseline = 'middle';
  context.textAlign = 'center';
  context.fillText(message, (surface.offsetWidth || 640) / 2, (surface.offsetHeight || 460) / 2);
  context.textAlign = 'start';
  context.textBaseline = 'alphabetic';
};

const base64ToBytes = input => {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  const clean = String(input ?? '').replace(/[^A-Za-z0-9+/=]/g, '');
  const bytes = [];
  let buffer = 0;
  let bits = 0;

  for (const char of clean) {
    if (char === '=') {
      break;
    }

    const value = alphabet.indexOf(char);
    if (value < 0) {
      continue;
    }

    buffer = (buffer << 6) | value;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }

  return new Uint8Array(bytes);
};

const createCanvasFactory = () => ({
  create(width, height) {
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.ceil(width));
    canvas.height = Math.max(1, Math.ceil(height));
    const canvasContext = canvas.getContext('2d');
    if (!canvasContext) {
      throw new Error('Unable to create an offscreen canvas context for PDF.js');
    }

    return {
      canvas,
      context: canvasContext
    };
  },
  reset(entry, width, height) {
    entry.canvas.width = Math.max(1, Math.ceil(width));
    entry.canvas.height = Math.max(1, Math.ceil(height));
    entry.context.clearRect(0, 0, entry.canvas.width, entry.canvas.height);
  },
  destroy(entry) {
    if (entry?.context && entry?.canvas) {
      entry.context.clearRect(0, 0, entry.canvas.width || 0, entry.canvas.height || 0);
      entry.canvas.width = 0;
      entry.canvas.height = 0;
    }
  }
});

let pdfjsLib;
try {
  const pdfjsModule = require(PDFJS_URL);
  pdfjsLib = pdfjsModule?.pdfjsLib ?? pdfjsModule?.default ?? (typeof window !== 'undefined' ? window.pdfjsLib : undefined) ?? pdfjsModule;
} catch (error) {
  report(`Failed to load PDF.js: ${error}`);
  throw error;
}

if (!pdfjsLib?.getDocument) {
  throw new Error('PDF.js module did not expose getDocument');
}

if (pdfjsLib.GlobalWorkerOptions) {
  pdfjsLib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER_URL;
}

try {
  const workerModule = require(PDFJS_WORKER_URL);
  const workerHandler = workerModule?.WorkerMessageHandler ?? workerModule?.default?.WorkerMessageHandler;
  if (workerHandler) {
    const workerGlobal = { WorkerMessageHandler: workerHandler };
    if (typeof globalThis !== 'undefined') {
      globalThis.pdfjsWorker = workerGlobal;
    }
    if (typeof window !== 'undefined') {
      window.pdfjsWorker = workerGlobal;
    }
  }
} catch (error) {
  console.warn(`PDF.js worker preload failed: ${error}`);
}

let currentDocument = null;
let currentName = 'Built-in sample.pdf';
let currentPage = 1;
let currentScale = 1;
let renderSequence = 0;
let promisePumpId = 0;

const ensurePromisePump = () => {
  if (promisePumpId || typeof window === 'undefined' || typeof window.setInterval !== 'function') {
    return;
  }

  // PDF.js resolves through chained promises. A lightweight timer keeps Jint's
  // event loop moving while the fake worker and render task settle.
  promisePumpId = window.setInterval(() => {}, 50);
};

const withTimeout = (promise, timeoutMs, onTimeout) => new Promise((resolve, reject) => {
  let settled = false;
  const timeoutId = setTimeout(() => {
    if (settled) {
      return;
    }

    settled = true;
    try {
      onTimeout?.();
    } catch {
    }

    reject(new Error(`operation timed out after ${timeoutMs} ms`));
  }, timeoutMs);

  Promise.resolve(promise).then(
    value => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeoutId);
      resolve(value);
    },
    error => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeoutId);
      reject(error);
    });
});

const computeViewport = page => {
  const view = Array.isArray(page.view) && page.view.length >= 4 ? page.view : [0, 0, 612, 792];
  const xMin = Number(view[0]) || 0;
  const yMin = Number(view[1]) || 0;
  const xMax = Number(view[2]) || 612;
  const yMax = Number(view[3]) || 792;
  const pageWidth = Math.max(1, Math.abs(xMax - xMin));
  const pageHeight = Math.max(1, Math.abs(yMax - yMin));
  const maxWidth = Math.max(120, (surface.offsetWidth || 640) - 36);
  const maxHeight = Math.max(120, (surface.offsetHeight || 460) - 36);
  const fitScale = Math.min(maxWidth / pageWidth, maxHeight / pageHeight);
  const scale = Math.max(0.25, fitScale * currentScale);

  return {
    viewBox: view,
    scale,
    rotation: page.rotate || 0,
    offsetX: 0,
    offsetY: 0,
    width: pageWidth * scale,
    height: pageHeight * scale,
    transform: [scale, 0, 0, -scale, -xMin * scale, yMax * scale]
  };
};

const drawPageShell = viewport => {
  clearSurface();
  const x = Math.max(12, ((surface.offsetWidth || 640) - viewport.width) / 2);
  const y = Math.max(12, ((surface.offsetHeight || 460) - viewport.height) / 2);
  context.fillStyle = '#ffffff';
  context.fillRect(x, y, viewport.width, viewport.height);
  context.strokeStyle = '#94a3b8';
  context.lineWidth = 1;
  context.strokeRect(x, y, viewport.width, viewport.height);
  return { x, y };
};

const drawWrappedLine = (text, x, y, maxWidth, lineHeight) => {
  const words = String(text ?? '').split(/\s+/).filter(Boolean);
  let line = '';
  let cursorY = y;

  for (const word of words) {
    const next = line ? `${line} ${word}` : word;
    if (context.measureText(next).width > maxWidth && line) {
      context.fillText(line, x, cursorY);
      cursorY += lineHeight;
      line = word;
    } else {
      line = next;
    }
  }

  if (line) {
    context.fillText(line, x, cursorY);
    cursorY += lineHeight;
  }

  return cursorY;
};

const drawUnavailablePreview = (name, reason) => {
  const viewport = {
    width: Math.max(240, (surface.offsetWidth || 640) - 44),
    height: Math.max(240, (surface.offsetHeight || 460) - 44)
  };
  const origin = drawPageShell(viewport);
  const maxWidth = viewport.width - 48;
  let y = origin.y + 42;

  context.fillStyle = '#0f172a';
  context.font = '18px Segoe UI';
  context.fillText(name || 'PDF document', origin.x + 24, y);
  y += 34;
  context.font = '13px Segoe UI';
  context.fillStyle = '#475569';
  context.fillText(`PDF.js ${pdfjsLib.version ?? ''}`, origin.x + 24, y);
  y += 38;

  context.font = '14px Segoe UI';
  context.fillStyle = '#111827';
  y = drawWrappedLine(
    'PDF.js could not render this page in the current JavaScript canvas runtime.',
    origin.x + 24,
    y,
    maxWidth,
    20);
  y += 10;
  context.fillStyle = '#64748b';
  drawWrappedLine(String(reason?.message ?? reason ?? 'No renderer error was provided.'), origin.x + 24, y, maxWidth, 18);

  updateInfo(`${name || 'PDF document'} - render unavailable`);
  report(`PDF.js ${pdfjsLib.version ?? ''} could not render the page: ${reason?.message ?? reason}`);
};

const renderTextPreview = (page, viewport, origin, renderError) => page.getTextContent().then(textContent => {
  context.save();
  try {
    context.translate(origin.x, origin.y);
    context.fillStyle = '#0f172a';
    context.textAlign = 'start';
    context.textBaseline = 'alphabetic';

    let drawn = 0;
    for (const item of textContent.items ?? []) {
      const text = String(item.str ?? '').trim();
      if (!text) {
        continue;
      }

      const transform = pdfjsLib.Util?.transform
        ? pdfjsLib.Util.transform(viewport.transform, item.transform)
        : item.transform;
      const size = Math.max(7, Math.hypot(transform[2] ?? 0, transform[3] ?? 0) || item.height || 10);
      const x = transform[4] ?? 0;
      const y = transform[5] ?? 0;
      context.font = `${size}px Segoe UI`;
      context.fillText(text, x, y);
      drawn += 1;
    }

    const reason = renderError?.message ?? renderError ?? 'unsupported canvas operation';
    if (drawn > 0) {
      report(`PDF.js text preview for ${currentName}. Canvas render fallback: ${reason}`);
    } else {
      report(`Loaded ${currentName}, but this page has no extractable text. Canvas render fallback: ${reason}`);
    }
  } finally {
    context.restore();
  }
}, error => {
  drawUnavailablePreview(currentName, error);
});

const renderPage = () => {
  if (!currentDocument) {
    drawEmptyState('Load a PDF document to begin.');
    return Promise.resolve();
  }

  ensurePromisePump();
  const sequence = ++renderSequence;
  setDebugStage('get-page');

  return currentDocument.getPage(currentPage).then(page => {
    if (sequence !== renderSequence) {
      return undefined;
    }

    setDebugStage('render-page');
    const viewport = computeViewport(page);
    setDebugStage('viewport-computed');
    const origin = drawPageShell(viewport);
    setDebugStage('page-shell-drawn');
    updateInfo(`${currentName} - page ${currentPage} of ${currentDocument.numPages} - zoom ${Math.round(currentScale * 100)}%`);

    let task;
    try {
      context.save();
      context.translate(origin.x, origin.y);
      task = page.render({
        canvasContext: context,
        viewport,
        canvasFactory: createCanvasFactory(),
        background: '#ffffff'
      });
      setDebugStage('render-task-created');
    } catch (error) {
      try {
        context.restore();
      } catch {
      }

      setDebugStage('render-page-fallback');
      drawPageShell(viewport);
      return renderTextPreview(page, viewport, origin, error);
    }

    return withTimeout(task.promise, 2500, () => {
      setDebugStage('render-page-timeout');
      task.cancel?.();
    }).then(() => {
      context.restore();
      report(`PDF.js ${pdfjsLib.version ?? ''} rendered ${currentName}.`);
    }, error => {
      try {
        context.restore();
      } catch {
      }

      setDebugStage('render-page-fallback');
      drawPageShell(viewport);
      return renderTextPreview(page, viewport, origin, error);
    });
  }, error => {
    drawUnavailablePreview(currentName, error);
    return undefined;
  });
};

const loadDocument = (name, data) => {
  report(`Loading ${name}...`);
  updateInfo('');
  drawEmptyState('Loading PDF...');
  ensurePromisePump();

  const loadingTask = pdfjsLib.getDocument({
    data,
    disableWorker: true,
    standardFontDataUrl: PDFJS_STANDARD_FONTS_URL
  });

  setDebugStage('await-document');
  return loadingTask.promise.then(document => {
    currentDocument = document;
    setDebugStage('document-loaded');
    currentName = name || 'Untitled.pdf';
    currentPage = 1;
    currentScale = 1;
    return renderPage();
  }, error => {
    currentDocument = null;
    currentName = name || 'Untitled.pdf';
    drawUnavailablePreview(currentName, error);
    return undefined;
  });
};

const loadBuiltIn = () => {
  loadDocument('Built-in sample.pdf', base64ToBytes(BUILT_IN_PDF_BASE64)).catch(error => {
    report(`Failed to load built-in PDF: ${error}`);
    drawEmptyState('PDF load failed.');
  });
};

openBtn.addEventListener('click', () => {
  const bridge = typeof playgroundFiles !== 'undefined' ? playgroundFiles : window.playgroundFiles;
  if (!bridge?.openPdfDocument) {
    report('Host file picker bridge is unavailable.');
    return;
  }

  report('Opening PDF file picker...');
  bridge.openPdfDocument(result => {
    if (!result || result.cancelled) {
      report('PDF open cancelled.');
      return;
    }

    if (!result.success) {
      report(`Failed to open PDF: ${result.error ?? 'unknown error'}`);
      return;
    }

    loadDocument(result.name || 'External document.pdf', base64ToBytes(result.dataBase64)).catch(error => {
      report(`Failed to load ${result.name}: ${error}`);
      drawEmptyState('PDF load failed.');
    });
  });
});

builtInBtn.addEventListener('click', loadBuiltIn);

prevBtn.addEventListener('click', () => {
  if (!currentDocument || currentPage <= 1) {
    return;
  }

  currentPage -= 1;
  renderPage().catch(error => report(`Failed to render previous page: ${error}`));
});

nextBtn.addEventListener('click', () => {
  if (!currentDocument || currentPage >= currentDocument.numPages) {
    return;
  }

  currentPage += 1;
  renderPage().catch(error => report(`Failed to render next page: ${error}`));
});

zoomInBtn.addEventListener('click', () => {
  currentScale = Math.min(3, currentScale + 0.15);
  renderPage().catch(error => report(`Failed to zoom in: ${error}`));
});

zoomOutBtn.addEventListener('click', () => {
  currentScale = Math.max(0.35, currentScale - 0.15);
  renderPage().catch(error => report(`Failed to zoom out: ${error}`));
});

report(`PDF.js ${pdfjsLib.version ?? ''} loaded. Loading the built-in sample...`);
loadBuiltIn();
