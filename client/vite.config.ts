import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * The dev server proxies the API and the hub, so the browser sees one origin and
 * there is no CORS and no credentialed cross-origin WebSocket to configure.
 * `ws: true` is required: without it the SignalR upgrade request 404s and the
 * client silently falls back - except that it cannot, because it is configured to
 * skip negotiation (S5.2).
 */
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_TARGET ?? 'http://localhost:5080',
        changeOrigin: true,
      },
      '/hub': {
        target: process.env.VITE_API_TARGET ?? 'http://localhost:5080',
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
