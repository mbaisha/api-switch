<template>
  <div class="login-page">
    <a-card title="管理员登录" class="login-card" :bordered="true">
      <a-form :model="form" layout="vertical" @submit="login">
        <a-form-item label="用户名">
          <a-input v-model="form.username" placeholder="请输入用户名" />
        </a-form-item>
        <a-form-item label="密码">
          <a-input-password v-model="form.password" placeholder="请输入密码" />
        </a-form-item>
        <a-form-item>
          <a-button type="primary" html-type="submit" long :loading="loading">登 录</a-button>
        </a-form-item>
      </a-form>
      <div class="login-tip">
        默认账号: admin / admin123
      </div>
    </a-card>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { useRouter } from 'vue-router'
import { authApi } from '../../api'

const router = useRouter()
const form = ref({ username: 'admin', password: '' })
const loading = ref(false)

async function login() {
  loading.value = true
  try {
    const res = await authApi.login(form.value)
    if (res.code === 200 && res.data?.token) {
      localStorage.setItem('admin_token', res.data.token)
      Message.success('登录成功')
      router.push('/')
    } else {
      Message.error(res.message || '登录失败')
    }
  } catch (e) {
    console.error(e)
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.login-page {
  display: flex;
  justify-content: center;
  align-items: center;
  height: 100vh;
  background: var(--color-fill-2);
}
.login-card {
  width: 400px;
}
.login-tip {
  text-align: center;
  margin-top: 12px;
  color: var(--color-text-3);
  font-size: 12px;
}
</style>
