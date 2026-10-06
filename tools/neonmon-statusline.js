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
    try { save(limits, payload.version || null); } catch {}
  }

  process.stdout.write(format(limits));
});

// Every open session redraws its status line with the last limits it saw, so an idle session would
// roll the saved numbers back. Merge per window instead: a later resets_at wins, within one window the
// higher usage wins, and windows that have already reset are dropped.
function save(incoming, version) {
  let stored = {};
  try { stored = JSON.parse(fs.readFileSync(target, 'utf8')).rate_limits || {}; } catch {}

  const nowSeconds = Date.now() / 1000;
  const merged = {};
  let current = false;
  for (const name of new Set([...Object.keys(stored), ...Object.keys(incoming)])) {
    const mine = live(incoming[name], nowSeconds);
    const best = pick(live(stored[name], nowSeconds), mine);
    if (best) merged[name] = best;
    if (mine && best === mine) current = true;
  }

  // A stale session neither changes the numbers nor refreshes savedAt.
  if (!current && JSON.stringify(merged) === JSON.stringify(stored)) return;
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const temporary = `${target}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify({
    savedAt: new Date().toISOString(),
    version,
    rate_limits: merged
  }));
  fs.renameSync(temporary, target);
}

function live(window, nowSeconds) {
  return window && typeof window.used_percentage === 'number' && typeof window.resets_at === 'number'
    && window.resets_at > nowSeconds ? window : null;
}

function pick(a, b) {
  if (!a || !b) return a || b;
  // resets_at differs by a few seconds between sessions; a minute apart is still the same window.
  if (Math.abs(a.resets_at - b.resets_at) > 60) return a.resets_at > b.resets_at ? a : b;
  return b.used_percentage >= a.used_percentage ? b : a;
}

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
