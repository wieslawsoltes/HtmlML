declare function require(path: string): any;

export type RoughPalette = readonly [string, string, string];

export interface CanvasContext2DLike {
  clearRect(x: number, y: number, width: number, height: number): void;
  fillRect(x: number, y: number, width: number, height: number): void;
  beginPath(): void;
  moveTo(x: number, y: number): void;
  lineTo(x: number, y: number): void;
  stroke(): void;
  fill(): void;
  arc(x: number, y: number, radius: number, startAngle: number, endAngle: number, counterClockwise?: boolean): void;
  fillStyle: string;
  strokeStyle: string;
  lineWidth: number;
  font?: string;
  fillText?(text: string, x: number, y: number): void;
}

export interface CanvasSurfaceLike {
  readonly offsetWidth: number;
  readonly offsetHeight: number;
  getContext(type: '2d'): CanvasContext2DLike | null;
}

export interface RoughCanvasOptions {
  roughness?: number;
  strokeWidth?: number;
  stroke?: string;
  fill?: string;
  fillStyle?: string;
  bowing?: number;
}

export interface RoughCanvas {
  rectangle(x: number, y: number, width: number, height: number, options?: RoughCanvasOptions): void;
  circle(x: number, y: number, diameter: number, options?: RoughCanvasOptions): void;
  linearPath(points: Array<[number, number]>, options?: RoughCanvasOptions): void;
}

export interface RoughSketchConfig {
  surface: CanvasSurfaceLike;
  notify?: (message: string) => void;
  palettes?: readonly RoughPalette[];
  initialRoughness?: number;
}

export interface RoughSketchController {
  render(): void;
  start(): void;
  stop(): void;
  cyclePalette(): RoughPalette;
  setRoughness(value: number): void;
}

const defaultPalettes: readonly RoughPalette[] = [
  ['#fef3c7', '#f97316', '#0f172a'],
  ['#dcfce7', '#22c55e', '#065f46'],
  ['#e0f2fe', '#3b82f6', '#1e3a8a'],
  ['#fce7f3', '#ec4899', '#831843'],
  ['#ede9fe', '#8b5cf6', '#4c1d95']
];

interface RoughModuleShim {
  canvas(surface: CanvasSurfaceLike): RoughCanvas;
}

const roughModuleCandidate: RoughModuleShim & { default?: RoughModuleShim } = require('https://cdn.jsdelivr.net/npm/roughjs@4.6.6/bundled/rough.cjs.js');

const roughFactory: RoughModuleShim = (() => {
  if (typeof roughModuleCandidate.canvas === 'function') {
    return roughModuleCandidate;
  }

  if (roughModuleCandidate.default && typeof roughModuleCandidate.default.canvas === 'function') {
    return roughModuleCandidate.default;
  }

  throw new Error('rough.js canvas factory not found');
})();

class RoughSketch implements RoughSketchController {
  private readonly surface: CanvasSurfaceLike;
  private readonly notify: (message: string) => void;
  private readonly palettes: readonly RoughPalette[];
  private paletteIndex = 0;
  private roughness: number;
  private frameHandle = 0;
  private startTime = 0;

  constructor(config: RoughSketchConfig) {
    this.surface = config.surface;
    this.notify = config.notify ?? (() => {});
    this.palettes = config.palettes ?? defaultPalettes;
    this.roughness = config.initialRoughness ?? 1.0;
  }

  public render(): void {
    this.drawScene(0);
  }

  public start(): void {
    if (this.frameHandle) {
      return;
    }

    this.startTime = Date.now();
    const tick = () => {
      const now = Date.now();
      const elapsed = (now - this.startTime) / 1000;
      this.drawScene(elapsed);
      this.frameHandle = (globalThis.requestAnimationFrame ?? setTimeout)(tick, 16) as unknown as number;
    };

    this.notify('Animating rough.js scene (TypeScript)');
    tick();
  }

  public stop(): void {
    if (!this.frameHandle) {
      return;
    }

    const cancel = globalThis.cancelAnimationFrame ?? clearTimeout;
    cancel(this.frameHandle);
    this.frameHandle = 0;
    this.notify('Animation stopped');
  }

  public cyclePalette(): RoughPalette {
    this.paletteIndex = (this.paletteIndex + 1) % this.palettes.length;
    const palette = this.palettes[this.paletteIndex];
    this.drawScene(0);
    this.notify(`Palette switched to ${palette.join(', ')}`);
    return palette;
  }

  public setRoughness(value: number): void {
    this.roughness = Number.isFinite(value) ? value : this.roughness;
    this.drawScene(0);
    this.notify(`Roughness set to ${this.roughness.toFixed(2)}`);
  }

  private drawScene(elapsedSeconds: number): void {
    const ctx = this.surface.getContext('2d');
    if (!ctx) {
      throw new Error('CanvasRenderingContext2D unavailable');
    }

    const w = this.surface.offsetWidth;
    const h = this.surface.offsetHeight;

    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#f8fafc';
    ctx.fillRect(0, 0, w, h);

    const palette = this.palettes[this.paletteIndex];
    const [fill, accent, stroke] = palette;

    const roughCanvas = roughFactory.canvas(this.surface);
    const baseOptions: RoughCanvasOptions = {
      roughness: this.roughness,
      strokeWidth: 2,
      stroke
    };

    roughCanvas.rectangle(32, 36, w * 0.32, h * 0.38, {
      ...baseOptions,
      fill,
      fillStyle: 'hachure'
    });

    const angle = elapsedSeconds;
    const cx = w * 0.65;
    const cy = h * 0.42;
    const radius = Math.min(w, h) * 0.18;
    const orbitRadius = radius * 0.82;

    roughCanvas.circle(cx, cy, radius * 2, {
      ...baseOptions,
      fill: accent,
      fillStyle: 'zigzag'
    });

    const orbitX = cx + Math.cos(angle) * orbitRadius;
    const orbitY = cy + Math.sin(angle) * orbitRadius;
    roughCanvas.circle(orbitX, orbitY, radius * 0.65, {
      ...baseOptions,
      fillStyle: 'cross-hatch',
      fill
    });

    const wavePoints = this.createWavePoints(w, h, elapsedSeconds);
    roughCanvas.linearPath(wavePoints, {
      ...baseOptions,
      bowing: 1.1,
      stroke: accent
    });

    ctx.lineWidth = 3;
    ctx.strokeStyle = stroke;
    ctx.beginPath();
    ctx.moveTo(36, h - 48);
    ctx.lineTo(w - 36, h - 48);
    ctx.stroke();

    if (ctx.fillText) {
      ctx.font = '18px Segoe UI';
      ctx.fillStyle = stroke;
      ctx.fillText('TypeScript + rough.js rendered inside Avalonia', 42, h - 20);
    }
  }

  private createWavePoints(width: number, height: number, time: number): Array<[number, number]> {
    const result: Array<[number, number]> = [];
    const segments = 32;
    const baseY = height * 0.78;
    const amplitude = Math.max(12, height * 0.08);
    const frequency = 0.75;
    for (let i = 0; i <= segments; i++) {
      const ratio = i / segments;
      const x = 36 + (width - 72) * ratio;
      const y = baseY + Math.sin((ratio * Math.PI * 2 * frequency) + time) * amplitude;
      result.push([x, y]);
    }
    return result;
  }
}

export function createRoughSketch(config: RoughSketchConfig): RoughSketchController {
  return new RoughSketch(config);
}
