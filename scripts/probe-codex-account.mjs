// Read-only protocol capability probe. Never prints account values or raw responses.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { resolve } from 'node:path';

const executable = process.argv[2];
if (!executable || !executable.toLowerCase().endsWith('.exe')) {
  throw new Error('Pass an explicit native Codex executable path.');
}
const proxy = process.argv.includes('--proxy');
const child = spawn(resolve(executable), proxy ? ['app-server', 'proxy'] : ['-s', 'read-only', '-a', 'on-request', 'app-server'], {
  windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
let diagnosticTail = '';
const diagnosticFlags = new Set();
child.stderr.on('data', chunk => {
  diagnosticTail = (diagnosticTail + chunk.toString()).slice(-4096);
  for (const [flag, pattern] of [
    ['platform-unsupported', /only supported on unix|not supported on (windows|this platform)|unsupported platform|unix.only|not available on windows/i],
    ['endpoint-missing', /no such file|cannot find|not found/i],
    ['connection-refused', /connection refused|actively refused/i],
    ['access-denied', /permission denied|access is denied/i],
  ]) if (pattern.test(diagnosticTail)) diagnosticFlags.add(flag);
}); // Only fixed classifications leave this process; never print raw stderr.
const pending = new Map();
let nextId = 0;
let accountChanged = false;
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  if (line.length > 1048576) { child.kill(); return; }
  try {
    const message = JSON.parse(line);
    if (message.method === 'account/updated') accountChanged = true;
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) {
      const detail = String(message.error.message ?? '');
      const category = /not logged|unauthoriz|authenticat|sign.in/i.test(detail) ? 'authentication'
        : /initializ/i.test(detail) ? 'initialization' : /not supported|unsupported|not available/i.test(detail) ? 'unsupported'
        : /invalid.*param/i.test(detail) ? 'parameters' : 'other';
      waiter.reject(new Error(`RPC ${waiter.method} rejected: code=${Number(message.error.code)}, category=${category} (details suppressed)`));
    }
    else waiter.resolve(message.result);
  } catch { /* Raw data is never logged. */ }
});
child.on('error', () => {
  for (const waiter of pending.values()) waiter.reject(new Error('Backend could not start'));
});
child.on('exit', () => {
  for (const waiter of pending.values()) waiter.reject(new Error('Backend exited'));
});
async function request(method, params = {}) {
  const id = ++nextId;
  let timer;
  try {
    return await new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject, method });
      timer = setTimeout(() => reject(new Error('RPC timeout')), 15000);
      child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    });
  } finally { clearTimeout(timer); pending.delete(id); }
}
const present = value => typeof value === 'string' && value.length > 0;
const identity = account => account?.accountId ?? account?.chatgptAccountId ?? account?.id;
try {
  await request('initialize', { clientInfo: { name: 'usage_loom_probe', version: '0.2.0' } });
  child.stdin.write(JSON.stringify({ method: 'initialized', params: {} }) + '\n');
  const before = (await request('account/read', { refreshToken: false }))?.account;
  console.log(JSON.stringify({ phase: 'account-before', accountPresent: !!before,
    chatgptMode: before?.type === 'chatgpt', stableIdPresent: present(identity(before)),
    emailPresent: present(before?.email), planPresent: present(before?.planType) }));
  const limits = await request('account/rateLimits/read');
  if(process.argv.includes('--limit-names')) console.log(JSON.stringify({limitNames:Object.entries(limits?.rateLimitsByLimitId??{}).map(([id,bucket])=>({id,name:bucket.limitName??null}))}));
  const after = (await request('account/read', { refreshToken: false }))?.account;
  console.log(JSON.stringify({
    transport: proxy ? 'existing-daemon-proxy' : 'independent-stdio',
    accountPresent: !!before,
    chatgptMode: before?.type === 'chatgpt',
    stableIdPresent: present(identity(before)),
    emailPresent: present(before?.email),
    planPresent: present(before?.planType),
    accountObservationUnchanged: JSON.stringify(before) === JSON.stringify(after) && !accountChanged,
    rateLimitsPresent: !!limits?.rateLimits,
    rateLimitBucketsPresent: !!limits?.rateLimitsByLimitId,
    resetCountPresent: Number.isInteger(limits?.rateLimitResetCredits?.availableCount),
  }, null, 2));
} catch (error) {
  console.error(error.message); console.log(JSON.stringify({diagnostics:[...diagnosticFlags]}));process.exitCode = 1;
} finally { lines.close(); child.stdin.end(); child.kill(); }
