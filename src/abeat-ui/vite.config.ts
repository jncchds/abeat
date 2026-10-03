import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'fs'
import path from 'path'

const appVersion = fs.readFileSync(path.resolve(import.meta.dirname, '../../VERSION'), 'utf-8').trim()

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  build: {
    outDir: '../Abeat.Web/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
})
