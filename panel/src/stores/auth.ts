import { defineStore } from 'pinia'
import { ref } from 'vue'
import { authApi } from '@/lib/api'
import router from '@/router'

export const useAuthStore = defineStore('auth', () => {
  const token      = ref<string | null>(localStorage.getItem('sn_token'))
  const serverName = ref<string>(localStorage.getItem('sn_server') ?? 'SphereNet')
  const loggedIn   = ref(!!token.value)

  async function login(password: string): Promise<void> {
    const { data } = await authApi.login(password)
    token.value      = data.token
    serverName.value = data.serverName
    loggedIn.value   = true
    localStorage.setItem('sn_token',  data.token)
    localStorage.setItem('sn_server', data.serverName)
    await router.push('/dashboard')
  }

  async function logout() {
    try { await authApi.logout() } catch { /* ignore */ }
    token.value    = null
    loggedIn.value = false
    localStorage.removeItem('sn_token')
    localStorage.removeItem('sn_server')
    await router.push('/login')
  }

  return { token, serverName, loggedIn, login, logout }
})
