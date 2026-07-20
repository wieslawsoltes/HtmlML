import React, { useEffect, useRef, useState } from 'react';
import { Badge, Card, SampleHeader, buttonStyle, colors, createSampleLifecycle, inputStyle, rowStyle, shellStyle } from '../../shared/ui';

function ReferenceCanvas(): React.ReactNode {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const graphics = ref.current?.getContext('2d');
    if (!graphics) return;
    graphics.fillStyle = '#0f172a';
    graphics.fillRect(0, 0, 240, 90);
    graphics.fillStyle = '#38bdf8';
    graphics.fillRect(12, 12, 96, 66);
    graphics.fillStyle = '#f8fafc';
    graphics.font = '16px sans-serif';
    graphics.fillText('REF-01', 126, 50);
  }, []);
  return <canvas ref={ref} width={240} height={90} aria-label="Deterministic reference canvas" />;
}

function App(): React.ReactNode {
  const [pointerCount, setPointerCount] = useState(0);
  const [keyCount, setKeyCount] = useState(0);
  const [value, setValue] = useState('fixture');
  return <main style={shellStyle} data-testid="headless-fixture">
    <SampleHeader eyebrow="Deterministic fixture" title="Headless component tests" detail="Stable DOM, CSS, pointer, keyboard and Canvas output for CI assertions and reference rendering." />
    <div style={rowStyle}><Badge tone="success">DOM ready</Badge><Badge>Reference REF-01</Badge></div>
    <Card style={{ marginTop: 14 }}>
      <div style={rowStyle}>
        <button data-testid="pointer-target" style={buttonStyle} onClick={() => setPointerCount(value => value + 1)}>Pointer events: {pointerCount}</button>
        <input data-testid="keyboard-target" style={inputStyle} value={value} onChange={event => setValue(event.currentTarget.value)} onKeyDown={() => setKeyCount(count => count + 1)} aria-label="Keyboard fixture" />
        <Badge>Key events: {keyCount}</Badge>
      </div>
      <p style={{ color: colors.muted }}>DOM assertion: <strong data-testid="fixture-value">{value}</strong></p>
      <ReferenceCanvas />
    </Card>
  </main>;
}

const lifecycle = createSampleLifecycle('HeadlessComponentTests', () => <App />);
export const mount = lifecycle.mount;
export const unmount = lifecycle.unmount;
Object.assign(globalThis, { mount, unmount });
