<template>
  <div class="tokens-page">
    <div class="page-header">
      <h2>令牌管理</h2>
      <a-space>
        <a-button @click="showTestPanel = !showTestPanel" :type="showTestPanel ? 'primary' : 'outline'">
          <template #icon><icon-experiment /></template>
          API 测试
        </a-button>
        <a-button type="primary" size="large" @click="createToken">
          <template #icon><icon-plus /></template>
          生成令牌
        </a-button>
      </a-space>
    </div>

    <a-card :bordered="false" class="table-card">
      <a-table :columns="columns" :data="tokens" row-key="id" :loading="loading" :pagination="false">
        <template #enabled="{ record }">
          <a-switch :model-value="record.enabled" size="small" @change="(v) => toggleToken(record, v)" />
        </template>
        <template #tokenValue="{ record }">
          <a-typography-text copyable :ellipsis="true" style="max-width: 280px; display: inline-block">
            {{ record.tokenValue }}
          </a-typography-text>
        </template>
        <template #usage="{ record }">
          <a-progress
            :percent="record.totalTokenLimit > 0 ? Math.min(100, record.usedTokens / record.totalTokenLimit * 100) : 0"
            :status="record.totalTokenLimit > 0 && record.usedTokens >= record.totalTokenLimit ? 'danger' : 'normal'"
            size="small" style="width: 100px"
          />
        </template>
        <template #balance="{ record }">
          <span :class="{ 'balance-low': record.remainingBalance < 1 }">
            ¥{{ (record.remainingBalance || 0).toFixed(2) }}
          </span>
        </template>
        <template #rateLimit="{ record }">
          <span v-if="record.rateLimitCount > 0" style="font-size:12px">
            {{ record.rateLimitCount }}次/{{ record.rateLimitWindow || 60 }}秒
          </span>
          <a-tag v-else size="small" color="green">不限流</a-tag>
        </template>
        <template #imageEnabled="{ record }">
          <a-tag :color="record.imageEnabled ? 'arcoblue' : 'gray'" size="small">
            {{ record.imageEnabled ? '已开通' : '未开通' }}
          </a-tag>
        </template>
        <template #action="{ record }">
          <a-space>
            <a-button size="small" type="text" @click="editToken(record)">编辑</a-button>
            <a-button size="small" type="text" @click="configModels(record)">模型权限</a-button>
            <a-popconfirm content="确认删除此令牌?" @ok="deleteToken(record.id)">
              <a-button size="small" type="text" status="danger">删除</a-button>
            </a-popconfirm>
          </a-space>
        </template>
      </a-table>
    </a-card>

    <!-- 编辑令牌弹窗 -->
    <a-modal v-model:visible="dialogVisible" title="编辑令牌" width="520px" @ok="saveToken">
      <a-form :model="form" layout="vertical">
        <a-form-item label="备注 / 归属用户">
          <a-input v-model="form.remark" placeholder="如：张三的令牌 / 生产环境 / 测试账号" />
        </a-form-item>
        <a-divider style="margin: 8px 0">用量限制</a-divider>
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="日 Token 限额">
              <a-input-number v-model="form.dailyTokenLimit" :min="0" placeholder="0 = 不限制" style="width:100%">
                <template #suffix>tokens</template>
              </a-input-number>
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="总 Token 限额">
              <a-input-number v-model="form.totalTokenLimit" :min="0" placeholder="0 = 不限制" style="width:100%">
                <template #suffix>tokens</template>
              </a-input-number>
            </a-form-item>
          </a-col>
        </a-row>
        <a-divider style="margin: 8px 0">计费</a-divider>
        <a-form-item label="账户余额 (元)">
          <a-input-number v-model="form.remainingBalance" :min="0" :precision="4" placeholder="0" style="width:100%">
            <template #prefix>¥</template>
          </a-input-number>
        </a-form-item>
        <a-divider style="margin: 8px 0">IP 限流</a-divider>
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="时间窗口 (秒)">
              <a-input-number v-model="form.rateLimitWindow" :min="0" placeholder="60" style="width:100%">
                <template #suffix>秒</template>
              </a-input-number>
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="窗口内最大请求数">
              <a-input-number v-model="form.rateLimitCount" :min="0" placeholder="60" style="width:100%">
                <template #suffix>次/IP</template>
              </a-input-number>
            </a-form-item>
          </a-col>
        </a-row>
        <div style="font-size:12px;color:var(--color-text-3);margin-top:-8px">
          每个IP在该令牌下，时间窗口内最多请求的次数。请求数填 0 表示不限流。
        </div>
        <a-form-item label="状态">
          <a-switch v-model="form.enabled">
            <template #checked>启用</template>
            <template #unchecked>禁用</template>
          </a-switch>
        </a-form-item>
        <a-divider style="margin: 8px 0">图片转发权限</a-divider>
        <a-form-item label="允许调用图片转发接口">
          <a-switch v-model="form.imageEnabled">
            <template #checked>已开通</template>
            <template #unchecked>未开通</template>
          </a-switch>
          <div style="font-size:12px;color:var(--color-text-3);margin-top:2px">
            开通后此令牌可调用 /v1/images/generations（文生图/图生图/多图）。与 LLM 权限独立管控，默认关闭。
          </div>
        </a-form-item>
      </a-form>
    </a-modal>

    <!-- 新建令牌结果弹窗 -->
    <a-modal v-model:visible="newTokenDialogVisible" title="令牌已生成" width="580px" :footer="false">
      <a-alert type="warning" style="margin-bottom: 16px">
        请立即复制并妥善保管此令牌，关闭后将无法再次查看完整令牌值。
      </a-alert>
      <div class="new-token-display">
        <a-typography-text copyable style="font-size: 16px; font-family: monospace; word-break: break-all">
          {{ newTokenValue }}
        </a-typography-text>
      </div>
      <a-divider style="margin: 16px 0">使用方式</a-divider>
      <div class="usage-guide">
        <p>将此令牌作为 API Key 调用本系统的 OpenAI 兼容接口：</p>
        <div class="code-block">
          <code>curl {{ apiBaseUrl }}/v1/chat/completions \</code><br/>
          <code>&nbsp;&nbsp;-H "Authorization: Bearer {{ newTokenValue }}" \</code><br/>
          <code>&nbsp;&nbsp;-H "Content-Type: application/json" \</code><br/>
          <code>&nbsp;&nbsp;-d '{"model": "gpt-4o", "messages": [{"role": "user", "content": "Hello"}]}'</code>
        </div>
        <p style="margin-top: 12px; font-size: 12px; color: var(--color-text-3)">
          系统将自动验证令牌权限，并将请求转发到后端配置的 AI 渠道。
        </p>
      </div>
      <div class="wizard-footer" style="margin-top: 16px">
        <a-button type="primary" @click="newTokenDialogVisible = false">我知道了</a-button>
      </div>
    </a-modal>

    <!-- 模型权限弹窗（使用可用模型列表） -->
    <a-modal v-model:visible="permDialogVisible" title="配置模型权限" width="640px" @ok="saveModelPermission" @cancel="permDialogVisible = false">
      <div class="perm-hint">勾选此令牌可调用的模型。模型来自渠道映射和模型链配置。</div>
      <div v-if="availableModels.length === 0" class="empty-state">暂无可用模型，请先在「渠道管理」中配置模型</div>
      <div v-else style="max-height: 400px; overflow-y: auto">
        <div v-for="m in availableModels" :key="m.customModelId" class="model-perm-item" style="display:flex;align-items:center;gap:8px;padding:6px 0">
          <input type="checkbox"
            :id="'perm-' + m.customModelId"
            :checked="selectedModels.includes(m.customModelId)"
            @change="(e) => togglePermModel(m.customModelId, e.target.checked)"
            style="width:16px;height:16px;cursor:pointer"
          />
          <label :for="'perm-' + m.customModelId" style="cursor:pointer">
            <span class="perm-model-id">{{ m.customModelId }}</span>
            <span v-if="m.displayName && m.displayName !== m.customModelId" class="perm-model-name">{{ m.displayName }}</span>
          </label>
        </div>
      </div>
    </a-modal>

    <!-- API 测试面板 -->
    <a-card v-if="showTestPanel" :bordered="false" class="table-card" title="API 调用测试">
      <a-row :gutter="16">
        <a-col :span="12">
          <a-form layout="vertical" size="small">
            <a-form-item label="选择令牌">
              <a-select v-model="testTokenId" placeholder="选择要测试的令牌" allow-clear style="width:100%">
                <a-option v-for="t in tokens" :key="t.id" :value="t.id">{{ t.remark || 'Token #' + t.id }} ({{ t.tokenValue?.substring(0, 15) }}...)</a-option>
              </a-select>
            </a-form-item>
            <a-form-item label="模型">
              <a-select v-model="testApiModel" placeholder="选择模型" allow-clear style="width:100%">
                <a-option v-for="m in availableModels" :key="m.customModelId" :value="m.customModelId">{{ m.customModelId }}</a-option>
              </a-select>
            </a-form-item>
            <a-form-item label="请求消息">
              <a-textarea v-model="testApiMessage" placeholder="输入测试消息" :rows="4" />
            </a-form-item>
            <a-space>
              <a-button type="primary" :loading="apiTesting" @click="runApiTest(false)">发送请求</a-button>
              <a-button :loading="apiTesting" @click="runApiTest(true)">流式请求 (SSE)</a-button>
            </a-space>
          </a-form>
        </a-col>
        <a-col :span="12">
          <div class="api-test-result">
            <div class="api-test-header">
              <a-tag v-if="apiTestResult" :color="apiTestResult.success ? 'green' : 'red'">
                {{ apiTestResult.success ? '成功' : '失败' }}
              </a-tag>
              <span v-if="apiTestResult" class="api-test-time">{{ apiTestResult.duration }}ms</span>
            </div>
            <pre class="api-test-body">{{ apiTestResult?.body || '点击左侧按钮发起测试...' }}</pre>
          </div>
        </a-col>
      </a-row>
    </a-card>
  </div>
</template>

<script setup>
import { ref, onMounted, computed } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconPlus, IconExperiment } from '@arco-design/web-vue/es/icon'
import { tokenApi, channelApi } from '../../api'
import axios from 'axios'

// ===== 数据 =====
const tokens = ref([])
const loading = ref(false)
const availableModels = ref([])

const columns = [
  { title: 'ID', dataIndex: 'id', width: 50 },
  { title: '令牌', slotName: 'tokenValue', width: 300 },
  { title: '备注', dataIndex: 'remark', width: 140 },
  { title: '用量', slotName: 'usage', width: 120 },
  { title: '余额', slotName: 'balance', width: 100 },
  { title: '限流', slotName: 'rateLimit', width: 120 },
  { title: '调用次数', dataIndex: 'totalCalls', width: 90 },
  { title: '图片权限', slotName: 'imageEnabled', width: 90 },
  { title: '状态', slotName: 'enabled', width: 80 },
  { title: '操作', slotName: 'action', width: 220, fixed: 'right' }
]

// ===== 加载 =====
async function loadTokens() {
  loading.value = true
  try {
    const res = await tokenApi.list()
    if (res.code === 200) {
      tokens.value = (res.data || []).sort((a, b) => a.id - b.id)
    }
  } finally { loading.value = false }
}

async function loadAvailableModels() {
  try {
    const res = await channelApi.getAvailableModels()
    if (res.code === 200) availableModels.value = res.data || []
  } catch { /* ignore */ }
}

// ===== 令牌 CRUD =====
async function createToken() {
  const res = await tokenApi.create({})
  if (res.code === 200 && res.data) {
    newTokenValue.value = res.data.tokenValue || ''
    newTokenDialogVisible.value = true
  }
  await loadTokens()
}

const dialogVisible = ref(false)
const form = ref({})
const currentId = ref(null)
const newTokenDialogVisible = ref(false)
const newTokenValue = ref('')
const apiBaseUrl = computed(() => window.location.origin)

function editToken(record) {
  currentId.value = record.id
  form.value = { ...record }
  dialogVisible.value = true
}

async function saveToken() {
  await tokenApi.update(currentId.value, form.value)
  Message.success('更新成功')
  dialogVisible.value = false
  await loadTokens()
}

async function toggleToken(record, val) {
  try {
    await tokenApi.update(record.id, { ...record, enabled: val })
    record.enabled = val
    Message.success(val ? '已启用' : '已禁用')
  } catch { /* ignore */ }
}

async function deleteToken(id) {
  await tokenApi.delete(id)
  Message.success('删除成功')
  await loadTokens()
}

// ===== 模型权限 =====
const permDialogVisible = ref(false)
const selectedModels = ref([])
const permTokenId = ref(null)

function configModels(record) {
  permTokenId.value = record.id
  selectedModels.value = [...(record.allowedModels || []).map(m => m.customModelId)]
  permDialogVisible.value = true
}

function togglePermModel(modelId, checked) {
  if (checked) {
    if (!selectedModels.value.includes(modelId)) selectedModels.value.push(modelId)
  } else {
    selectedModels.value = selectedModels.value.filter(x => x !== modelId)
  }
}

async function saveModelPermission() {
  try {
    const res = await tokenApi.setModels(permTokenId.value, selectedModels.value)
    if (res.code === 200) {
      Message.success('模型权限配置成功')
    } else {
      Message.error(res.message || '保存失败')
      return
    }
    permDialogVisible.value = false
    await loadTokens()
  } catch (e) {
    Message.error('保存失败: ' + (e.response?.data?.message || e.message))
  }
}

// ===== API 测试 =====
const showTestPanel = ref(false)
const testTokenId = ref(null)
const testApiModel = ref('')
const testApiMessage = ref('Hello! Please respond with a friendly greeting in one sentence.')
const apiTesting = ref(false)
const apiTestResult = ref(null)

async function runApiTest(stream = false) {
  if (!testTokenId.value) { Message.warning('请选择令牌'); return }
  if (!testApiModel.value) { Message.warning('请选择模型'); return }

  apiTesting.value = true
  apiTestResult.value = null
  const startTime = Date.now()

  try {
    // 找到令牌值
    const token = tokens.value.find(t => t.id === testTokenId.value)
    if (!token) { Message.error('令牌不存在'); apiTesting.value = false; return }

    const payload = {
      model: testApiModel.value,
      messages: [{ role: 'user', content: testApiMessage.value }],
      stream
    }

    if (stream) {
      // SSE 流式请求
      const resp = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token.tokenValue}`
        },
        body: JSON.stringify(payload)
      })

      const reader = resp.body?.getReader()
      const decoder = new TextDecoder()
      let fullText = ''

      if (reader) {
        while (true) {
          const { done, value } = await reader.read()
          if (done) break
          const chunk = decoder.decode(value, { stream: true })
          fullText += chunk
        }
      }

      apiTestResult.value = {
        success: resp.ok,
        duration: Date.now() - startTime,
        body: fullText || `HTTP ${resp.status}`
      }
    } else {
      // 普通请求
      const resp = await axios.post('/v1/chat/completions', payload, {
        headers: { 'Authorization': `Bearer ${token.tokenValue}` }
      })
      apiTestResult.value = {
        success: true,
        duration: Date.now() - startTime,
        body: JSON.stringify(resp.data, null, 2)
      }
    }
  } catch (e) {
    apiTestResult.value = {
      success: false,
      duration: Date.now() - startTime,
      body: e.response ? JSON.stringify(e.response.data, null, 2) : e.message
    }
  } finally {
    apiTesting.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadTokens(), loadAvailableModels()])
})
</script>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
  flex-wrap: wrap;
  gap: 12px;
}
.page-header h2 { margin: 0; }
.table-card { margin-bottom: 16px; }

.balance-low { color: rgb(var(--danger-6)); font-weight: 500; }

.perm-hint { font-size: 12px; color: var(--color-text-3); margin-bottom: 12px; }
.empty-state { text-align: center; padding: 40px; color: var(--color-text-3); }

.model-perm-item { padding: 4px 0; }
.perm-model-id { font-family: monospace; font-weight: 500; color: rgb(var(--primary-6)); }
.perm-model-name { font-size: 12px; color: var(--color-text-3); margin-left: 8px; }

.api-test-result {
  background: var(--color-fill-1); border-radius: 8px;
  padding: 12px; height: 100%; min-height: 200px;
}
.api-test-header { display: flex; align-items: center; gap: 12px; margin-bottom: 8px; }
.api-test-time { font-size: 12px; color: var(--color-text-3); }
.api-test-body {
  font-size: 12px; max-height: 300px; overflow-y: auto;
  white-space: pre-wrap; word-break: break-all; margin: 0;
  color: var(--color-text-2);
}

.new-token-display {
  background: var(--color-fill-1); border: 1px dashed rgb(var(--primary-6));
  border-radius: 8px; padding: 16px;
  text-align: center;
}
.usage-guide p { font-size: 13px; color: var(--color-text-2); margin-bottom: 8px; }
.code-block {
  background: #1e1e1e; color: #d4d4d4;
  border-radius: 6px; padding: 12px 16px;
  font-family: 'Consolas', 'Monaco', monospace; font-size: 13px;
  line-height: 1.6; overflow-x: auto; white-space: pre-wrap; word-break: break-all;
}
.wizard-footer {
  display: flex; justify-content: flex-end; gap: 8px;
}
</style>
