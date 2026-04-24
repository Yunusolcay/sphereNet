import axios from 'axios'
import { useAuthStore } from '@/stores/auth'

export const api = axios.create({
  baseURL: '/api',
  timeout: 10_000,
})

api.interceptors.request.use(config => {
  const auth = useAuthStore()
  if (auth.token) {
    config.headers.Authorization = `Bearer ${auth.token}`
  }
  return config
})

api.interceptors.response.use(
  r => r,
  err => {
    const url = err.config?.url ?? ''
    if (err.response?.status === 401 && !url.startsWith('/auth/')) {
      const auth = useAuthStore()
      auth.clearSession()
    }
    return Promise.reject(err)
  }
)

// --- Types ---

export interface ServerStats {
  serverName: string
  uptime: string
  uptimeSeconds: number
  onlinePlayers: number
  totalChars: number
  totalItems: number
  totalSectors: number
  tickCount: number
  memoryMB: number
  accounts: number
  cpuPercent: number
  threadCount: number
}

export interface PlayerInfo {
  charName: string
  accountName: string
  mapId: number
  x: number
  y: number
  ip: string
}

export interface AccountInfo {
  name: string
  privLevel: number
  isBanned: boolean
  lastIp: string
  lastLogin: string
  createDate: string
  charCount: number
}

export interface DebugState {
  packetDebug: boolean
  scriptDebug: boolean
}

export interface ScriptFileInfo {
  name: string
  relativePath: string
  sizeBytes: number
  lastModified: string
}

export interface SetupConfig {
  serverName: string
  servPort: number
  adminPassword: string
  adminPanelPort: number
  tickSleepMode?: number
  debugPackets?: boolean
  scriptDebug?: boolean
}

export interface SetupStatus {
  done: boolean
  hasScripts: boolean
}

// --- API helpers ---

export const serverApi = {
  status:    () => api.get<ServerStats>('/server/status'),
  running:   () => api.get<{ running: boolean }>('/server/running'),
  save:      () => api.post('/server/save'),
  shutdown:  () => api.post('/server/shutdown'),
  restart:     () => api.post('/server/restart'),
  startServer: () => api.post('/server/start'),
  resync:    () => api.post('/server/resync'),
  gc:        () => api.post('/server/gc'),
  respawn:   () => api.post('/server/respawn'),
  restock:   () => api.post('/server/restock'),
  broadcast: (message: string) => api.post('/server/broadcast', { message }),
  command:   (command: string) => api.post<{ lines: string[] }>('/server/command', { command }),
}

export const playersApi = {
  list: () => api.get<PlayerInfo[]>('/players'),
}

export const accountsApi = {
  list:        () => api.get<AccountInfo[]>('/accounts'),
  get:         (name: string) => api.get<AccountInfo>(`/accounts/${name}`),
  create:      (name: string, password: string) => api.post('/accounts', { name, password }),
  delete:      (name: string) => api.delete(`/accounts/${name}`),
  ban:         (name: string) => api.post(`/accounts/${name}/ban`),
  unban:       (name: string) => api.post(`/accounts/${name}/unban`),
  setPassword: (name: string, password: string) => api.put(`/accounts/${name}/password`, { password }),
  setPrivLevel:(name: string, level: number)    => api.put(`/accounts/${name}/plevel`, { level }),
}

export const authApi = {
  login:  (password: string) => api.post<{ token: string; serverName: string }>('/auth/login', { password }),
  logout: () => api.post('/auth/logout'),
}

export const setupApi = {
  needed:  () => api.get<{ needed: boolean }>('/setup/needed'),
  config:  () => api.get<SetupConfig>('/setup/config'),
  apply:   (cfg: SetupConfig) => api.post('/setup/apply', cfg),
  status:  () => api.get<SetupStatus>('/setup/status'),
}

export const settingsApi = {
  getDebug: () => api.get<DebugState>('/settings/debug'),
  setDebug: (state: DebugState) => api.post('/settings/debug', state),
}

export const scriptsApi = {
  list:     () => api.get<ScriptFileInfo[]>('/scripts'),
  content:  (path: string) => api.get<{ content: string }>(`/scripts/content`, { params: { path } }),
  download: () => api.post<{ filesInstalled: number }>('/scripts/download', null, { timeout: 120_000 }),
}
