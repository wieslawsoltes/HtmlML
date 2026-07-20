import React, { useEffect, useRef, useState } from 'react';
import { Badge, Card, SampleHeader, buttonStyle, colors, createSampleLifecycle, type MountContext, rowStyle, shellStyle } from '../../shared/ui';

function MiniChart({ seed }: { seed: number }): React.ReactNode {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const graphics = ref.current?.getContext('2d');
    if (!graphics) return;
    graphics.strokeStyle = seed % 2 ? '#7c3aed' : colors.primary;
    graphics.lineWidth = 2;
    graphics.beginPath();
    for (let index = 0; index < 8; index += 1) {
      const x = 8 + index * 34;
      const y = 62 - ((index * 13 + seed * 11) % 45);
      if (index) graphics.lineTo(x, y); else graphics.moveTo(x, y);
    }
    graphics.stroke();
  }, [seed]);
  return <canvas ref={ref} width={270} height={76} aria-label="Isolated instance chart" />;
}

function WorkstationPanel({ context }: { context: MountContext }): React.ReactNode {
  const [count, setCount] = useState(0);
  const seed = (context.instanceId ?? '').length;
  return <main style={{ ...shellStyle, padding: 14 }}>
    <SampleHeader eyebrow="Workstation cell" title="Isolated chart instance" detail="The catalog composes four copies of this package over one immutable asset cache." />
    <Card>
      <div style={rowStyle}><Badge tone="success">Local state</Badge><Badge>Warm code cache</Badge></div>
      <MiniChart seed={seed} />
      <button style={buttonStyle} onClick={() => setCount(value => value + 1)}>Local count: {count}</button>
    </Card>
  </main>;
}

const lifecycle = createSampleLifecycle('MultiInstanceWorkstation', context => <WorkstationPanel context={context} />);
export const mount = lifecycle.mount;
export const unmount = lifecycle.unmount;
Object.assign(globalThis, { mount, unmount });
