<template>
  <a-layout class="layout-container">
    <a-layout-sider collapsible breakpoint="lg" :width="220">
      <div class="logo">
        <span class="logo-text">AI 接口转发平台</span>
      </div>
      <a-menu
        :selected-keys="[currentRoute]"
        @menu-item-click="onMenuClick"
      >
        <a-menu-item key="dashboard">
          <template #icon><icon-dashboard /></template>
          数据看板
        </a-menu-item>
        <a-menu-item key="channels">
          <template #icon><icon-apps /></template>
          渠道管理
        </a-menu-item>
        <a-menu-item key="modelChains">
          <template #icon><icon-branch /></template>
          模型链配置
        </a-menu-item>
        <a-menu-item key="tokens">
          <template #icon><icon-safe /></template>
          令牌管理
        </a-menu-item>
        <a-menu-item key="logs">
          <template #icon><icon-file /></template>
          日志查询
        </a-menu-item>
        <a-menu-item key="billing">
          <template #icon><icon-bookmark /></template>
          账单管理
        </a-menu-item>
        <a-menu-item key="settings">
          <template #icon><icon-settings /></template>
          全局配置
        </a-menu-item>
      </a-menu>
    </a-layout-sider>
    <a-layout>
      <a-layout-header class="header">
        <div class="header-left">
          <span class="header-title">AI 接口转发管理后台</span>
        </div>
        <div class="header-right">
          <a-dropdown trigger="click">
            <a-button type="text">
              <template #icon><icon-user /></template>
              管理员
            </a-button>
            <template #content>
              <a-doption @click="logout">
                <template #icon><icon-export /></template>
                退出登录
              </a-doption>
            </template>
          </a-dropdown>
        </div>
      </a-layout-header>
      <a-layout-content class="content">
        <router-view />
      </a-layout-content>
    </a-layout>
  </a-layout>
</template>

<script setup>
import { computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import {
  IconDashboard, IconApps, IconSafe, IconFile,
  IconBookmark, IconSettings, IconExport, IconUser, IconBranch
} from '@arco-design/web-vue/es/icon'

const router = useRouter()
const route = useRoute()

const currentRoute = computed(() => {
  const name = route.name?.toLowerCase() || 'dashboard'
  // 映射 menu key 到 route name
  if (name === 'modelchains') return 'modelChains'
  return name
})

// menu key → route name 映射
const menuKeyToRouteName = {
  dashboard: 'Dashboard',
  channels: 'Channels',
  modelChains: 'ModelChains',
  tokens: 'Tokens',
  logs: 'Logs',
  billing: 'Billing',
  settings: 'Settings'
}

function onMenuClick(key) {
  const routeName = menuKeyToRouteName[key] || (key.charAt(0).toUpperCase() + key.slice(1))
  router.push({ name: routeName })
}

function logout() {
  localStorage.removeItem('admin_token')
  router.push('/login')
}
</script>

<style scoped>
.layout-container {
  height: 100vh;
}
.logo {
  height: 64px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid var(--color-border-2);
}
.logo-text {
  font-size: 16px;
  font-weight: 600;
  color: var(--color-text-1);
}
.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  border-bottom: 1px solid var(--color-border-2);
}
.header-title {
  font-size: 14px;
  color: var(--color-text-2);
}
.content {
  padding: 24px;
  overflow-y: auto;
}
</style>
