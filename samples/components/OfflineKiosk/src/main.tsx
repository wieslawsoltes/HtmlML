import React, { useState } from 'react';
import { Badge, Card, SampleHeader, buttonStyle, colors, createSampleLifecycle, gridStyle, rowStyle, shellStyle } from '../../shared/ui';

const departures = [
  ['09:40', 'Lisbon', 'On time'],
  ['10:15', 'Porto', 'Boarding'],
  ['11:05', 'Faro', 'On time']
];

function App(): React.ReactNode {
  const [ticket, setTicket] = useState<string>();
  return <main data-offline="true" style={{ ...shellStyle, background: '#061a2b', color: '#ffffff' }}>
    <SampleHeader eyebrow="Offline appliance" title="Station kiosk" detail="All data, icons and scripts are packaged locally. No network capability is declared or requested." />
    <div style={rowStyle}><Badge tone="success">Offline ready</Badge><Badge>1 declared bundle</Badge><Badge>Resolver restricted</Badge></div>
    <div style={{ ...gridStyle, marginTop: 16 }}>
      <Card style={{ flex: '2 1 420px' }}><h2 style={{ marginTop: 0 }}>Departures</h2>{departures.map(item => <div key={item[0]} style={{ display: 'flex', gap: 18, padding: 10, borderBottom: `1px solid ${colors.line}` }}><strong>{item[0]}</strong><span style={{ flex: 1 }}>{item[1]}</span><Badge tone={item[2] === 'Boarding' ? 'warning' : 'success'}>{item[2]}</Badge></div>)}</Card>
      <Card style={{ flex: '1 1 260px' }}><h2 style={{ marginTop: 0 }}>Local ticket</h2><p style={{ color: colors.muted }}>Ticket generation works with the network disabled.</p><button style={buttonStyle} onClick={() => setTicket(`K-${Math.floor(1000 + 8999 / 2)}`)}>Print ticket</button>{ticket ? <p><Badge tone="success">{ticket}</Badge></p> : null}</Card>
    </div>
  </main>;
}

const lifecycle = createSampleLifecycle('OfflineKiosk', () => <App />);
export const mount = lifecycle.mount;
export const unmount = lifecycle.unmount;
Object.assign(globalThis, { mount, unmount });
