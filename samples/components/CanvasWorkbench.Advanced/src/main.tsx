import React, { useEffect, useRef, useState } from 'react';
import { Badge, Card, SampleHeader, buttonStyle, colors, createSampleLifecycle, rowStyle, secondaryButtonStyle, shellStyle } from '../../shared/ui';

function ReferenceChart({ mode }: { mode: string }): React.ReactNode {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const graphics = ref.current?.getContext('2d');
    if (!graphics) return;
    graphics.fillStyle = '#0b1220';
    graphics.fillRect(0, 0, 720, 330);
    graphics.strokeStyle = '#1e293b';
    graphics.lineWidth = 1;
    for (let y = 30; y < 330; y += 45) { graphics.beginPath(); graphics.moveTo(0, y); graphics.lineTo(720, y); graphics.stroke(); }
    graphics.strokeStyle = mode === 'Candles' ? '#22c55e' : '#38bdf8';
    graphics.lineWidth = 3;
    graphics.beginPath();
    for (let index = 0; index < 48; index += 1) {
      const x = 8 + index * 15;
      const y = 190 - Math.sin(index / 4) * 54 - index * 1.3;
      if (index) graphics.lineTo(x, y); else graphics.moveTo(x, y);
    }
    graphics.stroke();
  }, [mode]);
  return <canvas ref={ref} width={720} height={330} aria-label={`${mode} reference chart`} />;
}

function App(): React.ReactNode {
  const [mode, setMode] = useState('Candles');
  const [indicator, setIndicator] = useState(false);
  return <main style={{ ...shellStyle, background: '#07111f' }}>
    <SampleHeader eyebrow="Advanced Canvas workload" title="Interactive canvas workbench" detail="This compact catalog view exercises Canvas, menus, overlays, pointer input, and lifecycle seams without depending on a product integration." />
    <div style={{ ...rowStyle, marginBottom: 12 }}><Badge tone="success">Chart ready</Badge><Badge>V8</Badge><Badge>Canvas + overlays</Badge></div>
    <Card style={{ padding: 10, background: '#0b1220', borderColor: '#1e293b' }}>
      <div style={{ ...rowStyle, marginBottom: 10 }}>
        {['Candles', 'Line', 'Area'].map(item => <button key={item} style={mode === item ? buttonStyle : secondaryButtonStyle} onClick={() => setMode(item)}>{item}</button>)}
        <button style={buttonStyle} onClick={() => setIndicator(value => !value)}>{indicator ? 'Remove indicator' : 'Add indicator'}</button>
      </div>
      <ReferenceChart mode={mode} />
      {indicator ? <div role="dialog" aria-label="Indicator overlay" style={{ position: 'absolute', right: 48, top: 150, width: 240, padding: 16, color: colors.ink, background: '#ffffff', borderRadius: 10 }}><strong>Moving Average</strong><p>Length: 20 · Source: close</p><button style={buttonStyle} onClick={() => setIndicator(false)}>Close</button></div> : null}
    </Card>
    <p style={{ color: '#94a3b8' }}>Full evidence: viewport/DPR matrix, wheel, pan, menus, dialogs, multi-chart isolation and lifecycle soak.</p>
  </main>;
}

const lifecycle = createSampleLifecycle('CanvasWorkbench.Advanced', () => <App />);
export const mount = lifecycle.mount;
export const unmount = lifecycle.unmount;
Object.assign(globalThis, { mount, unmount });
