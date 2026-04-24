import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import App from './App.vue'
import router from './router'
import './style.css'
import 'bootstrap-icons/font/bootstrap-icons.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(VueQueryPlugin)
app.mount('#app')
