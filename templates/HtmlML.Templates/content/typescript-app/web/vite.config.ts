import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { htmlml } from '@htmlml/sdk/vite';

export default defineConfig({
  define: { 'process.env.NODE_ENV': JSON.stringify('production') },
  plugins: [htmlml({ manifest: 'htmlml-component.json' }), react()],
  build: { outDir: '../Component', emptyOutDir: true, lib: { entry: 'src/main.tsx', formats: ['iife'], name: 'HtmlMlComponent', fileName: () => 'dist/main.js' } }
});
