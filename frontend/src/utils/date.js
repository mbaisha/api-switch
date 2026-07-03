/**
 * 日期格式化工具
 * 将所有时间强制显示为北京时间 (Asia/Shanghai, UTC+8)
 * 避免受浏览器/系统时区设置影响而显示 UTC 时间
 */

/**
 * 将 ISO 时间字符串格式化为北京时间
 * @param {string|Date} date - ISO 时间字符串或 Date 对象
 * @param {boolean} [showSeconds=true] - 是否显示秒
 * @returns {string} 格式化后的时间字符串，如 "2026-07-03 08:09:42"
 */
export function formatBeijingTime(date, showSeconds = true) {
  if (!date) return '-'
  try {
    const d = typeof date === 'string' ? new Date(date) : date
    const options = {
      timeZone: 'Asia/Shanghai',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      ...(showSeconds ? { second: '2-digit' } : {}),
      hour12: false
    }
    const formatter = new Intl.DateTimeFormat('en-CA', options)
    // en-CA locale gives YYYY-MM-DD format
    const parts = formatter.formatToParts(d)
    const get = (type) => parts.find(p => p.type === type)?.value || '00'
    if (showSeconds) {
      return `${get('year')}-${get('month')}-${get('day')} ${get('hour')}:${get('minute')}:${get('second')}`
    }
    return `${get('year')}-${get('month')}-${get('day')} ${get('hour')}:${get('minute')}`
  } catch {
    return '-'
  }
}

/**
 * 格式化为短日期（仅日期，无时间）
 * @param {string|Date} date
 * @returns {string} 如 "2026-07-03"
 */
export function formatBeijingDate(date) {
  if (!date) return '-'
  try {
    const d = typeof date === 'string' ? new Date(date) : date
    const formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone: 'Asia/Shanghai',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit'
    })
    return formatter.format(d)
  } catch {
    return '-'
  }
}