// Claude Code statusLine command for NeonMon.
// Saves the plan rate limits Claude Code passes to the status line so NeonMon can show them.
// Local only: no network access, no credentials, no model calls.
const fs = require('fs');
const path = require('path');

const target = path.join(process.env.LOCALAPPDATA || '.', 'NeonMon', 'claude-statusline.json');

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => (input += chunk));
process.stdin.on('end', () => {
  let payload = null;
  try { payload = JSON.parse(input); } catch {}

  const limits = payload && payload.rate_limits;
  if (limits) {
    try {
      fs.mkdirSync(path.dirname(target), { recursive: true });
      const temporary = target + '.tmp';
      fs.writeFileSync(temporary, JSON.stringify({
        savedAt: new Date().toISOString(),
        version: payload.version || null,
        rate_limits: limits
      }));
      fs.renameSync(temporary, target);
    } catch {}
  }

  process.stdout.write(format(limits));
});

function format(limits) {
  if (!limits) return 'usage: waiting for first response';
  const part = (label, window) => {
    if (!window || window.used_percentage == null) return null;
    const minutes = Math.max(0, Math.round((window.resets_at * 1000 - Date.now()) / 60000));
    const left = minutes >= 1440
      ? `${Math.floor(minutes / 1440)}d${Math.floor(minutes % 1440 / 60)}h`
      : `${Math.floor(minutes / 60)}h${minutes % 60}m`;
    return `${label} ${Math.round(100 - window.used_percentage)}% left (${left})`;
  };
  return [part('5h', limits.five_hour), part('7d', limits.seven_day)].filter(Boolean).join(' · ');
}
