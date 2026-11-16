import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev server proxies API calls to the ASP.NET Core backend.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/Ylm.Server/wwwroot', emptyOutDir: true },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5080',
      '/auth': 'http://localhost:5080',
      '/signin-oidc': 'http://localhost:5080',
      '/openapi': 'http://localhost:5080',
      '/hubs': { target: 'http://localhost:5080', ws: true },
    },
  },
});
