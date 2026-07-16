import axios from 'axios'
import { Message } from '@arco-design/web-vue'

const http = axios.create({
  baseURL: '/api',
  timeout: 30000
})

http.interceptors.request.use(config => {
  const token = localStorage.getItem('admin_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

http.interceptors.response.use(
  response => response.data,
  error => {
    const msg = error.response?.data?.message || '请求失败'
    Message.error(msg)
    if (error.response?.status === 401) {
      localStorage.removeItem('admin_token')
      window.location.href = '/login'
    }
    return Promise.reject(error)
  }
)

export default http

// ===== API 接口 =====

// 渠道管理
export const channelApi = {
  list: () => http.get('/admin/channels'),
  get: (id) => http.get(`/admin/channels/${id}`),
  create: (data) => http.post('/admin/channels', data),
  update: (id, data) => http.put(`/admin/channels/${id}`, data),
  delete: (id) => http.delete(`/admin/channels/${id}`),
  getKeys: (channelId) => http.get(`/admin/channels/${channelId}/keys`),
  addKey: (channelId, data) => http.post(`/admin/channels/${channelId}/keys`, data),
  batchAddKeys: (channelId, keys) => http.post(`/admin/channels/${channelId}/keys/batch`, keys),
  deleteKey: (channelId, keyId) => http.delete(`/admin/channels/${channelId}/keys/${keyId}`),
  getModels: (channelId) => http.get(`/admin/channels/${channelId}/models`),
  addModel: (channelId, data) => http.post(`/admin/channels/${channelId}/models`, data),
  batchAddModels: (channelId, models) => http.post(`/admin/channels/${channelId}/models/batch`, models),
  deleteModel: (channelId, modelId) => http.delete(`/admin/channels/${channelId}/models/${modelId}`),
  getSupplierPresets: () => http.get('/admin/channels/supplier-presets'),
  getAvailableModels: () => http.get('/admin/channels/available-models'),
  testModel: (data) => http.post('/admin/channels/test-model', data),
  testImageModel: (data) => http.post('/admin/channels/test-image-model', data),
  // 模型链
  getChains: () => http.get('/admin/channels/chains'),
  createChain: (data) => http.post('/admin/channels/chains', data),
  updateChain: (id, data) => http.put(`/admin/channels/chains/${id}`, data),
  deleteChain: (id) => http.delete(`/admin/channels/chains/${id}`)
}

// 令牌管理
export const tokenApi = {
  list: () => http.get('/admin/tokens'),
  create: (data) => http.post('/admin/tokens', data),
  update: (id, data) => http.put(`/admin/tokens/${id}`, data),
  delete: (id) => http.delete(`/admin/tokens/${id}`),
  setModels: (id, modelIds) => http.put(`/admin/tokens/${id}/models`, modelIds)
}

// 日志
export const logApi = {
  callLogs: (params) => http.get('/admin/logs/calls', { params }),
  operationLogs: (params) => http.get('/admin/logs/operations', { params }),
  exportCalls: (params) => http.get('/admin/logs/calls/export', { params, responseType: 'blob' }),
  exportOperations: (params) => http.get('/admin/logs/operations/export', { params, responseType: 'blob' })
}

// 看板
export const dashboardApi = {
  get: () => http.get('/admin/dashboard'),
  upstreamKeys: () => http.get('/admin/dashboard/upstream-keys'),
  modelUsage: () => http.get('/admin/dashboard/model-usage')
}

// 认证
export const authApi = {
  login: (data) => http.post('/admin/auth/login', data),
  changePassword: (data) => http.post('/admin/auth/change-password', data)
}

// 计费
export const billingApi = {
  records: (params) => http.get('/admin/billing/records', { params }),
  summary: (params) => http.get('/admin/billing/summary', { params }),
  rules: () => http.get('/admin/billing/rules'),
  createRule: (data) => http.post('/admin/billing/rules', data),
  updateRule: (id, data) => http.put(`/admin/billing/rules/${id}`, data),
  deleteRule: (id) => http.delete(`/admin/billing/rules/${id}`)
}

// 全局配置
export const configApi = {
  getAll: () => http.get('/admin/config'),
  setConfig: (data) => http.put('/admin/config', data),
  getByKey: (key) => http.get(`/admin/config/${key}`)
}

// 健康检测
export const healthApi = {
  check: () => http.get('/admin/health')
}
