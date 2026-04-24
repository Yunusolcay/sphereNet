<template>
  <div class="setup-wrap">
    <div class="setup-card">
      <div class="setup-header">
        <span class="setup-brand">⚔ SphereNet</span>
        <h1 class="setup-title">Server Setup</h1>
        <p class="setup-sub">Configure your server. Changes are written to sphere.ini.</p>
      </div>

      <!-- Step indicator -->
      <div class="steps">
        <template v-for="(s, i) in steps" :key="i">
          <div class="step" :class="{ active: step === i, done: step > i }">
            <div class="step-dot">
              <Check v-if="step > i" :size="11" />
              <span v-else>{{ i + 1 }}</span>
            </div>
            <span class="step-label">{{ s }}</span>
          </div>
          <div v-if="i < steps.length - 1" class="step-line" :class="{ done: step > i }" />
        </template>
      </div>

      <!-- ── Step 0: Server Identity ─────────────────────────────────── -->
      <div v-if="step === 0" class="step-body">
        <h2 class="step-title">Server Identity</h2>
        <div class="field">
          <label>Server Name</label>
          <input v-model="form.serverName" placeholder="My Sphere Server" />
        </div>
        <div class="field">
          <label>Game Port</label>
          <input v-model.number="form.servPort" type="number" placeholder="2593" />
          <span class="field-hint">Standard UO port is 2593</span>
        </div>
      </div>

      <!-- ── Step 1: Admin ───────────────────────────────────────────── -->
      <div v-if="step === 1" class="step-body">
        <h2 class="step-title">Admin Access</h2>
        <div class="field">
          <label>Admin Password</label>
          <input v-model="form.adminPassword" type="password" placeholder="Strong password" />
          <span class="field-hint">Used to log in to this panel</span>
        </div>
        <div class="field">
          <label>Panel Port</label>
          <input v-model.number="form.adminPanelPort" type="number" placeholder="0 = auto" />
          <span class="field-hint">0 = ServPort + 3. Requires restart to take effect.</span>
        </div>
      </div>

      <!-- ── Step 2: Server Config ───────────────────────────────────── -->
      <div v-if="step === 2" class="step-body">
        <h2 class="step-title">Server Config</h2>

        <div class="field">
          <label>Tick Sleep Mode</label>
          <select v-model.number="form.tickSleepMode" class="select-input">
            <option :value="0">0 — Spin (lowest latency, ~100% CPU)</option>
            <option :value="1">1 — Sleep (low CPU, ~15 ms latency)</option>
            <option :value="2">2 — Hybrid (balanced, recommended)</option>
          </select>
        </div>

        <!-- Advanced toggle -->
        <button class="advanced-toggle" @click="showAdvanced = !showAdvanced">
          <ChevronRight :size="14" :class="{ rotated: showAdvanced }" />
          Advanced settings
        </button>

        <div v-if="showAdvanced" class="advanced-panel">
          <div class="toggle-row">
            <div>
              <div class="toggle-label">Packet Debug</div>
              <div class="toggle-desc">Log raw network packets. Very verbose — use briefly.</div>
            </div>
            <button class="toggle-btn" :class="{ on: form.debugPackets }" @click="form.debugPackets = !form.debugPackets">
              {{ form.debugPackets ? 'ON' : 'OFF' }}
            </button>
          </div>
          <div class="toggle-row">
            <div>
              <div class="toggle-label">Script Debug</div>
              <div class="toggle-desc">Log every trigger dispatch. Extremely verbose.</div>
            </div>
            <button class="toggle-btn" :class="{ on: form.scriptDebug }" @click="form.scriptDebug = !form.scriptDebug">
              {{ form.scriptDebug ? 'ON' : 'OFF' }}
            </button>
          </div>
        </div>
      </div>

      <!-- ── Step 3: Scripts ─────────────────────────────────────────── -->
      <div v-if="step === 3" class="step-body">
        <h2 class="step-title">Scripts</h2>

        <div v-if="scriptsLoading" class="loading">Checking scripts folder…</div>

        <template v-else>
          <!-- Scripts already installed -->
          <template v-if="scriptsStatus.hasScripts">
            <div class="alert alert-warn">
              <AlertTriangle :size="16" />
              <div>
                <strong>Scripts already installed.</strong>
                <p>
                  Downloading will delete the existing scripts folder and replace it with the
                  <a href="https://github.com/UOSoftware/Scripts-T" target="_blank" rel="noopener" class="gh-link-inline">
                    <i class="bi bi-github" /> UOSoftware/Scripts-T <i class="bi bi-box-arrow-up-right" />
                  </a>
                  version. This cannot be undone.
                </p>
              </div>
            </div>
            <div class="field" style="margin-top: 12px;">
              <label style="color:var(--danger)">Type <code>OVERWRITE</code> to confirm</label>
              <input v-model="overwriteConfirm" placeholder="OVERWRITE" />
            </div>
            <button
              class="btn-danger-full"
              @click="downloadScripts"
              :disabled="overwriteConfirm !== 'OVERWRITE' || downloading"
            >
              <Download :size="14" />
              {{ downloading ? 'Downloading…' : 'Delete & Re-install from GitHub' }}
            </button>
          </template>

          <!-- No scripts yet -->
          <template v-else>
            <p class="step-desc">
              Download the community scripts from
              <a href="https://github.com/UOSoftware/Scripts-T" target="_blank" rel="noopener" class="gh-link-inline">
                <i class="bi bi-github" /> UOSoftware/Scripts-T <i class="bi bi-box-arrow-up-right" />
              </a>
              to get started quickly.
            </p>
            <button class="btn-primary" @click="downloadScripts" :disabled="downloading">
              <Download :size="14" />
              {{ downloading ? 'Downloading…' : 'Download & Install Scripts' }}
            </button>
            <button class="btn-ghost" style="margin-left: 8px" @click="skipScripts">
              Skip for now
            </button>
          </template>

          <!-- Download progress / result -->
          <div v-if="downloadMsg" class="download-result" :class="{ error: downloadError }">
            {{ downloadMsg }}
          </div>

          <div v-if="scriptsInstalled" class="alert alert-ok" style="margin-top: 12px;">
            <CheckCircle :size="16" />
            <span>{{ installedCount }} script files installed successfully.</span>
          </div>
        </template>
      </div>

      <!-- ── Step 4: Review ──────────────────────────────────────────── -->
      <div v-if="step === 4" class="step-body">
        <h2 class="step-title">Review</h2>
        <div class="review-grid">
          <div class="review-row"><span>Server Name</span><strong>{{ form.serverName }}</strong></div>
          <div class="review-row"><span>Game Port</span><strong>{{ form.servPort }}</strong></div>
          <div class="review-row"><span>Admin Password</span><strong>{{ form.adminPassword ? '••••••••' : '(not set)' }}</strong></div>
          <div class="review-row"><span>Panel Port</span><strong>{{ form.adminPanelPort || 'auto' }}</strong></div>
          <div class="review-row"><span>Tick Mode</span><strong>{{ tickModeLabel }}</strong></div>
          <div class="review-row"><span>Packet Debug</span><strong>{{ form.debugPackets ? 'ON' : 'OFF' }}</strong></div>
          <div class="review-row"><span>Script Debug</span><strong>{{ form.scriptDebug ? 'ON' : 'OFF' }}</strong></div>
        </div>
        <p v-if="!form.adminPassword" class="warn">⚠ Admin password is empty. You won't be able to log in after save.</p>
      </div>

      <!-- ── Step 5: Done ────────────────────────────────────────────── -->
      <div v-if="step === 5" class="step-body step-done">
        <CheckCircle :size="52" class="done-icon" />
        <h2>Setup Complete</h2>
        <p>Settings saved to sphere.ini and a script resync was initiated.</p>
        <button class="btn-primary" @click="goToDashboard">Go to Dashboard</button>
      </div>

      <!-- Actions -->
      <div v-if="step < 5" class="step-actions">
        <button v-if="step > 0" class="btn-ghost" @click="step--">Back</button>
        <span class="flex-1" />
        <button
          v-if="step < 4"
          class="btn-primary"
          @click="next"
          :disabled="!canNext"
        >
          {{ step === 3 ? 'Continue' : 'Next' }}
        </button>
        <button v-if="step === 4" class="btn-primary" @click="apply" :disabled="saving">
          {{ saving ? 'Saving…' : 'Apply & Save' }}
        </button>
      </div>

      <p v-if="error" class="error-msg">{{ error }}</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { Check, CheckCircle, ChevronRight, AlertTriangle, Download } from 'lucide-vue-next'
import { setupApi, scriptsApi } from '@/lib/api'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const auth   = useAuthStore()

const steps = ['Server Identity', 'Admin', 'Server Config', 'Scripts', 'Review', 'Done']
const step  = ref(0)
const saving = ref(false)
const error  = ref('')
const showAdvanced = ref(false)

// Scripts step state
const scriptsLoading    = ref(true)
const scriptsStatus     = ref({ hasScripts: false })
const overwriteConfirm  = ref('')
const downloading       = ref(false)
const downloadMsg       = ref('')
const downloadError     = ref(false)
const scriptsInstalled  = ref(false)
const installedCount    = ref(0)

const form = ref({
  serverName:     'SphereNet',
  servPort:       2593,
  adminPassword:  '',
  adminPanelPort: 0,
  tickSleepMode:  2,
  debugPackets:   false,
  scriptDebug:    false,
})

onMounted(async () => {
  try {
    const { data } = await setupApi.config()
    form.value = {
      serverName:     data.serverName,
      servPort:       data.servPort,
      adminPassword:  data.adminPassword,
      adminPanelPort: data.adminPanelPort,
      tickSleepMode:  data.tickSleepMode ?? 2,
      debugPackets:   data.debugPackets  ?? false,
      scriptDebug:    data.scriptDebug   ?? false,
    }
  } catch { /* server may not be configured yet */ }

  // Check scripts status
  try {
    const { data } = await setupApi.status()
    scriptsStatus.value = data as { hasScripts: boolean }
  } catch { /* ignore */ }
  scriptsLoading.value = false
})

const tickModeLabel = computed(() => {
  const labels: Record<number, string> = { 0: 'Spin', 1: 'Sleep', 2: 'Hybrid' }
  return labels[form.value.tickSleepMode] ?? '?'
})

const canNext = computed(() => {
  if (step.value === 0) return !!form.value.serverName && form.value.servPort > 0
  return true
})

function next() {
  error.value = ''
  step.value++
}

function skipScripts() {
  step.value++
}

async function downloadScripts() {
  downloading.value = true
  downloadMsg.value  = ''
  downloadError.value = false
  scriptsInstalled.value = false

  try {
    const { data } = await scriptsApi.download()
    installedCount.value = data.filesInstalled
    scriptsInstalled.value = true
    scriptsStatus.value.hasScripts = true
    overwriteConfirm.value = ''
  } catch (e: unknown) {
    const msg = (e as { response?: { data?: { detail?: string } } })?.response?.data?.detail
    downloadMsg.value   = msg ?? 'Download failed. Check server logs.'
    downloadError.value = true
  } finally {
    downloading.value = false
  }
}

async function apply() {
  saving.value = true
  error.value  = ''
  try {
    await setupApi.apply({
      serverName:     form.value.serverName,
      servPort:       form.value.servPort,
      adminPassword:  form.value.adminPassword,
      adminPanelPort: form.value.adminPanelPort,
      tickSleepMode:  form.value.tickSleepMode,
      debugPackets:   form.value.debugPackets,
      scriptDebug:    form.value.scriptDebug,
    })
    step.value = 5
  } catch (e: unknown) {
    const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error
    error.value = msg ?? 'Save failed'
  } finally {
    saving.value = false
  }
}

function goToDashboard() {
  auth.loggedIn ? router.push('/dashboard') : router.push('/login')
}
</script>

<style scoped>
.setup-wrap {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-primary);
  padding: 24px;
}

.setup-card {
  width: 100%;
  max-width: 560px;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 32px;
}

.setup-header { text-align: center; margin-bottom: 28px; }
.setup-brand { font-size: 18px; font-weight: 700; color: var(--accent); }
.setup-title { font-size: 22px; font-weight: 700; margin: 8px 0 4px; color: var(--text-primary); }
.setup-sub   { font-size: 13px; color: var(--text-muted); margin: 0; }

/* Steps */
.steps {
  display: flex;
  align-items: center;
  margin-bottom: 28px;
  overflow-x: auto;
  gap: 0;
}

.step {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.step-dot {
  width: 24px;
  height: 24px;
  border-radius: 50%;
  border: 2px solid var(--border);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  font-weight: 700;
  color: var(--text-muted);
  background: var(--bg-primary);
  transition: all 0.2s;
}

.step.active .step-dot { border-color: var(--accent); color: var(--accent); }
.step.done   .step-dot { border-color: var(--accent); background: var(--accent); color: #fff; }

.step-label {
  font-size: 10px;
  color: var(--text-muted);
  white-space: nowrap;
  text-align: center;
}
.step.active .step-label { color: var(--text-primary); font-weight: 600; }

.step-line {
  flex: 1;
  height: 2px;
  background: var(--border);
  margin: 0 4px;
  margin-bottom: 14px; /* align with dot center */
  transition: background 0.2s;
  min-width: 16px;
}
.step-line.done { background: var(--accent); }

/* Step body */
.step-body { margin-bottom: 24px; }
.step-title { font-size: 16px; font-weight: 600; color: var(--text-primary); margin: 0 0 18px; }
.step-desc  { font-size: 13px; color: var(--text-muted); margin: 0 0 16px; line-height: 1.6; }

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 16px;
}
.field label { font-size: 13px; font-weight: 500; color: var(--text-muted); }

.field input, .select-input {
  background: var(--bg-primary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 14px;
  padding: 9px 12px;
  outline: none;
  transition: border-color 0.15s;
}
.field input:focus, .select-input:focus { border-color: var(--accent); }
.field-hint { font-size: 11px; color: var(--text-muted); }

code {
  background: var(--bg-primary);
  border: 1px solid var(--border);
  border-radius: 3px;
  padding: 1px 5px;
  font-family: monospace;
  font-size: 12px;
}

/* Advanced */
.advanced-toggle {
  display: flex;
  align-items: center;
  gap: 6px;
  background: transparent;
  border: none;
  color: var(--text-muted);
  font-size: 13px;
  cursor: pointer;
  padding: 4px 0;
  margin-bottom: 12px;
}
.advanced-toggle:hover { color: var(--text-primary); }
.advanced-toggle .rotated { transform: rotate(90deg); }

.advanced-panel {
  background: var(--bg-primary);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  gap: 0;
}

.toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 0;
  border-bottom: 1px solid var(--border);
}
.toggle-row:last-child { border-bottom: none; }
.toggle-label { font-size: 13px; font-weight: 500; color: var(--text-primary); }
.toggle-desc  { font-size: 11px; color: var(--text-muted); margin-top: 2px; }

.toggle-btn {
  min-width: 50px;
  padding: 5px 10px;
  border-radius: 20px;
  border: 2px solid var(--border);
  background: var(--bg-secondary);
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 700;
  cursor: pointer;
  letter-spacing: 0.05em;
  transition: all 0.15s;
}
.toggle-btn.on { border-color: var(--accent); background: var(--accent); color: #fff; }

/* Scripts step */
.loading { font-size: 13px; color: var(--text-muted); }

.alert {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px 14px;
  border-radius: 8px;
  font-size: 13px;
}
.alert strong { display: block; margin-bottom: 4px; }
.alert p { margin: 0; color: var(--text-muted); }

.alert-warn {
  background: rgba(229, 160, 0, 0.1);
  border: 1px solid rgba(229, 160, 0, 0.3);
  color: var(--warning, #e5a000);
}
.alert-ok {
  background: rgba(63, 185, 80, 0.1);
  border: 1px solid rgba(63, 185, 80, 0.3);
  color: var(--success, #3fb950);
}

.btn-danger-full {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 9px 16px;
  margin-top: 12px;
  border-radius: 6px;
  border: 1px solid var(--danger);
  background: rgba(248, 81, 73, 0.08);
  color: var(--danger);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.15s;
}
.btn-danger-full:not(:disabled):hover { background: var(--danger); color: #fff; }
.btn-danger-full:disabled { opacity: 0.4; cursor: not-allowed; }

.download-result {
  margin-top: 10px;
  font-size: 12px;
  color: var(--success, #3fb950);
}
.download-result.error { color: var(--danger); }

/* Review */
.review-grid { display: flex; flex-direction: column; gap: 8px; }
.review-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  background: var(--bg-primary);
  border-radius: 6px;
  font-size: 13px;
}
.review-row span  { color: var(--text-muted); }
.review-row strong { color: var(--text-primary); }

.warn { margin-top: 12px; font-size: 13px; color: var(--warning, #e5a000); }

/* Done */
.step-done {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  text-align: center;
  padding: 16px 0;
}
.done-icon { color: var(--success, #3fb950); }
.step-done h2 { margin: 0; font-size: 18px; color: var(--text-primary); }
.step-done p  { margin: 0; font-size: 14px; color: var(--text-muted); }

/* Actions */
.step-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  padding-top: 8px;
}
.flex-1 { flex: 1; }

.btn-primary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 9px 20px;
  background: var(--accent);
  border: none;
  border-radius: 6px;
  color: #fff;
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity 0.15s;
}
.btn-primary:disabled { opacity: 0.5; cursor: not-allowed; }
.btn-primary:not(:disabled):hover { opacity: 0.85; }

.btn-ghost {
  padding: 9px 20px;
  background: transparent;
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-muted);
  font-size: 14px;
  cursor: pointer;
  transition: all 0.15s;
}
.btn-ghost:hover { border-color: var(--text-muted); color: var(--text-primary); }

.link { color: var(--accent); text-decoration: none; }
.link:hover { text-decoration: underline; }

.gh-link-inline {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--accent);
  text-decoration: none;
  font-weight: 500;
}
.gh-link-inline:hover { text-decoration: underline; }

.error-msg { margin-top: 12px; font-size: 13px; color: var(--danger); text-align: center; }
</style>
