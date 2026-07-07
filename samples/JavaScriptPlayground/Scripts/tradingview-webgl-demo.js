(function () {
  const LIGHTWEIGHT_CHARTS_URL = 'https://cdn.jsdelivr.net/npm/lightweight-charts@4.2.3/dist/lightweight-charts.standalone.production.js';
  const root = typeof globalThis !== 'undefined' ? globalThis : window;
  const win = typeof window !== 'undefined' ? window : root;

  function defineGlobal(name, value) {
    root[name] = value;
    if (win) {
      win[name] = value;
    }
  }

  function readElementSize(target, fallbackWidth, fallbackHeight) {
    const rect = target && typeof target.getBoundingClientRect === 'function'
      ? target.getBoundingClientRect()
      : null;
    const width = Number(target?.clientWidth || target?.offsetWidth || rect?.width || target?.width || fallbackWidth || 1);
    const height = Number(target?.clientHeight || target?.offsetHeight || rect?.height || target?.height || fallbackHeight || 1);

    return {
      width: Math.max(1, Math.round(width)),
      height: Math.max(1, Math.round(height))
    };
  }

  function installBrowserShims() {
    if (!root.performance) {
      defineGlobal('performance', { now: () => Date.now() });
    }

    if (!root.location) {
      defineGlobal('location', {
        href: 'https://localhost/JavaScriptPlayground',
        protocol: 'https:',
        host: 'localhost',
        hostname: 'localhost',
        pathname: '/JavaScriptPlayground',
        search: '',
        hash: ''
      });
    }

    if (typeof root.URL !== 'function') {
      class AvaloniaURL {
        constructor(input, base) {
          const raw = String(input ?? '');
          const baseText = String(base ?? root.location?.href ?? 'https://localhost/');
          this.href = this._resolve(raw, baseText);
          this._parse();
        }

        _resolve(raw, baseText) {
          if (/^[a-z][a-z0-9+.-]*:/i.test(raw)) {
            return raw;
          }

          const baseMatch = /^([a-z][a-z0-9+.-]*:\/\/[^/?#]*)([^?#]*)/i.exec(baseText);
          const origin = baseMatch?.[1] ?? 'https://localhost';
          const basePath = baseMatch?.[2] ?? '/';
          if (raw.startsWith('/')) {
            return `${origin}${raw}`;
          }

          const directory = basePath.endsWith('/') ? basePath : basePath.replace(/\/[^/]*$/, '/');
          return `${origin}${directory}${raw}`;
        }

        _parse() {
          const match = /^([a-z][a-z0-9+.-]*:)?\/\/([^/?#]*)([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(this.href);
          this.protocol = match?.[1] ?? '';
          this.host = match?.[2] ?? '';
          this.hostname = this.host.split(':')[0] ?? '';
          this.pathname = match?.[3] || '/';
          this.search = match?.[4] ?? '';
          this.hash = match?.[5] ?? '';
          this.origin = this.protocol && this.host ? `${this.protocol}//${this.host}` : '';
        }

        toString() {
          return this.href;
        }
      }

      AvaloniaURL.createObjectURL = () => 'blob:avalonia-object-url';
      AvaloniaURL.revokeObjectURL = () => {};
      defineGlobal('URL', AvaloniaURL);
    }

    if (win && typeof win.matchMedia !== 'function') {
      win.matchMedia = query => ({
        matches: false,
        media: String(query ?? ''),
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false
      });
    }

    if (typeof root.ResizeObserver !== 'function') {
      class AvaloniaResizeObserver {
        constructor(callback) {
          this._callback = callback;
          this._targets = [];
        }

        observe(target) {
          if (!target || this._targets.indexOf(target) >= 0) {
            return;
          }

          this._targets.push(target);
          this._notify(target);
        }

        unobserve(target) {
          this._targets = this._targets.filter(item => item !== target);
        }

        disconnect() {
          this._targets = [];
        }

        _notify(target) {
          const size = readElementSize(target);
          const ratio = Math.max(1, Number(win?.devicePixelRatio || 1));
          const entry = {
            target,
            contentRect: {
              x: 0,
              y: 0,
              top: 0,
              left: 0,
              right: size.width,
              bottom: size.height,
              width: size.width,
              height: size.height
            },
            contentBoxSize: [{ inlineSize: size.width, blockSize: size.height }],
            borderBoxSize: [{ inlineSize: size.width, blockSize: size.height }],
            devicePixelContentBoxSize: [{
              inlineSize: Math.max(1, Math.round(size.width * ratio)),
              blockSize: Math.max(1, Math.round(size.height * ratio))
            }]
          };

          this._callback([entry], this);
        }
      }

      defineGlobal('ResizeObserver', AvaloniaResizeObserver);
    }

    try {
      if (document && typeof document.defaultView === 'undefined') {
        document.defaultView = win;
      }
    } catch (error) {
    }
  }

  function patchElement(element) {
    if (!element) {
      return element;
    }

    try {
      if (typeof element.getClientRects !== 'function') {
        element.getClientRects = function () {
          const rect = typeof this.getBoundingClientRect === 'function'
            ? this.getBoundingClientRect()
            : readElementSize(this);
          return [rect];
        };
      }
    } catch (error) {
    }

    try {
      if (element.ownerDocument && typeof element.ownerDocument.defaultView === 'undefined') {
        element.ownerDocument.defaultView = win;
      }
    } catch (error) {
    }

    return element;
  }

  function getRequiredElement(id) {
    const element = document.getElementById(id);
    if (!element) {
      throw new Error(`${id} element not found`);
    }

    return patchElement(element);
  }

  function getOptionalElement(id) {
    return patchElement(document.getElementById(id));
  }

  function setElementText(element, text) {
    if (element) {
      element.textContent = text;
    }
  }

  function formatPrice(value) {
    return Number(value || 0).toFixed(2);
  }

  function formatCompactNumber(value) {
    const number = Math.abs(Number(value || 0));
    if (number >= 1000000) {
      return `${(value / 1000000).toFixed(2)}M`;
    }

    if (number >= 1000) {
      return `${(value / 1000).toFixed(1)}K`;
    }

    return String(Math.round(value || 0));
  }

  function removeChildren(element) {
    const children = Array.from(element.children || []);
    children.forEach(child => {
      if (child && typeof child.remove === 'function') {
        child.remove();
      } else if (element.removeChild) {
        element.removeChild(child);
      }
    });
  }

  function collectCanvasElements(element, result) {
    if (!element) {
      return result;
    }

    if (String(element.nodeName || element.tagName || '').toLowerCase() === 'canvas') {
      result.push(element);
    }

    Array.from(element.children || []).forEach(child => collectCanvasElements(child, result));
    return result;
  }

  function getChartCanvasStats(chartHost) {
    const canvases = collectCanvasElements(chartHost, []);
    let commandCount = 0;

    canvases.forEach(canvas => {
      try {
        const context = typeof canvas.getContext === 'function' ? canvas.getContext('2d') : null;
        if (context && typeof context.CommandCount === 'number') {
          commandCount += context.CommandCount;
        }
      } catch (error) {
      }
    });

    return {
      canvasCount: canvases.length,
      commandCount
    };
  }

  function loadLightweightCharts(status) {
    let module;
    try {
      module = require(LIGHTWEIGHT_CHARTS_URL);
    } catch (error) {
      const message = `Failed to load TradingView Lightweight Charts: ${error}`;
      if (status) {
        status.textContent = message;
      }
      console.error(message);
      throw error;
    }

    const api = module?.createChart
      ? module
      : module?.default?.createChart
        ? module.default
        : root.LightweightCharts || win?.LightweightCharts;

    if (!api?.createChart) {
      throw new Error('TradingView Lightweight Charts API was not detected');
    }

    return api;
  }

  function nextIsoDate(isoDate) {
    const date = new Date(`${isoDate}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + 1);
    return date.toISOString().slice(0, 10);
  }

  function createMarketData() {
    const rows = [];
    let time = '2025-01-02';
    let close = 186.4;

    for (let index = 0; index < 96; index++) {
      const wave = Math.sin(index / 5) * 2.8 + Math.cos(index / 11) * 1.7;
      const drift = index * 0.11;
      const open = close + Math.sin(index / 3) * 1.1;
      close = Math.max(120, open + wave * 0.35 + drift * 0.03);
      const high = Math.max(open, close) + 1.4 + Math.abs(Math.sin(index / 4)) * 2.2;
      const low = Math.min(open, close) - 1.2 - Math.abs(Math.cos(index / 6)) * 1.8;
      const volume = 520000 + Math.round((Math.sin(index / 6) + 1.4) * 180000 + index * 4200);

      rows.push({
        time,
        open: Number(open.toFixed(2)),
        high: Number(high.toFixed(2)),
        low: Number(low.toFixed(2)),
        close: Number(close.toFixed(2)),
        volume
      });

      time = nextIsoDate(time);
    }

    return rows;
  }

  function movingAverage(rows, length) {
    const result = [];
    for (let index = 0; index < rows.length; index++) {
      if (index + 1 < length) {
        continue;
      }

      let sum = 0;
      for (let cursor = index - length + 1; cursor <= index; cursor++) {
        sum += rows[cursor].close;
      }

      result.push({
        time: rows[index].time,
        value: Number((sum / length).toFixed(2))
      });
    }

    return result;
  }

  function exponentialMovingAverage(rows, length) {
    const result = [];
    const smoothing = 2 / (length + 1);
    let previous;

    rows.forEach((row, index) => {
      previous = index === 0 || typeof previous !== 'number'
        ? row.close
        : row.close * smoothing + previous * (1 - smoothing);

      if (index + 1 >= length) {
        result.push({
          time: row.time,
          value: Number(previous.toFixed(2))
        });
      }
    });

    return result;
  }

  function bollingerBands(rows, length, multiplier) {
    const upper = [];
    const lower = [];

    for (let index = length - 1; index < rows.length; index++) {
      const windowRows = rows.slice(index - length + 1, index + 1);
      const mean = windowRows.reduce((sum, row) => sum + row.close, 0) / length;
      const variance = windowRows.reduce((sum, row) => sum + Math.pow(row.close - mean, 2), 0) / length;
      const deviation = Math.sqrt(variance);

      upper.push({
        time: rows[index].time,
        value: Number((mean + deviation * multiplier).toFixed(2))
      });
      lower.push({
        time: rows[index].time,
        value: Number((mean - deviation * multiplier).toFixed(2))
      });
    }

    return { upper, lower };
  }

  function createSignalMarkers(rows) {
    const markers = [];
    for (let index = 18; index < rows.length; index += 24) {
      const row = rows[index];
      const previous = rows[index - 1];
      const rising = row.close >= previous.close;
      markers.push({
        time: toSeriesTime(row),
        position: rising ? 'belowBar' : 'aboveBar',
        color: rising ? '#22c55e' : '#ef4444',
        shape: rising ? 'arrowUp' : 'arrowDown',
        text: rising ? 'Breakout' : 'Distribution'
      });
    }

    return markers;
  }

  function createVolumeSeriesData(rows) {
    return rows.map(row => ({
      time: toSeriesTime(row),
      value: row.volume,
      color: row.close >= row.open
        ? 'rgba(16, 185, 129, 0.34)'
        : 'rgba(239, 68, 68, 0.32)'
    }));
  }

  function toSeriesTime(row) {
    return Math.floor(new Date(`${row.time}T00:00:00Z`).getTime() / 1000);
  }

  function createProgram(gl, vertexSource, fragmentSource) {
    const vertexShader = gl.createShader(gl.VERTEX_SHADER);
    gl.shaderSource(vertexShader, vertexSource);
    gl.compileShader(vertexShader);
    if (!gl.getShaderParameter(vertexShader, gl.COMPILE_STATUS)) {
      throw new Error(`Vertex shader failed: ${gl.getShaderInfoLog(vertexShader)}`);
    }

    const fragmentShader = gl.createShader(gl.FRAGMENT_SHADER);
    gl.shaderSource(fragmentShader, fragmentSource);
    gl.compileShader(fragmentShader);
    if (!gl.getShaderParameter(fragmentShader, gl.COMPILE_STATUS)) {
      throw new Error(`Fragment shader failed: ${gl.getShaderInfoLog(fragmentShader)}`);
    }

    const program = gl.createProgram();
    gl.attachShader(program, vertexShader);
    gl.attachShader(program, fragmentShader);
    gl.bindAttribLocation(program, 0, 'position');
    gl.bindAttribLocation(program, 1, 'color');
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      throw new Error(`WebGL program failed: ${gl.getProgramInfoLog(program)}`);
    }

    return program;
  }

  function createVolumeGeometry(rows) {
    const visible = rows.slice(-72);
    const maxVolume = Math.max(1, ...visible.map(row => row.volume));
    const vertices = [];
    const indices = [];
    const left = -0.96;
    const right = 0.96;
    const baseY = -0.82;
    const span = right - left;
    const step = span / visible.length;
    const gap = Math.min(0.006, step * 0.18);

    visible.forEach((row, index) => {
      const x0 = left + index * step + gap;
      const x1 = left + (index + 1) * step - gap;
      const y1 = baseY + 0.12 + (row.volume / maxVolume) * 1.52;
      const z = -0.25 + index * 0.002;
      const rising = row.close >= row.open;
      const color = rising
        ? [0.08, 0.76, 0.53]
        : [0.96, 0.24, 0.34];
      const vertexOffset = vertices.length / 6;

      vertices.push(
        x0, baseY, z, color[0], color[1], color[2],
        x1, baseY, z, color[0], color[1], color[2],
        x1, y1, z, color[0], color[1], color[2],
        x0, y1, z, color[0], color[1], color[2]
      );

      indices.push(
        vertexOffset, vertexOffset + 1, vertexOffset + 2,
        vertexOffset, vertexOffset + 2, vertexOffset + 3
      );
    });

    return {
      vertices: new Float32Array(vertices),
      indices: new Uint16Array(indices),
      barCount: visible.length
    };
  }

  function createWebGlRenderer(surface) {
    const ratio = Math.max(1, Number(win?.devicePixelRatio || 1));
    const size = readElementSize(surface, 720, 180);
    surface.width = Math.max(1, Math.round(size.width * ratio));
    surface.height = Math.max(1, Math.round(size.height * ratio));

    const gl = surface.getContext('webgl', {
      antialias: false,
      depth: true,
      stencil: true,
      alpha: false,
      preserveDrawingBuffer: true
    }) || surface.getContext('experimental-webgl');

    if (!gl) {
      throw new Error('Native WebGL context unavailable');
    }

    const program = createProgram(
      gl,
      `
attribute vec3 position;
attribute vec3 color;
varying vec3 vColor;
void main() {
  vColor = color;
  gl_Position = vec4(position, 1.0);
}`,
      `
precision mediump float;
varying vec3 vColor;
void main() {
  gl_FragColor = vec4(vColor, 1.0);
}`
    );

    const vertexBuffer = gl.createBuffer();
    const indexBuffer = gl.createBuffer();
    const positionLocation = gl.getAttribLocation(program, 'position');
    const colorLocation = gl.getAttribLocation(program, 'color');

    return {
      gl,
      render(rows) {
        const geometry = createVolumeGeometry(rows);
        const width = gl.drawingBufferWidth || surface.width || size.width;
        const height = gl.drawingBufferHeight || surface.height || size.height;

        gl.viewport(0, 0, width, height);
        gl.clearColor(0.02, 0.04, 0.08, 1);
        gl.clearDepth(1);
        gl.enable(gl.DEPTH_TEST);
        gl.depthFunc(gl.LEQUAL);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        gl.useProgram(program);

        gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, geometry.vertices, gl.STATIC_DRAW);
        gl.enableVertexAttribArray(positionLocation);
        gl.vertexAttribPointer(positionLocation, 3, gl.FLOAT, false, 24, 0);
        gl.enableVertexAttribArray(colorLocation);
        gl.vertexAttribPointer(colorLocation, 3, gl.FLOAT, false, 24, 12);

        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, geometry.indices, gl.STATIC_DRAW);
        gl.drawElements(gl.TRIANGLES, geometry.indices.length, gl.UNSIGNED_SHORT, 0);

        if (typeof gl.flush === 'function') {
          gl.flush();
        }

        return geometry;
      }
    };
  }

  function renderFallbackChart(rows) {
    const context = typeof chartHost.getContext === 'function' ? chartHost.getContext('2d') : null;
    if (!context) {
      root.tradingViewFallbackCommandCount = 0;
      root.tradingViewFallbackRendered = false;
      return 0;
    }

    const width = chartSize.width;
    const height = chartSize.height;
    const padLeft = 44;
    const padRight = 18;
    const padTop = 24;
    const padBottom = 34;
    const plotWidth = Math.max(1, width - padLeft - padRight);
    const plotHeight = Math.max(1, height - padTop - padBottom);
    const visible = rows.slice(-72);
    const minPrice = Math.min(...visible.map(row => row.low));
    const maxPrice = Math.max(...visible.map(row => row.high));
    const priceSpan = Math.max(1, maxPrice - minPrice);
    const xStep = plotWidth / visible.length;
    const candleWidth = Math.max(3, xStep * 0.58);
    const priceY = value => padTop + (maxPrice - value) / priceSpan * plotHeight;

    const dark = typeof darkTheme === 'undefined' ? true : darkTheme;
    const background = dark ? '#101827' : '#f8fafc';
    const gridColor = dark ? 'rgba(148, 163, 184, 0.18)' : 'rgba(100, 116, 139, 0.20)';
    const labelColor = dark ? '#d1d5db' : '#334155';

    const drawLine = (points, color, lineWidth) => {
      if (!points || points.length < 2) {
        return;
      }

      context.strokeStyle = color;
      context.lineWidth = lineWidth;
      context.beginPath();
      points.forEach((point, index) => {
        const sourceIndex = visible.findIndex(row => row.time === point.time);
        if (sourceIndex < 0) {
          return;
        }

        const x = padLeft + sourceIndex * xStep + xStep / 2;
        const y = priceY(point.value);
        if (index === 0) {
          context.moveTo(x, y);
        } else {
          context.lineTo(x, y);
        }
      });
      context.stroke();
    };

    context.clearRect(0, 0, width, height);
    context.fillStyle = background;
    context.fillRect(0, 0, width, height);

    context.strokeStyle = gridColor;
    context.lineWidth = 1;
    for (let index = 0; index <= 4; index++) {
      const y = padTop + plotHeight * index / 4;
      context.beginPath();
      context.moveTo(padLeft, y);
      context.lineTo(width - padRight, y);
      context.stroke();
    }

    visible.forEach((row, index) => {
      const x = padLeft + index * xStep + xStep / 2;
      const openY = priceY(row.open);
      const closeY = priceY(row.close);
      const highY = priceY(row.high);
      const lowY = priceY(row.low);
      const rising = row.close >= row.open;
      const color = rising ? '#10b981' : '#ef4444';
      const top = Math.min(openY, closeY);
      const bodyHeight = Math.max(2, Math.abs(closeY - openY));

      context.strokeStyle = color;
      context.fillStyle = color;
      context.lineWidth = 1.5;
      context.beginPath();
      context.moveTo(x, highY);
      context.lineTo(x, lowY);
      context.stroke();
      context.fillRect(x - candleWidth / 2, top, candleWidth, bodyHeight);
    });

    const average = movingAverage(visible, 12);
    const slowAverage = exponentialMovingAverage(visible, 26);
    const bands = bollingerBands(visible, 20, 2);
    if (indicatorsVisible) {
      drawLine(bands.upper, '#a78bfa', 1.5);
      drawLine(bands.lower, '#a78bfa', 1.5);
      drawLine(average, '#38bdf8', 2);
      drawLine(slowAverage, '#f59e0b', 2);
    }

    context.fillStyle = labelColor;
    context.font = '13px Segoe UI';
    context.fillText(`TradingView Lightweight Charts data: ${visible[0].time} to ${visible[visible.length - 1].time}`, padLeft, height - 12);
    context.fillText(maxPrice.toFixed(2), 4, padTop + 4);
    context.fillText(minPrice.toFixed(2), 4, padTop + plotHeight);

    const commandCount = typeof context.CommandCount === 'number' ? context.CommandCount : 0;
    root.tradingViewFallbackCommandCount = commandCount;
    root.tradingViewFallbackRendered = true;
    return commandCount;
  }

  installBrowserShims();

  const chartHost = getRequiredElement('tradingViewChartHost');
  const webglSurface = getRequiredElement('tradingViewWebGlSurface');
  const streamButton = getRequiredElement('tradingViewStream');
  const shuffleButton = getRequiredElement('tradingViewShuffle');
  const resetButton = getRequiredElement('tradingViewReset');
  const status = getRequiredElement('tradingViewStatus');
  const webglStatus = getRequiredElement('tradingViewWebGlStatus');
  const zoomButton = getOptionalElement('tradingViewZoom');
  const indicatorsButton = getOptionalElement('tradingViewIndicators');
  const themeButton = getOptionalElement('tradingViewTheme');
  const quoteText = getOptionalElement('tradingViewQuote');
  const indicatorText = getOptionalElement('tradingViewIndicatorStatus');
  const LightweightCharts = loadLightweightCharts(status);
  const chartSize = readElementSize(chartHost, 720, 360);

  removeChildren(chartHost);

  const chart = LightweightCharts.createChart(chartHost, {
    width: chartSize.width,
    height: chartSize.height,
    autoSize: false,
    layout: {
      background: { type: 'solid', color: '#101827' },
      textColor: '#d1d5db',
      fontFamily: 'Segoe UI',
      attributionLogo: false
    },
    grid: {
      vertLines: { color: 'rgba(148, 163, 184, 0.14)' },
      horzLines: { color: 'rgba(148, 163, 184, 0.14)' }
    },
    crosshair: {
      mode: LightweightCharts.CrosshairMode?.Normal ?? 0
    },
    handleScroll: {
      mouseWheel: true,
      pressedMouseMove: true,
      horzTouchDrag: true,
      vertTouchDrag: false
    },
    handleScale: {
      axisPressedMouseMove: true,
      mouseWheel: true,
      pinch: true
    },
    rightPriceScale: {
      borderColor: '#334155',
      scaleMargins: { top: 0.08, bottom: 0.18 }
    },
    timeScale: {
      borderColor: '#334155',
      timeVisible: false,
      secondsVisible: false
    }
  });

  const candleOptions = {
    upColor: '#10b981',
    downColor: '#ef4444',
    wickUpColor: '#34d399',
    wickDownColor: '#f87171',
    borderVisible: false
  };
  const candleSeries = typeof chart.addCandlestickSeries === 'function'
    ? chart.addCandlestickSeries(candleOptions)
    : chart.addSeries(LightweightCharts.CandlestickSeries, candleOptions);

  const averageOptions = {
    color: '#38bdf8',
    lineWidth: 2,
    priceLineVisible: false,
    lastValueVisible: false
  };
  const averageSeries = typeof chart.addLineSeries === 'function'
    ? chart.addLineSeries(averageOptions)
    : chart.addSeries(LightweightCharts.LineSeries, averageOptions);

  const slowAverageOptions = {
    color: '#f59e0b',
    lineWidth: 2,
    priceLineVisible: false,
    lastValueVisible: false
  };
  const slowAverageSeries = typeof chart.addLineSeries === 'function'
    ? chart.addLineSeries(slowAverageOptions)
    : chart.addSeries(LightweightCharts.LineSeries, slowAverageOptions);

  const bandOptions = color => ({
    color,
    lineWidth: 1,
    lineStyle: LightweightCharts.LineStyle?.Dashed ?? 2,
    priceLineVisible: false,
    lastValueVisible: false
  });
  const upperBandSeries = typeof chart.addLineSeries === 'function'
    ? chart.addLineSeries(bandOptions('#a78bfa'))
    : chart.addSeries(LightweightCharts.LineSeries, bandOptions('#a78bfa'));
  const lowerBandSeries = typeof chart.addLineSeries === 'function'
    ? chart.addLineSeries(bandOptions('#a78bfa'))
    : chart.addSeries(LightweightCharts.LineSeries, bandOptions('#a78bfa'));

  const volumeOptions = {
    priceScaleId: '',
    priceFormat: { type: 'volume' },
    lastValueVisible: false,
    priceLineVisible: false,
    scaleMargins: { top: 0.78, bottom: 0 }
  };
  const volumeSeries = typeof chart.addHistogramSeries === 'function'
    ? chart.addHistogramSeries(volumeOptions)
    : chart.addSeries(LightweightCharts.HistogramSeries, volumeOptions);
  try {
    chart.priceScale('').applyOptions({ scaleMargins: { top: 0.78, bottom: 0 } });
  } catch (error) {
  }

  const webglRenderer = createWebGlRenderer(webglSurface);
  let rows = createMarketData();
  let streamIndex = 0;
  let indicatorsVisible = true;
  let darkTheme = true;
  let lastGeometry = null;
  let lastFallbackCommandCount = 0;

  function applyIndicatorVisibility() {
    const options = { visible: indicatorsVisible };
    averageSeries.applyOptions?.(options);
    slowAverageSeries.applyOptions?.(options);
    upperBandSeries.applyOptions?.(options);
    lowerBandSeries.applyOptions?.(options);
    volumeSeries.applyOptions?.(options);
    if (indicatorsButton) {
      indicatorsButton.textContent = indicatorsVisible ? 'Hide indicators' : 'Show indicators';
    }
  }

  function applyTheme() {
    const palette = darkTheme
      ? {
          background: '#101827',
          text: '#d1d5db',
          grid: 'rgba(148, 163, 184, 0.14)',
          border: '#334155'
        }
      : {
          background: '#f8fafc',
          text: '#334155',
          grid: 'rgba(100, 116, 139, 0.18)',
          border: '#cbd5e1'
        };

    chart.applyOptions?.({
      layout: {
        background: { type: 'solid', color: palette.background },
        textColor: palette.text,
        fontFamily: 'Segoe UI',
        attributionLogo: false
      },
      grid: {
        vertLines: { color: palette.grid },
        horzLines: { color: palette.grid }
      },
      rightPriceScale: {
        borderColor: palette.border,
        scaleMargins: { top: 0.08, bottom: 0.18 }
      },
      timeScale: {
        borderColor: palette.border,
        timeVisible: false,
        secondsVisible: false
      }
    });

    if (themeButton) {
      themeButton.textContent = darkTheme ? 'Light theme' : 'Dark theme';
    }
  }

  function updateSupplementalText(reason) {
    const last = rows[rows.length - 1];
    const previous = rows[rows.length - 2] ?? last;
    const change = last.close - previous.close;
    const changePercent = previous.close ? change / previous.close * 100 : 0;
    const smaFast = movingAverage(rows, 12).slice(-1)[0]?.value ?? last.close;
    const emaSlow = exponentialMovingAverage(rows, 26).slice(-1)[0]?.value ?? last.close;
    const bands = bollingerBands(rows, 20, 2);
    const bandHigh = bands.upper.slice(-1)[0]?.value ?? last.high;
    const bandLow = bands.lower.slice(-1)[0]?.value ?? last.low;
    const quote = `Last ${formatPrice(last.close)} (${change >= 0 ? '+' : ''}${formatPrice(change)}, ${changePercent >= 0 ? '+' : ''}${changePercent.toFixed(2)}%). Volume ${formatCompactNumber(last.volume)}. Range ${formatPrice(last.low)}-${formatPrice(last.high)}.`;
    const indicatorSummary = `SMA12 ${formatPrice(smaFast)} | EMA26 ${formatPrice(emaSlow)} | Bollinger ${formatPrice(bandLow)}-${formatPrice(bandHigh)} | ${rows.length} bars | ${reason}.`;

    setElementText(quoteText, quote);
    setElementText(indicatorText, indicatorSummary);
    root.tradingViewQuoteSummary = quote;
    root.tradingViewIndicatorSummary = indicatorSummary;
  }

  function publishDiagnostics(reason, geometry, fallbackCommandCount) {
    const chartStats = getChartCanvasStats(chartHost);
    const gl = webglRenderer.gl;
    const rendered = gl.DrawCallCount > 0 && gl.TriangleCount > 0;

    root.tradingViewChart = chart;
    root.tradingViewSeries = candleSeries;
    root.tradingViewWebGlContext = gl;
    root.tradingViewCanvasLayerCount = chartStats.canvasCount;
    root.tradingViewCanvasCommandCount = chartStats.commandCount;
    root.tradingViewFallbackCommandCount = fallbackCommandCount ?? root.tradingViewFallbackCommandCount ?? 0;
    root.tradingViewVisibleRange = '';
    root.tradingViewLogicalRange = '';
    root.tradingViewAutoSizeActive = false;
    try {
      root.tradingViewVisibleRange = JSON.stringify(chart.timeScale().getVisibleRange?.() ?? null);
      root.tradingViewLogicalRange = JSON.stringify(chart.timeScale().getVisibleLogicalRange?.() ?? null);
      root.tradingViewAutoSizeActive = typeof chart.autoSizeActive === 'function' ? chart.autoSizeActive() : false;
    } catch (error) {
      root.tradingViewVisibleRange = `error: ${error}`;
      root.tradingViewLogicalRange = `error: ${error}`;
    }
    root.tradingViewWebGlDrawCallCount = gl.DrawCallCount;
    root.tradingViewWebGlTriangleCount = gl.TriangleCount;
    root.tradingViewWebGlNativeSurface = String(gl.RenderBackend || '').indexOf('Avalonia OpenGL') === 0;
    root.tradingViewIndicatorsVisible = indicatorsVisible;
    root.tradingViewIndicatorSeriesCount = indicatorsVisible ? 5 : 0;
    root.tradingViewTheme = darkTheme ? 'dark' : 'light';
    root.tradingViewLastClose = rows[rows.length - 1]?.close ?? 0;

    const version = typeof LightweightCharts.version === 'function'
      ? LightweightCharts.version()
      : LightweightCharts.version || LightweightCharts.VERSION || '4.2.3';
    updateSupplementalText(reason);
    status.textContent = `TradingView Lightweight Charts ${version} ${reason}. API range: ${root.tradingViewVisibleRange}; indicators: ${indicatorsVisible ? 'visible' : 'hidden'}; library canvases: ${chartStats.canvasCount}; host chart: ${root.tradingViewFallbackRendered ? 'rendered' : 'missing'}.`;
    webglStatus.textContent = `WebGL ${rendered ? 'rendered' : 'queued'} ${geometry?.barCount ?? 0} volume bars. Backend: ${gl.RenderBackend}; draw calls: ${gl.DrawCallCount}; triangles: ${gl.TriangleCount}; buffer: ${gl.drawingBufferWidth}x${gl.drawingBufferHeight}; ${gl.LastDrawStatus}.`;
  }

  function renderAll(reason, fitContent = true) {
    candleSeries.setData(rows.map(row => ({
      time: toSeriesTime(row),
      open: row.open,
      high: row.high,
      low: row.low,
      close: row.close
    })));
    averageSeries.setData(movingAverage(rows, 12).map(row => ({
      time: toSeriesTime(row),
      value: row.value
    })));
    slowAverageSeries.setData(exponentialMovingAverage(rows, 26).map(row => ({
      time: toSeriesTime(row),
      value: row.value
    })));
    const bands = bollingerBands(rows, 20, 2);
    upperBandSeries.setData(bands.upper.map(row => ({
      time: toSeriesTime(row),
      value: row.value
    })));
    lowerBandSeries.setData(bands.lower.map(row => ({
      time: toSeriesTime(row),
      value: row.value
    })));
    volumeSeries.setData(createVolumeSeriesData(rows));
    if (typeof candleSeries.setMarkers === 'function') {
      candleSeries.setMarkers(createSignalMarkers(rows));
    }
    applyIndicatorVisibility();
    // Lightweight Charts skips same-size repaints; nudge once to paint in this hosted DOM.
    if (chartSize.height > 1) {
      chart.resize(chartSize.width, chartSize.height - 1, true);
    }
    chart.resize(chartSize.width, chartSize.height, true);
    if (fitContent) {
      chart.timeScale().fitContent();
    }
    lastFallbackCommandCount = renderFallbackChart(rows);
    lastGeometry = webglRenderer.render(rows);
    publishDiagnostics(reason, lastGeometry, lastFallbackCommandCount);
    win.requestAnimationFrame(() => publishDiagnostics(reason, lastGeometry, lastFallbackCommandCount));
  }

  function mutateLastBar() {
    const last = rows[rows.length - 1];
    const phase = streamIndex / 3;
    const nextClose = Math.max(100, last.close + Math.sin(phase) * 1.8 + 0.45);
    rows[rows.length - 1] = {
      ...last,
      high: Number(Math.max(last.high, nextClose + 0.9).toFixed(2)),
      low: Number(Math.min(last.low, nextClose - 0.7).toFixed(2)),
      close: Number(nextClose.toFixed(2)),
      volume: last.volume + 24000 + streamIndex * 3500
    };
    streamIndex++;
  }

  function appendNextBar() {
    const last = rows[rows.length - 1];
    const time = nextIsoDate(last.time);
    const open = last.close;
    const close = Number((open + Math.sin(streamIndex / 4) * 2.2 + 0.6).toFixed(2));
    rows.push({
      time,
      open,
      high: Number((Math.max(open, close) + 1.8).toFixed(2)),
      low: Number((Math.min(open, close) - 1.5).toFixed(2)),
      close,
      volume: 640000 + Math.round((Math.cos(streamIndex / 5) + 1.2) * 190000)
    });
    rows = rows.slice(-120);
    streamIndex++;
  }

  streamButton.addEventListener('click', () => {
    if (streamIndex % 3 === 2) {
      appendNextBar();
    } else {
      mutateLastBar();
    }

    renderAll('stream tick rendered', false);
  });

  shuffleButton.addEventListener('click', () => {
    rows = rows.map((row, index) => {
      const shift = Math.sin(index * 0.71 + streamIndex) * 1.3;
      const close = Number((row.close + shift).toFixed(2));
      return {
        ...row,
        close,
        high: Number(Math.max(row.high, close + 0.8).toFixed(2)),
        low: Number(Math.min(row.low, close - 0.8).toFixed(2)),
        volume: row.volume + Math.round(Math.abs(shift) * 85000)
      };
    });
    streamIndex++;
    renderAll('dataset shuffled', false);
  });

  resetButton.addEventListener('click', () => {
    rows = createMarketData();
    streamIndex = 0;
    renderAll('sample reset');
  });

  if (zoomButton) {
    zoomButton.addEventListener('click', () => {
      const from = Math.max(0, rows.length - 42);
      chart.timeScale().setVisibleLogicalRange?.({ from, to: rows.length + 3 });
      lastFallbackCommandCount = renderFallbackChart(rows);
      lastGeometry = webglRenderer.render(rows);
      publishDiagnostics('zoomed to recent bars', lastGeometry, lastFallbackCommandCount);
    });
  }

  if (indicatorsButton) {
    indicatorsButton.addEventListener('click', () => {
      indicatorsVisible = !indicatorsVisible;
      renderAll(indicatorsVisible ? 'indicators enabled' : 'indicators hidden', false);
    });
  }

  if (themeButton) {
    themeButton.addEventListener('click', () => {
      darkTheme = !darkTheme;
      applyTheme();
      renderAll(`${darkTheme ? 'dark' : 'light'} theme applied`, false);
    });
  }

  if (typeof chart.subscribeCrosshairMove === 'function') {
    chart.subscribeCrosshairMove(param => {
      if (!param?.time) {
        return;
      }

      const row = rows.find(item => toSeriesTime(item) === param.time);
      if (!row) {
        return;
      }

      const message = `TradingView crosshair ${row.time}: O ${row.open} H ${row.high} L ${row.low} C ${row.close}`;
      status.textContent = message;
      setElementText(quoteText, `${message}; V ${formatCompactNumber(row.volume)}`);
      root.tradingViewCrosshairSummary = message;
    });
  }

  applyTheme();
  renderAll('rendered');
})();
