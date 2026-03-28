import React, { useState, useEffect } from 'react'

// ─── Styles (inline for IIFE compatibility) ───────────────────────────────────
const styles = {
  body: {
    fontFamily: 'sans-serif',
    background: '#f0f4f8',
    margin: 0,
    padding: 20,
  },
  container: { display: 'flex', flexDirection: 'column', gap: 16 },
  nav: {
    display: 'flex',
    flexDirection: 'row',
    gap: 24,
    background: '#61dafb',
    padding: '12px 20px',
    borderRadius: 8,
  },
  navItem: { color: '#1a1a2e', fontSize: 14, cursor: 'pointer', padding: '4px 10px' },
  navActive: { fontWeight: 'bold', textDecoration: 'underline' },
  card: {
    background: '#fff',
    border: '1px solid #dde1e7',
    borderRadius: 8,
    padding: 20,
  },
  h1: { color: '#61dafb', margin: '0 0 8px 0' },
  counter: { fontSize: 48, color: '#61dafb', textAlign: 'center' },
  mono: {
    fontFamily: 'monospace',
    background: '#f0f4f8',
    padding: '8px 12px',
    borderRadius: 4,
    border: '1px solid #dde1e7',
  },
  button: {
    background: '#61dafb',
    color: '#1a1a2e',
    border: 'none',
    borderRadius: 4,
    padding: '8px 16px',
    fontSize: 14,
    cursor: 'pointer',
  },
  page: { display: 'flex', flexDirection: 'column', gap: 16 },
}

// ─── App component ─────────────────────────────────────────────────────────────
export default function App() {
  const [tab, setTab]         = useState('home')
  const [counter, setCounter] = useState(0)
  const [appInfo, setAppInfo] = useState(null)
  const [clock, setClock]     = useState('--:--:--')
  const [todos, setTodos]     = useState(['Learn MioSharp', 'Build a desktop app', 'Ship it 🚀'])

  // Counter
  useEffect(() => {
    const id = setInterval(() => setCounter(c => c + 1), 1000)
    return () => clearInterval(id)
  }, [])

  // App info from C#
  useEffect(() => {
    window.mio.invoke('getAppInfo')
      .then(data => setAppInfo(data))
      .catch(e => console.error('getAppInfo:', e))
  }, [])

  // Live clock
  useEffect(() => {
    const updateClock = () => {
      window.mio.invoke('getClock')
        .then(data => setClock(data.time))
        .catch(() => {})
    }
    updateClock()
    const id = setInterval(updateClock, 1000)
    return () => clearInterval(id)
  }, [])

  const tabs = [
    { id: 'home',  label: 'Home' },
    { id: 'hooks', label: 'Hooks' },
    { id: 'info',  label: 'App Info' },
  ]

  return (
    <div style={styles.container}>
      {/* Navigation */}
      <nav style={styles.nav}>
        {tabs.map(t => (
          <span
            key={t.id}
            style={{ ...styles.navItem, ...(tab === t.id ? styles.navActive : {}) }}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </span>
        ))}
      </nav>

      {/* Home tab */}
      {tab === 'home' && (
        <div style={styles.page}>
          <div style={styles.card}>
            <h1 style={styles.h1}>Hello from React 18!</h1>
            <p>Running inside MioSharp — a pure C# rendering engine.</p>
            <p>React 18 hooks + Vite IIFE build + window.mio.invoke bridge.</p>
          </div>
          <div style={styles.card}>
            <div style={styles.counter}>{counter}</div>
            <p style={{ textAlign: 'center', color: '#888', fontSize: 13 }}>seconds running</p>
          </div>
          <div style={styles.card}>
            <p style={{ fontSize: 20, textAlign: 'center', color: '#61dafb' }}>🕐 {clock}</p>
          </div>
        </div>
      )}

      {/* Hooks tab */}
      {tab === 'hooks' && (
        <div style={styles.page}>
          <div style={styles.card}>
            <h1 style={styles.h1}>useState + useEffect</h1>
            <p>Standard React hooks work normally in MioSharp.</p>
          </div>
          <div style={styles.card}>
            <p>Todos ({todos.length}):</p>
            {todos.map((todo, i) => (
              <div key={i} style={{ padding: '4px 0', borderBottom: '1px solid #eee' }}>
                {i + 1}. {todo}
              </div>
            ))}
            <p>
              <button
                style={styles.button}
                onClick={() => setTodos(t => [...t, 'New todo ' + (t.length + 1)])}
              >
                Add todo
              </button>
            </p>
          </div>
        </div>
      )}

      {/* App Info tab */}
      {tab === 'info' && (
        <div style={styles.page}>
          <div style={styles.card}>
            <h1 style={styles.h1}>App Info from C#</h1>
            <p>Fetched via <code>window.mio.invoke('getAppInfo')</code></p>
          </div>
          {appInfo && (
            <div style={styles.card}>
              <div style={styles.mono}>
                <div>name: {appInfo.name}</div>
                <div>version: {appInfo.version}</div>
                <div>engine: {appInfo.engine}</div>
                <div>runtime: {appInfo.runtime}</div>
              </div>
            </div>
          )}
          {!appInfo && <div style={styles.card}><p>Loading...</p></div>}
        </div>
      )}
    </div>
  )
}
