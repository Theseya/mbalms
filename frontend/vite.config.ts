/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const apiTarget = process.env.VITE_API_PROXY ?? 'http://localhost:5001'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false },
    },
    // Tests compare localization keys with the API error codes and enums; the dev server stays restricted.
    fs: process.env.VITEST ? { allow: ['.', '../backend/src/MbaLms.Api'] } : undefined,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
})
