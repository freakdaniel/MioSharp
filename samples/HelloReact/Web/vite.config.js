import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../dist',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        // IIFE format: Jint cannot parse ES module import/export syntax.
        // IIFE bundles everything into a single self-executing function.
        format: 'iife',
        name: 'MioReactApp',
        entryFileNames: 'assets/app.js',
        assetFileNames: 'assets/[name].[ext]'
      }
    }
  }
})
