<template>
  <div class="server-page">

    <!-- Quick Actions -->
    <section class="section">
      <h2 class="section-title">Quick Actions</h2>
      <div class="actions-grid">
        <button class="action-card" @click="action('save')" :disabled="busy.save">
          <Save :size="22" class="action-icon success" />
          <span class="action-label">Save World</span>
          <span class="action-sub">Persist all game data</span>
        </button>
        <button class="action-card" @click="action('resync')" :disabled="busy.resync">
          <RefreshCw :size="22" class="action-icon accent" :class="{ spin: busy.resync }" />
          <span class="action-label">Resync Scripts</span>
          <span class="action-sub">Reload modified .scp files</span>
        </button>
        <button class="action-card" @click="action('respawn')" :disabled="busy.respawn">
          <Skull :size="22" class="action-icon warning" />
          <span class="action-label">Respawn NPCs</span>
          <span class="action-sub">Force respawn all NPCs</span>
        </button>
        <button class="action-card" @click="action('restock')" :disabled="busy.restock">
          <ShoppingBag :size="22" class="action-icon accent" />
          <span class="action-label">Restock Vendors</span>
          <span class="action-sub">Restock all vendor inventories</span>
        </button>
        <button class="action-card" @click="action('gc')" :disabled="busy.gc">
          <Trash2 :size="22" class="action-icon muted" />
          <span class="action-label">Force GC</span>
          <span class="action-sub">Run garbage collection</span>
        </button>
        <button class="action-card danger" @click="confirmShutdown" :disabled="busy.shutdown">
          <PowerOff :size="22" class="action-icon danger" />
          <span class="action-label">Shutdown Server</span>
          <span class="action-sub">Graceful shutdown</span>
        </button>
      </div>
    </section>

    <!-- Broadcast -->
    <section class="section">
      <h2 class="section-title">Broadcast Message</h2>
      <div class="broadcast-row">
        <input v-model="broadcastMsg" class="broadcast-input" placeholder="Message to all online players…" @keyup.enter="sendBroadcast" />
        <button class="btn-accent" @click="sendBroadcast" :disabled="!broadcastMsg.trim()">
          <Megaphone :size="15" /> Broadcast
        </button>
      </div>
      <p v-if="broadcastSent" class="sent-msg">Message sent.</p>
    </section>

    <!-- Console -->
    <section class="section">
      <h2 class="section-title">Server Console</h2>
      <div class="console-box">
        <div ref="consoleEl" class="console-output">
          <div v-for="(line, i) in consoleLines" :key="i" class="console-line">
            <span class="console-prompt" v-if="line.type === 'cmd'">» </span>
            <span :class="line.type === 'cmd' ? 'console-cmd' : 'console-resp'">{{ line.text }}</span>
          </div>
          <div v-if="consoleLines.length === 0" class="console-empty">Type a command and press Enter…</div>
        </div>
        <div class="console-input-row">
          <span class="prompt-label">»</span>
          <input
            ref="cmdInput"
            v-model="cmdText"
            class="console-input"
            placeholder="save / status / help …"
            @keyup.enter="runCommand"
            @keyup.up="historyUp"
            @keyup.down="historyDown"
          />
        </div>
      </div>
    </section>

    <p v-if="feedback" class="feedback">{{ feedback }}</p>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick } from 'vue'
import { Save, RefreshCw, Skull, ShoppingBag, Trash2, PowerOff, Megaphone } from 'lucide-vue-next'
import { serverApi } from '@/lib/api'
import { executeCommand } from '@/lib/signalr'

const broadcastMsg  = ref('')
const broadcastSent = ref(false)
const feedback      = ref('')

// busy flags per action
const busy = ref({ save: false, resync: false, respawn: false, restock: false, gc: false, shutdown: false })

type ActionKey = keyof typeof busy.value

async function action(key: ActionKey) {
  busy.value[key] = true
  feedback.value  = ''
  try {
    const res = await ({
      save:     serverApi.save,
      resync:   serverApi.resync,
      respawn:  serverApi.respawn,
      restock:  serverApi.restock,
      gc:       serverApi.gc,
      shutdown: serverApi.shutdown,
    } as Record<ActionKey, () => Promise<{ data: { message?: string; memoryMB?: number } }>>)[key]()
    feedback.value = res.data.message ?? (res.data.memoryMB ? `GC done — ${res.data.memoryMB} MB` : 'Done.')
  } catch {
    feedback.value = 'Action failed.'
  } finally {
    busy.value[key] = false
  }
}

function confirmShutdown() {
  if (confirm('Shutdown the server? All players will be disconnected.')) action('shutdown')
}

async function sendBroadcast() {
  if (!broadcastMsg.value.trim()) return
  await serverApi.broadcast(broadcastMsg.value.trim())
  broadcastMsg.value  = ''
  broadcastSent.value = true
  setTimeout(() => broadcastSent.value = false, 3000)
}

// --- Console ---
interface ConsoleLine { type: 'cmd' | 'resp'; text: string }

const consoleEl    = ref<HTMLElement | null>(null)
const cmdInput     = ref<HTMLInputElement | null>(null)
const cmdText      = ref('')
const consoleLines = ref<ConsoleLine[]>([])
const history      = ref<string[]>([])
const histIdx      = ref(-1)

async function runCommand() {
  const cmd = cmdText.value.trim()
  if (!cmd) return

  history.value.unshift(cmd)
  histIdx.value = -1
  consoleLines.value.push({ type: 'cmd', text: cmd })
  cmdText.value = ''

  try {
    const lines = await executeCommand(cmd)
    lines.forEach(l => consoleLines.value.push({ type: 'resp', text: l }))
  } catch {
    consoleLines.value.push({ type: 'resp', text: '(error: not connected or command failed)' })
  }

  await nextTick()
  const el = consoleEl.value
  if (el) el.scrollTop = el.scrollHeight
}

function historyUp() {
  if (history.value.length === 0) return
  histIdx.value = Math.min(histIdx.value + 1, history.value.length - 1)
  cmdText.value = history.value[histIdx.value] ?? ''
}

function historyDown() {
  histIdx.value = Math.max(histIdx.value - 1, -1)
  cmdText.value = histIdx.value >= 0 ? (history.value[histIdx.value] ?? '') : ''
}
</script>

<style scoped>
.server-page { display: flex; flex-direction: column; gap: 28px; }

.section { }

.section-title {
  font-size: 12px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted); margin: 0 0 14px;
}

.actions-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 12px;
}

.action-card {
  display: flex; flex-direction: column; align-items: flex-start;
  gap: 6px; padding: 18px;
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 10px; cursor: pointer; text-align: left;
  transition: border-color 0.15s, background 0.15s;
}

.action-card:hover:not(:disabled) { border-color: var(--accent); background: var(--bg-tertiary); }
.action-card:disabled { opacity: 0.5; cursor: not-allowed; }
.action-card.danger:hover:not(:disabled) { border-color: var(--danger); }

.action-icon { flex-shrink: 0; }
.action-icon.success { color: var(--success); }
.action-icon.accent  { color: var(--accent); }
.action-icon.warning { color: var(--warning); }
.action-icon.muted   { color: var(--text-muted); }
.action-icon.danger  { color: var(--danger); }

.action-label { font-size: 14px; font-weight: 600; color: var(--text-primary); }
.action-sub   { font-size: 12px; color: var(--text-muted); }

.broadcast-row { display: flex; gap: 10px; align-items: center; }

.broadcast-input {
  flex: 1; background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 14px;
  padding: 10px 14px; outline: none; transition: border-color 0.15s;
}

.broadcast-input:focus { border-color: var(--accent); }

.btn-accent {
  display: flex; align-items: center; gap: 6px;
  background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 10px 16px;
  cursor: pointer; white-space: nowrap; transition: background 0.15s;
}

.btn-accent:hover:not(:disabled) { background: var(--accent-hover); }
.btn-accent:disabled { opacity: 0.5; cursor: not-allowed; }

.sent-msg { font-size: 12px; color: var(--success); margin: 8px 0 0; }

.console-box {
  background: #0d1117; border: 1px solid var(--border);
  border-radius: 8px; overflow: hidden;
  display: flex; flex-direction: column;
}

.console-output {
  height: 280px; overflow-y: auto; padding: 14px;
  font-family: 'Courier New', Consolas, monospace; font-size: 12.5px; line-height: 1.7;
}

.console-empty { color: var(--text-muted); font-style: italic; }

.console-line { display: flex; gap: 4px; }
.console-prompt { color: var(--accent); }
.console-cmd  { color: var(--text-primary); font-weight: 600; }
.console-resp { color: var(--text-muted); }

.console-input-row {
  display: flex; align-items: center; gap: 8px;
  padding: 10px 14px; border-top: 1px solid var(--border);
  background: var(--bg-tertiary);
}

.prompt-label {
  color: var(--accent); font-family: 'Courier New', monospace;
  font-size: 14px; font-weight: 700;
}

.console-input {
  flex: 1; background: transparent; border: none;
  color: var(--text-primary); font-family: 'Courier New', Consolas, monospace;
  font-size: 13px; outline: none;
}

.spin { animation: spin 1s linear infinite; }

@keyframes spin { to { transform: rotate(360deg); } }

.feedback { font-size: 13px; color: var(--success); margin: 0; }
</style>
