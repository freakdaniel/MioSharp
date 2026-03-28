import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../dist',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        // IIFE format: Jint cannot parse ES module import/export syntax.
        // IIFE bundles everything into a single self-executing function.
        format: 'iife',
        name: 'MioVueApp',
        entryFileNames: 'assets/app.js',
        assetFileNames: 'assets/[name].[ext]'
      }
    }
  }
})
