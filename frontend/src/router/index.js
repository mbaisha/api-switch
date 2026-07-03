import { createRouter, createWebHistory } from 'vue-router'
import MainLayout from '../layouts/MainLayout.vue'

const routes = [
  {
    path: '/',
    component: MainLayout,
    redirect: '/dashboard',
    meta: { requiresAuth: true },
    children: [
      {
        path: 'dashboard',
        name: 'Dashboard',
        component: () => import('../views/dashboard/Dashboard.vue'),
        meta: { title: '数据看板', requiresAuth: true }
      },
      {
        path: 'channels',
        name: 'Channels',
        component: () => import('../views/admin/Channels.vue'),
        meta: { title: '渠道管理', requiresAuth: true }
      },
      {
        path: 'model-chains',
        name: 'ModelChains',
        component: () => import('../views/admin/ModelChains.vue'),
        meta: { title: '模型链配置', requiresAuth: true }
      },
      {
        path: 'tokens',
        name: 'Tokens',
        component: () => import('../views/admin/Tokens.vue'),
        meta: { title: '令牌管理', requiresAuth: true }
      },
      {
        path: 'logs',
        name: 'Logs',
        component: () => import('../views/admin/Logs.vue'),
        meta: { title: '日志查询', requiresAuth: true }
      },
      {
        path: 'billing',
        name: 'Billing',
        component: () => import('../views/admin/Billing.vue'),
        meta: { title: '账单管理', requiresAuth: true }
      },
      {
        path: 'settings',
        name: 'Settings',
        component: () => import('../views/admin/Settings.vue'),
        meta: { title: '全局配置', requiresAuth: true }
      }
    ]
  },
  {
    path: '/login',
    name: 'Login',
    component: () => import('../views/admin/Login.vue'),
    meta: { title: '登录' }
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
})

// 导航守卫
router.beforeEach((to, from, next) => {
  const token = localStorage.getItem('admin_token')
  if (to.meta.requiresAuth && !token) {
    next('/login')
  } else if (to.path === '/login' && token) {
    next('/')
  } else {
    next()
  }
})

export default router
