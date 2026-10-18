import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'fs'
import path from 'path'

const appVersion = fs.readFileSync(path.resolve(import.meta.dirname, '../../VERSION'), 'utf-8').trim()
// the manual page bundles docs/MANUAL.md ("@manual?raw")
const manual = path.resolve(import.meta.dirname, '../../docs/MANUAL.md')

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [{ find: /^@manual(\?.*)?$/, replacement: `${manual}$1` }],
  },
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  build: {
    outDir: '../Abeat.Web/wwwroot',
    emptyOutDir: true,
  },
  server: {
    fs: { allow: ['.', path.dirname(manual)] },
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
})
