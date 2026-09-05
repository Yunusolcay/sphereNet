import { defineStore } from 'pinia'
import { ref } from 'vue'
import { authApi } from '@/lib/api'
import router from '@/router'

export const useAuthStore = defineStore('auth', () => {
  const token      = ref<string | null>(localStorage.getItem('sn_token'))
  const serverName = ref<string>(localStorage.getItem('sn_server') ?? 'SphereNet')
  const loggedIn   = ref(!!token.value)

  /** Exchange the password for a token without leaving the current page.
   *  The setup wizard needs a session before its remaining steps can call
   *  protected endpoints, but must stay on the wizard while it does. */
  async function establishSession(password: string): Promise<void> {
    const { data } = await authApi.login(password)
    token.value      = data.token
    serverName.value = data.serverName
    loggedIn.value   = true
    localStorage.setItem('sn_token',  data.token)
    localStorage.setItem('sn_server', data.serverName)
  }

  async function login(password: string): Promise<void> {
    await establishSession(password)
    await router.push('/dashboard')
  }

  function clearSession() {
    token.value    = null
    loggedIn.value = false
    localStorage.removeItem('sn_token')
    localStorage.removeItem('sn_server')
    router.push('/login')
  }

  async function logout() {
    const currentToken = token.value
    clearSession()
    if (currentToken) {
      try { await authApi.logout(currentToken) } catch { /* local session is already cleared */ }
    }
  }

  return { token, serverName, loggedIn, establishSession, login, logout, clearSession }
})
