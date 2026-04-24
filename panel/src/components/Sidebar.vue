<template>
  <aside class="sidebar">
    <div class="brand">
      <span class="brand-icon">⚔</span>
      <span class="brand-name">SphereNet</span>
    </div>

    <nav class="nav">
      <RouterLink
        v-for="item in navItems"
        :key="item.to"
        :to="item.to"
        class="nav-item"
        active-class="active"
      >
        <component :is="item.icon" :size="18" />
        <span>{{ item.label }}</span>
      </RouterLink>
    </nav>

    <div class="sidebar-footer">
      <button class="logout-btn" @click="auth.logout()">
        <LogOut :size="16" />
        <span>Logout</span>
      </button>
    </div>
  </aside>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import {
  LayoutDashboard, Terminal, Users, UserCog, Server, LogOut, ScrollText, Settings,
} from 'lucide-vue-next'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()

const navItems = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/logs',      label: 'Console',   icon: Terminal        },
  { to: '/players',   label: 'Players',   icon: Users           },
  { to: '/accounts',  label: 'Accounts',  icon: UserCog         },
  { to: '/server',    label: 'Server',    icon: Server          },
  { to: '/scripts',   label: 'Scripts',   icon: ScrollText      },
  { to: '/settings',  label: 'Settings',  icon: Settings        },
]
</script>

<style scoped>
.sidebar {
  width: 220px;
  flex-shrink: 0;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  padding: 16px 0;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 20px 20px;
  border-bottom: 1px solid var(--border);
  margin-bottom: 8px;
}

.brand-icon { font-size: 20px; }

.brand-name {
  font-weight: 700;
  font-size: 15px;
  color: var(--text-primary);
  letter-spacing: 0.3px;
}

.nav {
  flex: 1;
  display: flex;
  flex-direction: column;
  padding: 8px;
  gap: 2px;
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 12px;
  border-radius: 6px;
  color: var(--text-muted);
  text-decoration: none;
  font-size: 14px;
  font-weight: 500;
  transition: background 0.15s, color 0.15s;
}

.nav-item:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.nav-item.active { background: var(--bg-tertiary); color: var(--accent); }

.sidebar-footer {
  padding: 12px;
  border-top: 1px solid var(--border);
}

.logout-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  border-radius: 6px;
  background: transparent;
  border: none;
  color: var(--text-muted);
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.logout-btn:hover { background: rgba(248, 81, 73, 0.1); color: var(--danger); }
</style>
