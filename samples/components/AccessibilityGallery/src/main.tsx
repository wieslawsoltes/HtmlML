import React, { useRef, useState } from 'react';
import { Badge, Card, SampleHeader, buttonStyle, colors, createSampleLifecycle, inputStyle, rowStyle, shellStyle } from '../../shared/ui';

function App(): React.ReactNode {
  const [announcement, setAnnouncement] = useState('No announcements');
  const [dialogOpen, setDialogOpen] = useState(false);
  const firstField = useRef<HTMLInputElement>(null);
  return <main style={shellStyle} aria-label="Accessibility gallery">
    <SampleHeader eyebrow="Semantic UI" title="Accessibility gallery" detail="Labels, focus order, keyboard actions, dialog semantics, live state and a chart fallback description." />
    <div style={rowStyle}><Badge tone="success">Labeled controls</Badge><Badge>Keyboard order 1–4</Badge><Badge>Live region</Badge></div>
    <Card style={{ marginTop: 14 }}>
      <h2 style={{ marginTop: 0 }}>Profile</h2>
      <div style={rowStyle}>
        <label htmlFor="accessible-name">Display name</label>
        <input ref={firstField} id="accessible-name" style={inputStyle} aria-describedby="name-help" />
        <button style={buttonStyle} onClick={() => setAnnouncement('Profile saved successfully')}>Save profile</button>
        <button style={buttonStyle} onClick={() => setDialogOpen(true)}>Open accessible dialog</button>
      </div>
      <p id="name-help" style={{ color: colors.muted }}>This label and help text are exposed to the automation tree.</p>
      <p aria-live="polite" role="status"><strong>Live status:</strong> {announcement}</p>
      <svg width="280" height="90" viewBox="0 0 280 90" role="img" aria-label="Quarterly trend increased from 40 to 78 percent">
        <title>Quarterly trend increased from 40 to 78 percent</title>
        <path d="M8 72 L62 58 L118 64 L174 34 L228 18 L270 24" fill="none" stroke={colors.primary} strokeWidth="4" />
      </svg>
    </Card>
    {dialogOpen ? <div role="dialog" aria-modal="true" aria-labelledby="a11y-dialog-title" style={{ position: 'absolute', inset: 48, padding: 24, color: colors.ink, background: '#ffffff', border: `2px solid ${colors.primary}`, borderRadius: 14 }}><h2 id="a11y-dialog-title">Accessible dialog</h2><p>Focus returns to the gallery when this dialog closes.</p><button style={buttonStyle} onClick={() => { setDialogOpen(false); firstField.current?.focus(); }}>Close dialog</button></div> : null}
  </main>;
}

const lifecycle = createSampleLifecycle('AccessibilityGallery', () => <App />);
export const mount = lifecycle.mount;
export const unmount = lifecycle.unmount;
Object.assign(globalThis, { mount, unmount });
