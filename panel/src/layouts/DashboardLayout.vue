<template>
  <div class="layout">
    <Sidebar />
    <div class="main">
      <TopBar />
      <div class="content">
        <RouterView />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { RouterView } from 'vue-router'
import Sidebar from '@/components/Sidebar.vue'
import TopBar from '@/components/TopBar.vue'
import { startConnection, stopConnection, onLog, onLogBatch, onStatsUpdate, getConnection } from '@/lib/signalr'
import { useLogsStore } from '@/stores/logs'
import { useServerStore } from '@/stores/server'

const logs   = useLogsStore()
const server = useServerStore()

onMounted(async () => {
  try {
    await startConnection()
    server.setConnected(true)

    onLog(entry => logs.addEntry(entry))
    onLogBatch(batch => logs.addBatch(batch))
    onStatsUpdate(stats => server.updateStats(stats))

    const conn = getConnection()
    conn.onreconnected(() => server.setConnected(true))
    conn.onreconnecting(() => server.setConnected(false))
    conn.onclose(() => server.setConnected(false))
  } catch {
    server.setConnected(false)
  }
})

onUnmounted(async () => {
  await stopConnection()
  server.setConnected(false)
})
</script>

<style scoped>
.layout {
  display: flex;
  height: 100vh;
  overflow: hidden;
  background-color: var(--bg-primary);
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.content {
  flex: 1;
  overflow-y: auto;
  padding: 24px;
}
</style>
