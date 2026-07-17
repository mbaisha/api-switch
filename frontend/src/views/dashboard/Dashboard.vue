<template>
  <div class="dashboard">
    <h2>数据看板</h2>

    <a-row :gutter="16" class="stats-row">
      <a-col :span="4" v-for="stat in stats" :key="stat.label">
        <a-card :hoverable="true">
          <a-statistic :title="stat.label" :value="stat.value" :precision="stat.precision" :prefix="stat.prefix" />
        </a-card>
      </a-col>
    </a-row>

    <a-row :gutter="16" class="chart-row">
      <a-col :span="12">
        <a-card title="模型调用量排行 (Top 5)">
          <a-table :columns="modelColumns" :data="modelUsage" row-key="model" size="small" :pagination="false">
            <template #rank="{ rowIndex }">
              <a-tag :color="rowIndex === 0 ? 'red' : rowIndex === 1 ? 'orange' : 'arcoblue'">
                #{{ rowIndex + 1 }}
              </a-tag>
            </template>
          </a-table>
        </a-card>
      </a-col>
      <a-col :span="12">
        <a-card title="令牌调用量排行 (Top 5)">
          <a-table :columns="tokenColumns" :data="tokenUsage" row-key="token" size="small" :pagination="false">
            <template #rank="{ rowIndex }">
              <a-tag :color="rowIndex === 0 ? 'red' : rowIndex === 1 ? 'orange' : 'arcoblue'">
                #{{ rowIndex + 1 }}
              </a-tag>
            </template>
          </a-table>
        </a-card>
      </a-col>
    </a-row>

    <!-- API Key 用量（按渠道+密钥） -->
    <a-card title="API Key 调用量（按渠道）" class="chart-row" :loading="keyLoading">
      <a-collapse :default-active-key="upstreamStats.map(c => c.channelId)" accordion>
        <a-collapse-item v-for="ch in upstreamStats" :key="ch.channelId" :header="`${ch.channelName} (${ch.supplierType}) · 共 ${ch.totalKeys} 个密钥 · 调用 ${ch.totalCalls} 次 · 成功 ${ch.successCalls} 次`">
          <a-table :columns="keyColumns" :data="ch.keys" row-key="id" size="small" :pagination="false">
            <template #status="{ record }">
              <a-tag :color="record.status === 1 ? 'green' : record.status === 2 ? 'red' : 'gray'" size="small">
                {{ record.status === 1 ? '正常' : record.status === 2 ? '失效' : '禁用' }}
              </a-tag>
            </template>
          </a-table>
        </a-collapse-item>
      </a-collapse>
      <a-empty v-if="upstreamStats.length === 0" description="暂无密钥用量数据" />
    </a-card>

    <!-- 模型用量明细（按自定义模型ID + 按上游原始模型ID 双视角） -->
    <a-row :gutter="16" class="chart-row">
      <a-col :span="12">
        <a-card title="模型用量明细（按对外模型ID）" :loading="modelDetailLoading">
          <a-table :columns="modelByCustomColumns" :data="modelByCustom" row-key="model" size="small" :pagination="{ pageSize: 8 }" />
        </a-card>
      </a-col>
      <a-col :span="12">
        <a-card title="模型用量明细（按上游模型/渠道）" :loading="modelDetailLoading">
          <a-table :columns="modelByOriginalColumns" :data="modelByOriginal" row-key="customModel" size="small" :pagination="{ pageSize: 8 }" />
        </a-card>
      </a-col>
    </a-row>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { dashboardApi } from '../../api'

const stats = ref([
  { label: '总调用量', value: 0 },
  { label: '今日调用', value: 0 },
  { label: '今日成功', value: 0 },
  { label: '今日失败', value: 0 },
  { label: '成功率', value: 0, precision: 1, prefix: '', suffix: '%' },
  { label: '今日消费', value: 0, precision: 4, prefix: '¥' },
  { label: '总输入Token', value: 0 },
  { label: '总输出Token', value: 0 },
  { label: '活跃令牌', value: 0 },
  { label: '活跃渠道', value: 0 }
])

const modelUsage = ref([])
const tokenUsage = ref([])
const upstreamStats = ref([])
const modelByCustom = ref([])
const modelByOriginal = ref([])
const keyLoading = ref(false)
const modelDetailLoading = ref(false)

const modelColumns = [
  { title: '#', slotName: 'rank', width: 50 },
  { title: '模型', dataIndex: 'model' },
  { title: '调用次数', dataIndex: 'count', width: 100 },
  { title: 'Token用量', dataIndex: 'tokens', width: 100 }
]
const tokenColumns = [
  { title: '#', slotName: 'rank', width: 50 },
  { title: '令牌', dataIndex: 'token' },
  { title: '调用次数', dataIndex: 'count', width: 100 },
  { title: 'Token用量', dataIndex: 'tokens', width: 100 }
]
const keyColumns = [
  { title: '密钥', dataIndex: 'keyValue', ellipsis: true, width: 220 },
  { title: '调用次数', dataIndex: 'totalCalls', width: 90 },
  { title: '成功次数', dataIndex: 'successCalls', width: 90 },
  { title: 'Token用量', dataIndex: 'usedTokens', width: 100 },
  { title: '权重', dataIndex: 'weight', width: 60 },
  { title: '状态', slotName: 'status', width: 80 }
]
const modelByCustomColumns = [
  { title: '对外模型ID', dataIndex: 'model', ellipsis: true },
  { title: '调用', dataIndex: 'calls', width: 65 },
  { title: '成功', dataIndex: 'success', width: 55 },
  { title: '失败', dataIndex: 'failed', width: 55 },
  { title: '输入Token', dataIndex: 'inputTokens', width: 100, cellClass: 'cell-nowrap' },
  { title: '输出Token', dataIndex: 'outputTokens', width: 100, cellClass: 'cell-nowrap' },
  { title: '总Token', dataIndex: 'totalTokens', width: 100, cellClass: 'cell-nowrap' }
]
const modelByOriginalColumns = [
  { title: '对外模型', dataIndex: 'customModel', ellipsis: true },
  { title: '上游模型', dataIndex: 'originalModel', ellipsis: true },
  { title: '渠道', dataIndex: 'channel', ellipsis: true },
  { title: '调用', dataIndex: 'calls', width: 65 },
  { title: '成功', dataIndex: 'success', width: 55 },
  { title: '失败', dataIndex: 'failed', width: 55 },
  { title: '总Token', dataIndex: 'totalTokens', width: 100, cellClass: 'cell-nowrap' }
]

async function loadMain() {
  try {
    const res = await dashboardApi.get()
    if (res.code === 200 && res.data) {
      const d = res.data
      stats.value[0].value = d.totalCalls
      stats.value[1].value = d.todayCalls
      stats.value[2].value = d.todaySuccess
      stats.value[3].value = d.todayFailed
      stats.value[4].value = d.successRate
      stats.value[5].value = d.todayCost
      stats.value[6].value = d.totalInputTokens
      stats.value[7].value = d.totalOutputTokens
      stats.value[8].value = d.tokenCount
      stats.value[9].value = d.channelCount
      modelUsage.value = d.modelUsage || []
      tokenUsage.value = d.tokenUsage || []
    }
  } catch (e) { console.error(e) }
}

async function loadUpstreamKeys() {
  keyLoading.value = true
  try {
    const res = await dashboardApi.upstreamKeys()
    if (res.code === 200) upstreamStats.value = res.data?.channels || []
  } catch (e) { console.error(e) } finally { keyLoading.value = false }
}

async function loadModelUsage() {
  modelDetailLoading.value = true
  try {
    const res = await dashboardApi.modelUsage()
    if (res.code === 200) {
      modelByCustom.value = res.data?.byCustom || []
      modelByOriginal.value = res.data?.byOriginal || []
    }
  } catch (e) { console.error(e) } finally { modelDetailLoading.value = false }
}

// 三个请求并行发起，互不阻塞；任一失败不影响其他
onMounted(() => {
  loadMain()
  loadUpstreamKeys()
  loadModelUsage()
})
</script>

<style scoped>
.dashboard h2 { margin-bottom: 24px; }
.stats-row { margin-bottom: 24px; }
.chart-row { margin-bottom: 24px; }
:deep(.cell-nowrap) { white-space: nowrap; }
</style>
