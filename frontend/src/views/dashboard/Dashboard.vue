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

const modelColumns = [
  { title: '#', slotName: 'rank', width: 50 },
  { title: '模型', dataIndex: 'model' },
  { title: '调用次数', dataIndex: 'count', width: 100 },
  { title: 'Token用量', dataIndex: 'tokens', width: 100 }
]

const tokenColumns = [
  { title: '#', slotName: 'rank', width: 50 },
  { title: '令牌', dataIndex: 'token' },
  { title: '调用次数', dataIndex: 'count', width: 100 }
]

onMounted(async () => {
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
  } catch (e) {
    console.error(e)
  }
})
</script>

<style scoped>
.dashboard h2 { margin-bottom: 24px; }
.stats-row { margin-bottom: 24px; }
.chart-row { margin-bottom: 24px; }
</style>
