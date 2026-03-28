<template>
  <div class="container">
    <!-- Navigation -->
    <nav class="nav">
      <span v-for="tab in tabs" :key="tab.id" class="nav-item" :class="{ active: currentTab === tab.id }"
        @click="currentTab = tab.id">{{ tab.label }}</span>
    </nav>

    <!-- Home tab -->
    <div v-if="currentTab === 'home'" class="page">
      <div class="card">
        <h1>Hello from Vue 3!</h1>
        <p>Running inside MioSharp — a pure C# rendering engine.</p>
        <p>Vue 3 Composition API + Vite IIFE build + window.mio.invoke bridge.</p>
      </div>
      <div class="card">
        <div class="counter">{{ counter }}</div>
        <p style="text-align: center; color: #888; font-size: 13px">seconds running</p>
      </div>
      <div class="card" v-if="stats">
        <p>Uptime: {{ stats.uptime }}s</p>
        <p>Memory: {{ stats.memory }} MB</p>
        <p>Version: {{ stats.version }}</p>
      </div>
    </div>

    <!-- Reactivity tab -->
    <div v-if="currentTab === 'reactive'" class="page">
      <div class="card">
        <h1>Vue 3 Reactivity</h1>
        <p>v-model, v-if, v-for — all rendered by MioSharp.</p>
      </div>
      <div class="card">
        <p>Message: <input v-model="message" style="padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px;"></p>
        <p>Reversed: <strong>{{ reversed }}</strong></p>
      </div>
      <div class="card">
        <p v-for="(item, i) in items" :key="i">{{ i + 1 }}. {{ item }}</p>
        <p><button @click="addItem">Add item</button></p>
      </div>
    </div>

    <!-- Ping tab -->
    <div v-if="currentTab === 'ping'" class="page">
      <div class="card">
        <h1>C# Ping via mio.invoke</h1>
        <p>Direct in-process call to the C# backend — no HTTP.</p>
      </div>
      <div class="card">
        <p><button @click="doPing">Ping C#</button></p>
        <p v-if="pingResult" style="font-family: monospace; background: #f0f4f8; padding: 8px; border-radius: 4px;">
          {{ pingResult }}
        </p>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue'

const tabs = [
  { id: 'home', label: 'Home' },
  { id: 'reactive', label: 'Reactivity' },
  { id: 'ping', label: 'Ping C#' }
]
const currentTab = ref('home')

const counter = ref(0)
let intervalId = null
onMounted(() => { intervalId = setInterval(() => counter.value++, 1000) })
onUnmounted(() => { if (intervalId) clearInterval(intervalId) })

const stats = ref(null)
onMounted(() => {
  window.mio.invoke('getStats')
    .then(data => { stats.value = data })
    .catch(e => console.error('getStats:', e))
})

const message = ref('MioSharp')
const reversed = computed(() => message.value.split('').reverse().join(''))
const items = ref(['Silk.NET', 'SkiaSharp', 'AngleSharp', 'Jint'])
let itemCount = items.value.length
const addItem = () => { itemCount++; items.value.push('Item ' + itemCount) }

const pingResult = ref('')
const doPing = () => {
  window.mio.invoke('ping')
    .then(data => { pingResult.value = 'pong: ' + data.ts })
    .catch(e => { pingResult.value = 'error: ' + e })
}
</script>

<style>
:root {
  --primary: #42b883;
  --primary-dark: #33a06f;
  --surface: #ffffff;
  --bg: #f5f5f5;
  --border: #e0e0e0;
  --text: #2c3e50;
}

* {
  box-sizing: border-box;
}

body {
  font-family: sans-serif;
  background: var(--bg);
  margin: 0;
  padding: 20px;
  color: var(--text);
}

.container {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.card {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 20px;
}

h1 {
  color: var(--primary);
  margin: 0 0 8px 0;
}

p {
  margin: 6px 0;
}

.nav {
  display: flex;
  flex-direction: row;
  gap: 24px;
  background: var(--primary);
  padding: 12px 20px;
  border-radius: 8px;
}

.nav-item {
  color: #fff;
  font-size: 14px;
  cursor: pointer;
  padding: 4px 10px;
}

.nav-item.active {
  font-weight: bold;
  text-decoration: underline;
}

.counter {
  font-size: 48px;
  color: var(--primary);
  text-align: center;
}

button {
  background: var(--primary);
  color: #fff;
  border: none;
  border-radius: 4px;
  padding: 8px 16px;
  font-size: 14px;
  cursor: pointer;
}
</style>
