<template>
  <div class="logs-page">
    <div class="page-header">
      <h2>日志查询</h2>
    </div>

    <a-tabs default-active-key="calls">
      <a-tab-pane key="calls" title="调用日志">
        <!-- 筛选 -->
        <a-row :gutter="12" class="filter-row">
          <a-col :span="4">
            <a-input v-model="callFilter.token" placeholder="令牌" allow-clear />
          </a-col>
          <a-col :span="4">
            <a-input v-model="callFilter.model" placeholder="模型" allow-clear />
          </a-col>
          <a-col :span="3">
            <a-select v-model="callFilter.status" placeholder="状态" allow-clear>
              <a-option value="Success">成功</a-option>
              <a-option value="Failed">失败</a-option>
            </a-select>
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="callFilter.startTime" placeholder="开始时间" style="width:100%" />
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="callFilter.endTime" placeholder="结束时间" style="width:100%" />
          </a-col>
          <a-col :span="5">
            <a-space>
              <a-button type="primary" @click="loadCallLogs(1)">查询</a-button>
              <a-button @click="exportCallLogs">导出CSV</a-button>
            </a-space>
          </a-col>
        </a-row>

        <a-table :columns="callColumns" :data="callLogs" row-key="id" :loading="loading" :pagination="false">
          <template #status="{ record }">
            <a-tag
              :color="record.status === 'Success' ? 'green' : 'red'"
              :class="record.status !== 'Success' ? 'status-failed' : ''"
              @click="record.status !== 'Success' && showError(record)"
            >
              {{ record.status === 'Success' ? '成功' : '失败' }}
            </a-tag>
          </template>
          <template #isStream="{ record }">
            <a-tag :color="record.isStream ? 'blue' : 'gray'" size="small">
              {{ record.isStream ? '流式' : '普通' }}
            </a-tag>
          </template>
          <template #createdAt="{ record }">
            {{ formatBeijingTime(record.createdAt) }}
          </template>
        </a-table>
        <a-pagination
          :total="callTotal" :current="callPage" :page-size="20"
          @change="onCallPageChange" style="margin-top: 16px"
        />
      </a-tab-pane>

      <a-tab-pane key="operations" title="操作日志">
        <a-row :gutter="12" class="filter-row">
          <a-col :span="4">
            <a-select v-model="opFilter.action" placeholder="操作类型" allow-clear>
              <a-option value="Create">创建</a-option>
              <a-option value="Update">更新</a-option>
              <a-option value="Delete">删除</a-option>
            </a-select>
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="opFilter.startTime" placeholder="开始时间" style="width:100%" />
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="opFilter.endTime" placeholder="结束时间" style="width:100%" />
          </a-col>
          <a-col :span="4">
            <a-space>
              <a-button type="primary" @click="loadOpLogs(1)">查询</a-button>
              <a-button @click="exportOpLogs">导出CSV</a-button>
            </a-space>
          </a-col>
        </a-row>

        <a-table :columns="opColumns" :data="opLogs" row-key="id" :loading="loading" :pagination="false">
          <template #createdAt="{ record }">
            {{ formatBeijingTime(record.createdAt) }}
          </template>
        </a-table>
        <a-pagination
          :total="opTotal" :current="opPage" :page-size="20"
          @change="onOpPageChange" style="margin-top: 16px"
        />
      </a-tab-pane>
    </a-tabs>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { logApi } from '../../api'
import { formatBeijingTime } from '../../utils/date'

const callLogs = ref([])
const opLogs = ref([])
const loading = ref(false)
const callPage = ref(1)
const callTotal = ref(0)
const opPage = ref(1)
const opTotal = ref(0)

const callFilter = ref({ token: '', model: '', status: '', startTime: '', endTime: '' })
const opFilter = ref({ action: '', startTime: '', endTime: '' })

// 点击"失败"状态标签时，弹出详细错误原因
function showError(record) {
  const msg = record.errorMessage || '无详细错误信息'
  Message.error({
    content: msg,
    duration: 0,          // 不自动关闭，方便复制/阅读
    closable: true,
    showIcon: true
  })
}

const callColumns = [
  { title: 'ID', dataIndex: 'id', width: 60 },
  { title: '令牌', dataIndex: 'tokenValue', width: 180 },
  { title: '备注', dataIndex: 'tokenRemark', ellipsis: true, width: 100 },
  { title: 'IP', dataIndex: 'clientIp', width: 130 },
  { title: '模型', dataIndex: 'customModelId', width: 120 },
  { title: '渠道', dataIndex: 'channelName', width: 100 },
  { title: '状态', slotName: 'status', width: 70 },
  { title: '类型', slotName: 'isStream', width: 60 },
  { title: '输入', dataIndex: 'inputTokens', width: 70 },
  { title: '输出', dataIndex: 'outputTokens', width: 70 },
  { title: '耗时(ms)', dataIndex: 'durationMs', width: 80 },
  { title: '时间', slotName: 'createdAt', width: 160 }
]

const opColumns = [
  { title: 'ID', dataIndex: 'id', width: 60 },
  { title: '操作人', dataIndex: 'operator' },
  { title: '操作', dataIndex: 'action', width: 80 },
  { title: '目标', dataIndex: 'target', width: 80 },
  { title: '内容', dataIndex: 'content', ellipsis: true },
  { title: 'IP', dataIndex: 'clientIp', width: 120 },
  { title: '时间', slotName: 'createdAt', width: 160 }
]

async function loadCallLogs(page = 1) {
  loading.value = true
  callPage.value = page
  try {
    const params = { page, pageSize: 20 }
    if (callFilter.value.token) params.token = callFilter.value.token
    if (callFilter.value.model) params.model = callFilter.value.model
    if (callFilter.value.status) params.status = callFilter.value.status
    if (callFilter.value.startTime) params.startTime = callFilter.value.startTime
    if (callFilter.value.endTime) params.endTime = callFilter.value.endTime
    const res = await logApi.callLogs(params)
    if (res.code === 200) {
      callLogs.value = res.data.list
      callTotal.value = res.data.total
    }
  } finally { loading.value = false }
}

async function exportCallLogs() {
  try {
    const params = {}
    if (callFilter.value.startTime) params.startTime = callFilter.value.startTime
    if (callFilter.value.endTime) params.endTime = callFilter.value.endTime
    if (callFilter.value.status) params.status = callFilter.value.status
    const res = await logApi.exportCalls(params)
    downloadBlob(res, `call_logs_${new Date().toISOString().slice(0,10)}.csv`)
    Message.success('导出成功')
  } catch (e) { console.error(e) }
}

async function loadOpLogs(page = 1) {
  loading.value = true
  opPage.value = page
  try {
    const params = { page, pageSize: 20 }
    if (opFilter.value.action) params.action = opFilter.value.action
    if (opFilter.value.startTime) params.startTime = opFilter.value.startTime
    if (opFilter.value.endTime) params.endTime = opFilter.value.endTime
    const res = await logApi.operationLogs(params)
    if (res.code === 200) {
      opLogs.value = res.data.list
      opTotal.value = res.data.total
    }
  } finally { loading.value = false }
}

async function exportOpLogs() {
  try {
    const params = {}
    if (opFilter.value.startTime) params.startTime = opFilter.value.startTime
    if (opFilter.value.endTime) params.endTime = opFilter.value.endTime
    const res = await logApi.exportOperations(params)
    downloadBlob(res, `operation_logs_${new Date().toISOString().slice(0,10)}.csv`)
    Message.success('导出成功')
  } catch (e) { console.error(e) }
}

function downloadBlob(data, filename) {
  const url = window.URL.createObjectURL(new Blob([data]))
  const link = document.createElement('a')
  link.href = url
  link.setAttribute('download', filename)
  document.body.appendChild(link)
  link.click()
  link.remove()
}

function onCallPageChange(page) { loadCallLogs(page) }
function onOpPageChange(page) { loadOpLogs(page) }

onMounted(() => {
  loadCallLogs()
  loadOpLogs()
})
</script>

<style scoped>
.page-header { margin-bottom: 16px; }
.page-header h2 { margin: 0; }
.filter-row { margin-bottom: 16px; }
.status-failed { cursor: pointer; }
</style>
