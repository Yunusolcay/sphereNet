import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { setupApi } from '@/lib/api'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      redirect: '/dashboard',
    },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/pages/LoginPage.vue'),
      meta: { public: true },
    },
    {
      path: '/setup',
      name: 'setup',
      component: () => import('@/pages/SetupPage.vue'),
      meta: { public: true },
    },
    {
      path: '/',
      component: () => import('@/layouts/DashboardLayout.vue'),
      children: [
        {
          path: 'dashboard',
          name: 'dashboard',
          component: () => import('@/pages/DashboardPage.vue'),
        },
        {
          path: 'logs',
          name: 'logs',
          component: () => import('@/pages/LogsPage.vue'),
        },
        {
          path: 'players',
          name: 'players',
          component: () => import('@/pages/PlayersPage.vue'),
        },
        {
          path: 'accounts',
          name: 'accounts',
          component: () => import('@/pages/AccountsPage.vue'),
        },
        {
          path: 'server',
          name: 'server',
          component: () => import('@/pages/ServerPage.vue'),
        },
        {
          path: 'scripts',
          name: 'scripts',
          component: () => import('@/pages/ScriptsPage.vue'),
        },
        {
          path: 'settings',
          name: 'settings',
          component: () => import('@/pages/SettingsPage.vue'),
        },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
  ],
})

let setupChecked = false
let setupDone    = false

router.beforeEach(async to => {
  // Check setup status once per session (skip for the setup page itself)
  if (!setupChecked && to.name !== 'setup') {
    try {
      const { data } = await setupApi.needed()
      setupDone    = !data.needed
      setupChecked = true
    } catch {
      setupDone    = true  // panel already configured — let normal auth guard run
      setupChecked = true
    }
    if (!setupDone) return { name: 'setup' }
  }

  const auth = useAuthStore()
  if (!to.meta.public && !auth.loggedIn) {
    return { name: 'login' }
  }
})

export default router
