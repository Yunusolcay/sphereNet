<template>
  <header class="topbar">
    <h1 class="page-title">{{ title }}</h1>
    <div class="topbar-right">
      <!-- Game server status + controls -->
      <div class="server-ctrl">
        <span class="status-badge" :class="gameRunning ? 'running' : 'stopped'">
          <span class="dot" />
          {{ gameRunning ? 'Server Running' : 'Server Stopped' }}
        </span>
        <button v-if="!gameRunning" class="ctrl-btn start" @click="startServer" :disabled="busy">
          <Play :size="12" /> Start
        </button>
        <button v-if="gameRunning" class="ctrl-btn restart" @click="restartServer" :disabled="busy">
          <RotateCw :size="12" /> Restart
        </button>
        <button v-if="gameRunning" class="ctrl-btn stop" @click="stopServer" :disabled="busy">
          <Square :size="12" /> Stop
        </button>
      </div>

      <!-- Panel SignalR connection -->
      <span class="connection-badge" :class="server.connected ? 'online' : 'offline'">
        <span class="dot" />
        {{ server.connected ? 'Panel Connected' : 'Panel Disconnected' }}
      </span>
      <span class="server-name">{{ auth.serverName }}</span>
    </div>
  </header>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import { Play, Square, RotateCw } from 'lucide-vue-next'
import { useAuthStore } from '@/stores/auth'
import { useServerStore } from '@/stores/server'
import { serverApi } from '@/lib/api'

const auth   = useAuthStore()
const server = useServerStore()
const route  = useRoute()

const busy        = ref(false)
const gameRunning = ref(true) // assume running until first poll
let   pollTimer: ReturnType<typeof setInterval> | null = null

const titleMap: Record<string, string> = {
  dashboard: 'Dashboard',
  logs:      'Console',
  players:   'Players',
  accounts:  'Accounts',
  server:    'Server',
  scripts:   'Scripts',
  settings:  'Settings',
}

const title = computed(() => titleMap[route.name as string] ?? 'SphereNet')

async function pollRunning() {
  try {
    const { data } = await serverApi.running()
    gameRunning.value = data.running
  } catch { /* ignore */ }
}

onMounted(() => {
  pollRunning()
  pollTimer = setInterval(pollRunning, 5000)
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
})

async function startServer() {
  busy.value = true
  try { await serverApi.startServer() } catch { /* ignore */ }
  finally { busy.value = false }
  setTimeout(pollRunning, 3000)
}

async function restartServer() {
  if (!confirm('Restart the game server?')) return
  busy.value = true
  try { await serverApi.restart() } catch { /* ignore */ }
  finally { busy.value = false }
  setTimeout(pollRunning, 5000)
}

async function stopServer() {
  if (!confirm('Stop the game server?')) return
  busy.value = true
  try { await serverApi.shutdown() } catch { /* ignore */ }
  finally { busy.value = false }
  setTimeout(pollRunning, 3000)
}
</script>

<style scoped>
.topbar {
  height: 56px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border);
}

.page-title {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
  color: var(--text-primary);
}

.topbar-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

/* Server status + controls */
.server-ctrl {
  display: flex;
  align-items: center;
  gap: 6px;
}

.status-badge {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 500;
  padding: 4px 10px;
  border-radius: 9999px;
}

.status-badge.running { background: rgba(63, 185, 80, 0.15);  color: var(--success, #3fb950); }
.status-badge.stopped { background: rgba(248, 81, 73, 0.15);  color: var(--danger,  #f85149); }

.ctrl-btn {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 10px;
  border-radius: 6px;
  border: 1px solid;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.15s;
}

.ctrl-btn:disabled { opacity: 0.5; cursor: not-allowed; }

.ctrl-btn.start   { border-color: var(--success, #3fb950); color: var(--success, #3fb950); background: transparent; }
.ctrl-btn.restart { border-color: var(--accent); color: var(--accent); background: transparent; }
.ctrl-btn.stop    { border-color: var(--danger, #f85149); color: var(--danger, #f85149); background: transparent; }

.ctrl-btn.start:not(:disabled):hover   { background: rgba(63, 185, 80, 0.15); }
.ctrl-btn.restart:not(:disabled):hover { background: rgba(88, 166, 255, 0.1); }
.ctrl-btn.stop:not(:disabled):hover    { background: rgba(248, 81, 73, 0.1); }

/* Panel connection badge */
.connection-badge {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 500;
  padding: 4px 10px;
  border-radius: 9999px;
}

.connection-badge.online  { background: rgba(63, 185, 80, 0.15);  color: var(--success, #3fb950); }
.connection-badge.offline { background: rgba(248, 81, 73, 0.15);  color: var(--danger,  #f85149); }

.dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentColor;
}

.server-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-muted);
}
</style>
