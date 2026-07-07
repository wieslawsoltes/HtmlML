(function () {
  const LIGHTWEIGHT_CHARTS_URLS = [
    'https://cdn.jsdelivr.net/npm/lightweight-charts@4.2.3/dist/lightweight-charts.standalone.production.js',
    'https://cdn.jsdelivr.net/npm/lightweight-charts@5.2.0/dist/lightweight-charts.standalone.production.js'
  ];
  const root = typeof globalThis !== 'undefined' ? globalThis : window;
  const win = typeof window !== 'undefined' ? window : root;
  let loadedLightweightChartsUrl = '';

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
          this._callback([{
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
          }], this);
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

  function loadLightweightCharts(status) {
    let lastError = null;
    for (const url of LIGHTWEIGHT_CHARTS_URLS) {
      try {
        const module = require(url);
        const api = module?.createChart
          ? module
          : module?.default?.createChart
            ? module.default
            : root.LightweightCharts || win?.LightweightCharts;

        if (api?.createChart) {
          loadedLightweightChartsUrl = url;
          return api;
        }
      } catch (error) {
        lastError = error;
      }
    }

    const message = `Failed to load TradingView Lightweight Charts: ${lastError}`;
    setElementText(status, message);
    console.error(message);
    throw new Error(message);
  }

  function addSeries(chart, kind, options, LightweightCharts) {
    const constructors = {
      candlestick: LightweightCharts.CandlestickSeries,
      line: LightweightCharts.LineSeries,
      histogram: LightweightCharts.HistogramSeries,
      area: LightweightCharts.AreaSeries
    };
    const legacy = {
      candlestick: 'addCandlestickSeries',
      line: 'addLineSeries',
      histogram: 'addHistogramSeries',
      area: 'addAreaSeries'
    };

    if (typeof chart.addSeries === 'function' && constructors[kind]) {
      return chart.addSeries(constructors[kind], options);
    }

    if (typeof chart[legacy[kind]] === 'function') {
      return chart[legacy[kind]](options);
    }

    throw new Error(`Lightweight Charts ${kind} series API unavailable`);
  }

  function nextIsoDate(isoDate) {
    const date = new Date(`${isoDate}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + 1);
    return date.toISOString().slice(0, 10);
  }

  function toSeriesTime(row) {
    return Math.floor(new Date(`${row.time}T00:00:00Z`).getTime() / 1000);
  }

  function normalizeSymbol(symbol) {
    return String(symbol || '').trim().toUpperCase().replace(/[^A-Z0-9]/g, '') || 'NFLX';
  }

  function hashSymbol(symbol) {
    return normalizeSymbol(symbol).split('').reduce((sum, ch, index) => sum + ch.charCodeAt(0) * (index + 3), 0);
  }

  function formatPrice(value) {
    const number = Number(value || 0);
    return Math.abs(number) >= 1000 ? number.toLocaleString('en-US', { maximumFractionDigits: 2, minimumFractionDigits: 2 }) : number.toFixed(2);
  }

  function formatSigned(value) {
    const number = Number(value || 0);
    return `${number >= 0 ? '+' : ''}${formatPrice(number)}`;
  }

  function formatPercent(value) {
    const number = Number(value || 0);
    return `${number >= 0 ? '+' : ''}${number.toFixed(2)}%`;
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

  const symbolProfiles = {
    AAPL: { name: 'Apple Inc.', exchange: 'NASDAQ', base: 264, drift: -0.04, volatility: 3.8, volume: 51000000, seed: 1.1 },
    NFLX: { name: 'Netflix, Inc.', exchange: 'NASDAQ', base: 96, drift: 0.11, volatility: 2.5, volume: 20000000, seed: 2.4 },
    TSLA: { name: 'Tesla, Inc.', exchange: 'NASDAQ', base: 402, drift: -0.02, volatility: 5.6, volume: 73000000, seed: 4.2 },
    MSFT: { name: 'Microsoft Corp.', exchange: 'NASDAQ', base: 392, drift: -0.03, volatility: 4.2, volume: 27000000, seed: 3.3 },
    NVDA: { name: 'NVIDIA Corp.', exchange: 'NASDAQ', base: 177, drift: -0.08, volatility: 4.9, volume: 46000000, seed: 5.2 },
    BTCUSD: { name: 'Bitcoin / U.S. Dollar', exchange: 'Coinbase', base: 66042, drift: 16, volatility: 820, volume: 840000, seed: 6.7 },
    KO: { name: 'Coca-Cola Company', exchange: 'NYSE', base: 81, drift: 0.03, volatility: 1.2, volume: 14000000, seed: 7.4 },
    NKE: { name: 'Nike, Inc.', exchange: 'NYSE', base: 62, drift: -0.06, volatility: 1.8, volume: 12000000, seed: 8.4 },
    BABA: { name: 'Alibaba Group', exchange: 'NYSE', base: 144, drift: -0.12, volatility: 3.5, volume: 19000000, seed: 9.1 },
    INTC: { name: 'Intel Corp.', exchange: 'NASDAQ', base: 45, drift: 0.02, volatility: 1.6, volume: 39000000, seed: 10.2 },
    EBAY: { name: 'eBay Inc.', exchange: 'NASDAQ', base: 90, drift: 0.05, volatility: 2.1, volume: 8400000, seed: 11.2 },
    PYPL: { name: 'PayPal Holdings', exchange: 'NASDAQ', base: 46, drift: 0.04, volatility: 1.9, volume: 16000000, seed: 12.2 }
  };

  const watchSymbols = ['AAPL', 'NFLX', 'TSLA', 'MSFT', 'NVDA', 'BTCUSD'];
  const dataBySymbol = {};

  function getProfile(symbol) {
    const normalized = normalizeSymbol(symbol);
    if (symbolProfiles[normalized]) {
      return symbolProfiles[normalized];
    }

    const seed = hashSymbol(normalized);
    return {
      name: `${normalized} Holdings`,
      exchange: 'NASDAQ',
      base: 40 + seed % 420,
      drift: ((seed % 17) - 8) * 0.015,
      volatility: 1.6 + (seed % 40) / 10,
      volume: 7000000 + (seed % 45) * 1100000,
      seed: seed / 17
    };
  }

  function createSymbolData(symbol) {
    const normalized = normalizeSymbol(symbol);
    const profile = getProfile(normalized);
    const rows = [];
    let time = '2025-01-02';
    let close = profile.base;

    for (let index = 0; index < 220; index++) {
      const wave = Math.sin(index / 5 + profile.seed) * profile.volatility;
      const slower = Math.cos(index / 18 + profile.seed) * profile.volatility * 0.55;
      const impulse = normalized === 'NFLX' && index > 180 ? (index - 180) * 0.23 : 0;
      const open = close + Math.sin(index / 3 + profile.seed) * profile.volatility * 0.22;
      close = Math.max(1, open + wave * 0.24 + slower * 0.18 + profile.drift + impulse);
      const high = Math.max(open, close) + profile.volatility * (0.28 + Math.abs(Math.sin(index / 7)) * 0.35);
      const low = Math.min(open, close) - profile.volatility * (0.22 + Math.abs(Math.cos(index / 6)) * 0.28);
      const volume = Math.round(profile.volume * (0.72 + Math.abs(Math.sin(index / 9 + profile.seed)) * 0.48 + index * 0.0015));

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

  function getData(symbol) {
    const normalized = normalizeSymbol(symbol);
    if (!dataBySymbol[normalized]) {
      dataBySymbol[normalized] = createSymbolData(normalized);
    }
    return dataBySymbol[normalized];
  }

  function appendReplayBar(symbol) {
    const normalized = normalizeSymbol(symbol);
    const rows = getData(normalized);
    const profile = getProfile(normalized);
    const last = rows[rows.length - 1];
    const time = nextIsoDate(last.time);
    const open = last.close;
    const step = replayStep + rows.length;
    const impulse = Math.sin(step / 3 + profile.seed) * Math.max(0.18, open * 0.006);
    const close = Number((open + impulse + profile.drift).toFixed(2));
    rows.push({
      time,
      open,
      high: Number((Math.max(open, close) + Math.max(0.4, profile.volatility * 0.4)).toFixed(2)),
      low: Number((Math.min(open, close) - Math.max(0.4, profile.volatility * 0.35)).toFixed(2)),
      close,
      volume: Math.round(last.volume * (0.82 + Math.abs(Math.sin(step / 4)) * 0.44))
    });
    dataBySymbol[normalized] = rows.slice(-240);
  }

  function movingAverage(rows, length) {
    const result = [];
    for (let index = length - 1; index < rows.length; index++) {
      let sum = 0;
      for (let cursor = index - length + 1; cursor <= index; cursor++) {
        sum += rows[cursor].close;
      }
      result.push({ time: rows[index].time, value: Number((sum / length).toFixed(2)) });
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
        result.push({ time: row.time, value: Number(previous.toFixed(2)) });
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
      upper.push({ time: rows[index].time, value: Number((mean + deviation * multiplier).toFixed(2)) });
      lower.push({ time: rows[index].time, value: Number((mean - deviation * multiplier).toFixed(2)) });
    }
    return { upper, lower };
  }

  function vwapSeries(rows) {
    const result = [];
    let cumulativePriceVolume = 0;
    let cumulativeVolume = 0;
    rows.forEach(row => {
      const typical = (row.high + row.low + row.close) / 3;
      cumulativePriceVolume += typical * row.volume;
      cumulativeVolume += row.volume;
      result.push({ time: row.time, value: Number((cumulativePriceVolume / cumulativeVolume).toFixed(2)) });
    });
    return result;
  }

  function toLineData(rows) {
    return rows.map(row => ({ time: toSeriesTime(row), value: row.value }));
  }

  function createMarkers(rows) {
    const markers = [];
    for (let index = 44; index < rows.length; index += 54) {
      const row = rows[index];
      const prior = rows[index - 8] || rows[index - 1];
      const rising = row.close >= prior.close;
      markers.push({
        time: toSeriesTime(row),
        position: rising ? 'belowBar' : 'aboveBar',
        color: rising ? '#10b981' : '#f43f5e',
        shape: rising ? 'arrowUp' : 'arrowDown',
        text: rising ? 'long' : 'trim'
      });
    }
    return markers;
  }

  function createOrderBookText(rows) {
    const last = rows[rows.length - 1];
    const spread = Math.max(0.01, Math.abs(last.close) * 0.0008);
    const lines = [];
    for (let index = 3; index >= 1; index--) {
      lines.push(`Ask ${formatPrice(last.close + spread * index)}  ${formatCompactNumber(last.volume * (0.10 + index * 0.03))}`);
    }
    for (let index = 1; index <= 3; index++) {
      lines.push(`Bid ${formatPrice(last.close - spread * index)}  ${formatCompactNumber(last.volume * (0.18 - index * 0.025))}`);
    }
    return lines.join('\n');
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  function getVisibleWindow(panel) {
    const sourceRows = getData(panel.symbol);
    const bars = clamp(Math.round(panel.visibleBars || 110), 12, sourceRows.length);
    const to = clamp(Math.round(panel.visibleTo || sourceRows.length), bars, sourceRows.length);
    const from = Math.max(0, to - bars);
    const rows = sourceRows.slice(from, to);

    return {
      rows,
      from,
      to,
      bars
    };
  }

  function applyPanelVisibleRange(panel) {
    const visible = getVisibleWindow(panel);
    panel.visibleBars = visible.bars;
    panel.visibleTo = visible.to;
    try {
      panel.chart.timeScale().setVisibleLogicalRange?.({
        from: visible.from,
        to: visible.to + 2
      });
    } catch (error) {
    }
    return visible;
  }

  function drawFallbackPanel(panel, reason) {
    const context = typeof panel.host.getContext === 'function' ? panel.host.getContext('2d') : null;
    if (!context) {
      return 0;
    }

    const visible = getVisibleWindow(panel);
    const rows = visible.rows;
    const size = panel.size;
    const padLeft = 42;
    const padRight = 48;
    const padTop = 10;
    const padBottom = 24;
    const plotWidth = Math.max(1, size.width - padLeft - padRight);
    const plotHeight = Math.max(1, size.height - padTop - padBottom);
    const minPrice = Math.min(...rows.map(row => row.low));
    const maxPrice = Math.max(...rows.map(row => row.high));
    const priceSpan = Math.max(1, maxPrice - minPrice);
    const xStep = plotWidth / rows.length;
    const candleWidth = Math.max(3, xStep * 0.55);
    const priceY = value => padTop + (maxPrice - value) / priceSpan * plotHeight;

    context.clearRect(0, 0, size.width, size.height);
    context.fillStyle = '#08090b';
    context.fillRect(0, 0, size.width, size.height);
    context.strokeStyle = 'rgba(75, 85, 99, 0.22)';
    context.lineWidth = 1;
    for (let index = 0; index <= 4; index++) {
      const y = padTop + plotHeight * index / 4;
      context.beginPath();
      context.moveTo(padLeft, y);
      context.lineTo(size.width - padRight, y);
      context.stroke();
    }

    if (panel.style === 'heatmap') {
      const zoneTop = priceY(maxPrice - priceSpan * 0.22);
      const zoneMid = priceY(minPrice + priceSpan * 0.52);
      context.fillStyle = 'rgba(244, 63, 94, 0.24)';
      context.fillRect(padLeft, zoneTop, plotWidth, Math.max(10, zoneMid - zoneTop));
      context.fillStyle = 'rgba(20, 184, 166, 0.24)';
      context.fillRect(padLeft, zoneMid, plotWidth, Math.max(10, padTop + plotHeight - zoneMid));
      context.strokeStyle = 'rgba(96, 165, 250, 0.65)';
      for (let index = 0; index < 18; index++) {
        const offset = index * 8;
        context.beginPath();
        context.moveTo(padLeft + offset, padTop + plotHeight);
        context.lineTo(padLeft + plotWidth, padTop + 34 + index * 6);
        context.stroke();
      }
    }

    if (panel.style === 'bubble') {
      for (let index = 0; index < 4; index++) {
        const x = padLeft + plotWidth * (0.2 + index * 0.2);
        const y = padTop + plotHeight * (0.64 - Math.sin(index) * 0.08);
        const radius = 36 + index * 8;
        const gradient = context.createRadialGradient ? context.createRadialGradient(x, y, 4, x, y, radius) : null;
        if (gradient?.addColorStop) {
          gradient.addColorStop(0, 'rgba(244, 63, 94, 0.5)');
          gradient.addColorStop(1, 'rgba(79, 70, 229, 0.08)');
          context.fillStyle = gradient;
        } else {
          context.fillStyle = 'rgba(168, 85, 247, 0.25)';
        }
        context.beginPath();
        context.arc(x, y, radius, 0, Math.PI * 2);
        context.fill();
      }
    }

    const maxVolume = Math.max(1, ...rows.map(row => row.volume));
    rows.forEach((row, index) => {
      const x = padLeft + index * xStep + xStep / 2;
      const openY = priceY(row.open);
      const closeY = priceY(row.close);
      const highY = priceY(row.high);
      const lowY = priceY(row.low);
      const rising = row.close >= row.open;
      const color = rising ? '#00c2a0' : '#ff3864';
      const volumeHeight = Math.max(2, row.volume / maxVolume * 42);

      context.fillStyle = rising ? 'rgba(0, 194, 160, 0.32)' : 'rgba(255, 56, 100, 0.30)';
      context.fillRect(x - candleWidth / 2, size.height - padBottom - volumeHeight, candleWidth, volumeHeight);
      context.strokeStyle = color;
      context.fillStyle = color;
      context.lineWidth = 1.2;
      context.beginPath();
      context.moveTo(x, highY);
      context.lineTo(x, lowY);
      context.stroke();
      context.fillRect(x - candleWidth / 2, Math.min(openY, closeY), candleWidth, Math.max(2, Math.abs(closeY - openY)));
    });

    const drawLine = (points, color, width) => {
      if (!points || points.length < 2) {
        return;
      }
      context.strokeStyle = color;
      context.lineWidth = width;
      context.beginPath();
      let started = false;
      points.forEach(point => {
        const sourceIndex = rows.findIndex(row => row.time === point.time);
        if (sourceIndex < 0) {
          return;
        }
        const x = padLeft + sourceIndex * xStep + xStep / 2;
        const y = priceY(point.value);
        if (!started) {
          context.moveTo(x, y);
          started = true;
        } else {
          context.lineTo(x, y);
        }
      });
      context.stroke();
    };

    if (indicatorMode !== 'clean') {
      drawLine(exponentialMovingAverage(rows, 20), '#22d3ee', 2);
      drawLine(vwapSeries(rows), '#3b82f6', 1.5);
    }
    if (indicatorMode === 'all') {
      const bands = bollingerBands(rows, 20, 2);
      drawLine(bands.upper, '#14b8a6', 1);
      drawLine(bands.lower, '#14b8a6', 1);
    }

    if (panel.style === 'profile') {
      const buckets = 18;
      const profile = Array.from({ length: buckets }, () => 0);
      rows.forEach(row => {
        const mid = (row.high + row.low + row.close) / 3;
        const bucket = Math.max(0, Math.min(buckets - 1, Math.floor((mid - minPrice) / priceSpan * buckets)));
        profile[bucket] += row.volume;
      });
      const maxBucket = Math.max(1, ...profile);
      profile.forEach((volume, index) => {
        const y = padTop + plotHeight - (index + 1) * plotHeight / buckets;
        const width = volume / maxBucket * 88;
        context.fillStyle = index % 3 === 0 ? 'rgba(234, 179, 8, 0.82)' : 'rgba(37, 99, 235, 0.76)';
        context.fillRect(size.width - padRight - width, y + 2, width, Math.max(3, plotHeight / buckets - 3));
      });
      context.fillStyle = '#f8fafc';
      context.font = '11px Segoe UI';
      context.fillText('VAH', padLeft + plotWidth * 0.32, padTop + plotHeight * 0.30);
      context.fillText('POC', padLeft + plotWidth * 0.32, padTop + plotHeight * 0.50);
      context.fillText('VAL', padLeft + plotWidth * 0.32, padTop + plotHeight * 0.68);
    }

    const last = rows[rows.length - 1];
    context.strokeStyle = 'rgba(248, 250, 252, 0.55)';
    context.setLineDash?.([4, 5]);
    context.beginPath();
    context.moveTo(padLeft, priceY(last.close));
    context.lineTo(size.width - padRight, priceY(last.close));
    context.stroke();
    context.setLineDash?.([]);

    if (panel.hover) {
      const hoverX = clamp(panel.hover.x, padLeft, size.width - padRight);
      const hoverY = clamp(panel.hover.y, padTop, padTop + plotHeight);
      const hoverIndex = clamp(Math.floor((hoverX - padLeft) / Math.max(1, xStep)), 0, rows.length - 1);
      const hoverRow = rows[hoverIndex];
      context.strokeStyle = 'rgba(226, 232, 240, 0.55)';
      context.setLineDash?.([3, 4]);
      context.beginPath();
      context.moveTo(hoverX, padTop);
      context.lineTo(hoverX, padTop + plotHeight);
      context.moveTo(padLeft, hoverY);
      context.lineTo(size.width - padRight, hoverY);
      context.stroke();
      context.setLineDash?.([]);
      context.fillStyle = '#f8fafc';
      context.font = '11px Segoe UI';
      context.fillText(`${hoverRow.time}  O ${formatPrice(hoverRow.open)} H ${formatPrice(hoverRow.high)} L ${formatPrice(hoverRow.low)} C ${formatPrice(hoverRow.close)}`, padLeft + 8, padTop + 14);
    }

    panel.annotations.forEach((annotation, index) => {
      const x = padLeft + plotWidth * (0.18 + (index % 5) * 0.14);
      const y = padTop + plotHeight * (0.18 + (index % 3) * 0.18);
      context.strokeStyle = annotation.tool === 'trend' ? '#60a5fa' : '#f97316';
      context.fillStyle = context.strokeStyle;
      context.lineWidth = 1.8;
      context.beginPath();
      context.moveTo(x, y + 36);
      context.lineTo(x + 82, y);
      context.stroke();
      context.font = '11px Segoe UI';
      context.fillText(annotation.tool.toUpperCase(), x + 4, y + 48);
    });

    context.fillStyle = '#d1d5db';
    context.font = '11px Segoe UI';
    context.fillText(`${panel.symbol} ${timeframeKey} ${reason}`, padLeft, size.height - 8);
    context.fillStyle = last.close >= last.open ? '#00c2a0' : '#ff3864';
    context.fillText(formatPrice(last.close), size.width - padRight + 4, priceY(last.close) + 4);

    const rawCount = typeof context.CommandCount === 'number' ? context.CommandCount : 0;
    return Math.max(rawCount, rows.length * 8 + panel.annotations.length * 6 + 20);
  }

  function getChartCanvasStats() {
    const canvases = panels.flatMap(panel => collectCanvasElements(panel.host, []));
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
    return { canvasCount: canvases.length, commandCount };
  }

  function applySeriesVisibility(panel) {
    const showCore = indicatorMode !== 'clean';
    const showBands = indicatorMode === 'all';
    panel.emaSeries.applyOptions?.({ visible: showCore });
    panel.vwapSeries.applyOptions?.({ visible: showCore });
    panel.upperBandSeries.applyOptions?.({ visible: showBands && riskOverlayVisible });
    panel.lowerBandSeries.applyOptions?.({ visible: showBands && riskOverlayVisible });
    panel.volumeSeries.applyOptions?.({ visible: true });
  }

  function clearRiskLines(panel) {
    panel.riskLines.forEach(line => {
      try {
        panel.candleSeries.removePriceLine?.(line);
      } catch (error) {
      }
    });
    panel.riskLines = [];
  }

  function applyRiskLines(panel) {
    clearRiskLines(panel);
    if (!riskOverlayVisible || panel.symbol !== activeSymbol) {
      return;
    }
    const rows = getData(panel.symbol);
    const last = rows[rows.length - 1];
    const target = Number((last.close * 1.035).toFixed(2));
    const stop = Number((last.close * 0.982).toFixed(2));
    if (typeof panel.candleSeries.createPriceLine !== 'function') {
      return;
    }
    panel.riskLines.push(panel.candleSeries.createPriceLine({
      price: target,
      color: '#10b981',
      lineWidth: 2,
      lineStyle: LightweightCharts.LineStyle?.Dashed ?? 2,
      axisLabelVisible: true,
      title: 'Target'
    }));
    panel.riskLines.push(panel.candleSeries.createPriceLine({
      price: stop,
      color: '#f43f5e',
      lineWidth: 2,
      lineStyle: LightweightCharts.LineStyle?.Dashed ?? 2,
      axisLabelVisible: true,
      title: 'Stop'
    }));
  }

  function renderPanel(panel, reason, fitContent) {
    const rows = getData(panel.symbol);
    const candles = rows.map(row => ({
      time: toSeriesTime(row),
      open: row.open,
      high: row.high,
      low: row.low,
      close: row.close
    }));
    const bands = bollingerBands(rows, 20, 2);
    panel.candleSeries.setData(candles);
    panel.emaSeries.setData(toLineData(exponentialMovingAverage(rows, 20)));
    panel.vwapSeries.setData(toLineData(vwapSeries(rows)));
    panel.upperBandSeries.setData(toLineData(bands.upper));
    panel.lowerBandSeries.setData(toLineData(bands.lower));
    panel.volumeSeries.setData(rows.map(row => ({
      time: toSeriesTime(row),
      value: row.volume,
      color: row.close >= row.open ? 'rgba(0, 194, 160, 0.32)' : 'rgba(255, 56, 100, 0.30)'
    })));
    if (typeof panel.candleSeries.setMarkers === 'function') {
      panel.candleSeries.setMarkers(createMarkers(rows));
    }
    applySeriesVisibility(panel);
    applyRiskLines(panel);
    panel.chart.resize(panel.size.width, Math.max(1, panel.size.height - 1), true);
    panel.chart.resize(panel.size.width, panel.size.height, true);
    if (fitContent) {
      panel.visibleTo = rows.length;
      panel.chart.timeScale().fitContent();
    } else {
      applyPanelVisibleRange(panel);
    }
    panel.fallbackCommandCount = drawFallbackPanel(panel, reason);
  }

  function updatePanelHeader(panel) {
    const profile = getProfile(panel.symbol);
    const label = panel.style === 'profile'
      ? `${profile.name} - 30 - ${profile.exchange} - TPO`
      : panel.style === 'heatmap'
        ? `${profile.name} - 15 - ${profile.exchange}`
        : `${profile.name} - ${panel.interval} - ${profile.exchange}`;
    setElementText(panel.title, label);
  }

  function renderAll(reason, fitContent = false) {
    panels.forEach(panel => {
      updatePanelHeader(panel);
      renderPanel(panel, reason, fitContent);
    });
    updateWatchlist();
    updateDetails(reason);
    publishDiagnostics(reason);
    win.requestAnimationFrame(() => publishDiagnostics(reason));
  }

  function applyTimeframeToPanel(panel, key) {
    const barsByKey = {
      '1D': 36,
      '5D': 70,
      '1M': 110,
      '6M': 160,
      YTD: 190,
      All: 230
    };
    const rows = getData(panel.symbol);
    const bars = Math.min(rows.length, barsByKey[key] || 110);
    panel.visibleBars = bars;
    panel.visibleTo = rows.length;
    applyPanelVisibleRange(panel);
  }

  function setTimeframe(key) {
    timeframeKey = key;
    panels.forEach(panel => applyTimeframeToPanel(panel, key));
    renderAll(`timeframe ${key}`, false);
    root.professionalTradingViewInputHandled = true;
  }

  function setIndicatorMode() {
    const modes = ['all', 'core', 'clean'];
    const index = modes.indexOf(indicatorMode);
    indicatorMode = modes[(index + 1) % modes.length];
    if (indicatorToggleButton) {
      indicatorToggleButton.textContent = indicatorMode === 'all'
        ? 'Indicators'
        : indicatorMode === 'core'
          ? 'Core only'
          : 'Clean';
    }
    renderAll(`indicator mode ${indicatorMode}`, false);
    root.professionalTradingViewInputHandled = true;
  }

  function setTool(tool) {
    activeTool = tool;
    setElementText(statusText, `Selected ${tool} tool. Click a chart panel to place an annotation.`);
    root.professionalTradingViewActiveTool = activeTool;
    root.professionalTradingViewInputHandled = true;
  }

  function addAnnotation(panel) {
    panel.annotations.push({ tool: activeTool, time: Date.now() });
    drawingCount++;
    renderPanel(panel, `${activeTool} annotation`, false);
    publishDiagnostics(`${activeTool} annotation`);
    root.professionalTradingViewDrawingCount = drawingCount;
    root.professionalTradingViewInputHandled = true;
  }

  function readPanelPoint(panel, evt) {
    const x = Number(evt?.offsetX ?? evt?.x ?? evt?.clientX ?? 0);
    const y = Number(evt?.offsetY ?? evt?.y ?? evt?.clientY ?? 0);
    return {
      x: Number.isFinite(x) ? clamp(x, 0, panel.size.width) : 0,
      y: Number.isFinite(y) ? clamp(y, 0, panel.size.height) : 0
    };
  }

  function repaintPanelInteraction(panel, reason) {
    applyPanelVisibleRange(panel);
    panel.fallbackCommandCount = drawFallbackPanel(panel, reason);
    updateDetails(reason);
    publishDiagnostics(reason);
  }

  function updatePanelHover(panel, evt) {
    panel.hover = readPanelPoint(panel, evt);
    panel.fallbackCommandCount = drawFallbackPanel(panel, 'crosshair');
    root.professionalTradingViewMouseMoveCount = (root.professionalTradingViewMouseMoveCount || 0) + 1;
    root.professionalTradingViewInputHandled = true;
  }

  function panPanel(panel, dx) {
    const rows = getData(panel.symbol);
    const barsPerPixel = panel.visibleBars / Math.max(1, panel.size.width);
    const startTo = panel.dragStartVisibleTo || panel.visibleTo || rows.length;
    panel.visibleTo = clamp(startTo - dx * barsPerPixel, panel.visibleBars, rows.length);
    root.professionalTradingViewPanCount = (root.professionalTradingViewPanCount || 0) + 1;
    root.professionalTradingViewInputHandled = true;
    repaintPanelInteraction(panel, 'panned');
  }

  function zoomPanel(panel, evt) {
    const rows = getData(panel.symbol);
    const point = readPanelPoint(panel, evt);
    const visible = getVisibleWindow(panel);
    const anchorRatio = clamp(point.x / Math.max(1, panel.size.width), 0, 1);
    const anchorLogical = visible.from + anchorRatio * visible.bars;
    const deltaY = Number(evt?.deltaY ?? 0);
    const factor = deltaY < 0 ? 0.82 : 1.18;
    const nextBars = clamp(Math.round(visible.bars * factor), 12, rows.length);
    const nextFrom = anchorLogical - anchorRatio * nextBars;
    panel.visibleBars = nextBars;
    panel.visibleTo = clamp(nextFrom + nextBars, nextBars, rows.length);
    root.professionalTradingViewZoomCount = (root.professionalTradingViewZoomCount || 0) + 1;
    root.professionalTradingViewInputHandled = true;
    evt?.preventDefault?.();
    repaintPanelInteraction(panel, deltaY < 0 ? 'zoomed in' : 'zoomed out');
  }

  function installPanelInteractions(panel) {
    panel.host.tabIndex = 0;
    panel.host.addEventListener('pointerdown', evt => {
      if (evt.button !== 0) {
        return;
      }

      const point = readPanelPoint(panel, evt);
      const rows = getData(panel.symbol);
      panel.isDragging = true;
      panel.dragMoved = false;
      panel.dragStartX = point.x;
      panel.dragStartY = point.y;
      panel.dragStartVisibleTo = panel.visibleTo || rows.length;
      panel.host.focus?.();
      evt.preventDefault?.();
      root.professionalTradingViewPointerActive = true;
      root.professionalTradingViewInputHandled = true;
    });

    panel.host.addEventListener('pointermove', evt => {
      const point = readPanelPoint(panel, evt);
      panel.hover = point;
      if (panel.isDragging) {
        const dx = point.x - panel.dragStartX;
        const dy = point.y - panel.dragStartY;
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) {
          panel.dragMoved = true;
          panPanel(panel, dx);
        }
        evt.preventDefault?.();
        return;
      }

      updatePanelHover(panel, evt);
    });

    panel.host.addEventListener('pointerup', evt => {
      if (!panel.isDragging) {
        return;
      }

      const moved = panel.dragMoved;
      panel.isDragging = false;
      panel.dragStartVisibleTo = 0;
      root.professionalTradingViewPointerActive = false;
      evt.preventDefault?.();
      if (!moved && activeTool !== 'crosshair' && activeTool !== 'magnet') {
        addAnnotation(panel);
      } else {
        updatePanelHover(panel, evt);
      }
    });

    panel.host.addEventListener('pointerleave', () => {
      panel.isDragging = false;
      panel.hover = null;
      root.professionalTradingViewPointerActive = false;
      panel.fallbackCommandCount = drawFallbackPanel(panel, 'pointer left');
      publishDiagnostics('pointer left');
    });

    panel.host.addEventListener('wheel', evt => {
      zoomPanel(panel, evt);
    });

    panel.host.addEventListener('dblclick', evt => {
      panel.visibleTo = getData(panel.symbol).length;
      applyTimeframeToPanel(panel, timeframeKey);
      panel.fallbackCommandCount = drawFallbackPanel(panel, 'view reset');
      publishDiagnostics('view reset');
      evt.preventDefault?.();
    });
  }

  function selectSymbol(symbol, reason) {
    activeSymbol = normalizeSymbol(symbol);
    if (symbolInput) {
      symbolInput.value = activeSymbol;
    }
    mainPanel.symbol = activeSymbol;
    renderAll(reason || `${activeSymbol} selected`, true);
    root.professionalTradingViewSelectedSymbol = activeSymbol;
    root.professionalTradingViewInputHandled = true;
  }

  function updateWatchlist() {
    watchSymbols.forEach(symbol => {
      const button = watchButtons[symbol];
      if (!button) {
        return;
      }
      const rows = getData(symbol);
      const last = rows[rows.length - 1];
      const previous = rows[rows.length - 2] || last;
      const change = last.close - previous.close;
      const percent = previous.close ? change / previous.close * 100 : 0;
      button.textContent = `${symbol.padEnd(7)} ${formatPrice(last.close).padStart(9)} ${formatSigned(change).padStart(8)} ${formatPercent(percent).padStart(8)}`;
      button.foreground = symbol === activeSymbol ? '#f8fafc' : (change >= 0 ? '#14b8a6' : '#f43f5e');
    });
  }

  function updateDetails(reason) {
    const rows = getData(activeSymbol);
    const profile = getProfile(activeSymbol);
    const last = rows[rows.length - 1];
    const previous = rows[rows.length - 2] || last;
    const change = last.close - previous.close;
    const percent = previous.close ? change / previous.close * 100 : 0;
    const min52 = Math.min(...rows.map(row => row.low));
    const max52 = Math.max(...rows.map(row => row.high));
    const vwap = vwapSeries(rows).slice(-1)[0]?.value ?? last.close;
    const atrProxy = rows.slice(-20).reduce((sum, row) => sum + row.high - row.low, 0) / 20;
    const target = last.close * 1.035;
    const stop = last.close * 0.982;

    setElementText(quoteText, `${activeSymbol} ${formatPrice(last.close)} USD  ${formatSigned(change)} ${formatPercent(percent)}`);
    setElementText(executionText, `${profile.name} - ${profile.exchange}\nMarket closed\nDay range ${formatPrice(last.low)} - ${formatPrice(last.high)}\n52W range ${formatPrice(min52)} - ${formatPrice(max52)}\nVWAP ${formatPrice(vwap)} | ATR ${formatPrice(atrProxy)}`);
    setElementText(orderBookText, createOrderBookText(rows));
    setElementText(riskStatus, riskOverlayVisible
      ? `Risk overlay on. Target ${formatPrice(target)} / stop ${formatPrice(stop)}.`
      : 'Risk overlay off. Price lines and bands are hidden.');
    setElementText(statusText, `TradingView Lightweight Charts workspace ${reason}. Input: symbol search, watchlist, timeframe, tools, replay, risk, indicators.`);
    setElementText(clockText, `${new Date().toISOString().slice(11, 19)} UTC   RTH   ADJ`);
  }

  function publishDiagnostics(reason) {
    const stats = getChartCanvasStats();
    const fallbackCount = panels.reduce((sum, panel) => sum + (panel.fallbackCommandCount || 0), 0);
    let visibleRange = '';
    try {
      const range = mainPanel.chart.timeScale().getVisibleRange?.() || {
        from: toSeriesTime(getData(mainPanel.symbol)[0]),
        to: toSeriesTime(getData(mainPanel.symbol).slice(-1)[0])
      };
      visibleRange = JSON.stringify(range);
    } catch (error) {
      visibleRange = `error: ${error}`;
    }

    root.professionalTradingViewCharts = panels.map(panel => panel.chart);
    root.professionalTradingViewPriceChart = mainPanel.chart;
    root.professionalTradingViewSelectedSymbol = activeSymbol;
    root.professionalTradingViewLastSymbol = activeSymbol;
    root.professionalTradingViewTimeframe = timeframeKey;
    root.professionalTradingViewActiveTool = activeTool;
    root.professionalTradingViewIndicatorMode = indicatorMode;
    root.professionalTradingViewRiskOverlayVisible = riskOverlayVisible;
    root.professionalTradingViewReplayStep = replayStep;
    root.professionalTradingViewDrawingCount = drawingCount;
    root.professionalTradingViewPanCount = root.professionalTradingViewPanCount || 0;
    root.professionalTradingViewZoomCount = root.professionalTradingViewZoomCount || 0;
    root.professionalTradingViewMouseMoveCount = root.professionalTradingViewMouseMoveCount || 0;
    root.professionalTradingViewPointerActive = !!root.professionalTradingViewPointerActive;
    root.professionalTradingViewChartCount = panels.length;
    root.professionalTradingViewSeriesCount = panels.length * 6;
    root.professionalTradingViewChartLayerCount = stats.canvasCount;
    root.professionalTradingViewCanvasCommandCount = stats.commandCount;
    root.professionalTradingViewFallbackCommandCount = fallbackCount;
    root.professionalTradingViewVisibleRange = visibleRange;
    root.professionalTradingViewLibraryUrl = loadedLightweightChartsUrl;
    root.professionalTradingViewLibraryVersion = typeof LightweightCharts.version === 'function'
      ? LightweightCharts.version()
      : LightweightCharts.version || LightweightCharts.VERSION || 'unknown';
    root.professionalTradingViewStatus = reason;
  }

  installBrowserShims();

  const statusText = getRequiredElement('professionalStatus');
  const quoteText = getRequiredElement('professionalQuote');
  const orderBookText = getRequiredElement('professionalOrderBook');
  const executionText = getRequiredElement('professionalExecution');
  const riskStatus = getRequiredElement('professionalRiskStatus');
  const clockText = getRequiredElement('professionalClock');
  const symbolInput = getRequiredElement('professionalSymbolInput');
  const symbolApplyButton = getRequiredElement('professionalSymbolApply');
  const replayButton = getRequiredElement('professionalReplay');
  const riskButton = getRequiredElement('professionalRisk');
  const indicatorToggleButton = getRequiredElement('professionalIndicatorToggle');
  const undoButton = getRequiredElement('professionalUndo');
  const LightweightCharts = loadLightweightCharts(statusText);

  const panelSpecs = [
    { key: 'aapl', hostId: 'professionalChartAaplHost', titleId: 'professionalPanelAaplTitle', symbol: 'AAPL', interval: '30', style: 'profile' },
    { key: 'main', hostId: 'professionalPriceChartHost', titleId: 'professionalPanelMainTitle', symbol: 'NFLX', interval: '1h', style: 'standard' },
    { key: 'btc', hostId: 'professionalChartBtcHost', titleId: 'professionalPanelBtcTitle', symbol: 'BTCUSD', interval: '15', style: 'heatmap' },
    { key: 'tsla', hostId: 'professionalChartTslaHost', titleId: 'professionalPanelTslaTitle', symbol: 'TSLA', interval: '1', style: 'bubble' }
  ];
  let activeSymbol = 'NFLX';
  let timeframeKey = '1D';
  let indicatorMode = 'all';
  let riskOverlayVisible = true;
  let activeTool = 'crosshair';
  let replayStep = 0;
  let drawingCount = 0;
  let mainPanel;

  const panels = panelSpecs.map(spec => {
    const host = getRequiredElement(spec.hostId);
    removeChildren(host);
    const size = readElementSize(host, 450, 270);
    const chart = LightweightCharts.createChart(host, {
      width: size.width,
      height: size.height,
      autoSize: false,
      layout: {
        background: { type: 'solid', color: '#08090b' },
        textColor: '#9ca3af',
        fontFamily: 'Segoe UI',
        attributionLogo: false
      },
      grid: {
        vertLines: { color: 'rgba(75, 85, 99, 0.16)' },
        horzLines: { color: 'rgba(75, 85, 99, 0.18)' }
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
        borderColor: '#2a2d33',
        scaleMargins: { top: 0.08, bottom: 0.2 }
      },
      timeScale: {
        borderColor: '#2a2d33',
        timeVisible: false,
        secondsVisible: false
      }
    });

    const panel = {
      ...spec,
      host,
      title: getRequiredElement(spec.titleId),
      chart,
      size,
      visibleBars: 110,
      visibleTo: 0,
      hover: null,
      isDragging: false,
      dragMoved: false,
      dragStartX: 0,
      dragStartY: 0,
      dragStartVisibleTo: 0,
      annotations: [],
      riskLines: [],
      fallbackCommandCount: 0,
      candleSeries: addSeries(chart, 'candlestick', {
        upColor: '#00c2a0',
        downColor: '#ff3864',
        wickUpColor: '#00c2a0',
        wickDownColor: '#ff3864',
        borderVisible: false
      }, LightweightCharts),
      emaSeries: null,
      vwapSeries: null,
      upperBandSeries: null,
      lowerBandSeries: null,
      volumeSeries: null
    };
    panel.emaSeries = addSeries(chart, 'line', { color: '#22d3ee', lineWidth: 2, priceLineVisible: false, lastValueVisible: false }, LightweightCharts);
    panel.vwapSeries = addSeries(chart, 'line', { color: '#3b82f6', lineWidth: 2, priceLineVisible: false, lastValueVisible: false }, LightweightCharts);
    panel.upperBandSeries = addSeries(chart, 'line', { color: '#14b8a6', lineWidth: 1, priceLineVisible: false, lastValueVisible: false }, LightweightCharts);
    panel.lowerBandSeries = addSeries(chart, 'line', { color: '#14b8a6', lineWidth: 1, priceLineVisible: false, lastValueVisible: false }, LightweightCharts);
    panel.volumeSeries = addSeries(chart, 'histogram', {
      priceScaleId: '',
      priceFormat: { type: 'volume' },
      lastValueVisible: false,
      priceLineVisible: false,
      scaleMargins: { top: 0.78, bottom: 0 }
    }, LightweightCharts);
    try {
      chart.priceScale('').applyOptions({ scaleMargins: { top: 0.78, bottom: 0 } });
    } catch (error) {
    }

    installPanelInteractions(panel);
    if (panel.key === 'main') {
      mainPanel = panel;
    }
    return panel;
  });

  const watchButtons = {
    AAPL: getRequiredElement('professionalWatchAapl'),
    NFLX: getRequiredElement('professionalWatchNflx'),
    TSLA: getRequiredElement('professionalWatchTsla'),
    MSFT: getRequiredElement('professionalWatchMsft'),
    NVDA: getRequiredElement('professionalWatchNvda'),
    BTCUSD: getRequiredElement('professionalWatchBtc')
  };

  Object.keys(watchButtons).forEach(symbol => {
    watchButtons[symbol].addEventListener('click', () => selectSymbol(symbol, `watchlist ${symbol}`));
  });

  symbolApplyButton.addEventListener('click', () => selectSymbol(symbolInput.value, `symbol ${normalizeSymbol(symbolInput.value)} loaded`));
  symbolInput.addEventListener('keydown', evt => {
    if (evt.key === 'Enter') {
      evt.preventDefault?.();
      selectSymbol(symbolInput.value, `symbol ${normalizeSymbol(symbolInput.value)} loaded from keyboard`);
    }
  });

  replayButton.addEventListener('click', () => {
    appendReplayBar(activeSymbol);
    replayStep++;
    renderAll('replay advanced one bar', false);
    root.professionalTradingViewInputHandled = true;
  });

  riskButton.addEventListener('click', () => {
    riskOverlayVisible = !riskOverlayVisible;
    riskButton.textContent = riskOverlayVisible ? 'Risk' : 'Risk off';
    renderAll(riskOverlayVisible ? 'risk overlays enabled' : 'risk overlays hidden', false);
    root.professionalTradingViewInputHandled = true;
  });

  indicatorToggleButton.addEventListener('click', setIndicatorMode);
  undoButton.addEventListener('click', () => {
    const panelWithAnnotation = panels.slice().reverse().find(panel => panel.annotations.length > 0);
    if (panelWithAnnotation) {
      panelWithAnnotation.annotations.pop();
      drawingCount = Math.max(0, drawingCount - 1);
      renderPanel(panelWithAnnotation, 'annotation undone', false);
      publishDiagnostics('annotation undone');
    }
    root.professionalTradingViewInputHandled = true;
  });

  [
    ['professionalTf1H', '1D'],
    ['professionalTf1D', '1D'],
    ['professionalTf5D', '5D'],
    ['professionalTf1M', '1M'],
    ['professionalTf6M', '6M'],
    ['professionalTfYtd', 'YTD'],
    ['professionalTfAll', 'All']
  ].forEach(([id, key]) => {
    getOptionalElement(id)?.addEventListener('click', () => setTimeframe(key));
  });

  [
    ['professionalToolCross', 'crosshair'],
    ['professionalToolTrend', 'trend'],
    ['professionalToolFib', 'fib'],
    ['professionalToolBrush', 'brush'],
    ['professionalToolText', 'text'],
    ['professionalToolMeasure', 'measure'],
    ['professionalToolMagnet', 'magnet'],
    ['professionalToolTrash', 'trash']
  ].forEach(([id, tool]) => {
    getOptionalElement(id)?.addEventListener('click', () => {
      if (tool === 'trash') {
        panels.forEach(panel => { panel.annotations = []; });
        drawingCount = 0;
        renderAll('annotations cleared', false);
        return;
      }
      setTool(tool);
    });
  });

  ['professionalAlert', 'professionalCandleMode', 'professionalLayout', 'professionalScreenshot', 'professionalTrade', 'professionalPublish'].forEach(id => {
    getOptionalElement(id)?.addEventListener('click', () => {
      const label = id.replace('professional', '').toLowerCase();
      setElementText(statusText, `${label} command handled in the sample shell.`);
      root.professionalTradingViewInputHandled = true;
      root.professionalTradingViewLastCommand = label;
    });
  });

  renderAll('ready', true);
})();
