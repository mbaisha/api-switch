<template>
  <div class="settings-page">
    <div class="page-header">
      <h2>全局配置</h2>
    </div>

    <a-row :gutter="16">
      <a-col :span="12">
        <a-card title="系统配置">
          <a-form layout="vertical">
            <a-form-item label="全局冷却时长(秒)">
              <a-input-number v-model="configs.global_cooldown" :min="0" :max="3600" style="width:100%" />
            </a-form-item>
            <a-form-item label="全部节点故障兜底文案">
              <a-textarea v-model="configs.fallback_message" :max-length="500" show-word-limit :auto-size="{ minRows: 3 }" />
            </a-form-item>
            <a-form-item label="日志留存天数">
              <a-input-number v-model="configs.log_retention_days" :min="1" :max="365" style="width:100%" />
            </a-form-item>
            <a-form-item label="每分钟请求限制">
              <a-input-number v-model="configs.rate_limit_per_minute" :min="1" :max="10000" style="width:100%" />
            </a-form-item>
            <a-form-item>
              <a-button type="primary" :loading="saving" @click="saveConfig">保存配置</a-button>
            </a-form-item>
          </a-form>
        </a-card>
      </a-col>

      <a-col :span="12">
        <a-card title="渠道健康检测">
          <div style="margin-bottom:16px">
            <a-button type="primary" :loading="checking" @click="checkHealth">开始检测</a-button>
          </div>
          <a-table :columns="healthColumns" :data="healthResults" row-key="keyId" size="small">
            <template #healthy="{ record }">
              <a-tag :color="record.healthy ? 'green' : 'red'">
                {{ record.healthy ? '正常' : '异常' }}
              </a-tag>
            </template>
            <template #checkedAt="{ record }">
              {{ formatBeijingTime(record.checkedAt) }}
            </template>
          </a-table>
        </a-card>

        <a-card title="修改密码" style="margin-top:16px">
          <a-form layout="vertical">
            <a-form-item label="旧密码">
              <a-input-password v-model="pwdForm.oldPassword" placeholder="输入旧密码" />
            </a-form-item>
            <a-form-item label="新密码">
              <a-input-password v-model="pwdForm.newPassword" placeholder="输入新密码" />
            </a-form-item>
            <a-form-item>
              <a-button @click="changePassword">修改密码</a-button>
            </a-form-item>
          </a-form>
        </a-card>
      </a-col>
    </a-row>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { configApi, healthApi, authApi } from '../../api'
import { formatBeijingTime } from '../../utils/date'

const saving = ref(false)
const configs = ref({
  global_cooldown: 60,
  fallback_message: '所有可用渠道暂时不可用，请稍后重试',
  log_retention_days: 30,
  rate_limit_per_minute: 60
})

async function loadConfigs() {
  try {
    const res = await configApi.getAll()
    if (res.code === 200 && res.data) {
      Object.assign(configs.value, res.data)
    }
  } catch (e) { console.error(e) }
}

async function saveConfig() {
  saving.value = true
  try {
    const res = await configApi.setConfig(configs.value)
    if (res.code === 200) Message.success('配置已保存')
  } catch (e) { console.error(e) }
  finally { saving.value = false }
}

// 健康检测
const checking = ref(false)
const healthResults = ref([])
const healthColumns = [
  { title: '渠道', dataIndex: 'channelName' },
  { title: '状态', slotName: 'healthy', width: 80 },
  { title: '检测时间', slotName: 'checkedAt', width: 160 }
]

async function checkHealth() {
  checking.value = true
  try {
    const res = await healthApi.check()
    if (res.code === 200) healthResults.value = res.data
    Message.success('检测完成')
  } catch (e) { console.error(e) }
  finally { checking.value = false }
}

// 修改密码
const pwdForm = ref({ oldPassword: '', newPassword: '' })

async function changePassword() {
  if (!pwdForm.value.oldPassword || !pwdForm.value.newPassword) {
    Message.warning('请填写密码')
    return
  }
  try {
    const res = await authApi.changePassword(pwdForm.value)
    if (res.code === 200) {
      Message.success('密码修改成功')
      pwdForm.value = { oldPassword: '', newPassword: '' }
    }
  } catch (e) { console.error(e) }
}

onMounted(loadConfigs)
</script>

<style scoped>
.page-header { margin-bottom: 16px; }
.page-header h2 { margin: 0; }
</style>