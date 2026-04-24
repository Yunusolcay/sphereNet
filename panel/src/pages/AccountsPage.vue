<template>
  <div>
    <div class="toolbar">
      <input v-model="search" class="search-input" placeholder="Search accounts…" />
      <button class="btn-accent" @click="showCreate = true">
        <Plus :size="15" /> New Account
      </button>
    </div>

    <div class="table-wrap">
      <table class="table" v-if="accounts.length > 0">
        <thead>
          <tr>
            <th>Name</th>
            <th>PrivLevel</th>
            <th>Status</th>
            <th>Last IP</th>
            <th>Last Login</th>
            <th>Chars</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="a in filtered" :key="a.name">
            <td class="bold">{{ a.name }}</td>
            <td>
              <span class="badge" :class="`plevel-${a.privLevel}`">{{ privLabel(a.privLevel) }}</span>
            </td>
            <td>
              <span class="badge" :class="a.isBanned ? 'banned' : 'active'">
                {{ a.isBanned ? 'Banned' : 'Active' }}
              </span>
            </td>
            <td class="mono text-muted">{{ a.lastIp || '—' }}</td>
            <td class="text-muted">{{ fmtDate(a.lastLogin) }}</td>
            <td>{{ a.charCount }}</td>
            <td class="actions">
              <button class="icon-btn" :title="a.isBanned ? 'Unban' : 'Ban'" @click="toggleBan(a)">
                <component :is="a.isBanned ? ShieldCheck : ShieldOff" :size="15" />
              </button>
              <button class="icon-btn danger" title="Delete" @click="confirmDelete(a.name)">
                <Trash2 :size="15" />
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="empty">
        <UserCog :size="32" class="empty-icon" />
        <p>No accounts found.</p>
      </div>
    </div>

    <!-- Create account modal -->
    <div v-if="showCreate" class="modal-overlay" @click.self="showCreate = false">
      <div class="modal">
        <h3>Create Account</h3>
        <div class="field">
          <label>Username</label>
          <input v-model="newName" placeholder="account name" />
        </div>
        <div class="field">
          <label>Password</label>
          <input v-model="newPass" type="password" placeholder="password" />
        </div>
        <p v-if="createError" class="error-msg">{{ createError }}</p>
        <div class="modal-actions">
          <button class="btn-ghost" @click="showCreate = false">Cancel</button>
          <button class="btn-accent" @click="createAccount">Create</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { Plus, ShieldOff, ShieldCheck, Trash2, UserCog } from 'lucide-vue-next'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { accountsApi, type AccountInfo } from '@/lib/api'

const qc = useQueryClient()
const { data } = useQuery({
  queryKey: ['accounts'],
  queryFn: () => accountsApi.list().then(r => r.data),
  refetchInterval: 10_000,
})

const accounts = computed(() => data.value ?? [])
const search   = ref('')
const filtered = computed(() => {
  if (!search.value) return accounts.value
  const q = search.value.toLowerCase()
  return accounts.value.filter(a => a.name.toLowerCase().includes(q))
})

// Create
const showCreate  = ref(false)
const newName     = ref('')
const newPass     = ref('')
const createError = ref('')

async function createAccount() {
  createError.value = ''
  try {
    await accountsApi.create(newName.value, newPass.value)
    showCreate.value = false
    newName.value = newPass.value = ''
    qc.invalidateQueries({ queryKey: ['accounts'] })
  } catch (e: unknown) {
    createError.value = (e as { response?: { data?: { error?: string } } }).response?.data?.error ?? 'Failed'
  }
}

async function toggleBan(a: AccountInfo) {
  a.isBanned ? await accountsApi.unban(a.name) : await accountsApi.ban(a.name)
  qc.invalidateQueries({ queryKey: ['accounts'] })
}

async function confirmDelete(name: string) {
  if (!confirm(`Delete account "${name}"? This cannot be undone.`)) return
  await accountsApi.delete(name)
  qc.invalidateQueries({ queryKey: ['accounts'] })
}

const privLabels: Record<number, string> = {
  0: 'Guest', 1: 'Player', 2: 'Counselor', 3: 'Seer',
  4: 'GM', 5: 'Dev', 6: 'Admin', 7: 'Owner',
}

function privLabel(n: number): string { return privLabels[n] ?? `L${n}` }

function fmtDate(s: string): string {
  if (!s || s.startsWith('0001')) return '—'
  return new Date(s).toLocaleDateString()
}
</script>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
  gap: 12px;
}

.search-input {
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 13px;
  padding: 8px 12px;
  outline: none;
  width: 220px;
}

.search-input:focus { border-color: var(--accent); }

.btn-accent {
  display: flex; align-items: center; gap: 6px;
  background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 8px 14px;
  cursor: pointer; transition: background 0.15s;
}

.btn-accent:hover { background: var(--accent-hover); }

.btn-ghost {
  padding: 8px 14px; border-radius: 6px; border: 1px solid var(--border);
  background: transparent; color: var(--text-muted);
  font-size: 13px; font-weight: 500; cursor: pointer; transition: all 0.15s;
}

.btn-ghost:hover { background: var(--bg-tertiary); color: var(--text-primary); }

.table-wrap {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 10px; overflow: hidden;
}

.table { width: 100%; border-collapse: collapse; font-size: 13px; }

.table thead th {
  text-align: left; padding: 12px 16px;
  font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted); border-bottom: 1px solid var(--border);
}

.table tbody td { padding: 11px 16px; border-bottom: 1px solid var(--border); color: var(--text-primary); }
.table tbody tr:last-child td { border-bottom: none; }
.table tbody tr:hover { background: rgba(255,255,255,0.02); }

.bold { font-weight: 600; }
.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }

.badge {
  display: inline-block; padding: 2px 8px; border-radius: 9999px;
  font-size: 11px; font-weight: 600;
}

.active  { background: rgba(63,185,80,0.15);  color: var(--success); }
.banned  { background: rgba(248,81,73,0.15);   color: var(--danger); }
.plevel-0 { background: rgba(139,148,158,0.15); color: var(--text-muted); }
.plevel-1 { background: rgba(88,166,255,0.1);  color: var(--accent); }
.plevel-4, .plevel-5, .plevel-6, .plevel-7 { background: rgba(210,153,34,0.15); color: var(--warning); }

.actions { display: flex; gap: 6px; }

.icon-btn {
  display: flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); cursor: pointer; transition: all 0.15s;
}

.icon-btn:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.icon-btn.danger:hover { border-color: var(--danger); color: var(--danger); }

.empty {
  display: flex; flex-direction: column; align-items: center;
  gap: 12px; padding: 64px; color: var(--text-muted);
}

.empty-icon { opacity: 0.3; }
.empty p { margin: 0; font-size: 14px; }

.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.6);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}

.modal {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 12px; padding: 28px; width: 380px;
  display: flex; flex-direction: column; gap: 16px;
}

.modal h3 { margin: 0; font-size: 16px; font-weight: 600; }

.field { display: flex; flex-direction: column; gap: 6px; }
.field label { font-size: 12px; font-weight: 500; color: var(--text-muted); }
.field input {
  background: var(--bg-tertiary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 14px; padding: 9px 12px; outline: none;
}
.field input:focus { border-color: var(--accent); }

.error-msg { font-size: 13px; color: var(--danger); margin: 0; }

.modal-actions { display: flex; justify-content: flex-end; gap: 8px; }
</style>
